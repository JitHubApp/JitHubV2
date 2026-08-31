using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class InlineAlignmentContractTests
{
    private static readonly HashSet<string> IconElementNames = new(StringComparer.Ordinal)
    {
        "AnimatedIcon",
        "AppIcon",
        "Avatar",
        "BitmapIcon",
        "Ellipse",
        "FontIcon",
        "IconSourceElement",
        "Image",
        "ImageIcon",
        "InfoBadge",
        "Path",
        "PathIcon",
        "PersonPicture",
        "ProgressRing",
        "RepoLabel",
        "SymbolIcon",
        "Viewbox"
    };

    [Fact]
    public void IconAndTextPeers_AreExplicitlyCentered()
    {
        List<string> offenders = [];

        foreach ((string path, XDocument document) in LoadViewDocuments())
        {
            foreach (XElement stack in document.Descendants().Where(static element =>
                element.Name.LocalName == "StackPanel" &&
                string.Equals(element.Attribute("Orientation")?.Value, "Horizontal", StringComparison.Ordinal)))
            {
                AssertCenteredPeers(path, stack, stack.Elements(), offenders);
            }

            foreach (XElement grid in document.Descendants().Where(static element => element.Name.LocalName == "Grid"))
            {
                XElement[] children = grid.Elements()
                    .Where(static element => !element.Name.LocalName.StartsWith("Grid.", StringComparison.Ordinal))
                    .ToArray();
                foreach (IGrouping<string, XElement> row in children.GroupBy(static element =>
                    element.Attribute("Grid.Row")?.Value ?? "0", StringComparer.Ordinal))
                {
                    AssertCenteredPeers(path, grid, row, offenders);
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Icon/text peers must use VerticalAlignment=Center or an approved centered catalog style:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void MixedTypographyInlineText_UsesOneTextBlockUnlessItIsIconLikeContent()
    {
        List<string> offenders = [];

        foreach ((string path, XDocument document) in LoadViewDocuments())
        {
            foreach (XElement stack in document.Descendants().Where(static element =>
                element.Name.LocalName == "StackPanel" &&
                string.Equals(element.Attribute("Orientation")?.Value, "Horizontal", StringComparison.Ordinal)))
            {
                XElement[] textBlocks = stack.Elements()
                    .Where(static element => element.Name.LocalName == "TextBlock")
                    .ToArray();
                if (textBlocks.Length < 2 || HasIconPeer(stack))
                {
                    continue;
                }

                string[] typography = textBlocks.Select(GetTypographySignature).Distinct(StringComparer.Ordinal).ToArray();
                bool isReactionChip = path.EndsWith(
                    Path.Combine("Controls", "Common", "CommentInteractionBar.xaml"),
                    StringComparison.OrdinalIgnoreCase);
                if (typography.Length > 1 && !isReactionChip)
                {
                    offenders.Add(Describe(path, stack));
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Mixed-size inline text must share one TextBlock with Runs so it has one real baseline:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Theory]
    [InlineData("Pages", "NotificationsPage.xaml", "Notifications", "ResultCountText")]
    [InlineData("Pages", "RepoManagePage.xaml", "PageTitle", "ResultCountText")]
    [InlineData("Pages", "StarsPage.xaml", "CurrentViewTitle", "ResultCountText")]
    [InlineData("Pages", "GistsPage.xaml", "SelectedVisibilityText", "SelectedUpdatedText")]
    [InlineData("Pages", "ProfilePage.xaml", "StatusEmojiText", "StatusMessageText")]
    [InlineData("Pages", "ProfilePage.xaml", "Contributions", "ContributionSubtitleText")]
    [InlineData("Pages", "MyIssuesPage.xaml", "Comments", "DetailCollectionStatusText")]
    [InlineData("Pages", "MyPullRequestsPage.xaml", "ConversationSectionLabel", "DetailCollectionStatusText")]
    public void MixedTypographyHeaders_ShareOneTextBaseline(
        string directory,
        string fileName,
        string firstText,
        string secondText)
    {
        XDocument document = XDocument.Load(Path.Combine(ViewsRoot(), directory, fileName));

        XElement match = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "TextBlock" &&
            element.Descendants().Any(run =>
                run.Name.LocalName == "Run" &&
                run.Attribute("Text")?.Value.Contains(firstText, StringComparison.Ordinal) == true) &&
            element.Descendants().Any(run =>
                run.Name.LocalName == "Run" &&
                run.Attribute("Text")?.Value.Contains(secondText, StringComparison.Ordinal) == true));

        Assert.True(match.Descendants().Count(static element => element.Name.LocalName == "Run") >= 2);
    }

    [Fact]
    public void RepositoryLanguageDots_UseTightInlineMetadataText()
    {
        foreach ((string path, XDocument document) in LoadViewDocuments())
        {
            XElement[] languageLabels = document.Descendants()
                .Where(static element =>
                    element.Name.LocalName == "TextBlock" &&
                    element.Attribute("Text")?.Value.Contains("Language", StringComparison.Ordinal) == true &&
                    element.Parent?.Elements().Any(static sibling => sibling.Name.LocalName == "Ellipse") == true)
                .ToArray();

            Assert.All(languageLabels, label =>
            {
                Assert.Equal(
                    "{StaticResource AppInlineMetadataTextBlockStyle}",
                    label.Attribute("Style")?.Value);
                XElement dot = Assert.Single(label.Parent!.Elements(), static sibling =>
                    sibling.Name.LocalName == "Ellipse");
                Assert.Equal(
                    "{StaticResource AppLanguageIndicatorDotStyle}",
                    dot.Attribute("Style")?.Value);
            });
        }

        string styles = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Styles",
            "TextBlock.xaml"));
        Assert.Contains("x:Key=\"AppInlineMetadataTextBlockStyle\"", styles, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"TextLineBounds\" Value=\"Tight\" />", styles, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"LineHeight\" Value=\"{ThemeResource AppLineHeight16}\" />", styles, StringComparison.Ordinal);

        string controlCatalog = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Styles",
            "Primitives",
            "ControlCatalog.xaml"));
        Assert.Contains("x:Key=\"AppLanguageIndicatorDotStyle\"", controlCatalog, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"VerticalAlignment\" Value=\"Center\" />", controlCatalog, StringComparison.Ordinal);
        Assert.Contains(
            "<TranslateTransform Y=\"{ThemeResource AppLanguageIndicatorOpticalOffset}\" />",
            controlCatalog,
            StringComparison.Ordinal);
        Assert.DoesNotContain("AppMargin0_Neg4_0_0", controlCatalog, StringComparison.Ordinal);

        string layoutTokens = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Styles",
            "Foundation",
            "Tokens.Layout.xaml"));
        Assert.Contains("x:Key=\"AppLanguageIndicatorOpticalOffset\">-3<", layoutTokens, StringComparison.Ordinal);
    }

    [Fact]
    public void CommitListMetadataUsesOneTextBaselineBesideItsAvatar()
    {
        XDocument document = XDocument.Load(Path.Combine(ViewsRoot(), "Pages", "RepoCommitsPage.xaml"));
        XElement template = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "DataTemplate" &&
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Key" &&
                attribute.Value == "RepoCommitListItemTemplate"));
        XElement metadataRow = Assert.Single(template.Descendants(), element =>
            element.Name.LocalName == "Grid" &&
            element.Elements().Any(child => child.Name.LocalName == "Avatar"));
        XElement avatar = Assert.Single(metadataRow.Elements(), element => element.Name.LocalName == "Avatar");
        XElement metadata = Assert.Single(metadataRow.Elements(), element => element.Name.LocalName == "TextBlock");

        Assert.Equal("Center", (string?)avatar.Attribute("VerticalAlignment"));
        Assert.Equal("Center", (string?)metadata.Attribute("VerticalAlignment"));
        Assert.All(
            new[] { "ShortSha", "AuthorDisplayName", "AuthorDate", "Stats.SummaryText" },
            binding => Assert.Contains(metadata.Descendants(), run =>
                run.Name.LocalName == "Run" &&
                run.Attribute("Text")?.Value.Contains(binding, StringComparison.Ordinal) == true));
    }

    [Theory]
    [InlineData("Controls", "Issue", "RepoIssueListPane.xaml", "RepoIssueListItemTemplate")]
    [InlineData("Pages", "", "RepoPullRequestPage.xaml", "RepoPullRequestListItemTemplate")]
    public void RepositoryIssueAndPullRequestMetadataSharesTightLineMetrics(
        string firstDirectory,
        string secondDirectory,
        string fileName,
        string templateKey)
    {
        string path = Path.Combine(ViewsRoot(), firstDirectory, secondDirectory, fileName);
        XDocument document = XDocument.Load(path);
        XElement template = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "DataTemplate" &&
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Key" && attribute.Value == templateKey));
        XElement avatar = Assert.Single(template.Descendants(), element => element.Name.LocalName == "Avatar");
        XElement[] metadata = template.Descendants()
            .Where(element =>
                element.Name.LocalName == "TextBlock" &&
                string.Equals(
                    element.Attribute("Style")?.Value,
                    "{StaticResource AppInlineMetadataTextBlockStyle}",
                    StringComparison.Ordinal))
            .ToArray();

        Assert.True(metadata.Length >= 2);
        Assert.Equal("{StaticResource AppInlineIdentityTextBlockStyle}", avatar.Attribute("LabelStyle")?.Value);
        Assert.Equal("Center", avatar.Attribute("VerticalAlignment")?.Value);
    }

    [Fact]
    public void CommandSearchFieldsCenterContentAndReserveSpaceOnlyForLeadingIcons()
    {
        string root = FindRepositoryRoot();
        XDocument spacing = XDocument.Load(Path.Combine(
            root,
            "JitHub.WinUI",
            "Styles",
            "Foundation",
            "Tokens.Spacing.xaml"));
        XElement padding = Assert.Single(spacing.Descendants(), element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Key" && attribute.Value == "AppCommandSearchPadding"));
        XElement leadingIconPadding = Assert.Single(spacing.Descendants(), element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Key" && attribute.Value == "AppCommandSearchLeadingIconPadding"));
        Assert.Equal("12,8,12,6", padding.Value);
        Assert.Equal("36,8,12,6", leadingIconPadding.Value);

        XDocument catalog = XDocument.Load(Path.Combine(
            root,
            "JitHub.WinUI",
            "Styles",
            "Primitives",
            "ControlCatalog.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement leadingIconStyle = Assert.Single(catalog.Descendants(), element =>
            element.Name.LocalName == "Style" &&
            element.Attribute(x + "Key")?.Value == "AppCommandLeadingIconSearchTextBoxStyle");
        Assert.Equal("{StaticResource AppCommandSearchTextBoxStyle}", leadingIconStyle.Attribute("BasedOn")?.Value);
        Assert.Contains(leadingIconStyle.Elements(), element =>
            element.Name.LocalName == "Setter" &&
            element.Attribute("Property")?.Value == "Padding" &&
            element.Attribute("Value")?.Value == "{ThemeResource AppCommandSearchLeadingIconPadding}");

        (string FileName, string AutomationId)[] leadingIconFields =
        [
            ("StarsPage.xaml", "StarsSearch"),
            ("GistsPage.xaml", "GistsSearch"),
            ("NotificationsPage.xaml", "NotificationsSearch"),
            ("RepoManagePage.xaml", "RepositoryLibrarySearch")
        ];
        foreach ((string fileName, string automationId) in leadingIconFields)
        {
            XDocument page = XDocument.Load(Path.Combine(ViewsRoot(), "Pages", fileName));
            XElement field = Assert.Single(page.Descendants(), element =>
                element.Name.LocalName == "TextBox" &&
                element.Attribute("AutomationProperties.AutomationId")?.Value == automationId);
            Assert.Equal(
                "{StaticResource AppCommandLeadingIconSearchTextBoxStyle}",
                field.Attribute("Style")?.Value);
            XElement icon = Assert.Single(field.Parent!.Elements(), element => element.Name.LocalName == "FontIcon");
            Assert.Equal("Center", icon.Attribute("VerticalAlignment")?.Value);
        }
    }

    [Theory]
    [InlineData("Controls", "Issue", "RepoIssueListPane.xaml", "RepoIssuesSearchBox")]
    [InlineData("Pages", "", "RepoPullRequestPage.xaml", "RepoPullRequestsSearchBox")]
    public void RepositoryListFiltersUseNativeTokenizedSearchFields(
        string firstDirectory,
        string secondDirectory,
        string fileName,
        string automationId)
    {
        XDocument document = XDocument.Load(Path.Combine(ViewsRoot(), firstDirectory, secondDirectory, fileName));
        XElement filter = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "AutoSuggestBox" &&
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName.EndsWith(".AutomationId", StringComparison.Ordinal) &&
                attribute.Value == automationId));

        Assert.Equal("Find", filter.Attribute("QueryIcon")?.Value);
        Assert.Equal("{StaticResource AppAutoSuggestBoxStyle}", filter.Attribute("Style")?.Value);
    }

    private static void AssertCenteredPeers(
        string path,
        XElement container,
        IEnumerable<XElement> candidates,
        ICollection<string> offenders)
    {
        XElement[] peers = candidates.ToArray();
        XElement[] icons = peers.Where(static element => IconElementNames.Contains(element.Name.LocalName)).ToArray();
        XElement[] textBlocks = peers.Where(static element => element.Name.LocalName == "TextBlock").ToArray();
        if (icons.Length == 0 || textBlocks.Length == 0)
        {
            return;
        }

        foreach (XElement peer in icons.Concat(textBlocks))
        {
            bool isCenteredCatalogStyle = string.Equals(
                peer.Attribute("Style")?.Value,
                "{StaticResource AppLanguageIndicatorDotStyle}",
                StringComparison.Ordinal);
            if (!string.Equals(peer.Attribute("VerticalAlignment")?.Value, "Center", StringComparison.Ordinal) &&
                !isCenteredCatalogStyle)
            {
                offenders.Add($"{Describe(path, container)}: {peer.Name.LocalName}");
            }
        }
    }

    private static bool HasIconPeer(XElement container) =>
        container.Elements().Any(static element => IconElementNames.Contains(element.Name.LocalName));

    private static string GetTypographySignature(XElement textBlock) => string.Join(
        '|',
        textBlock.Attribute("Style")?.Value ?? string.Empty,
        textBlock.Attribute("FontFamily")?.Value ?? string.Empty,
        textBlock.Attribute("FontSize")?.Value ?? string.Empty,
        textBlock.Attribute("FontWeight")?.Value ?? string.Empty);

    private static string Describe(string path, XElement element)
    {
        int line = element is IXmlLineInfo lineInfo && lineInfo.HasLineInfo() ? lineInfo.LineNumber : 0;
        return $"{Path.GetRelativePath(FindRepositoryRoot(), path)}:{line}";
    }

    private static IEnumerable<(string Path, XDocument Document)> LoadViewDocuments()
    {
        foreach (string path in Directory.EnumerateFiles(ViewsRoot(), "*.xaml", SearchOption.AllDirectories))
        {
            yield return (path, XDocument.Load(path, LoadOptions.SetLineInfo));
        }
    }

    private static string ViewsRoot() => Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Views");

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
