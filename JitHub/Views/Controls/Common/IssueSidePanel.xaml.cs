using JitHub.ViewModels.IssueViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Common
{
    public sealed partial class IssueSidePanel : UserControl
    {
        public static DependencyProperty ViewModelProperty = DependencyProperty.Register(
            nameof(ViewModel),
            typeof(IssueSideBarViewModel),
            typeof(IssueSidePanel),
            new PropertyMetadata(default(IssueSideBarViewModel), OnViewModelChange)
        );

        private static void OnViewModelChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is IssueSidePanel self && e.NewValue != null)
            {
                var viewModel = e.NewValue as IssueSideBarViewModel;
                self.DataContext = viewModel;
            }
        }

        public IssueSideBarViewModel ViewModel
        {
            get => (IssueSideBarViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }

        public IssueSidePanel()
        {
            this.InitializeComponent();
        }
    }
}
