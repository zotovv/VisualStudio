﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using System.Globalization;
using GitHub.App;
using GitHub.Extensions;
using GitHub.Factories;
using GitHub.Helpers;
using GitHub.Logging;
using GitHub.Models;
using GitHub.Services;
using LibGit2Sharp;
using ReactiveUI;
using Serilog;

namespace GitHub.ViewModels.GitHubPane
{
    /// <summary>
    /// A view model which displays the details of a pull request.
    /// </summary>
    [Export(typeof(IPullRequestDetailViewModel))]
    [PartCreationPolicy(CreationPolicy.NonShared)]
    public sealed class PullRequestDetailViewModel : PanePageViewModelBase, IPullRequestDetailViewModel
    {
        static readonly ILogger log = LogManager.ForContext<PullRequestDetailViewModel>();

        readonly IModelServiceFactory modelServiceFactory;
        readonly IPullRequestService pullRequestsService;
        readonly IPullRequestSessionManager sessionManager;
        readonly IUsageTracker usageTracker;
        readonly ITeamExplorerContext teamExplorerContext;
        readonly IStatusBarNotificationService statusBarNotificationService;
        IModelService modelService;
        IPullRequestModel model;
        string sourceBranchDisplayName;
        string targetBranchDisplayName;
        int commentCount;
        string body;
        IReadOnlyList<IPullRequestReviewSummaryViewModel> reviews;
        IPullRequestCheckoutState checkoutState;
        IPullRequestUpdateState updateState;
        string operationError;
        bool isCheckedOut;
        bool isFromFork;
        bool isInCheckout;
        bool active;
        bool refreshOnActivate;
        Uri webUrl;

        /// <summary>
        /// Initializes a new instance of the <see cref="PullRequestDetailViewModel"/> class.
        /// </summary>
        /// <param name="localRepository">The local repository.</param>
        /// <param name="modelService">The model service.</param>
        /// <param name="pullRequestsService">The pull requests service.</param>
        /// <param name="sessionManager">The pull request session manager.</param>
        /// <param name="usageTracker">The usage tracker.</param>
        /// <param name="teamExplorerContext">The context for tracking repo changes</param>
        [ImportingConstructor]
        public PullRequestDetailViewModel(
            IPullRequestService pullRequestsService,
            IPullRequestSessionManager sessionManager,
            IModelServiceFactory modelServiceFactory,
            IUsageTracker usageTracker,
            ITeamExplorerContext teamExplorerContext,
            IStatusBarNotificationService statusBarNotificationService,
            IPullRequestFilesViewModel files)
        {
            Guard.ArgumentNotNull(pullRequestsService, nameof(pullRequestsService));
            Guard.ArgumentNotNull(sessionManager, nameof(sessionManager));
            Guard.ArgumentNotNull(modelServiceFactory, nameof(modelServiceFactory));
            Guard.ArgumentNotNull(usageTracker, nameof(usageTracker));
            Guard.ArgumentNotNull(teamExplorerContext, nameof(teamExplorerContext));
            Guard.ArgumentNotNull(statusBarNotificationService, nameof(statusBarNotificationService));

            this.pullRequestsService = pullRequestsService;
            this.sessionManager = sessionManager;
            this.modelServiceFactory = modelServiceFactory;
            this.usageTracker = usageTracker;
            this.teamExplorerContext = teamExplorerContext;
            this.statusBarNotificationService = statusBarNotificationService;
            Files = files;

            Checkout = ReactiveCommand.CreateAsyncObservable(
                this.WhenAnyValue(x => x.CheckoutState)
                    .Cast<CheckoutCommandState>()
                    .Select(x => x != null && x.IsEnabled),
                DoCheckout);
            Checkout.IsExecuting.Subscribe(x => isInCheckout = x);
            SubscribeOperationError(Checkout);

            Pull = ReactiveCommand.CreateAsyncObservable(
                this.WhenAnyValue(x => x.UpdateState)
                    .Cast<UpdateCommandState>()
                    .Select(x => x != null && x.PullEnabled),
                DoPull);
            SubscribeOperationError(Pull);

            Push = ReactiveCommand.CreateAsyncObservable(
                this.WhenAnyValue(x => x.UpdateState)
                    .Cast<UpdateCommandState>()
                    .Select(x => x != null && x.PushEnabled),
                DoPush);
            SubscribeOperationError(Push);

            SyncSubmodules = ReactiveCommand.CreateAsyncTask(
                this.WhenAnyValue(x => x.UpdateState)
                    .Cast<UpdateCommandState>()
                    .Select(x => x != null && x.SyncSubmodulesEnabled),
                DoSyncSubmodules);
            SyncSubmodules.Subscribe(_ => Refresh().ToObservable());
            SubscribeOperationError(SyncSubmodules);

            OpenOnGitHub = ReactiveCommand.Create();
            ShowReview = ReactiveCommand.Create().OnExecuteCompleted(DoShowReview);
        }

