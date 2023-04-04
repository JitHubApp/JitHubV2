using JitHub.ViewModels.ActivityViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Activity
{
    public sealed partial class MemberActivity : UserControl
    {
        public static DependencyProperty ViewModelProperty = DependencyProperty.Register(
            nameof(ViewModel),
            typeof(MemberActivityViewModel),
            typeof(MemberActivity),
            new PropertyMetadata(default(MemberActivityViewModel), null)
        );

        public static void OnViewModelChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MemberActivity self && e.NewValue != null)
            {
                self.DataContext = self.ViewModel;
            }
        }

        public MemberActivityViewModel ViewModel
        {
            get => (MemberActivityViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }
        public MemberActivity()
        {
            this.InitializeComponent();
        }
    }
}
