using JitHub.ViewModels.ActivityViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Activity
{
    public sealed partial class IssueCommentActivity : UserControl
    {
        public static DependencyProperty ViewModelProperty = DependencyProperty.Register(
            nameof(ViewModel),
            typeof(IssueCommentActivityViewModel),
            typeof(IssueCommentActivity),
            new PropertyMetadata(default(IssueCommentActivityViewModel), null)
        );

        public static void OnViewModelChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is IssueCommentActivity self && e.NewValue != null)
            {
                self.DataContext = self.ViewModel;
            }
        }

        public IssueCommentActivityViewModel ViewModel
        {
            get => (IssueCommentActivityViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }
        public IssueCommentActivity()
        {
            this.InitializeComponent();
        }
    }
}