        /// <summary>
        /// Gets the underlying pull request model.
        /// </summary>
        public IPullRequestModel Model
        {
            get { return model; }
            private set
            {
                // PullRequestModel overrides Equals such that two PRs with the same number are
                // considered equal. This was causing the Model not to be updated on refresh:
                // we need to use ReferenceEquals.
                if (!ReferenceEquals(model, value))
                {
                    this.RaisePropertyChanging(nameof(Model));
                    model = value;
                    this.RaisePropertyChanged(nameof(Model));
                }
            }
        }

        /// <summary>
        /// Gets the local repository.
        /// </summary>
        public ILocalRepositoryModel LocalRepository { get; private set; }

        /// <summary>
        /// Gets the owner of the remote repository that contains the pull request.
        /// </summary>
        /// <remarks>
        /// The remote repository may be different from the local repository if the local
        /// repository is a fork and the user is viewing pull requests from the parent repository.
        /// </remarks>
        public string RemoteRepositoryOwner { get; private set; }

        /// <summary>
        /// Gets the Pull Request number.
        /// </summary>
        public int Number { get; private set; }

        /// <summary>
        /// Gets the session for the pull request.
        /// </summary>
        public IPullRequestSession Session { get; private set; }

        /// <summary>
        /// Gets a string describing how to display the pull request's source branch.
        /// </summary>
        public string SourceBranchDisplayName
        {
            get { return sourceBranchDisplayName; }
            private set { this.RaiseAndSetIfChanged(ref sourceBranchDisplayName, value); }
        }

        /// <summary>
        /// Gets a string describing how to display the pull request's target branch.
        /// </summary>
        public string TargetBranchDisplayName
        {
            get { return targetBranchDisplayName; }
            private set { this.RaiseAndSetIfChanged(ref targetBranchDisplayName, value); }
        }

        /// <summary>
        /// Gets the number of comments made on the pull request.
        /// </summary>
        public int CommentCount
        {
            get { return commentCount; }
            private set { this.RaiseAndSetIfChanged(ref commentCount, value); }
        }

        /// Gets a value indicating whether the pull request branch is checked out.
        /// </summary>
        public bool IsCheckedOut
        {
            get { return isCheckedOut; }
            private set { this.RaiseAndSetIfChanged(ref isCheckedOut, value); }
        }

        /// <summary>
        /// Gets a value indicating whether the pull request comes from a fork.
        /// </summary>
        public bool IsFromFork
        {
            get { return isFromFork; }
            private set { this.RaiseAndSetIfChanged(ref isFromFork, value); }
        }

        /// <summary>
        /// Gets the pull request body.
        /// </summary>
        public string Body
        {
            get { return body; }
            private set { this.RaiseAndSetIfChanged(ref body, value); }
        }

        /// <summary>
        /// Gets the state associated with the <see cref="Checkout"/> command.
        /// </summary>
        public IPullRequestCheckoutState CheckoutState
        {
            get { return checkoutState; }
            private set { this.RaiseAndSetIfChanged(ref checkoutState, value); }
        }

        /// <summary>
        /// Gets the state associated with the <see cref="Pull"/> and <see cref="Push"/> commands.
        /// </summary>
        public IPullRequestUpdateState UpdateState
        {
            get { return updateState; }
            private set { this.RaiseAndSetIfChanged(ref updateState, value); }
        }

        /// <summary>
        /// Gets the error message to be displayed in the action area as a result of an error in a
        /// git operation.
        /// </summary>
        public string OperationError
        {
            get { return operationError; }
            private set { this.RaiseAndSetIfChanged(ref operationError, value); }
        }

        /// <summary>
        /// Gets the latest pull request review for each user.
        /// </summary>
        public IReadOnlyList<IPullRequestReviewSummaryViewModel> Reviews
        {
            get { return reviews; }
            private set { this.RaiseAndSetIfChanged(ref reviews, value); }
        }

        /// <summary>
        /// Gets the pull request's changed files.
        /// </summary>
        public IPullRequestFilesViewModel Files { get; }

