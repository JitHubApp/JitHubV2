using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class VNextLocalizationContractTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly HashSet<string> LocalizedAttributes =
    [
        "Text",
        "Content",
        "Header",
        "PlaceholderText",
        "Title",
        "Description",
        "Label",
        "OffContent",
        "OnContent",
        "PrimaryButtonText",
        "SecondaryButtonText",
        "CloseButtonText",
        "AutomationProperties.Name",
        "AutomationProperties.HelpText",
        "ToolTipService.ToolTip"
    ];

    private static readonly string[] RequiredPages =
    [
        "DashboardPage.xaml",
        "GistsPage.xaml",
        "LoginPage.xaml",
        "MyIssuesPage.xaml",
        "MyPullRequestsPage.xaml",
        "NotificationsPage.xaml",
        "ProfilePage.xaml",
        "RepoCodePage.xaml",
        "RepoCommitsPage.xaml",
        "RepoDetailPage.xaml",
        "RepoIssuePage.xaml",
        "RepoManagePage.xaml",
        "RepoPullRequestPage.xaml",
        "RepoSearchResultPage.xaml",
        "SettingsPage.xaml",
        "ShellPage.xaml",
        "StarsPage.xaml"
    ];

    [Fact]
    public void ReachableProductXaml_LiteralFallbacksHaveStableResourceOwners()
    {
        string root = FindRepositoryRoot();
        string viewsRoot = Path.Combine(root, "JitHub.WinUI", "Views");
        IReadOnlyDictionary<string, string> english = LoadResources(
            Path.Combine(root, "JitHub.WinUI", "Strings", "en-US", "Resources.resw"));
        var ownedFallbacks = new Dictionary<string, string>(StringComparer.Ordinal);
        var failures = new List<string>();

        string[] xamlFiles = Directory.EnumerateFiles(viewsRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Pages{Path.DirectorySeparatorChar}Design{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.EndsWith($"{Path.DirectorySeparatorChar}DevConsole.xaml", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        foreach (string requiredPage in RequiredPages)
        {
            Assert.Contains(xamlFiles, path => Path.GetFileName(path).Equals(requiredPage, StringComparison.Ordinal));
        }

        foreach (string path in xamlFiles)
        {
            XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (XElement element in document.Descendants())
            {
                foreach (XAttribute attribute in element.Attributes().Where(IsLiteralUserFacingAttribute))
                {
                    ValidateOwnedFallback(path, element, attribute.Name.LocalName, attribute.Value, english, ownedFallbacks, failures);
                }

                if (TryGetDirectUserFacingText(element, out string? fallback))
                {
                    string property = element.Name.LocalName == "MenuFlyoutItem" ? "Text" :
                        element.Name.LocalName is "TextBlock" or "Run" ? "Text" : "Content";
                    ValidateOwnedFallback(path, element, property, fallback!, english, ownedFallbacks, failures);
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void PseudoLocale_CoversEveryEnglishResourceAndExpandsHumanText()
    {
        string root = FindRepositoryRoot();
        IReadOnlyDictionary<string, string> english = LoadResources(
            Path.Combine(root, "JitHub.WinUI", "Strings", "en-US", "Resources.resw"));
        IReadOnlyDictionary<string, string> pseudo = LoadResources(
            Path.Combine(root, "JitHub.WinUI", "Strings", "qps-ploc", "Resources.resw"));

        Assert.Equal(english.Keys.OrderBy(key => key), pseudo.Keys.OrderBy(key => key));
        foreach ((string key, string value) in english.Where(pair => ContainsLetter(pair.Value)))
        {
            Assert.True(pseudo.TryGetValue(key, out string? pseudoValue), $"Missing pseudo resource '{key}'.");
            Assert.StartsWith("⟦", pseudoValue, StringComparison.Ordinal);
            Assert.EndsWith("⟧", pseudoValue, StringComparison.Ordinal);
            Assert.Contains(value, pseudoValue, StringComparison.Ordinal);
            Assert.True(pseudoValue.Length >= value.Length + 8, $"Pseudo resource '{key}' was not meaningfully expanded.");
        }
    }

    [Fact]
    public void RuntimeCreatedAccessibilityAndDialogStringsUseFallbackAwareResources()
    {
        string pagesRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Views", "Pages");
        string[] files =
        [
            "SettingsPage.xaml.cs",
            "RepoPullRequestPage.xaml.cs",
            "RepoCommitsPage.xaml.cs",
            "StarsPage.xaml.cs",
            "RepoIssuePage.xaml.cs",
            "MyIssuesPage.xaml.cs",
            "MyPullRequestsPage.xaml.cs"
        ];
        Regex[] forbiddenLiteralPatterns =
        [
            new(@"AutomationProperties\.SetName\(\s*[^,\r\n]+,\s*""", RegexOptions.CultureInvariant),
            new(@"ToolTipService\.SetToolTip\(\s*[^,\r\n]+,\s*""", RegexOptions.CultureInvariant),
            new(@"(?:Title|PrimaryButtonText|SecondaryButtonText|CloseButtonText|PlaceholderText)\s*=\s*""", RegexOptions.CultureInvariant)
        ];

        foreach (string file in files)
        {
            string source = File.ReadAllText(Path.Combine(pagesRoot, file));
            foreach (Regex pattern in forbiddenLiteralPatterns)
            {
                Match match = pattern.Match(source);
                Assert.False(match.Success, $"{file} contains an unowned runtime UI literal: {match.Value}");
            }
        }
    }

    [Fact]
    public void RuntimeGeneratedShellTimelineProfileAndMarkdownCopyIsResourceBacked()
    {
        string root = FindRepositoryRoot();
        string shell = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "ViewModels", "Pages", "ShellPageViewModel.cs"));
        string timeline = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "PullRequestViewModels",
            "ConversationViewModels",
            "PullRequestTimelineItemViewModel.cs"));
        string contributionGraph = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "Views", "Controls", "Profile", "ProfileContributionGraph.xaml.cs"));
        string markdown = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "Views", "Controls", "Common", "MarkdownViewer.xaml.cs"));

        Assert.Contains("ShellText($\"Command.", shell, StringComparison.Ordinal);
        Assert.Contains("ShellFormat(\"RepositoryStatus.", shell, StringComparison.Ordinal);
        Assert.Contains("ShellFormat(\"Search.QueryTitle\"", shell, StringComparison.Ordinal);
        Assert.Contains("LocalizedSentence(", timeline, StringComparison.Ordinal);
        Assert.Contains("LocalizedResourceText.GetString", timeline, StringComparison.Ordinal);
        Assert.DoesNotContain("Text(\" closed this pull request\")", timeline, StringComparison.Ordinal);
        Assert.DoesNotContain("Text(\" requested review from \")", timeline, StringComparison.Ordinal);
        Assert.Contains("Profile.ContributionGraph.KeyboardHelp", contributionGraph, StringComparison.Ordinal);
        Assert.Contains("Profile.ContributionGraph.DayAccessibleName", contributionGraph, StringComparison.Ordinal);
        Assert.Contains("Markdown.RemoteImage.InsecureMessage", markdown, StringComparison.Ordinal);
        Assert.Contains("Markdown.RemoteImage.ProtectedTitle", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeDashboardProfileRepositoryAndCommentCopyIsResourceBacked()
    {
        string root = FindRepositoryRoot();
        string dashboard = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "ViewModels", "Pages", "DashboardPageViewModel.cs"));
        string profile = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "ViewModels", "Pages", "ProfilePageViewModel.cs"));
        string repository = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "ViewModels", "RepositoryViewModels", "RepoDetailViewModel.cs"));
        string comments = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "ViewModels", "UserViewModel", "UserCommentBlockViewModel.cs"));

        Assert.Contains("Dashboard/Greeting/Default", dashboard, StringComparison.Ordinal);
        Assert.Contains("Profile.Status.Loading", profile, StringComparison.Ordinal);
        Assert.Contains("RepoDetail.Star.ActionUnavailable", repository, StringComparison.Ordinal);
        Assert.Contains("Comment.Menu.CopyLink", comments, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new Regex(@"(?:DashboardStatusText|NotificationStatusText)\s*=\s*\""", RegexOptions.CultureInvariant),
            dashboard);
        Assert.DoesNotMatch(
            new Regex(@"(?:StatusText|BioText|ReadmeEmptyText)\s*=\s*\""", RegexOptions.CultureInvariant),
            profile);
        Assert.DoesNotMatch(
            new Regex(@"ShowActionStatus\(\s*\""", RegexOptions.CultureInvariant),
            repository);
        Assert.DoesNotContain("new MenuItem(\"", comments, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomationPseudoLocale_IsExplicitAndCannotLeakIntoNormalLaunches()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Program.cs"));
        Assert.Contains("vnext-pseudo-localized", source, StringComparison.Ordinal);
        Assert.Contains("using Microsoft.Windows.Globalization;", source, StringComparison.Ordinal);
        Assert.Contains("ApplicationLanguages.PrimaryLanguageOverride = pseudoLanguage", source, StringComparison.Ordinal);
        Assert.Contains("ApplicationLanguages.PrimaryLanguageOverride = string.Empty", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows.ApplicationModel.Resources.Core", source, StringComparison.Ordinal);
        int launchOptionsIndex = source.IndexOf("CurrentLaunchOptions = LaunchOptions.Parse(args);", StringComparison.Ordinal);
        int languageOverrideIndex = source.IndexOf("ConfigureAutomationLanguageOverride();", StringComparison.Ordinal);
        int applicationStartIndex = source.IndexOf("Application.Start", StringComparison.Ordinal);
        Assert.True(launchOptionsIndex >= 0 && languageOverrideIndex > launchOptionsIndex);
        Assert.True(applicationStartIndex > languageOverrideIndex);

        string project = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "JitHub.WinUI.csproj"));
        Assert.DoesNotContain("Strings\\qps-ploc\\**\\*", project, StringComparison.OrdinalIgnoreCase);

        string localizationService = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Services",
            "LocalizationService.cs"));
        Assert.Contains("Microsoft.Windows.ApplicationModel.Resources", localizationService, StringComparison.Ordinal);
        Assert.DoesNotContain("using Windows.ApplicationModel.Resources;", localizationService, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyAutomationPseudoLocalization", localizationService, StringComparison.Ordinal);

        string localizedResourceText = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Helpers",
            "LocalizedResourceText.cs"));
        Assert.Contains("Microsoft.Windows.ApplicationModel.Resources", localizedResourceText, StringComparison.Ordinal);
        Assert.DoesNotContain("using Windows.ApplicationModel.Resources;", localizedResourceText, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyAutomationPseudoLocalization", localizedResourceText, StringComparison.Ordinal);

        string shell = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "ShellPage.xaml.cs"));
        Assert.Contains("Shell.Search.CompactPlaceholder", shell, StringComparison.Ordinal);
        Assert.Contains("SearchSubmitButton.Visibility = _isShellSearchCompact", shell, StringComparison.Ordinal);
        Assert.Contains("SearchShortcutBadge.Visibility = _isShellSearchCompact", shell, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetHelpText(SearchTextBox, searchPlaceholder)", shell, StringComparison.Ordinal);

        string shellXaml = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "ShellPage.xaml"));
        Assert.Contains("Target=\"SearchBoxFrame.Width\" Value=\"220\"", shellXaml, StringComparison.Ordinal);

        string automation = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI.Automation",
            "Program.cs"));
        Assert.Contains("vnext-pseudo-localization", automation, StringComparison.Ordinal);
        foreach (string page in new[]
                 {
                     "settings",
                     "repo-pulls",
                     "repo-commits",
                     "stars",
                     "repo-issues",
                     "my-issues",
                     "my-pull-requests"
                 })
        {
            Assert.Contains($"(\"{page}\"", automation, StringComparison.Ordinal);
        }

        foreach (string viewport in new[] { "(1366, 900)", "(760, 650)", "(640, 600)" })
        {
            Assert.Contains(viewport, automation, StringComparison.Ordinal);
        }

        Assert.Contains("IsInsideWindowBounds(primary, window)", automation, StringComparison.Ordinal);
        Assert.Contains("commandName.StartsWith(\"⟦\"", automation, StringComparison.Ordinal);
        Assert.Contains("shellSearchName.StartsWith(\"⟦\"", automation, StringComparison.Ordinal);
        Assert.Contains("shellSearchPlaceholder.StartsWith(\"⟦\"", automation, StringComparison.Ordinal);
        Assert.Contains("requiredSettingsCheckpoints", automation, StringComparison.Ordinal);
        Assert.Contains("missingSettingsCheckpoints.Length == 0", automation, StringComparison.Ordinal);
        Assert.Contains("LoadEnglishUiFallbacks(options.AppPath)", automation, StringComparison.Ordinal);
        Assert.Contains("!englishUiFallbacks.Contains(commandName)", automation, StringComparison.Ordinal);
        Assert.Contains("shellSearch.BoundingRectangle.Width >= 128", automation, StringComparison.Ordinal);
        Assert.Contains("compact shell search text viewport was too narrow", automation, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsThemeAndShellSearch_RuntimeCheckpointsHaveRealPseudoResources()
    {
        string root = FindRepositoryRoot();
        IReadOnlyDictionary<string, string> english = LoadResources(
            Path.Combine(root, "JitHub.WinUI", "Strings", "en-US", "Resources.resw"));
        IReadOnlyDictionary<string, string> pseudo = LoadResources(
            Path.Combine(root, "JitHub.WinUI", "Strings", "qps-ploc", "Resources.resw"));
        string[] requiredKeys =
        [
            "PagesSettingsPageTextBlockTheme.Text",
            "PagesSettingsPageTextBlockSystem.Text",
            "PagesSettingsPageTextBlockLight.Text",
            "PagesSettingsPageTextBlockDark.Text",
            "PagesSettingsPageSettingsThemeSystem.AutomationProperties.Name",
            "PagesSettingsPageSettingsThemeLight.AutomationProperties.Name",
            "PagesSettingsPageSettingsThemeDark.AutomationProperties.Name",
            "PagesShellPageShellSearchTextBox.AutomationProperties.Name",
            "PagesShellPageShellSearchTextBox.PlaceholderText",
            "Shell.Search.CompactPlaceholder"
        ];

        foreach (string key in requiredKeys)
        {
            Assert.True(english.TryGetValue(key, out string? englishValue), $"Missing English runtime resource '{key}'.");
            Assert.True(pseudo.TryGetValue(key, out string? pseudoValue), $"Missing pseudo runtime resource '{key}'.");
            Assert.StartsWith("⟦", pseudoValue, StringComparison.Ordinal);
            Assert.EndsWith("⟧", pseudoValue, StringComparison.Ordinal);
            Assert.NotEqual(englishValue, pseudoValue);
        }

        string settingsXaml = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "SettingsPage.xaml"));
        foreach (string automationId in new[]
                 {
                     "SettingsThemeHeading",
                     "SettingsThemeSystemLabel",
                     "SettingsThemeLightLabel",
                     "SettingsThemeDarkLabel"
                 })
        {
            Assert.Contains($"AutomationProperties.AutomationId=\"{automationId}\"", settingsXaml, StringComparison.Ordinal);
        }
    }

    private static bool IsLiteralUserFacingAttribute(XAttribute attribute) =>
        LocalizedAttributes.Contains(attribute.Name.LocalName) &&
        IsLiteral(attribute.Value);

    private static bool IsLiteral(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.TrimStart().StartsWith('{') &&
        ContainsLetter(WebUtility.HtmlDecode(value));

    private static bool ContainsLetter(string value) => value.Any(char.IsLetter);

    private static bool TryGetDirectUserFacingText(XElement element, out string? fallback)
    {
        fallback = null;
        if (element.Name.LocalName is not ("TextBlock" or "Run" or "Button" or "ToggleButton" or
            "RadioButton" or "CheckBox" or "ComboBoxItem" or "SegmentedItem" or "SelectorBarItem" or "MenuFlyoutItem"))
        {
            return false;
        }

        XText? text = element.Nodes().OfType<XText>().SingleOrDefault();
        if (text is null || element.Elements().Any() || !IsLiteral(text.Value))
        {
            return false;
        }

        fallback = text.Value.Trim();
        return true;
    }

    private static void ValidateOwnedFallback(
        string path,
        XElement element,
        string property,
        string fallback,
        IReadOnlyDictionary<string, string> english,
        Dictionary<string, string> ownedFallbacks,
        List<string> failures)
    {
        string? uid = (string?)element.Attribute(Xaml + "Uid");
        string location = $"{Path.GetFileName(path)}:{((IXmlLineInfo)element).LineNumber}";
        if (string.IsNullOrWhiteSpace(uid))
        {
            failures.Add($"{location} has unowned literal {element.Name.LocalName}.{property}='{fallback}'.");
            return;
        }

        string key = $"{uid}.{property}";
        if (!english.TryGetValue(key, out string? resourceValue))
        {
            failures.Add($"{location} is owned by '{key}', but the English resource is missing.");
            return;
        }

        if (!string.Equals(resourceValue, fallback, StringComparison.Ordinal))
        {
            failures.Add($"{location} fallback '{fallback}' does not match resource '{key}' value '{resourceValue}'.");
        }

        if (ownedFallbacks.TryGetValue(key, out string? existing) && !string.Equals(existing, fallback, StringComparison.Ordinal))
        {
            failures.Add($"Resource owner '{key}' is reused for conflicting fallbacks '{existing}' and '{fallback}'.");
        }
        else
        {
            ownedFallbacks[key] = fallback;
        }
    }

    private static IReadOnlyDictionary<string, string> LoadResources(string path) =>
        XDocument.Load(path)
            .Root!
            .Elements("data")
            .ToDictionary(
                element => (string)element.Attribute("name")!,
                element => (string?)element.Element("value") ?? string.Empty,
                StringComparer.Ordinal);

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
