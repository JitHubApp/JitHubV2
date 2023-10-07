using JitHub.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Commit
{
    public sealed partial class CommitItem : UserControl
    {
        public static DependencyProperty ViewModelProperty = DependencyProperty.Register(
            nameof(ViewModel),
            typeof(CommandableCommit),
            typeof(CommitItem),
            new PropertyMetadata(default(CommandableCommit), OnViewModelChanged)
        );

        public static DependencyProperty ShowHoverMenuProperty = DependencyProperty.Register(
            nameof(ShowHoverMenu),
            typeof(bool),
            typeof(CommitItem),
            new PropertyMetadata(default(bool), null)
        );

        private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CommitItem self && e.NewValue != null)
            {
                self.DataContext = e.NewValue;
            }
        }

        public CommandableCommit ViewModel
        {
            get => (CommandableCommit)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }

        public bool ShowHoverMenu
        {
            get => (bool)GetValue(ShowHoverMenuProperty);
            set => SetValue(ShowHoverMenuProperty, value);
        }
        public CommitItem()
        {
            this.InitializeComponent();
        }

        private void UserControl_PointerEntered(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var cond = e.Pointer.PointerDeviceType == Windows.Devices.Input.PointerDeviceType.Mouse || e.Pointer.PointerDeviceType == Windows.Devices.Input.PointerDeviceType.Pen;
            var widthOk = ActualWidth >= 400;
            if (cond && ShowHoverMenu && widthOk)
            {
                VisualStateManager.GoToState(sender as Control, "HoverButtonsShown", true);
            }
        }

        private void UserControl_PointerExited(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var widthOk = ActualWidth >= 400;
            if (ShowHoverMenu && widthOk)
            {
                VisualStateManager.GoToState(sender as Control, "HoverButtonsHidden", true);
            }
        }
    }
}
