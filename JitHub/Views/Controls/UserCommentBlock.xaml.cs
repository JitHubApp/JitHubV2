using JitHub.Models;
using JitHub.ViewModels.UserViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.Views.Controls
{
    public sealed partial class UserCommentBlock : UserControl
    {
        public static DependencyProperty SizeProperty = DependencyProperty.Register(
            "Size",
            typeof(UISize),
            typeof(UserCommentBlock),
            new PropertyMetadata(UISize.BIG, null));

        public static DependencyProperty ViewModelProperty = DependencyProperty.Register(
            nameof(ViewModel),
            typeof(UserCommentBlockViewModel),
            typeof(UserCommentBlock),
            new PropertyMetadata(default(UserCommentBlockViewModel), OnViewModelChange));

        private static void OnViewModelChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is UserCommentBlock self && e.NewValue != null)
            {
                self.DataContext = self.ViewModel;
            }
        }
        
        public UserCommentBlockViewModel ViewModel
        {
            get {  return (UserCommentBlockViewModel)GetValue(ViewModelProperty); }
            set {  SetValue(ViewModelProperty, value); }
        }

        public UISize Size
        {
            get => (UISize)GetValue(SizeProperty);
            set
            {
                SetValue(SizeProperty, value);
            }
        }

        public UserCommentBlock()
        {
            this.InitializeComponent();
        }
    }
}
