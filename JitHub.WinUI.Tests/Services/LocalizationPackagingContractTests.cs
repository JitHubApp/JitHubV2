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
    public void ProjectExcludesEveryNonProductCatalogFromDefaultPriPackaging()
    {
        string root = FindRepositoryRoot();
        string projectPath = Path.Combine(root, "JitHub.WinUI", "JitHub.WinUI.csproj");
        XDocument project = XDocument.Load(projectPath);
        Assert.Equal("en-US", project.Descendants("DefaultLanguage").Single().Value);
        Assert.Equal("false", project.Descendants("EnablePseudoLocalization").Single().Value);

        XElement[] removals = project.Descendants("PRIResource")
            .Where(item => !string.IsNullOrWhiteSpace((string?)item.Attribute("Remove")))
            .ToArray();
        string removePatterns = string.Join(';', removals.Select(item => (string)item.Attribute("Remove")!));
        string stringsRoot = Path.Combine(root, "JitHub.WinUI", "Strings");
        string[] nonProductCatalogs = Directory
            .EnumerateDirectories(stringsRoot)
            .Select(Path.GetFileName)
            .Where(static locale => !string.Equals(locale, "en-US", StringComparison.OrdinalIgnoreCase))
            .Cast<string>()
            .ToArray();

        Assert.NotEmpty(nonProductCatalogs);
        Assert.All(
            nonProductCatalogs,
            locale => Assert.Contains($"Strings\\{locale}\\**\\*", removePatterns, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Strings\\en-US", removePatterns, StringComparison.OrdinalIgnoreCase);

        XElement pseudoRemoval = Assert.Single(
            removals,
            item => ((string)item.Attribute("Remove")!)
                .Contains("Strings\\qps-ploc", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            "'$(EnablePseudoLocalization)' != 'true'",
            (string?)pseudoRemoval.Attribute("Condition"));
    }

    [Fact]
    public void ProjectInvalidatesPriOutputsWhenPseudoLocalizationModeChanges()
    {
        XDocument project = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "JitHub.WinUI.csproj"));

        Assert.Contains(
            "InvalidateProjectPriWhenPseudoLocalizationModeChanges",
            project.Descendants("GenerateProjectPriFileDependsOn").Single().Value,
            StringComparison.Ordinal);

        XElement target = project.Descendants("Target").Single(element =>
            string.Equals(
                (string?)element.Attribute("Name"),
                "InvalidateProjectPriWhenPseudoLocalizationModeChanges",
                StringComparison.Ordinal));
        Assert.NotNull(target.Descendants("ReadLinesFromFile").SingleOrDefault());
        Assert.Equal(
            "true",
            (string?)target.Descendants("WriteLinesToFile").Single().Attribute("WriteOnlyWhenDifferent"));

        string invalidatedFiles = (string?)target.Descendants("Delete").Single().Attribute("Files") ?? string.Empty;
        Assert.Contains("$(ProjectPriFullPath)", invalidatedFiles, StringComparison.Ordinal);
        Assert.Contains("$(_PriConfigXmlPath)", invalidatedFiles, StringComparison.Ordinal);
        Assert.Contains("$(_ResourcesResfilesPath)", invalidatedFiles, StringComparison.Ordinal);
        Assert.Contains("$(_PriResfilesPath)", invalidatedFiles, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
