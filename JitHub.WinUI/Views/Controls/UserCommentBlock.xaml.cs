using JitHub.Models;
using JitHub.WinUI.ViewModels.UserViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls
{
    public sealed partial class UserCommentBlock : UserControl
    {
        private UserCommentBlockViewModel? _loadedViewModel;

        public static DependencyProperty SizeProperty = DependencyProperty.Register(
            "Size",
            typeof(UISize),
            typeof(UserCommentBlock),
            new PropertyMetadata(UISize.BIG, OnSizeChanged));

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
                if (!ReferenceEquals(self._loadedViewModel, self.ViewModel))
                {
                    self._loadedViewModel = null;
                }

                self.Bindings.Update();
            }
        }

        private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is UserCommentBlock self)
            {
                self.Bindings.Update();
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

        private void UserCommentBlock_Loaded(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null || ReferenceEquals(_loadedViewModel, ViewModel))
            {
                return;
            }

            if (ViewModel?.LoadCommand?.CanExecute(null) == true)
            {
                _loadedViewModel = ViewModel;
                ViewModel.LoadCommand.Execute(null);
            }
        }
    }
}


