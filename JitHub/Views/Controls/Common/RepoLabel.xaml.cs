using Octokit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Common
{
    public sealed partial class RepoLabel : UserControl
    {
        public static DependencyProperty LabelProperty = DependencyProperty.Register(
            nameof(Label),
            typeof(Label),
            typeof(RepoLabel),
            new PropertyMetadata(default(Label), null));
        
        public Label Label
        {
            get =>  (Label)GetValue(LabelProperty);
            set
            {
                SetValue(LabelProperty, value);
            }
        }

        public RepoLabel()
        {
            this.InitializeComponent();
        }
    }
}
