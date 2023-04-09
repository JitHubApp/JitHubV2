using JitHub.ViewModels.ActivityViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Activity
{
    public sealed partial class CommitCommentActivity : UserControl
    {
        public static DependencyProperty ViewModelProperty = DependencyProperty.Register(
            nameof(ViewModel),
            typeof(CommitCommentActivityViewModel),
            typeof(CommitCommentActivity),
            new PropertyMetadata(default(CommitCommentActivityViewModel), null)
        );

        public static void OnViewModelChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CommitCommentActivity self && e.NewValue != null)
            {
                self.ViewModel = e.NewValue as CommitCommentActivityViewModel;
                self.DataContext = self.ViewModel;
            }
        }

        public CommitCommentActivityViewModel ViewModel
        {
            get => (CommitCommentActivityViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }
        public CommitCommentActivity()
        {
            this.InitializeComponent();
        }
    }
}
