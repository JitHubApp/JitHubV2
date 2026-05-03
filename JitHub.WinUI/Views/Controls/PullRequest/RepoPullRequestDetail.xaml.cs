using JitHub.WinUI.ViewModels.PullRequestViewModels;
using JitHub.Models.LegacyGitHub;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.WinUI.Views.Controls.PullRequest
{
    public sealed partial class RepoPullRequestDetail : UserControl
    {
        private static readonly Brush OpenStateBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x3E, 0x7B, 0x64));
        private static readonly Brush ClosedStateBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xA2, 0x4E, 0x3C));

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

