using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using System.Collections.Generic;
using GitHub.Commands;
using GitHub.Extensions;
using GitHub.Models;
using GitHub.VisualStudio;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.TextManager.Interop;
using Task = System.Threading.Tasks.Task;

namespace GitHub.Services
{
    /// <summary>
    /// Services for opening views of pull request files in Visual Studio.
    /// </summary>
    [Export(typeof(IPullRequestEditorService))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class PullRequestEditorService : IPullRequestEditorService
    {
        readonly IGitHubServiceProvider serviceProvider;
        readonly IPullRequestService pullRequestService;
        readonly IVsEditorAdaptersFactoryService vsEditorAdaptersFactory;
        readonly IStatusBarNotificationService statusBar;
        readonly IUsageTracker usageTracker;
        readonly INavigationService navigationService;

        // If the target line doesn't have a unique match, search this number of lines above looking for a match.
        public const int MatchLinesAboveTarget = 4;

        [ImportingConstructor]
        public PullRequestEditorService(
            IGitHubServiceProvider serviceProvider,
            IPullRequestService pullRequestService,
            IVsEditorAdaptersFactoryService vsEditorAdaptersFactory,
            IStatusBarNotificationService statusBar,
            IUsageTracker usageTracker,
            INavigationService navigationService)
        {
            Guard.ArgumentNotNull(serviceProvider, nameof(serviceProvider));
            Guard.ArgumentNotNull(pullRequestService, nameof(pullRequestService));
            Guard.ArgumentNotNull(vsEditorAdaptersFactory, nameof(vsEditorAdaptersFactory));
            Guard.ArgumentNotNull(statusBar, nameof(statusBar));
            Guard.ArgumentNotNull(usageTracker, nameof(usageTracker));
            Guard.ArgumentNotNull(navigationService, nameof(navigationService));

            this.serviceProvider = serviceProvider;
            this.pullRequestService = pullRequestService;
            this.vsEditorAdaptersFactory = vsEditorAdaptersFactory;
            this.statusBar = statusBar;
            this.usageTracker = usageTracker;
            this.navigationService = navigationService;
        }

        /// <inheritdoc/>
        public async Task OpenFile(
            IPullRequestSession session,
            string relativePath,
            bool workingDirectory)
        {
            Guard.ArgumentNotNull(session, nameof(session));
            Guard.ArgumentNotEmptyString(relativePath, nameof(relativePath));

            try
            {
                var file = await session.GetFile(relativePath);
                var fullPath = GetAbsolutePath(session, file);
                var fileName = workingDirectory ? fullPath : await ExtractFile(session, file, true);

                using (workingDirectory ? null : OpenInProvisionalTab())
                {
                    var window = VisualStudio.Services.Dte.ItemOperations.OpenFile(fileName);
                    window.Document.ReadOnly = !workingDirectory;

                    var buffer = GetBufferAt(fileName);

                    if (!workingDirectory)
                    {
                        AddBufferTag(buffer, session, fullPath, null);

                        var textView = FindActiveView();
                        EnableNavigateToEditor(textView, session, file);
                    }
                }

                if (workingDirectory)
                    await usageTracker.IncrementCounter(x => x.NumberOfPRDetailsOpenFileInSolution);
                else
                    await usageTracker.IncrementCounter(x => x.NumberOfPRDetailsViewFile);
            }
            catch (Exception e)
            {
                ShowErrorInStatusBar("Error opening file", e);
            }
        }

        /// <inheritdoc/>
        public async Task OpenDiff(
            IPullRequestSession session,
            string relativePath,
            bool workingDirectory)
        {
            Guard.ArgumentNotNull(session, nameof(session));
            Guard.ArgumentNotEmptyString(relativePath, nameof(relativePath));

            try
            {
                var file = await session.GetFile(relativePath);
                var rightPath = file.RelativePath;
                var leftPath = await GetBaseFileName(session, file);
                var rightFile = workingDirectory ? GetAbsolutePath(session, file) : await ExtractFile(session, file, true);
                var leftFile = await ExtractFile(session, file, false);
                var leftLabel = $"{leftPath};{session.GetBaseBranchDisplay()}";
                var rightLabel = workingDirectory ? rightPath : $"{rightPath};PR {session.PullRequest.Number}";
                var caption = $"Diff - {Path.GetFileName(file.RelativePath)}";
                var options = __VSDIFFSERVICEOPTIONS.VSDIFFOPT_DetectBinaryFiles |
                    __VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary;

                if (!workingDirectory)
                {
                    options |= __VSDIFFSERVICEOPTIONS.VSDIFFOPT_RightFileIsTemporary;
                }

                IVsWindowFrame frame;
                using (OpenInProvisionalTab())
                {
                    var tooltip = $"{leftLabel}\nvs.\n{rightLabel}";

                    // Diff window will open in provisional (right hand) tab until document is touched.
                    frame = VisualStudio.Services.DifferenceService.OpenComparisonWindow2(
                        leftFile,
                        rightFile,
                        caption,
                        tooltip,
                        leftLabel,
                        rightLabel,
                        string.Empty,
                        string.Empty,
                        (uint)options);
                }

                object docView;
                frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out docView);
                var diffViewer = ((IVsDifferenceCodeWindow)docView).DifferenceViewer;

                AddBufferTag(diffViewer.LeftView.TextBuffer, session, leftPath, DiffSide.Left);

                if (!workingDirectory)
                {
                    AddBufferTag(diffViewer.RightView.TextBuffer, session, rightPath, DiffSide.Right);

                    EnableNavigateToEditor(diffViewer.LeftView, session, file);
                    EnableNavigateToEditor(diffViewer.RightView, session, file);
                    EnableNavigateToEditor(diffViewer.InlineView, session, file);
                }

                if (workingDirectory)
                    await usageTracker.IncrementCounter(x => x.NumberOfPRDetailsCompareWithSolution);
                else
                    await usageTracker.IncrementCounter(x => x.NumberOfPRDetailsViewChanges);
            }
            catch (Exception e)
            {
                ShowErrorInStatusBar("Error opening file", e);
            }
        }

        /// <inheritdoc/>
        public async Task OpenDiff(
            IPullRequestSession session,
            string relativePath,
            IInlineCommentThreadModel thread)
        {
            Guard.ArgumentNotNull(session, nameof(session));
            Guard.ArgumentNotEmptyString(relativePath, nameof(relativePath));
            Guard.ArgumentNotNull(thread, nameof(thread));

            await OpenDiff(session, relativePath, false);

            // HACK: We need to wait here for the diff view to set itself up and move its cursor
            // to the first changed line. There must be a better way of doing this.
            await Task.Delay(1500);

            var param = (object)new InlineCommentNavigationParams
            {
                FromLine = thread.LineNumber - 1,
            };

            VisualStudio.Services.Dte.Commands.Raise(
                Guids.CommandSetString,
                PkgCmdIDList.NextInlineCommentId,
                ref param,
                null);
        }

        void EnableNavigateToEditor(IWpfTextView textView, IPullRequestSession session, IPullRequestSessionFile file)
        {
            var view = vsEditorAdaptersFactory.GetViewAdapter(textView);
            EnableNavigateToEditor(view, session, file);
        }

        void EnableNavigateToEditor(IVsTextView textView, IPullRequestSession session, IPullRequestSessionFile file)
        {
            var commandGroup = VSConstants.CMDSETID.StandardCommandSet2K_guid;
            var commandId = (int)VSConstants.VSStd2KCmdID.RETURN;
            new TextViewCommandDispatcher(textView, commandGroup, commandId).Exec += async (s, e) => await NavigateToEditor(session, file);

            var contextMenuCommandGroup = new Guid(Guids.guidContextMenuSetString);
            var goToCommandId = PkgCmdIDList.openFileInSolutionCommand;
            new TextViewCommandDispatcher(textView, contextMenuCommandGroup, goToCommandId).Exec += async (s, e) => await NavigateToEditor(session, file);
        }

        async Task NavigateToEditor(IPullRequestSession session, IPullRequestSessionFile file)
        {
            try
            {
                if (!session.IsCheckedOut)
                {
                    ShowInfoMessage("Checkout PR branch before opening file in solution.");
                    return;
                }

                var fullPath = GetAbsolutePath(session, file);

                var activeView = FindActiveView();
                if (activeView == null)
                {
                    statusBar.ShowError("Couldn't find active view");
                    return;
                }

                NavigateToEquivalentPosition(activeView, fullPath);

                await usageTracker.IncrementCounter(x => x.NumberOfPRDetailsNavigateToEditor);
            }
            catch (Exception e)
            {
                ShowErrorInStatusBar("Error navigating to editor", e);
            }
        }

        void ShowInfoMessage(string message)
        {
            ErrorHandler.ThrowOnFailure(VsShellUtilities.ShowMessageBox(
                serviceProvider, message, null,
                OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST));
        }

        IVsTextView NavigateToEquivalentPosition(IVsTextView sourceView, string targetFile)
        {
            int line;
            int column;
            ErrorHandler.ThrowOnFailure(sourceView.GetCaretPos(out line, out column));
            var text1 = GetText(sourceView);

            var view = OpenDocument(targetFile);
            var text2 = VsShellUtilities.GetRunningDocumentContents(serviceProvider, targetFile);

            var fromLines = ReadLines(text1);
            var toLines = ReadLines(text2);
            var matchingLine = navigationService.FindMatchingLine(fromLines, toLines, line, matchLinesAbove: MatchLinesAboveTarget);
            if (matchingLine == -1)
            {
                // If we can't match line use orignal as best guess.
                matchingLine = line < toLines.Count ? line : toLines.Count - 1;
                column = 0;
            }

            ErrorHandler.ThrowOnFailure(view.SetCaretPos(matchingLine, column));
            ErrorHandler.ThrowOnFailure(view.CenterLines(matchingLine, 1));

            return view;
        }

        IVsTextView FindActiveView()
        {
            var textManager = serviceProvider.GetService<SVsTextManager, IVsTextManager2>();
            IVsTextView view;
            var hresult = textManager.GetActiveView2(1, null, (uint)_VIEWFRAMETYPE.vftCodeWindow, out view);
            return hresult == VSConstants.S_OK ? view : null;
        }

        string GetText(IVsTextView textView)
        {
            IVsTextLines buffer;
            ErrorHandler.ThrowOnFailure(textView.GetBuffer(out buffer));

            int line;
            int index;
            ErrorHandler.ThrowOnFailure(buffer.GetLastLineIndex(out line, out index));

            string text;
            ErrorHandler.ThrowOnFailure(buffer.GetLineText(0, 0, line, index, out text));
            return text;
        }

        IVsTextView OpenDocument(string fullPath)
        {
            var logicalView = VSConstants.LOGVIEWID.TextView_guid;
            IVsUIHierarchy hierarchy;
            uint itemID;
            IVsWindowFrame windowFrame;
            IVsTextView view;
            VsShellUtilities.OpenDocument(serviceProvider, fullPath, logicalView, out hierarchy, out itemID, out windowFrame, out view);
            return view;
        }

        static IList<string> ReadLines(string text)
        {
            var lines = new List<string>();
            var reader = new DiffUtilities.LineReader(text);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                lines.Add(line);
            }

            return lines;
        }