        /// <summary>
        /// Gets the web URL for the pull request.
        /// </summary>
        public Uri WebUrl
        {
            get { return webUrl; }
            private set { this.RaiseAndSetIfChanged(ref webUrl, value); }
        }

        /// <summary>
        /// Gets a command that checks out the pull request locally.
        /// </summary>
        public ReactiveCommand<Unit> Checkout { get; }

        /// <summary>
        /// Gets a command that pulls changes to the current branch.
        /// </summary>
        public ReactiveCommand<Unit> Pull { get; }

        /// <summary>
        /// Gets a command that pushes changes from the current branch.
        /// </summary>
        public ReactiveCommand<Unit> Push { get; }

        /// <summary>
        /// Sync submodules for PR branch.
        /// </summary>
        public ReactiveCommand<Unit> SyncSubmodules { get; }

        /// <summary>
        /// Gets a command that opens the pull request on GitHub.
        /// </summary>
        public ReactiveCommand<object> OpenOnGitHub { get; }

        /// <summary>
        /// Gets a command that navigates to a pull request review.
        /// </summary>
        public ReactiveCommand<object> ShowReview { get; }

        /// <summary>
        /// Initializes the view model.
        /// </summary>
        /// <param name="localRepository">The local repository.</param>
        /// <param name="connection">The connection to the repository host.</param>
        /// <param name="owner">The pull request's repository owner.</param>
        /// <param name="repo">The pull request's repository name.</param>
        /// <param name="number">The pull request number.</param>
        public async Task InitializeAsync(
            ILocalRepositoryModel localRepository,
            IConnection connection,
            string owner,
            string repo,
            int number)
        {
            if (repo != localRepository.Name)
            {
                throw new NotSupportedException();
            }

            IsLoading = true;

            try
            {
                LocalRepository = localRepository;
                RemoteRepositoryOwner = owner;
                Number = number;
                WebUrl = LocalRepository.CloneUrl.ToRepositoryUrl().Append("pull/" + number);
                modelService = await modelServiceFactory.CreateAsync(connection);

                await Refresh();
                teamExplorerContext.StatusChanged += RefreshIfActive;
            }
            finally
            {
                IsLoading = false;
            }
        }

        void RefreshIfActive(object sender, EventArgs e)
        {
            if (active)
            {
                Refresh().Forget();
            }
            else
            {
                refreshOnActivate = true;
            }
        }

