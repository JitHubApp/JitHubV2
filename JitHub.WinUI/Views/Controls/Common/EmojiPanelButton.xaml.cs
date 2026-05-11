using JitHub.WinUI.ViewModels.EmojiViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.Common
{
    public sealed partial class EmojiPanelButton : UserControl
    {
        public static DependencyProperty ViewModelProperty = DependencyProperty.Register(
            nameof(ViewModel),
            typeof(EmojiPanelViewModel),
            typeof(EmojiPanelButton),
            new PropertyMetadata(default(EmojiPanelViewModel), OnViewModelChange));

        private static void OnViewModelChange(DependencyObject d, DependencyPropertyChangedEventArgs args)
        {
            if (d is EmojiPanelButton self && args.NewValue != null)
            {
                self.DataContext = self.ViewModel;
                self.Bindings.Update();
            }
        }

        public EmojiPanelViewModel ViewModel
        {
            get => (EmojiPanelViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }

        public EmojiPanelButton()
        {
            this.InitializeComponent();
        }
    }
}