        void AddBufferTag(ITextBuffer buffer, IPullRequestSession session, string path, DiffSide? side)
        {
            buffer.Properties.GetOrCreateSingletonProperty(
                typeof(PullRequestTextBufferInfo),
                () => new PullRequestTextBufferInfo(session, path, side));

            var projection = buffer as IProjectionBuffer;

            if (projection != null)
            {
                foreach (var source in projection.SourceBuffers)
                {
                    AddBufferTag(source, session, path, side);
                }
            }
        }

        async Task<string> ExtractFile(IPullRequestSession session, IPullRequestSessionFile file, bool head)
        {
            var encoding = pullRequestService.GetEncoding(session.LocalRepository, file.RelativePath);
            var relativePath = head ? file.RelativePath : await GetBaseFileName(session, file);

            return await pullRequestService.ExtractFile(
                session.LocalRepository,
                session.PullRequest,
                relativePath,
                head,
                encoding).ToTask();
        }

        ITextBuffer GetBufferAt(string filePath)
        {
            IVsUIHierarchy uiHierarchy;
            uint itemID;
            IVsWindowFrame windowFrame;

            if (VsShellUtilities.IsDocumentOpen(
                    serviceProvider,
                    filePath,
                    Guid.Empty,
                    out uiHierarchy,
                    out itemID,
                    out windowFrame))
            {
                IVsTextView view = VsShellUtilities.GetTextView(windowFrame);
                IVsTextLines lines;
                if (view.GetBuffer(out lines) == 0)
                {
                    var buffer = lines as IVsTextBuffer;
                    if (buffer != null)
                        return vsEditorAdaptersFactory.GetDataBuffer(buffer);
                }
            }

            return null;
        }

        async Task<string> GetBaseFileName(IPullRequestSession session, IPullRequestSessionFile file)
        {
            using (var changes = await pullRequestService.GetTreeChanges(
                session.LocalRepository,
                session.PullRequest))
            {
                var fileChange = changes.FirstOrDefault(x => x.Path == file.RelativePath);
                return fileChange?.Status == LibGit2Sharp.ChangeKind.Renamed ?
                    fileChange.OldPath : file.RelativePath;
            }
        }

        static string GetAbsolutePath(IPullRequestSession session, IPullRequestSessionFile file)
        {
            return Path.Combine(session.LocalRepository.LocalPath, file.RelativePath);
        }

        static IDisposable OpenInProvisionalTab()
        {
            return new NewDocumentStateScope(
                __VSNEWDOCUMENTSTATE.NDS_Provisional,
                VSConstants.NewDocumentStateReason.SolutionExplorer);
        }

        void ShowErrorInStatusBar(string message, Exception e)
        {
            statusBar.ShowMessage(message + ": " + e.Message);
        }
    }
}
