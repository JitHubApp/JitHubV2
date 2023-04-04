using JitHub.Models;
using JitHub.Services;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Common
{
    public sealed partial class CreditPersonaleButton : UserControl
    {
        public static DependencyProperty PersonProperty = DependencyProperty.Register(
            nameof(Person),
            typeof(CreditPersonale),
            typeof(CreditPersonaleButton),
            new PropertyMetadata(default(Color), null)
        );

        private ModalService _modalService;

        public CreditPersonale Person
        {
            get => (CreditPersonale)GetValue(PersonProperty);
            set => SetValue(PersonProperty, value);
        }
        public CreditPersonaleButton()
        {
            this.InitializeComponent();
            _modalService = Ioc.Default.GetService<ModalService>();
        }

        private void OnClose()
        {
            _modalService.Close();
        }

        public void OnClick()
        {
            _modalService.Open(new CreditPersonaleDialog(Person, new RelayCommand(OnClose)));
        }
    }
}
