using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ShellLocalizationContractTests
{
    private static readonly string[] NavigationKeys =
    [
        "Shell/Navigation/Home",
        "Shell/Navigation/Issues",
        "Shell/Navigation/PullRequests",
        "Shell/Navigation/Notifications",
        "Shell/Navigation/Stars",
        "Shell/Navigation/Gists",
        "Shell/Navigation/Search",
        "Shell/Navigation/Settings"
    ];

    [Fact]
    public void CodeGeneratedNavigationLabelsUseTheRuntimeResourcePath()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "JitHub.WinUI", "ViewModels", "Pages", "ShellPageViewModel.cs"));

        Assert.Contains("ShellNavigationText(\"Home\"", source, StringComparison.Ordinal);
        Assert.Contains("ShellNavigationText(\"Issues\"", source, StringComparison.Ordinal);
        Assert.Contains("LocalizedResourceText.GetString($\"Shell.Navigation.{key}\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new(\"issues\", \"Issues\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalAndPseudoCatalogsContainEveryNavigationLabel()
    {
        IReadOnlyDictionary<string, string> english = ReadCatalog("en-US");
        IReadOnlyDictionary<string, string> pseudo = ReadCatalog("qps-ploc");

        Assert.All(NavigationKeys, key =>
        {
            Assert.True(english.ContainsKey(key), $"English catalog is missing {key}.");
            Assert.True(pseudo.TryGetValue(key, out string? value), $"Pseudo catalog is missing {key}.");
            Assert.StartsWith("⟦", value, StringComparison.Ordinal);
        });
    }

    private static IReadOnlyDictionary<string, string> ReadCatalog(string language)
    {
        XDocument document = XDocument.Load(FindRepositoryFile(
            "JitHub.WinUI", "Strings", language, "Resources.resw"));
        return document.Root!
            .Elements("data")
            .ToDictionary(
                element => (string)element.Attribute("name")!,
                element => (string?)element.Element("value") ?? string.Empty,
                StringComparer.Ordinal);
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
