using JitHub.Models;
using Octokit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.Helpers
{
    public class RepoIconTemplateSelector : DataTemplateSelector
    {
        public DataTemplate PublicTemplate { get; set; }
        public DataTemplate PrivateTemplate { get; set; }
        public DataTemplate ForkTemplate { get; set; }
        protected override DataTemplate SelectTemplateCore(object item)
        {
            Repository repo;
            if (item is RepoModel model)
            {
                repo = model.Repository;
            }
            else if (item is SelectableRepoModel selectableRepo)
            {
                repo = selectableRepo.Repo.Repository;
            }
            else
            {
                repo = item as Repository;
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
