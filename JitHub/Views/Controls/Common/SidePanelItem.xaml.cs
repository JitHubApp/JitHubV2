using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Common
{
    public sealed partial class SidePanelItem : UserControl
    {
        public static DependencyProperty HeaderProperty = DependencyProperty.Register(
            nameof(Header),
            typeof(FrameworkElement),
            typeof(SidePanelItem),
            new PropertyMetadata(default(FrameworkElement), null)
        );

        public static DependencyProperty BodyProperty = DependencyProperty.Register(
            nameof(Body),
            typeof(FrameworkElement),
            typeof(SidePanelItem),
            new PropertyMetadata(default(FrameworkElement), null)
        );

        public FrameworkElement Header
        {
            get => (FrameworkElement)GetValue(HeaderProperty);
            set => SetValue(HeaderProperty, value);
        }

        public FrameworkElement Body
        {
            get => (FrameworkElement)GetValue(BodyProperty);
            set => SetValue(BodyProperty, value);
        }

        public SidePanelItem()
        {
            this.InitializeComponent();
        }
    }
}
