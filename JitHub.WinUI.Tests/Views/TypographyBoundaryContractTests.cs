using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class TypographyBoundaryContractTests
{
    private const string UiFont = "{ThemeResource AppUiFontFamily}";
    private const string BodyFont = "{ThemeResource AppBodyFontFamily}";
    private const string MonoFont = "{ThemeResource AppMonoFontFamily}";

    [Fact]
    public void SemanticNavigationAndStatusCopy_UsesUiTypography()
    {
        XDocument interactionPrimitives = LoadXaml(
            "JitHub.WinUI",
            "Styles",
            "Foundation",
            "WinUIResourceBridge.xaml");
        XDocument typographyTokens = LoadXaml(
            "JitHub.WinUI",
            "Styles",
            "Foundation",
            "Tokens.Typography.xaml");
        XDocument myPullRequests = LoadXaml(
            "JitHub.WinUI",
            "Views",
            "Pages",
            "MyPullRequestsPage.xaml");
        XDocument repoCommits = LoadXaml(
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoCommitsPage.xaml");

        XElement pivotHeaderFont = FindKeyedElement(interactionPrimitives, "PivotHeaderItemFontFamily");
        Assert.Equal(FindKeyedElement(typographyTokens, "AppUiFontFamily").Value, pivotHeaderFont.Value);

        AssertUiOrBodyFont(FindTextElement(myPullRequests, "{x:Bind State, Mode=OneWay}"));
        AssertUiOrBodyFont(FindTextElement(repoCommits, "{Binding Conclusion}"));
        Assert.All(
            FindTextElements(repoCommits, "{Binding SelectedCommitVerificationText}"),
            AssertUiOrBodyFont);
    }

    [Fact]
    public void DiffProseUsesBodyTypography_WhileDiffContentAndMetadataRemainMonospaced()
    {
        XDocument diffViewer = LoadXaml(
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Commit",
            "CommitDiffViewer.xaml");
        XDocument repoCommits = LoadXaml(
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoCommitsPage.xaml");

        Assert.Equal(BodyFont, FontFamily(FindNamedElement(diffViewer, "UnavailableTextBlock")));
        Assert.Equal(BodyFont, FontFamily(FindNamedElement(diffViewer, "SearchNoResultsTextBlock")));

        Assert.Equal(MonoFont, FontFamily(FindNamedElement(diffViewer, "FileHeaderTextBlock")));
        Assert.Equal(MonoFont, FontFamily(FindNamedElement(diffViewer, "DiffLineTextBlock")));
        Assert.Equal(MonoFont, FontFamily(FindNamedElement(diffViewer, "HunkTextBlock")));
        Assert.Equal(MonoFont, FontFamily(FindTextElement(repoCommits, "{Binding SelectedCommitStatsText}")));
        Assert.All(
            repoCommits.Descendants().Where(element =>
                string.Equals((string?)element.Attribute("Text"), "{Binding ShortSha}", StringComparison.Ordinal)),
            element => Assert.Equal(MonoFont, FontFamily(element)));
    }

    private static void AssertUiOrBodyFont(XElement element)
    {
        string? fontFamily = FontFamily(element);
        if (fontFamily is not null)
        {
            Assert.Contains(fontFamily, new[] { UiFont, BodyFont });
            return;
        }

        Assert.Equal(
            "{StaticResource SectionSecondaryTextBlockStyle}",
            (string?)element.Attribute("Style"));
    }

    private static string? FontFamily(XElement element) =>
        (string?)element.Attribute("FontFamily");

    private static XElement FindKeyedElement(XDocument document, string key)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return document.Descendants().Single(element =>
            string.Equals((string?)element.Attribute(x + "Key"), key, StringComparison.Ordinal));
    }

    private static XElement FindNamedElement(XDocument document, string name)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return document.Descendants().Single(element =>
            string.Equals((string?)element.Attribute(x + "Name"), name, StringComparison.Ordinal));
    }

    private static XElement FindTextElement(XDocument document, string text) =>
        document.Descendants().Single(element =>
            string.Equals((string?)element.Attribute("Text"), text, StringComparison.Ordinal));

    private static XElement[] FindTextElements(XDocument document, string text) =>
        document.Descendants().Where(element =>
            string.Equals((string?)element.Attribute("Text"), text, StringComparison.Ordinal)).ToArray();

    private static XDocument LoadXaml(params string[] pathParts) =>
        XDocument.Load(Path.Combine([FindRepositoryRoot(), .. pathParts]));

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
