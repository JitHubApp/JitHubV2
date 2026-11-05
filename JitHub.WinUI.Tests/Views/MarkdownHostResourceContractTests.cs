using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

/// <summary>
/// Checks the markdown host resources, notices, renderer feature references and their
/// theme tokens.
/// </summary>
public sealed class MarkdownHostResourceContractTests
{
    [Fact]
    public void MarkdownViewer_NoticesNeverResizeTheReadingRegion()
    {
        // An image failing below the fold opened the remote-image
        // notice in an Auto row, shrinking the README viewport by ~50 DIP
        // mid-read and shifting the document. Notices overlay the region.
        string root = FindRepositoryRoot();
        string viewerRoot = Path.Combine(root, "JitHub.WinUI", "Views", "Controls", "Common");
        XDocument xaml = XDocument.Load(Path.Combine(viewerRoot, "MarkdownViewer.xaml"));
        XElement rootGrid = xaml.Root!.Elements().Single(element => element.Name.LocalName == "Grid");

        Assert.DoesNotContain(rootGrid.Elements(), element => element.Name.LocalName.EndsWith("RowDefinitions", StringComparison.Ordinal));
        Assert.DoesNotContain(xaml.Descendants(), element => element.Attributes().Any(attribute => attribute.Name.LocalName == "Grid.Row"));
        XElement[] layers = rootGrid.Elements().Where(element => !element.Name.LocalName.Contains('.', StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, layers.Length);
        // Declared first (keyboard and UI Automation reach the notice before
        // the document) and drawn on top by ZIndex.
        Assert.Equal("NoticeLayer", (string?)layers[0].Attributes().Single(attribute => attribute.Name.LocalName == "Name"));
        Assert.Equal("1", (string?)layers[0].Attributes().Single(attribute => attribute.Name.LocalName == "Canvas.ZIndex"));
        Assert.Equal("Top", (string?)layers[0].Attribute("VerticalAlignment"));
        Assert.Equal("ReadingRegion", (string?)layers[1].Attributes().Single(attribute => attribute.Name.LocalName == "Name"));
        Assert.Contains(layers[1].Descendants(), element => (string?)element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Name") == "RendererHost");

        // InfoBar severity fills are translucent: each notice sits on an opaque
        // surface so the document never shows through it.
        XElement[] surfaces = layers[0].Elements().ToArray();
        Assert.Equal(2, surfaces.Length);
        Assert.All(surfaces, surface =>
        {
            Assert.Equal("Border", surface.Name.LocalName);
            Assert.Equal("{ThemeResource AppSurfaceBrush}", (string?)surface.Attribute("Background"));
            Assert.Single(surface.Elements(), element => element.Name.LocalName == "InfoBar");
        });

        XElement remoteNotice = layers[0].Descendants().Single(element =>
            (string?)element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Name") == "RemoteImageInfoBar");
        Assert.Equal("True", (string?)remoteNotice.Attribute("IsClosable"));
        Assert.Equal("RemoteImageInfoBar_Closed", (string?)remoteNotice.Attribute("Closed"));
        // Dismissal lasting for the document, and resetting for a new document
        // or host reuse, is covered by MarkdownViewerFeedbackTests. That the Closed
        // handler forwards a close-button dismissal to the feedback owner needs a
        // live XAML runtime to observe.
    }

    [Fact]
    public void SafeHtmlDetailsUseStatefulAccessibleDisclosuresAndTelemetry()
    {
        string root = FindRepositoryRoot();
        string renderer = File.ReadAllText(Path.Combine(
            root, "MarkdownRenderer", "src", "MarkdownRenderer", "Html", "Renderers", "HtmlDetailsBuilder.cs"));
        string control = File.ReadAllText(Path.Combine(
            root, "MarkdownRenderer", "src", "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs"));
        string peer = File.ReadAllText(Path.Combine(
            root, "MarkdownRenderer", "src", "MarkdownRenderer", "Accessibility", "MarkdownLinkPeer.cs"));

        Assert.Contains("details.TryGetAttribute(\"open\"", renderer, StringComparison.Ordinal);
        Assert.Contains("context.IsDisclosureExpanded", renderer, StringComparison.Ordinal);
        Assert.Contains("if (!expanded)", renderer, StringComparison.Ordinal);
        Assert.Contains("DisclosureId = disclosureId", renderer, StringComparison.Ordinal);
        string documentInputs = File.ReadAllText(Path.Combine(
            root, "MarkdownRenderer", "src", "MarkdownRenderer", "Controls", "Pipeline", "DocumentInputs.cs"));
        string inputCapture = File.ReadAllText(Path.Combine(
            root, "MarkdownRenderer", "src", "MarkdownRenderer", "Controls", "Pipeline", "RebuildInputCapture.cs"));
        Assert.Contains("_disclosureStates.Clear()", documentInputs, StringComparison.Ordinal);
        Assert.Contains("new(_disclosureStates, StringComparer.Ordinal)", documentInputs, StringComparison.Ordinal);
        Assert.Contains("DisclosureStates = _documentInputs.CopyDisclosureStates()", inputCapture, StringComparison.Ordinal);
        string layoutSetup = File.ReadAllText(Path.Combine(
            root, "MarkdownRenderer", "src", "MarkdownRenderer", "Controls", "Pipeline", "RebuildLayoutSetup.cs"));
        Assert.Contains("DisclosureStates = inputs.DisclosureStates", layoutSetup, StringComparison.Ordinal);
        string linkActivation = File.ReadAllText(Path.Combine(
            root, "MarkdownRenderer", "src", "MarkdownRenderer", "Controls", "Input", "LinkActivation.cs"));
        Assert.Contains("TryActivateDisclosure", linkActivation, StringComparison.Ordinal);
        Assert.Contains("DisclosureToggled?.Invoke", control, StringComparison.Ordinal);
        Assert.Contains("IExpandCollapseProvider", peer, StringComparison.Ordinal);
        Assert.Contains("PatternInterface.ExpandCollapse", peer, StringComparison.Ordinal);
        Assert.Contains("AutomationControlType.Button", peer, StringComparison.Ordinal);
        // Disclosure telemetry results are covered by MarkdownViewerFeedbackTests; the
        // viewer forwarding DisclosureToggled to it needs a live XAML runtime to observe.
    }

    [Fact]
    public void LinkedMarkdownImagesAreKeyboardAndAutomationHyperlinks()
    {
        string root = FindRepositoryRoot();
        string snapshot = File.ReadAllText(Path.Combine(
            root, "MarkdownRenderer", "src", "MarkdownRenderer", "Layout", "LayoutSnapshot.cs"));
        string snapshotIndexes = File.ReadAllText(Path.Combine(
            root, "MarkdownRenderer", "src", "MarkdownRenderer", "Layout", "LayoutSnapshotIndexBuilder.cs"));
        string semanticDocument = File.ReadAllText(Path.Combine(
            root, "MarkdownRenderer", "src", "MarkdownRenderer", "Accessibility", "MarkdownSemanticDocumentBuilder.cs"));
        string automationPeer = File.ReadAllText(Path.Combine(
            root, "MarkdownRenderer", "src", "MarkdownRenderer", "Accessibility", "MarkdownAutomationPeer.cs"));
        string imagePeer = File.ReadAllText(Path.Combine(
            root, "MarkdownRenderer", "src", "MarkdownRenderer", "Accessibility", "MarkdownLinkedImagePeer.cs"));
        string linkTargets = File.ReadAllText(Path.Combine(
            root, "MarkdownRenderer", "src", "MarkdownRenderer", "Controls", "Input", "LinkTargets.cs"));

        Assert.Contains("MarkdownSemanticDocument.Build(this, matchingTextProjection, indexBuilder)", snapshot, StringComparison.Ordinal);
        Assert.Contains("_focusables = new SnapshotFocusablePlan(", snapshot, StringComparison.Ordinal);
        Assert.Contains("indexBuilder.CompleteFocusablePlan()", snapshot, StringComparison.Ordinal);
        Assert.Contains("if (run is LinkRun or InlineImageRun { IsLinked: true } or", snapshotIndexes, StringComparison.Ordinal);
        int observeInlineRun = semanticDocument.IndexOf("_layoutIndexes?.ObserveInlineRun(inline, run);", StringComparison.Ordinal);
        int decorativeImageFilter = semanticDocument.IndexOf(
            "if (run is InlineImageRun { IsLinked: false } && accessibleLength == 0)",
            StringComparison.Ordinal);
        Assert.True(observeInlineRun >= 0, "Inline layout indexes must be observed from semantic construction.");
        Assert.True(
            decorativeImageFilter > observeInlineRun,
            "The focus planner must observe linked images before decorative empty-alt runs are omitted from UIA semantics.");
        string automation = File.ReadAllText(Path.Combine(
            root, "MarkdownRenderer", "src", "MarkdownRenderer", "Controls", "RendererAutomation.cs"));
        string focusableItems = File.ReadAllText(Path.Combine(
            root, "MarkdownRenderer", "src", "MarkdownRenderer", "Controls", "Focus", "FocusableItemsPublication.cs"));
        Assert.Contains("TryGetFocusedLinkedAtomic", automation, StringComparison.Ordinal);
        Assert.Contains("case InlineImageRun { IsLinked: true, LinkUrl: { } imageUrl } image:", linkTargets, StringComparison.Ordinal);
        Assert.Contains("_owner.AutomationHost.ActivateLinkedAtomic(_run);", imagePeer, StringComparison.Ordinal);
        Assert.Contains("LinkTargets.TryCreateLinkedAtomicTarget(run, out LinkTarget target)", automation, StringComparison.Ordinal);
        Assert.Contains("InvalidateAutomationLayout();", focusableItems, StringComparison.Ordinal);
        Assert.Contains("FrameworkElementAutomationPeer.FromElement(_control)?.InvalidatePeer();", automation, StringComparison.Ordinal);
        Assert.Contains("RaiseFocusForLinkedImage", automationPeer, StringComparison.Ordinal);
        Assert.Contains("IInvokeProvider", imagePeer, StringComparison.Ordinal);
        Assert.Contains("AutomationControlType.Hyperlink", imagePeer, StringComparison.Ordinal);
        Assert.Contains("GetImageName(_run, state)", imagePeer, StringComparison.Ordinal);
        Assert.Contains("private string GetImageName(InlineImageRun run,", imagePeer, StringComparison.Ordinal);
        Assert.Contains("string.IsNullOrWhiteSpace(run.AltText)", imagePeer, StringComparison.Ordinal);
        Assert.Contains(": run.AltText;", imagePeer, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownClipboardActionsUseHostStylingAndResultTelemetry()
    {
        string root = FindRepositoryRoot();
        string control = File.ReadAllText(Path.Combine(
            root, "MarkdownRenderer", "src", "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs"));
        string selectionCommands = File.ReadAllText(Path.Combine(
            root, "MarkdownRenderer", "src", "MarkdownRenderer", "Controls", "Selection", "SelectionCommands.cs"));

        Assert.Contains("CodeBlockCopyButtonStyle", control, StringComparison.Ordinal);
        Assert.Contains("CopyCompleted?.Invoke", control, StringComparison.Ordinal);
        string codeBlockClipboard = File.ReadAllText(Path.Combine(
            root, "MarkdownRenderer", "src", "MarkdownRenderer", "Controls", "Embeds", "CodeBlockClipboard.cs"));
        Assert.Contains("MarkdownCopyKind.CodeBlock", codeBlockClipboard, StringComparison.Ordinal);
        Assert.Contains("_host.RaiseCopyCompleted(MarkdownCopyKind.Selection", selectionCommands, StringComparison.Ordinal);
        // Copy telemetry results are covered by MarkdownViewerFeedbackTests. The viewer
        // forwarding CopyCompleted and the configurator applying AppToolbarButtonStyle
        // to the copy button need a live XAML runtime to observe.
    }

    [Fact]
    public void App_ReferencesEveryShippingRendererFeatureAndShipsItsNotices()
    {
        // Which features the shared runtime composes, and how the viewer and shell
        // borrow it, is wired in code that needs the renderer feature packs and a
        // live XAML runtime; see the PR notes for how that is verified.
        string root = FindRepositoryRoot();
        string project = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "JitHub.WinUI.csproj"));

        foreach (string projectName in new[]
        {
            "MarkdownRenderer",
            "MarkdownRenderer.Math",
            "MarkdownRenderer.Mermaid",
            "MarkdownRenderer.Svg.Resvg",
            "MarkdownRenderer.SyntaxHighlighting.TextMate",
            "MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common",
        })
        {
            Assert.Contains(projectName + "\\" + projectName + ".csproj", project, StringComparison.Ordinal);
        }

        Assert.Contains("THIRD-PARTY-NOTICES.md", project, StringComparison.Ordinal);
        Assert.Contains("MPL2-SOURCE.md", project, StringComparison.Ordinal);
        Assert.Contains("NATIVE_RUST_DEPENDENCIES.json", project, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Web.WebView2\\LICENSE.txt", project, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Web.WebView2\\NOTICE.txt", project, StringComparison.Ordinal);
        Assert.Contains("Microsoft.WindowsAppSDK\\Base-LICENSE.txt", project, StringComparison.Ordinal);
        Assert.Contains("Microsoft.WindowsAppSDK\\WinUI-NOTICE.txt", project, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownViewer_CodeBackgroundsResolveThroughHighContrastAwareThemeTokens()
    {
        string root = FindRepositoryRoot();
        string rendererResources = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Styles",
            "MarkdownRenderer.xaml"));
        string colorTokens = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Styles",
            "Foundation",
            "Tokens.Colors.xaml"));

        Assert.Contains(
            "x:Key=\"MarkdownRenderer.CodeBlock.BackgroundBrush\" ResourceKey=\"AppCanvasInsetBrush\"",
            rendererResources,
            StringComparison.Ordinal);
        Assert.Contains("<ResourceDictionary x:Key=\"HighContrast\">", rendererResources, StringComparison.Ordinal);
        Assert.Contains("ResourceKey=\"AppHighContrastMonoFontFamily\"", rendererResources, StringComparison.Ordinal);
        Assert.Contains("<ResourceDictionary x:Key=\"HighContrast\">", colorTokens, StringComparison.Ordinal);
        Assert.Contains(
            "<StaticResource x:Key=\"AppCanvasInsetColor\" ResourceKey=\"SystemColorWindowColor\" />",
            colorTokens,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownViewer_UsesStableResourceKeysAndOneExplicitViewportOwner()
    {
        string root = FindRepositoryRoot();
        XDocument viewer = XDocument.Load(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownViewer.xaml"));
        string app = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "App.xaml"));

        Assert.Contains("Styles/MarkdownRenderer.xaml", app, StringComparison.Ordinal);
        Assert.DoesNotContain(viewer.Descendants(), element => element.Name.LocalName == "ScrollViewer");
        // Which renderer the viewer creates for OwnsScrollViewport, and the reset to the
        // top on a new document, need a live XAML runtime to observe. The reading-region
        // sizing rules are covered by MarkdownReadingRegionLayoutTests.

        string renderer = File.ReadAllText(Path.Combine(
            root,
            "MarkdownRenderer",
            "src",
            "MarkdownRenderer",
            "Controls",
            "Pipeline",
            "ReplacementPublisher.cs"));
        Assert.Contains("bool sameDocumentIdentity = _committedImageDocumentId == imageDocumentId", renderer, StringComparison.Ordinal);
        Assert.Contains("string? imageDocumentId = plan.Inputs.ImageDocumentSource?.DocumentId;", renderer, StringComparison.Ordinal);
        Assert.Contains("ReplacementScrollAnchor? scrollAnchor = sameDocumentIdentity", renderer, StringComparison.Ordinal);
        Assert.Contains("? ReplacementScrollAnchor.Capture(_published.Snapshot, _surface)", renderer, StringComparison.Ordinal);
        Assert.Contains("old is not null && sameDocumentIdentity &&", renderer, StringComparison.Ordinal);
        Assert.Contains("_committedImageDocumentId = imageDocumentId", renderer, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownRendererResourceBridge_UsesExistingSemanticTokensWithoutDuplicateKeys()
    {
        string root = FindRepositoryRoot();
        string stylesRoot = Path.Combine(root, "JitHub.WinUI", "Styles");
        XDocument bridge = XDocument.Load(Path.Combine(stylesRoot, "MarkdownRenderer.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement bridgeRoot = Assert.IsType<XElement>(bridge.Root);

        foreach (XElement dictionary in bridgeRoot.DescendantsAndSelf()
            .Where(element => element.Name.LocalName == "ResourceDictionary"))
        {
            string[] directKeys = dictionary.Elements()
                .Select(element => (string?)element.Attribute(x + "Key"))
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Cast<string>()
                .ToArray();
            Assert.Equal(directKeys.Length, directKeys.Distinct(StringComparer.Ordinal).Count());
        }

        string foundationTokens = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(Path.Combine(stylesRoot, "Foundation"), "Tokens.*.xaml")
                .Select(File.ReadAllText));
        XElement[] redirects = bridgeRoot.Descendants()
            .Where(element => element.Name.LocalName == "StaticResource")
            .ToArray();

        Assert.NotEmpty(redirects);
        foreach (XElement redirect in redirects)
        {
            string key = Assert.IsType<XAttribute>(redirect.Attribute(x + "Key")).Value;
            string token = Assert.IsType<XAttribute>(redirect.Attribute("ResourceKey")).Value;
            Assert.StartsWith("MarkdownRenderer.", key, StringComparison.Ordinal);
            Assert.Contains($"x:Key=\"{token}\"", foundationTokens, StringComparison.Ordinal);
        }
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