        /// <summary>
        /// Loads the view model from octokit models.
        /// </summary>
        /// <param name="pullRequest">The pull request model.</param>
        public async Task Load(IPullRequestModel pullRequest)
        {
            try
            {
                var firstLoad = (Model == null);
                Model = pullRequest;
                Session = await sessionManager.GetSession(pullRequest);
                Title = Resources.PullRequestNavigationItemText + " #" + pullRequest.Number;

                IsBusy = true;
                IsFromFork = !pullRequestsService.IsPullRequestFromRepository(LocalRepository, Model);
                SourceBranchDisplayName = GetBranchDisplayName(IsFromFork, pullRequest.Head?.Label);
                TargetBranchDisplayName = GetBranchDisplayName(IsFromFork, pullRequest.Base?.Label);
                CommentCount = pullRequest.Comments.Count + pullRequest.ReviewComments.Count;
                Body = !string.IsNullOrWhiteSpace(pullRequest.Body) ? pullRequest.Body : Resources.NoDescriptionProvidedMarkdown;
                Reviews = PullRequestReviewSummaryViewModel.BuildByUser(Session.User, pullRequest).ToList();

                await Files.InitializeAsync(Session);

                var localBranches = await pullRequestsService.GetLocalBranches(LocalRepository, pullRequest).ToList();

                IsCheckedOut = localBranches.Contains(LocalRepository.CurrentBranch);

                if (IsCheckedOut)
                {
                    var divergence = await pullRequestsService.CalculateHistoryDivergence(LocalRepository, Model.Number);
                    var pullEnabled = divergence.BehindBy > 0;
                    var pushEnabled = divergence.AheadBy > 0 && !pullEnabled;
                    string pullToolTip;
                    string pushToolTip;

                    if (pullEnabled)
                    {
                        pullToolTip = string.Format(
                            Resources.PullRequestDetailsPullToolTip,
                            IsFromFork ? Resources.Fork : Resources.Remote,
                            SourceBranchDisplayName);
                    }
                    else
                    {
                        pullToolTip = Resources.NoCommitsToPull;
                    }

                    if (pushEnabled)
                    {
                        pushToolTip = string.Format(
                            Resources.PullRequestDetailsPushToolTip,
                            IsFromFork ? Resources.Fork : Resources.Remote,
                            SourceBranchDisplayName);
                    }
                    else if (divergence.AheadBy == 0)
                    {
                        pushToolTip = Resources.NoCommitsToPush;
                    }
                    else
                    {
                        pushToolTip = Resources.MustPullBeforePush;
                    }

                    var submodulesToSync = await pullRequestsService.CountSubmodulesToSync(LocalRepository);
                    var syncSubmodulesToolTip = string.Format(Resources.SyncSubmodules, submodulesToSync);

                    UpdateState = new UpdateCommandState(divergence, pullEnabled, pushEnabled, pullToolTip, pushToolTip, syncSubmodulesToolTip, submodulesToSync);
                    CheckoutState = null;
                }
                else
                {
                    var caption = localBranches.Count > 0 ?
                        string.Format(Resources.PullRequestDetailsCheckout, localBranches.First().DisplayName) :
                        string.Format(Resources.PullRequestDetailsCheckoutTo, await pullRequestsService.GetDefaultLocalBranchName(LocalRepository, Model.Number, Model.Title));
                    var clean = await pullRequestsService.IsWorkingDirectoryClean(LocalRepository);
                    string disabled = null;

                    if (pullRequest.Head == null || !pullRequest.Head.RepositoryCloneUrl.IsValidUri)
                    {
                        disabled = Resources.SourceRepositoryNoLongerAvailable;
                    }
                    else if (!clean)
                    {
                        disabled = Resources.WorkingDirectoryHasUncommittedCHanges;
                    }

                    CheckoutState = new CheckoutCommandState(caption, disabled);
                    UpdateState = null;
                }

                if (firstLoad)
                {
                    usageTracker.IncrementCounter(x => x.NumberOfPullRequestsOpened).Forget();
                }

                if (!isInCheckout)
                {
                    pullRequestsService.RemoveUnusedRemotes(LocalRepository).Subscribe(_ => { });
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Refreshes the contents of the view model.
        /// </summary>
        public override async Task Refresh()
        {
            try
            {
                await ThreadingHelper.SwitchToMainThreadAsync();

                Error = null;
                OperationError = null;
                IsBusy = true;
                var pullRequest = await modelService.GetPullRequest(RemoteRepositoryOwner, LocalRepository.Name, Number);
                await Load(pullRequest);
            }
            catch (Exception ex)
            {
                log.Error(
                    ex,
                    "Error loading pull request {Owner}/{Repo}/{Number} from {Address}",
                    RemoteRepositoryOwner,
                    LocalRepository.Name,
                    Number,
                    modelService.ApiClient.HostAddress.Title);
                Error = ex;
                IsBusy = false;
            }
        }

        /// <summary>
        /// Gets a file as it appears in the pull request.
        /// </summary>
        /// <param name="file">The changed file.</param>
        /// <param name="head">
        /// If true, gets the file at the PR head, otherwise gets the file at the PR merge base.
        /// </param>
        /// <returns>The path to a temporary file.</returns>
        public Task<string> ExtractFile(IPullRequestFileNode file, bool head)
        {
            var path = file.RelativePath;
            var encoding = pullRequestsService.GetEncoding(LocalRepository, path);

            if (!head && file.OldPath != null)
            {
                path = file.OldPath;
            }

            return pullRequestsService.ExtractFile(LocalRepository, model, path, head, encoding).ToTask();
        }

        /// <summary>
        /// Gets the full path to a file in the working directory.
        /// </summary>
        /// <param name="file">The file.</param>
        /// <returns>The full path to the file in the working directory.</returns>
        public string GetLocalFilePath(IPullRequestFileNode file)
        {
            return Path.Combine(LocalRepository.LocalPath, file.RelativePath);
        }

        /// <inheritdoc/>
        public override void Activated()
        {
            active = true;

            if (refreshOnActivate)
            {
                Refresh().Forget();
                refreshOnActivate = false;
            }
        }

        /// <inheritdoc/>
        public override void Deactivated() => active = false;

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                teamExplorerContext.StatusChanged -= RefreshIfActive;
            }
        }

        void SubscribeOperationError(ReactiveCommand<Unit> command)
        {
            command.ThrownExceptions.Subscribe(x => OperationError = x.Message);
            command.IsExecuting.Select(x => x).Subscribe(x => OperationError = null);
        }

        static string GetBranchDisplayName(bool isFromFork, string targetBranchLabel)
        {
            if (targetBranchLabel != null)
            {
                return isFromFork ? targetBranchLabel : targetBranchLabel.Split(':')[1];
            }
            else
            {
                return Resources.InvalidBranchName;
            }
        }

        IObservable<Unit> DoCheckout(object unused)
        {
            return Observable.Defer(async () =>
            {
                var localBranches = await pullRequestsService.GetLocalBranches(LocalRepository, Model).ToList();

                if (localBranches.Count > 0)
                {
                    return pullRequestsService.SwitchToBranch(LocalRepository, Model);
                }
                else
                {
                    return pullRequestsService
                        .GetDefaultLocalBranchName(LocalRepository, Model.Number, Model.Title)
                        .SelectMany(x => pullRequestsService.Checkout(LocalRepository, Model, x));
                }
            }).Do(_ =>
            {
                if (IsFromFork)
                    usageTracker.IncrementCounter(x => x.NumberOfForkPullRequestsCheckedOut).Forget();
                else
                    usageTracker.IncrementCounter(x => x.NumberOfLocalPullRequestsCheckedOut).Forget();
            });
        }

        IObservable<Unit> DoPull(object unused)
        {
            return pullRequestsService.Pull(LocalRepository)
                .Do(_ =>
                {
                    if (IsFromFork)
                        usageTracker.IncrementCounter(x => x.NumberOfForkPullRequestPulls).Forget();
                    else
                        usageTracker.IncrementCounter(x => x.NumberOfLocalPullRequestPulls).Forget();
                });
        }

        IObservable<Unit> DoPush(object unused)
        {
            return pullRequestsService.Push(LocalRepository)
                .Do(_ =>
                {
                    if (IsFromFork)
                        usageTracker.IncrementCounter(x => x.NumberOfForkPullRequestPushes).Forget();
                    else
                        usageTracker.IncrementCounter(x => x.NumberOfLocalPullRequestPushes).Forget();
                });
        }

        async Task DoSyncSubmodules(object unused)
        {
            try
            {
                IsBusy = true;
                usageTracker.IncrementCounter(x => x.NumberOfSyncSubmodules).Forget();

                var writer = new StringWriter(CultureInfo.CurrentCulture);
                var complete = await pullRequestsService.SyncSubmodules(LocalRepository, line =>
                {
                    writer.WriteLine(line);
                    statusBarNotificationService.ShowMessage(line);
                });
                if (!complete)
                {
                    throw new ApplicationException(writer.ToString());
                }
            }
            finally
            {
                IsBusy = false;
                statusBarNotificationService.ShowMessage(string.Empty);
            }
        }

        void DoShowReview(object item)
        {
            // TODO
        }

        class CheckoutCommandState : IPullRequestCheckoutState
        {
            public CheckoutCommandState(string caption, string disabledMessage)
            {
                Caption = caption;
                IsEnabled = disabledMessage == null;
                ToolTip = disabledMessage ?? caption;
            }

            public string Caption { get; }
            public bool IsEnabled { get; }
            public string ToolTip { get; }
        }

        class UpdateCommandState : IPullRequestUpdateState
        {
            public UpdateCommandState(
                BranchTrackingDetails divergence,
                bool pullEnabled,
                bool pushEnabled,
                string pullToolTip,
                string pushToolTip,
                string syncSubmodulesToolTip,
                int submodulesToSync)
            {
                CommitsAhead = divergence.AheadBy ?? 0;
                CommitsBehind = divergence.BehindBy ?? 0;
                PushEnabled = pushEnabled;
                PullEnabled = pullEnabled;
                PullToolTip = pullToolTip;
                PushToolTip = pushToolTip;
                SyncSubmodulesToolTip = syncSubmodulesToolTip;
                SubmodulesToSync = submodulesToSync;
            }

            public int CommitsAhead { get; }
            public int CommitsBehind { get; }
            public bool UpToDate => CommitsAhead == 0 && CommitsBehind == 0 && !SyncSubmodulesEnabled;
            public bool PullEnabled { get; }
            public bool PushEnabled { get; }
            public bool SyncSubmodulesEnabled => SubmodulesToSync > 0;
            public string PullToolTip { get; }
            public string PushToolTip { get; }
            public string SyncSubmodulesToolTip { get; }
            public int SubmodulesToSync { get; }
        }
    }
}
