using Windows.UI.Xaml.Controls;

namespace JitHub.Views.Controls.Widgets;

public sealed partial class ActivityList : UserControl
{
    private string _id;

    public ActivityList(string id)
    {
        this.InitializeComponent();
        _id = id;
    }
}
