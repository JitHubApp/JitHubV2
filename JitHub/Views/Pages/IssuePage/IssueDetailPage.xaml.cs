using JitHub.ViewModels.IssueViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace JitHub.Views.Pages.IssuePage
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class IssueDetailPage : Page
    {
        public RepoIssueDetailViewModel ViewModel { get; set; }
        public IssueDetailPage()
        {
            this.InitializeComponent();
        }

        override protected void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel = e.Parameter as RepoIssueDetailViewModel;
            this.DataContext = ViewModel;
            ViewModel.LoadCommand.Execute(null);
        }

        private void Page_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
        {
            if (e.NewSize.Width > 768)
            {
                VisualStateManager.GoToState(this, "WideLayout", false);
            }
            else
            {
                VisualStateManager.GoToState(this, "NarrowLayout", false);
            }
        }
    }
}
