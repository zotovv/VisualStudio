using System.ComponentModel.Composition;
using GitHub.Models;

namespace GitHub.Services
{
    [Export(typeof(ILocalRepositoryModelFactory))]
    class LocalRepositoryModelFactory : ILocalRepositoryModelFactory
    {
        public ILocalRepositoryModel Create(string localPath)
        {
            return new LocalRepositoryModel(localPath);
        }
    }
}
