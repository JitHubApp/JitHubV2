using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace JitHub.WinUI.Tests.Views.Pages;

public sealed class LoginPageViewModelTests
{
    [Fact]
    public void ActiveLoginPage_UsesCompiledBindingsAndStableAutomationIdentity()
    {
        string xamlPath = FindRepositoryFile("JitHub.WinUI", "Views", "Pages", "LoginPage.xaml");
        string xaml = File.ReadAllText(xamlPath);

        Assert.DoesNotContain("{Binding", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"LoginSignInButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"LoginErrorInfoBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("{x:Bind ViewModel.IsLoginEnabled, Mode=OneWay}", xaml, StringComparison.Ordinal);
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
