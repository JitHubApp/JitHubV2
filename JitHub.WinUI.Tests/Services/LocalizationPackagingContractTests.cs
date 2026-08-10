using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class LocalizationPackagingContractTests
{
    private static readonly XNamespace AppxNamespace =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

    [Theory]
    [InlineData("Package.appxmanifest")]
    [InlineData("Package.Debug.appxmanifest")]
    public void ManifestAdvertisesOnlyCompleteEnglishCatalog(string manifestName)
    {
        XDocument manifest = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            manifestName));

        string[] languages = manifest
            .Descendants(AppxNamespace + "Resource")
            .Select(resource => (string?)resource.Attribute("Language"))
            .Where(static language => !string.IsNullOrWhiteSpace(language))
            .Cast<string>()
            .ToArray();

        Assert.Equal(["en-US"], languages);
    }

    [Fact]
    public void ProjectExcludesEveryIncompleteCatalogFromPriPackaging()
    {
        string root = FindRepositoryRoot();
        string projectPath = Path.Combine(root, "JitHub.WinUI", "JitHub.WinUI.csproj");
        XDocument project = XDocument.Load(projectPath);
        Assert.Equal("en-US", project.Descendants("DefaultLanguage").Single().Value);

        string removePatterns = string.Join(
            ';',
            project.Descendants("PRIResource")
                .Select(item => (string?)item.Attribute("Remove"))
                .Where(static value => !string.IsNullOrWhiteSpace(value)));
        string stringsRoot = Path.Combine(root, "JitHub.WinUI", "Strings");
        string[] incompleteCatalogs = Directory
            .EnumerateDirectories(stringsRoot)
            .Select(Path.GetFileName)
            .Where(static locale =>
                !string.Equals(locale, "en-US", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(locale, "qps-ploc", StringComparison.OrdinalIgnoreCase))
            .Cast<string>()
            .ToArray();

        Assert.NotEmpty(incompleteCatalogs);
        Assert.All(
            incompleteCatalogs,
            locale => Assert.Contains($"Strings\\{locale}\\**\\*", removePatterns, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Strings\\en-US", removePatterns, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Strings\\qps-ploc", removePatterns, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
