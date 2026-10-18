using System;
using System.IO;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Sample-shell wiring checks. Actual theme publication and restoration remain
/// live UI tests; a source check cannot prove the app's rendered appearance.
/// </summary>
public sealed class MarkdownSampleAppearanceContractTests
{
    [Fact]
    public void ThemeToggleReflectsStartupThemeBeforeHandlersCanMutateTheShell()
    {
        string source = ReadSampleWindowSource();
        int created = source.IndexOf("var themeToggle = new AppBarToggleButton", StringComparison.Ordinal);
        Assert.True(created >= 0);
        int initialized = source.IndexOf(
            "IsChecked = Application.Current.RequestedTheme == ApplicationTheme.Dark", created, StringComparison.Ordinal);
        int checkedHandler = source.IndexOf("themeToggle.Checked", created, StringComparison.Ordinal);
        int uncheckedHandler = source.IndexOf("themeToggle.Unchecked", created, StringComparison.Ordinal);

        Assert.True(initialized > created);
        Assert.True(checkedHandler > initialized && uncheckedHandler > initialized);
        Assert.Contains("themeToggle.Checked   += (_, _) => SetTheme(ElementTheme.Dark)", source, StringComparison.Ordinal);
        Assert.Contains("themeToggle.Unchecked += (_, _) => SetTheme(ElementTheme.Light)", source, StringComparison.Ordinal);
    }

    private static string ReadSampleWindowSource()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName,
                "MarkdownRenderer", "MarkdownRenderer.Sample", "MainWindow.xaml.cs");
            if (File.Exists(path))
                return File.ReadAllText(path);
        }

        throw new FileNotFoundException("Could not locate the sample window source.");
    }
}
