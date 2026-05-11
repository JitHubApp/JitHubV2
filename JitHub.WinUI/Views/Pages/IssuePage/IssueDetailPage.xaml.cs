using JitHub.WinUI.ViewModels.IssueViewModels;
using JitHub.Models.LegacyGitHub;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace JitHub.WinUI.Views.Pages.IssuePage
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class IssueDetailPage : Page
    {
        private static readonly Brush OpenStateBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x3E, 0x7B, 0x64));
        private static readonly Brush ClosedStateBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xA2, 0x4E, 0x3C));

        public RepoIssueDetailViewModel? ViewModel { get; set; }
        public IssueDetailPage()
        {
            this.InitializeComponent();
        }

        override protected void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is RepoIssueDetailViewModel viewModel)
            {
                ViewModel = viewModel;
                DataContext = viewModel;
                Bindings.Update();
                if (viewModel.LoadCommand.CanExecute(null))
                {
                    viewModel.LoadCommand.Execute(null);
                }
            }
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

        public Brush GetStateBackgroundBrush(object? stateValue)
        {
            var state = stateValue switch
            {
                StringEnum<ItemState> stringEnum => stringEnum.Value,
                ItemState itemState => itemState,
                _ => ItemState.Open
            };

            return GetAppBrush(state == ItemState.Open ? "AppSuccessBrush" : "AppDangerBrush")
                ?? (state == ItemState.Open ? OpenStateBrush : ClosedStateBrush);
        }

        private static Brush? GetAppBrush(string resourceKey)
        {
            return Application.Current.Resources.TryGetValue(resourceKey, out object value) && value is Brush brush
                ? brush
                : null;
        }
    }
}

