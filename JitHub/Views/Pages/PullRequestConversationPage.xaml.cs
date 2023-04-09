using JitHub.Models.NavArgs;
using JitHub.ViewModels.PullRequestViewModels;
using CommunityToolkit.Mvvm.Input;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;
using Page = Microsoft.UI.Xaml.Controls.Page;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace JitHub.Views.Pages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class PullRequestConversationPage : Page
    {
        public PullRequestConversationViewModel ViewModel { get; private set; }
        public PullRequestConversationPage()
        {
            this.InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            var arg = (PullRequestConvPageNavArg)e.Parameter;
            ViewModel = new PullRequestConversationViewModel(arg.Repository, arg.PullRequest, arg.RefreshCommand, ScrollToBottom, new RelayCommand<UIElement>(ScrollToElement));
            this.DataContext = ViewModel;
            await ViewModel.OnNavigatedTo();

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
            catch { }
        }

        private void ScrollToBottom()
        {
            CommentsPane.Measure(CommentsPane.RenderSize);
            CommentsPane.ChangeView(null, CommentsPane.ScrollableHeight, null);
        }

        private void ScrollToElement(UIElement element)
        {
            var transform = element.TransformToVisual((UIElement)CommentsPane.Content);
            var position = transform.TransformPoint(new Point(0, 0));

            CommentsPane.ChangeView(null, position.Y, null, false);
        }
    }
}
