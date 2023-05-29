using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;


namespace JitHub.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        this.InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Initialize(MenuContainer);
    }
}
