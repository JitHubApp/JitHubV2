using JitHub.WinUI.ViewModels.EmojiViewModels;
using JitHub.WinUI.Helpers;
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

        public static readonly DependencyProperty AutomationInstanceIdProperty = DependencyProperty.Register(
            nameof(AutomationInstanceId),
            typeof(string),
            typeof(EmojiPanelButton),
            new PropertyMetadata(string.Empty, OnAutomationInstanceIdChanged));

        private static void OnViewModelChange(DependencyObject d, DependencyPropertyChangedEventArgs args)
        {
            if (d is EmojiPanelButton self && args.NewValue != null)
            {
                self.DataContext = self.ViewModel;
                self.Bindings.Update();
            }
        }

        private static void OnAutomationInstanceIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs args)
        {
            if (d is EmojiPanelButton self)
            {
                self.Bindings.Update();
            }
        }

        public EmojiPanelViewModel ViewModel
        {
            get => (EmojiPanelViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }

        public string AutomationInstanceId
        {
            get => (string)GetValue(AutomationInstanceIdProperty);
            set => SetValue(AutomationInstanceIdProperty, value);
        }

        public string GetLauncherAutomationId(string automationInstanceId) =>
            AutomationIdentity.CreateScopedId("EmojiPanelLauncherButton", automationInstanceId);

        public EmojiPanelButton()
        {
            this.InitializeComponent();
        }
    }
}

