using JitHub.Services;
using JitHub.WinUI.ViewModels.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.App;

public sealed partial class DashboardWidgetCustomizeDialog : UserControl, IModalSessionAware, IModalContentLayout
{
    private ModalSession? _modalSession;

    public DashboardWidgetCustomizeDialog()
    {
        InitializeComponent();
    }

    public void AttachModalSession(ModalSession session) => _modalSession = session;

    public bool OwnsScrolling => true;

    public void SetModalViewport(double width, double height)
    {
        MaxWidth = width;
        Height = height;
        MaxHeight = height;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_modalSession is not { } session ||
            DataContext is not DashboardPageViewModel viewModel ||
            !viewModel.SaveCustomizeCommand.CanExecute(null) ||
            !session.TryBeginMutation())
        {
            return;
        }

        try
        {
            viewModel.SaveCustomizeCommand.Execute(null);
        }
        finally
        {
            session.EndMutation();
            if (!viewModel.IsCustomizeDialogOpen)
            {
                _ = session.TryClose();
            }
        }
    }
}
