using JitHub.ViewModels.PullRequestViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.PullRequest
{
    public sealed partial class RepoPullRequestDetail : UserControl
    {
        public static DependencyProperty ViewModelProperty = DependencyProperty.Register(
            "ViewModel",
            typeof(RepoPullRequestDetailViewModel),
            typeof(RepoPullRequestDetail),
            new PropertyMetadata(default(RepoPullRequestDetailViewModel), OnViewModelChanged));

        private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is RepoPullRequestDetail self && e.NewValue != null)
            {
                self.DataContext = self.ViewModel;
                self.ViewModel.Frame = self.RepoPullRequestDetailFrame;
                self.ViewModel.GoToConversationPage();
                //loading things
            }
        }

        public RepoPullRequestDetailViewModel ViewModel
        {
            get => (RepoPullRequestDetailViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }

        public RepoPullRequestDetail()
        {
            this.InitializeComponent();
        }
    }
}
