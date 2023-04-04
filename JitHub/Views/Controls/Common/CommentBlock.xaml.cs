using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Common
{
    public sealed partial class CommentBlock : UserControl
    {
        public static DependencyProperty HeaderContentProperty = DependencyProperty.Register(
            nameof(HeaderContent),
            typeof(FrameworkElement),
            typeof(CommentBlock),
            new PropertyMetadata(default(FrameworkElement), null)
            );

        public static DependencyProperty BodyContentProperty = DependencyProperty.Register(
            nameof(BodyContent),
            typeof(FrameworkElement),
            typeof(CommentBlock),
            new PropertyMetadata(default(FrameworkElement), null)
            );

        public FrameworkElement HeaderContent
        {
            get => (FrameworkElement)GetValue(HeaderContentProperty);
            set
            {
                SetValue(HeaderContentProperty, value);
            }
        }

        public FrameworkElement BodyContent
        {
            get => (FrameworkElement)GetValue(BodyContentProperty);
            set
            {
                SetValue(BodyContentProperty, value);
            }
        }

        public CommentBlock()
        {
            this.InitializeComponent();
        }
    }
}
