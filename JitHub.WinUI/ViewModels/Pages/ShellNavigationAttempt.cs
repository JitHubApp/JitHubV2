using System;
using JitHub.Services;

namespace JitHub.WinUI.ViewModels.Pages;

internal readonly record struct ShellNavigationAttempt(bool Accepted, string Result)
{
    public static ShellNavigationAttempt EvaluateModal(
        bool isShellModalOpen,
        bool isNativeDialogOpen,
        Func<bool> tryCloseShellModal)
    {
        if (isNativeDialogOpen)
        {
            return new ShellNavigationAttempt(false, TelemetryTaxonomy.Results.Rejected);
        }

        return !isShellModalOpen || tryCloseShellModal()
            ? new ShellNavigationAttempt(true, TelemetryTaxonomy.Results.Success)
            : new ShellNavigationAttempt(false, TelemetryTaxonomy.Results.Rejected);
    }

    public static ShellNavigationAttempt Navigate(Func<bool> navigate)
    {
        try
        {
            bool accepted = navigate();
            return new ShellNavigationAttempt(
                accepted,
                TelemetryTaxonomy.NavigationResult(accepted));
        }
        catch
        {
            return new ShellNavigationAttempt(false, TelemetryTaxonomy.Results.Error);
        }
    }
}
