using JitHub.Models;
using JitHub.Models.LegacyGitHub;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Helpers
{
    public partial class RepoIconTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? PublicTemplate { get; set; }
        public DataTemplate? PrivateTemplate { get; set; }
        public DataTemplate? ForkTemplate { get; set; }

        protected override DataTemplate? SelectTemplateCore(object item)
        {
            Repository? repo = item switch
            {
                RepoModel model => model.Repository,
                SelectableRepoModel selectableRepo => selectableRepo.Repo.Repository,
                Repository repository => repository,
                _ => null
            };

            if (repo is null)
            {
                return PublicTemplate ?? PrivateTemplate ?? ForkTemplate;
            }

            if (repo.Fork)
            {
                return ForkTemplate;
            }
            else if (repo.Private)
            {
                return PrivateTemplate;
            }
            else
            {
                return PublicTemplate;
            }
        }
    }
}

