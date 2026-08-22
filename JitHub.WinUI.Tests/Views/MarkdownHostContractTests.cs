using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using JitHub.WinUI.Views.Controls.Common;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class MarkdownHostContractTests
{
    [Fact]
    public void EveryXamlMarkdownHost_DeclaresCanonicalHostKind()
    {
        string root = FindRepositoryRoot();
        string viewsRoot = Path.Combine(root, "JitHub.WinUI", "Views");
        List<string> failures = [];

        foreach (string path in Directory.EnumerateFiles(viewsRoot, "*.xaml", SearchOption.AllDirectories))
        {
            XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (XElement viewer in document.Descendants().Where(element => element.Name.LocalName == "MarkdownViewer"))
            {
                XAttribute? kind = viewer.Attribute("HostKind");
                if (kind is null || !Enum.TryParse(kind.Value, ignoreCase: true, out MarkdownHostKind _))
                {
                    IXmlLineInfo lineInfo = (IXmlLineInfo)viewer;
                    failures.Add($"{Path.GetRelativePath(root, path)}:{lineInfo.LineNumber}");
                }
            }
        }

        Assert.True(failures.Count == 0, "Markdown hosts without a canonical HostKind: " + string.Join(", ", failures));
    }

    [Fact]
    public void EveryXamlMarkdownHost_HasAStableInstanceIdentity()
    {
        string root = FindRepositoryRoot();
        string viewsRoot = Path.Combine(root, "JitHub.WinUI", "Views");
        List<string> failures = [];

        foreach (string path in Directory.EnumerateFiles(viewsRoot, "*.xaml", SearchOption.AllDirectories))
        {
            XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (XElement viewer in document.Descendants().Where(element => element.Name.LocalName == "MarkdownViewer"))
            {
                bool assignedInCodeBehind = path.EndsWith("MarkdownForm.xaml", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(viewer.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value, "PreviewViewer", StringComparison.Ordinal);
                if (viewer.Attribute("AutomationInstanceId") is null && !assignedInCodeBehind)
                {
                    IXmlLineInfo lineInfo = (IXmlLineInfo)viewer;
                    failures.Add($"{Path.GetRelativePath(root, path)}:{lineInfo.LineNumber}");
                }
            }
        }

        Assert.True(failures.Count == 0, "Markdown hosts without an instance identity: " + string.Join(", ", failures));
    }

    [Fact]
    public void ProgrammaticProfileHost_UsesCanonicalProfileContract()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "ProfilePage.xaml.cs"));

        Assert.Contains("HostKind = MarkdownHostContract.ProfileReadme", source, StringComparison.Ordinal);
        Assert.Contains("AutomationInstanceId = \"ProfileReadme\"", source, StringComparison.Ordinal);
        Assert.Contains("MarkdownViewer.DocumentSourceProperty", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SurfaceColorToken", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryReachableRepositoryMarkdownHost_RequiresCanonicalDocumentSource()
    {
        string root = FindRepositoryRoot();
        string viewsRoot = Path.Combine(root, "JitHub.WinUI", "Views");
        List<string> failures = [];

        foreach (string path in Directory.EnumerateFiles(viewsRoot, "*.xaml", SearchOption.AllDirectories))
        {
            if (path.EndsWith("DesignLabPage.xaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
            IEnumerable<XElement> hosts = document.Descendants().Where(element =>
                element.Name.LocalName is "MarkdownViewer" or "MarkdownForm");
            foreach (XElement host in hosts)
            {
                bool internalFormPreview = path.EndsWith("MarkdownForm.xaml", StringComparison.OrdinalIgnoreCase) &&
                    host.Name.LocalName == "MarkdownViewer";
                if (internalFormPreview || host.Attribute("DocumentSource") is not null)
                {
                    continue;
                }

                IXmlLineInfo lineInfo = (IXmlLineInfo)host;
                failures.Add($"{Path.GetRelativePath(root, path)}:{lineInfo.LineNumber}");
            }
        }

        Assert.True(failures.Count == 0,
            "Repository-backed Markdown hosts without DocumentSource: " + string.Join(", ", failures));
    }

    [Fact]
    public void HostKinds_ResolveStableThemeAndAutomationMetadata()
    {
        HashSet<string> automationIds = new(StringComparer.Ordinal);
        foreach (MarkdownHostKind kind in Enum.GetValues<MarkdownHostKind>())
        {
            string value = kind.ToString();
            Assert.False(string.IsNullOrWhiteSpace(MarkdownHostContract.GetSurfaceColorToken(value)));
            Assert.StartsWith("#", MarkdownHostContract.GetSurfaceFallback(value, dark: false));
            Assert.StartsWith("#", MarkdownHostContract.GetSurfaceFallback(value, dark: true));
            Assert.Contains("Markdown", MarkdownHostContract.GetAutomationName(value), StringComparison.Ordinal);
            Assert.StartsWith("MarkdownHost_", MarkdownHostContract.GetAutomationId(value), StringComparison.Ordinal);
            Assert.True(automationIds.Add(MarkdownHostContract.GetAutomationId(value)),
                $"Duplicate Markdown host automation id for {value}.");
        }
    }

    [Fact]
    public void AutomationIdentity_NormalizesAndSeparatesHostInstances()
    {
        Assert.Equal(
            "MarkdownHost_Comment_IssueComment_42",
            MarkdownHostContract.GetAutomationId(MarkdownHostContract.Comment, "IssueComment:42"));
        Assert.NotEqual(
            MarkdownHostContract.GetAutomationId(MarkdownHostContract.Comment, "IssueComment:42"),
            MarkdownHostContract.GetAutomationId(MarkdownHostContract.Comment, "IssueComment:43"));
    }

    [Fact]
    public void MarkdownViewer_AppliesCanonicalIdentityAndTextScalingToRenderer()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownViewer.xaml.cs"));

        Assert.Contains("MarkdownHostContract.GetAutomationName(HostKind)", source, StringComparison.Ordinal);
        Assert.Contains("MarkdownHostContract.GetAutomationId(HostKind, AutomationInstanceId)", source, StringComparison.Ordinal);
        Assert.Contains("_uiSettings?.TextScaleFactor", source, StringComparison.Ordinal);
        Assert.Contains("_uiSettings.TextScaleFactorChanged +=", source, StringComparison.Ordinal);
        Assert.Contains("_uiSettings.TextScaleFactorChanged -=", source, StringComparison.Ordinal);
        Assert.Contains("_accessibilitySettings.HighContrastChanged +=", source, StringComparison.Ordinal);
        Assert.Contains("_accessibilitySettings.HighContrastChanged -=", source, StringComparison.Ordinal);
        Assert.Contains("QueueRuntimeThemeRefresh", source, StringComparison.Ordinal);
        Assert.Contains("MarkdownLifecycleAutomationBridge.GetRuntimeSettingsRevision()", source, StringComparison.Ordinal);
        Assert.Contains("LifecycleRuntimeSettingsTimer_Tick", source, StringComparison.Ordinal);
        Assert.Contains("_lifecycleRuntimeSettingsTimer.Stop()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownViewer_UsesPerDocumentConsentAndCanonicalSourceContext()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownViewer.xaml.cs"));

        Assert.Contains("MarkdownRemoteContentConsent", source, StringComparison.Ordinal);
        Assert.Contains("DocumentSourceProperty", source, StringComparison.Ordinal);
        Assert.Contains("WithImageDocumentSource(DocumentSource)", source, StringComparison.Ordinal);
        Assert.Contains("_remoteContentConsent.Grant()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_allowRemoteImagesForDocument", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownForm_DoesNotConvertAutomationIdentityIntoDocumentConsentIdentity()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownForm.xaml.cs"));

        Assert.Contains("PreviewViewer.DocumentSource = DocumentSource;", source, StringComparison.Ordinal);
        Assert.Contains("ModeSegmented.SelectedItem = PreviewModeItem;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("editor:{prefix}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new MarkdownDocumentSource", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownForm_PreviewIdentityRemainsStableAcrossBindingRefreshes()
    {
        string root = FindRepositoryRoot();
        XDocument document = XDocument.Load(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownForm.xaml"));
        XElement preview = document.Descendants().Single(element =>
            element.Name.LocalName == "MarkdownViewer" &&
            string.Equals(
                element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value,
                "PreviewViewer",
                StringComparison.Ordinal));
        string source = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownForm.xaml.cs"));

        Assert.Contains("EffectivePreviewAutomationInstanceId", (string?)preview.Attribute("AutomationInstanceId"));
        Assert.Contains("$\"{ResolveAutomationPrefix()}_Preview\"", source, StringComparison.Ordinal);
        Assert.Contains("PreviewViewer.AutomationInstanceId = EffectivePreviewAutomationInstanceId;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownForm_PreviewOwnsAVerticalOnlyScrollViewport()
    {
        XDocument document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownForm.xaml"));

        XElement preview = document.Descendants().Single(element =>
            element.Name.LocalName == "MarkdownViewer" &&
            string.Equals(
                element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value,
                "PreviewViewer",
                StringComparison.Ordinal));
        XElement scrollViewer = preview.Ancestors().First(element => element.Name.LocalName == "ScrollViewer");

        Assert.Equal("Enabled", (string?)scrollViewer.Attribute("VerticalScrollMode"));
        Assert.Equal("Auto", (string?)scrollViewer.Attribute("VerticalScrollBarVisibility"));
        Assert.Equal("Disabled", (string?)scrollViewer.Attribute("HorizontalScrollMode"));
        Assert.Equal("Disabled", (string?)scrollViewer.Attribute("HorizontalScrollBarVisibility"));
        Assert.Equal("Disabled", (string?)scrollViewer.Attribute("ZoomMode"));
    }

    [Fact]
    public void PullRequestConversation_UsesOnDemandComposerAndShyHeader()
    {
        string xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoPullRequestPage.xaml"));
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoPullRequestPage.xaml.cs"));

        Assert.Contains("x:Name=\"RepoPullRequestsOpenCompactCommentButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PullRequestCommentFlyout\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PullRequestCompactCommentForm\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"PullRequestCommentForm\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PullRequestShySectionComboBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SizeChanged=\"PullRequestScrollableSection_SizeChanged\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PullRequestFilesSection\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Loaded=\"PullRequestScrollableSection_Loaded\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Unloaded=\"PullRequestScrollableSection_Unloaded\"", xaml, StringComparison.Ordinal);
        Assert.Contains("labs:TransitionHelper.Id=\"PullRequestHeaderSurface\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource AppTransientOverlayBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ShyHeaderStartOffset", source, StringComparison.Ordinal);
        Assert.Contains("ShyHeaderRestoreOffset", source, StringComparison.Ordinal);
        Assert.Contains("ShyHeaderRevealTravel", source, StringComparison.Ordinal);
        Assert.Contains("ShyHeaderRehideTravel", source, StringComparison.Ordinal);
        Assert.Contains("PullRequestSectionScrollViewer_ViewChanged", source, StringComparison.Ordinal);
        Assert.Contains("RegisterPropertyChangedCallback", source, StringComparison.Ordinal);
        Assert.Contains("new TransitionHelper", source, StringComparison.Ordinal);
        Assert.Contains("_headerTransition.StartAsync", source, StringComparison.Ordinal);
        Assert.Contains("_headerTransition.ReverseAsync", source, StringComparison.Ordinal);
        Assert.Contains("AnimateContentReflow", source, StringComparison.Ordinal);
        Assert.Contains("TranslationTransition", source, StringComparison.Ordinal);
        Assert.Contains("PullRequestDetailLayout.UpdateLayout()", source, StringComparison.Ordinal);
        Assert.Contains("generation != _headerTransitionGeneration", source, StringComparison.Ordinal);
        Assert.Contains("AttachActiveSectionScrollSources", source, StringComparison.Ordinal);
        Assert.Contains("PullRequestSectionComboBox.Visibility = IsCompactWorkspace", source, StringComparison.Ordinal);
        Assert.Contains("PullRequestShySectionComboBox.Visibility = !IsCompactWorkspace", source, StringComparison.Ordinal);
        Assert.Contains("PullRequestCompactCommentForm.FocusEditor()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("forceInlineComposerForLifecycle", source, StringComparison.Ordinal);
        Assert.DoesNotContain("JITHUB_MARKDOWN_LIFECYCLE", source, StringComparison.Ordinal);
    }

    [Fact]
    public void IssueConversation_UsesOnDemandComposerAndShyHeader()
    {
        string xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Issue",
            "RepoIssueDetailPane.xaml"));
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Issue",
            "RepoIssueDetailPane.xaml.cs"));

        Assert.Contains("AutomationProperties.AutomationId=\"RepoIssuesOpenCommentButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"IssueCommentFlyout\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewChanged=\"IssueConversationScrollViewer_ViewChanged\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AdaptiveWorkspaceMode.Narrow or AdaptiveWorkspaceMode.Compact", source, StringComparison.Ordinal);
        Assert.Contains("ShyHeaderStartOffset", source, StringComparison.Ordinal);
        Assert.Contains("ShyHeaderRestoreOffset", source, StringComparison.Ordinal);
        Assert.Contains("ShyHeaderRevealTravel", source, StringComparison.Ordinal);
        Assert.Contains("ShyHeaderRehideTravel", source, StringComparison.Ordinal);
        Assert.Contains("labs:TransitionHelper.Id=\"IssueHeaderSurface\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource AppTransientOverlayBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("new TransitionHelper", source, StringComparison.Ordinal);
        Assert.Contains("_headerTransition.StartAsync", source, StringComparison.Ordinal);
        Assert.Contains("_headerTransition.ReverseAsync", source, StringComparison.Ordinal);
        Assert.Contains("AnimateContentReflow", source, StringComparison.Ordinal);
        Assert.Contains("TranslationTransition", source, StringComparison.Ordinal);
        Assert.Contains("RepoIssuesDetailLayout.UpdateLayout()", source, StringComparison.Ordinal);
        Assert.Contains("generation != _headerTransitionGeneration", source, StringComparison.Ordinal);
        Assert.Contains("IssueCommentForm.FocusEditor()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IssueCommentForm.EffectiveEditorHeight", source, StringComparison.Ordinal);
        Assert.DoesNotContain("JITHUB_MARKDOWN_LIFECYCLE", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownViewer_ResolverFailureInstallsDenyAllResolver()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownViewer.xaml.cs"));

        Assert.Contains("DenyAllMarkdownImageResolver.Instance", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private readonly IMarkdownImageResolver? _imageResolver", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownViewer_CodeBackgroundsResolveThroughHighContrastAwareThemeTokens()
    {
        string root = FindRepositoryRoot();
        string viewerSource = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownViewer.xaml.cs"));
        string colorTokens = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Styles",
            "Foundation",
            "Tokens.Colors.xaml"));

        Assert.Contains("ResolveColor(\"AppCanvasInset\"", viewerSource, StringComparison.Ordinal);
        Assert.Contains("<ResourceDictionary x:Key=\"HighContrast\">", colorTokens, StringComparison.Ordinal);
        Assert.Contains(
            "<StaticResource x:Key=\"AppCanvasInsetColor\" ResourceKey=\"SystemColorWindowColor\" />",
            colorTokens,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProductHosts_DoNotInstantiateRendererDirectly()
    {
        string root = FindRepositoryRoot();
        string viewsRoot = Path.Combine(root, "JitHub.WinUI", "Views");
        string[] offenders = Directory.EnumerateFiles(viewsRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".cs" or ".xaml")
            .Where(path => !path.EndsWith("MarkdownViewer.xaml.cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("MarkdownRendererControl", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        Assert.Empty(offenders);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
