using JitHub.ViewModels.Base;
using NavigationView = Microsoft.UI.Xaml.Controls.NavigationView;
using NavigationViewItemInvokedEventArgs = Microsoft.UI.Xaml.Controls.NavigationViewItemInvokedEventArgs;
using System.Windows.Input;
using JitHub.Models.CommandArgs;
using Octokit;

namespace JitHub.ViewModels.IssueViewModels
{
    public class RepoIssuePostingViewModel : RepoViewModel
    {
        private string _title;
        private string _text;
        private Issue _issue;
        private long _repoId;
        private string _selectedBodyView = "Write";

        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        public string Text
        {
            get => _text;
            set => SetProperty(ref _text, value);
        }

        public Issue Issue
        {
            get => _issue;
            set => SetProperty(ref _issue, value);
        }
        
        public long RepoId
        {
            get => _repoId;
            set => SetProperty(ref _repoId, value);
        }

        public string SelectedBodyView
        {
            get => _selectedBodyView;
            set => SetProperty(ref _selectedBodyView, value);
        }

        public ICommand SubmitCommand { get; set; }

        public void OnNavChange(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            SelectedBodyView = (string)args.InvokedItem;
        }

        public void OnSubmit()
        {
            var issueFormArgs = new IssueFormArgs()
            {
                Issue = _issue,
                Title = _title,
                Body = _text,
                RepoId = _repoId,
            };
            SubmitCommand.Execute(issueFormArgs);
        }
    }
}
