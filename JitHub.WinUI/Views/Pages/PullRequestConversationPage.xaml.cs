using JitHub.Models.NavArgs;
using JitHub.WinUI.ViewModels.PullRequestViewModels;
using CommunityToolkit.Mvvm.Input;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;
using Page = Microsoft.UI.Xaml.Controls.Page;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace JitHub.WinUI.Views.Pages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class PullRequestConversationPage : Page
    {
        public PullRequestConversationViewModel? ViewModel { get; private set; }
        public PullRequestConversationPage()
        {
            this.InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            try
            {
                if (e.Parameter is PullRequestConvPageNavArg arg)
                {
                    ViewModel = new PullRequestConversationViewModel(arg.Repository, arg.PullRequest, arg.RefreshCommand, ScrollToBottom, new RelayCommand<UIElement?>(ScrollToElement));
                    DataContext = ViewModel;
                    await ViewModel.OnNavigatedTo();
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to open pull request conversation: {ex}");
            }

        }

        private void Page_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
        {
            try
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
            catch (System.InvalidOperationException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to update pull request conversation layout state: {ex}");
            }
        }

        private void ScrollToBottom()
        {
            CommentsPane.Measure(CommentsPane.RenderSize);
            CommentsPane.ChangeView(null, CommentsPane.ScrollableHeight, null);
        }

        private void ScrollToElement(UIElement? element)
        {
            if (element is null || CommentsPane.Content is not UIElement content)
            {
                return;
            }

            var transform = element.TransformToVisual(content);
            var position = transform.TransformPoint(new Point(0, 0));

            CommentsPane.ChangeView(null, position.Y, null, false);
        }
    }
}


