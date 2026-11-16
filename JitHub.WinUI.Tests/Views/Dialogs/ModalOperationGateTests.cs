using System.Collections.Concurrent;
using System.Windows.Input;
using Xunit;

namespace JitHub.WinUI.Tests.Views.Dialogs;

public sealed class ModalOperationGateTests
{
    [Fact]
    public void ShellDismissal_RoutesThroughModalServiceGate()
    {
        string shellPage = File.ReadAllText(FindRepositoryFile(
            "JitHub.WinUI",
            "Views",
            "Pages",
            "ShellPage.xaml.cs"));
        string shellViewModel = File.ReadAllText(FindRepositoryFile(
            "JitHub.WinUI",
            "ViewModels",
            "Shell",
            "ShellPageViewModel.cs"));

        Assert.Contains("ViewModel.RequestCloseModal();", shellPage, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModel.CloseModalWithControl();", shellPage, StringComparison.Ordinal);
        Assert.Contains("public void RequestCloseModal()", shellViewModel, StringComparison.Ordinal);
        Assert.Contains("_modalService.Close();", shellViewModel, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine([current.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(segments)}.");
    }
}
