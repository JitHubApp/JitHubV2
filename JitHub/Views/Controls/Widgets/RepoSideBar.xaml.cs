using Windows.UI.Xaml.Controls;

namespace JitHub.Views.Controls.Widgets;

public sealed partial class RepoSideBar : UserControl
{
    private string _id;

    public RepoSideBar(string id)
    {
        this.InitializeComponent();
        _id = id;
    }
}
