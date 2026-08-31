using JitHub.Services;
using JitHub.WinUI.ViewModels.Pages;
using Xunit;

namespace JitHub.WinUI.Tests.ViewModels;

public sealed class ShellNavigationAttemptTests
{
    [Fact]
    public void ModalRejection_PreventsNavigationAndReportsRejected()
    {
        bool navigationInvoked = false;

        ShellNavigationAttempt modal = ShellNavigationAttempt.EvaluateModal(
            isShellModalOpen: true,
            isNativeDialogOpen: false,
            tryCloseShellModal: static () => false);
        if (modal.Accepted)
        {
            navigationInvoked = ShellNavigationAttempt.Navigate(() => true).Accepted;
        }

        Assert.False(modal.Accepted);
        Assert.Equal(TelemetryTaxonomy.Results.Rejected, modal.Result);
        Assert.False(navigationInvoked);
    }

    [Fact]
    public void NativeDialog_PreventsNavigationWithoutTryingToCloseShellModal()
    {
        int closeAttempts = 0;

        ShellNavigationAttempt attempt = ShellNavigationAttempt.EvaluateModal(
            isShellModalOpen: false,
            isNativeDialogOpen: true,
            tryCloseShellModal: () =>
            {
                closeAttempts++;
                return true;
            });

        Assert.False(attempt.Accepted);
        Assert.Equal(TelemetryTaxonomy.Results.Rejected, attempt.Result);
        Assert.Equal(0, closeAttempts);
    }

    [Fact]
    public void ClosableShellModal_AllowsNavigationAfterOneCloseAttempt()
    {
        int closeAttempts = 0;

        ShellNavigationAttempt attempt = ShellNavigationAttempt.EvaluateModal(
            isShellModalOpen: true,
            isNativeDialogOpen: false,
            tryCloseShellModal: () =>
            {
                closeAttempts++;
                return true;
            });

        Assert.True(attempt.Accepted);
        Assert.Equal(TelemetryTaxonomy.Results.Success, attempt.Result);
        Assert.Equal(1, closeAttempts);
    }

    [Theory]
    [InlineData(true, true, "success")]
    [InlineData(false, false, "rejected")]
    public void FrameResult_PropagatesTruthfully(bool frameResult, bool accepted, string result)
    {
        ShellNavigationAttempt attempt = ShellNavigationAttempt.Navigate(() => frameResult);

        Assert.Equal(accepted, attempt.Accepted);
        Assert.Equal(result, attempt.Result);
    }

    [Fact]
    public void FrameException_ReportsErrorWithoutAcceptance()
    {
        Exception? reportedException = null;
        ShellNavigationAttempt attempt = ShellNavigationAttempt.Navigate(
            static () => throw new InvalidOperationException("navigation failed"),
            exception => reportedException = exception);

        Assert.False(attempt.Accepted);
        Assert.Equal(TelemetryTaxonomy.Results.Error, attempt.Result);
        Assert.IsType<InvalidOperationException>(reportedException);
        Assert.Equal("navigation failed", reportedException.Message);
    }
}
