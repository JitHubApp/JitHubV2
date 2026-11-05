using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using JitHub.Services.Markdown;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

/// <summary>
/// Checks every XAML markdown host declares its host kind, a stable identity and a
/// canonical document source, and that product hosts go through MarkdownViewer.
/// </summary>
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
    public void DeclarativeProfileHost_UsesCanonicalProfileContract()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "ProfilePage.xaml"));
        string source = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "ProfilePage.xaml.cs"));

        Assert.Contains("HostKind=\"ProfileReadme\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationInstanceId=\"ProfileReadme\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DocumentSource=\"{x:Bind ViewModel.ReadmeDocumentSource, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MarkdownViewer.DocumentSourceProperty", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SurfaceColorToken", xaml, StringComparison.Ordinal);
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
                bool atomicRepositoryPreview = path.EndsWith("MarkdownPreview.xaml", StringComparison.OrdinalIgnoreCase) &&
                    File.ReadAllText(Path.ChangeExtension(path, ".xaml.cs"))
                        .Contains("RichPanel.SetDocumentPayload", StringComparison.Ordinal);
                if (internalFormPreview || atomicRepositoryPreview || host.Attribute("DocumentSource") is not null)
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
    public void MarkdownViewer_UsesSemanticTokensAndReportsRecoverableRenderFailures()
    {
        // The viewer's renderer event wiring (RenderCompleted, RenderFailed and
        // ImageUnavailable into MarkdownViewerFeedback) can only be observed with a
        // live XAML runtime; the feedback results are covered by MarkdownViewerFeedbackTests.
        string root = FindRepositoryRoot();
        string viewerRoot = Path.Combine(root, "JitHub.WinUI", "Views", "Controls", "Common");
        string resources = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Styles", "MarkdownRenderer.xaml"));
        XDocument xaml = XDocument.Load(Path.Combine(viewerRoot, "MarkdownViewer.xaml"));
        XElement errorInfoBar = xaml.Descendants().Single(element =>
            element.Name.LocalName == "InfoBar" &&
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" && attribute.Value == "RenderErrorInfoBar"));

        Assert.Equal("{StaticResource AppErrorInfoBarStyle}", (string?)errorInfoBar.Attribute("Style"));
        Assert.Contains("MarkdownRenderer.Body.FontSize", resources, StringComparison.Ordinal);
        Assert.Contains("ResourceKey=\"AppMarkdownBodyFontSize\"", resources, StringComparison.Ordinal);
        Assert.Contains("ResourceKey=\"AppHighContrastMonoFontFamily\"", resources, StringComparison.Ordinal);
        Assert.Contains("MarkdownRenderer.TableCell.Padding", resources, StringComparison.Ordinal);
        Assert.Contains("ResourceKey=\"AppMarkdownHeading1Margin\"", resources, StringComparison.Ordinal);
        Assert.Contains("ResourceKey=\"AppMarkdownStandaloneParagraphMargin\"", resources, StringComparison.Ordinal);
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
        Assert.Contains("EffectivePreviewAutomationInstanceId", (string?)preview.Attribute("AutomationInstanceId"));
        // The preview instance id the form computes and applies is covered by
        // MarkdownFormAutomationIdentityTests.
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

    [Fact]
    public void RepositoryMarkdownPreview_LazilyCreatesTheInactivePlainEditor()
    {
        string root = FindRepositoryRoot();
        string previewRoot = Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "CodeViewer",
            "Renderers");
        XDocument xaml = XDocument.Load(Path.Combine(previewRoot, "MarkdownPreview.xaml"));
        string source = File.ReadAllText(Path.Combine(previewRoot, "MarkdownPreview.xaml.cs"));

        Assert.DoesNotContain(
            xaml.Descendants(),
            element => element.Name.LocalName == "CodeEditorControl");
        Assert.Contains("x:Name=\"PlainPanelHost\"", File.ReadAllText(Path.Combine(previewRoot, "MarkdownPreview.xaml")), StringComparison.Ordinal);
        Assert.Contains("private void EnsurePlainPanel()", source, StringComparison.Ordinal);
        Assert.Contains("if (!rich)", source, StringComparison.Ordinal);
        Assert.Contains("_plainPanel = new CodeEditorControl", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryMarkdownPreview_ResynchronizesLatestViewModelStateAfterReload()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "CodeViewer",
            "Renderers",
            "MarkdownPreview.xaml.cs"));
        const string loadedSignature = "private void OnLoaded(object sender, RoutedEventArgs e)";
        int loadedStart = source.IndexOf(loadedSignature, StringComparison.Ordinal);
        int unloadedStart = source.IndexOf("private void OnUnloaded", loadedStart, StringComparison.Ordinal);

        Assert.True(loadedStart >= 0, "MarkdownPreview must synchronize its state when loaded.");
        Assert.True(unloadedStart > loadedStart, "MarkdownPreview must keep load and unload lifecycle handlers distinct.");

        string onLoaded = source[loadedStart..unloadedStart];
        Assert.Contains("RepoFilePreviewViewModel? viewModel = ViewModel;", onLoaded, StringComparison.Ordinal);
        Assert.Contains("SubscribeToViewModel(viewModel);", onLoaded, StringComparison.Ordinal);
        Assert.Contains("SyncSegmented();", onLoaded, StringComparison.Ordinal);
        Assert.Contains("SyncPanels();", onLoaded, StringComparison.Ordinal);
        Assert.Contains("RichPanel.SetDocumentPayload(viewModel?.RichPreviewDocument);", onLoaded, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) ||
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
