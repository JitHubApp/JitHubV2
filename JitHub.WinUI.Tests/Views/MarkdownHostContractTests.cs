using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using JitHub.Services.Markdown;
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
    public void MarkdownViewer_DelegatesWindowsEnvironmentMonitoringAndKeepsLifecycleScaleDeterministic()
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
        Assert.DoesNotContain("TextScaleFactorChanged", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ColorValuesChanged", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AppThemeSettingsMonitor", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ThemeSettingsHelper", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ActualThemeChanged", source, StringComparison.Ordinal);
        // Harness text scale and its runtime-settings poll live in the
        // automation-only hooks; the viewer only reapplies resources on change.
        string automationRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Services", "Markdown", "Automation");
        string textScale = File.ReadAllText(Path.Combine(automationRoot, "MarkdownLifecycleTextScaleResources.cs"));
        string watch = File.ReadAllText(Path.Combine(automationRoot, "MarkdownLifecycleRuntimeSettingsWatch.cs"));
        Assert.Contains("MarkdownEnvironmentMonitor applies it", textScale, StringComparison.Ordinal);
        Assert.Contains("GetLifecycleTokenScaleFactor", textScale, StringComparison.Ordinal);
        Assert.Contains("Math.Clamp(requestedScale, 1, 3) / systemScale", textScale, StringComparison.Ordinal);
        Assert.Contains("_automation.WatchRuntimeSettings(DispatcherQueue, QueueRendererResourceRefresh)", source, StringComparison.Ordinal);
        Assert.Contains("_runtimeSettingsWatch?.Dispose()", source, StringComparison.Ordinal);
        Assert.Contains("_automation.ApplyRendererResources(resources)", source, StringComparison.Ordinal);
        Assert.Contains("MarkdownLifecycleAutomationBridge.GetRuntimeSettingsRevision()", watch, StringComparison.Ordinal);
        Assert.Contains("Timer_Tick", watch, StringComparison.Ordinal);
        Assert.Contains("_timer.Stop()", watch, StringComparison.Ordinal);
        Assert.DoesNotContain("MarkdownLifecycleAutomationBridge", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Colors.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownViewer_UsesSemanticTokensAndReportsRecoverableRenderFailures()
    {
        string root = FindRepositoryRoot();
        string viewerRoot = Path.Combine(root, "JitHub.WinUI", "Views", "Controls", "Common");
        string source = File.ReadAllText(Path.Combine(viewerRoot, "MarkdownViewer.xaml.cs"));
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
        string hostingRoot = Path.Combine(root, "JitHub.WinUI", "Services", "Markdown", "Hosting");
        string rendererResources = File.ReadAllText(Path.Combine(hostingRoot, "MarkdownRendererResources.cs"));
        string configurator = File.ReadAllText(Path.Combine(hostingRoot, "MarkdownRendererConfigurator.cs"));
        Assert.Contains("MarkdownResourceKeys.DocumentSurfaceBrush", rendererResources, StringComparison.Ordinal);
        Assert.Contains("new MarkdownStyleSelector(MarkdownStyleRole.Body, minimumNestingDepth: 1)", configurator, StringComparison.Ordinal);
        Assert.Contains("Margin = MarkdownRendererResources.ResolveThickness(\"AppMarkdownBodyMargin\"", configurator, StringComparison.Ordinal);
        Assert.DoesNotContain("_theme.Overrides", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_theme.Overrides", configurator, StringComparison.Ordinal);
        Assert.Contains("renderer.RenderCompleted += OnRendererRenderCompleted", source, StringComparison.Ordinal);
        Assert.Contains("renderer.RenderFailed += OnRendererRenderFailed", source, StringComparison.Ordinal);
        // Render-failure reporting, retry telemetry and blocked-image filtering
        // moved to MarkdownViewerFeedback and are covered by MarkdownViewerFeedbackTests.
        Assert.Contains("_feedback.OnRenderFailed(e.Exception)", source, StringComparison.Ordinal);
        Assert.Contains("_feedback.OnImageUnavailable(e.Source, e.Reason)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("e.Exception.Message", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownViewer_NoticesNeverResizeTheReadingRegion()
    {
        // An image failing below the fold opened the remote-image
        // notice in an Auto row, shrinking the README viewport by ~50 DIP
        // mid-read and shifting the document. Notices overlay the region.
        string root = FindRepositoryRoot();
        string viewerRoot = Path.Combine(root, "JitHub.WinUI", "Views", "Controls", "Common");
        XDocument xaml = XDocument.Load(Path.Combine(viewerRoot, "MarkdownViewer.xaml"));
        string source = File.ReadAllText(Path.Combine(viewerRoot, "MarkdownViewer.xaml.cs"));
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
        Assert.Contains(
            "_feedback.OnRemoteImageNoticeClosed(args.Reason == InfoBarCloseReason.CloseButton)",
            source,
            StringComparison.Ordinal);
        // Dismissal lasting for the document, and resetting for a new document
        // or host reuse, is covered by MarkdownViewerFeedbackTests.
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
        string viewer = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "Views", "Controls", "Common", "MarkdownViewer.xaml.cs"));

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
        Assert.Contains("renderer.DisclosureToggled += OnRendererDisclosureToggled", viewer, StringComparison.Ordinal);
        Assert.Contains("_feedback.OnDisclosureToggled(e.IsExpanded)", viewer, StringComparison.Ordinal);
        // Disclosure telemetry results are covered by MarkdownViewerFeedbackTests.
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
        string viewer = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "Views", "Controls", "Common", "MarkdownViewer.xaml.cs"));
        string configurator = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "Services", "Markdown", "Hosting", "MarkdownRendererConfigurator.cs"));

        Assert.Contains("CodeBlockCopyButtonStyle", control, StringComparison.Ordinal);
        Assert.Contains("CopyCompleted?.Invoke", control, StringComparison.Ordinal);
        string codeBlockClipboard = File.ReadAllText(Path.Combine(
            root, "MarkdownRenderer", "src", "MarkdownRenderer", "Controls", "Embeds", "CodeBlockClipboard.cs"));
        Assert.Contains("MarkdownCopyKind.CodeBlock", codeBlockClipboard, StringComparison.Ordinal);
        Assert.Contains("_host.RaiseCopyCompleted(MarkdownCopyKind.Selection", selectionCommands, StringComparison.Ordinal);
        Assert.Contains("renderer.CopyCompleted += OnRendererCopyCompleted", viewer, StringComparison.Ordinal);
        Assert.Contains("AppToolbarButtonStyle", configurator, StringComparison.Ordinal);
        Assert.Contains("_feedback.OnCopyCompleted(e.Kind == MarkdownCopyKind.CodeBlock, e.Succeeded)", viewer, StringComparison.Ordinal);
        // Copy telemetry results are covered by MarkdownViewerFeedbackTests.
    }

    [Fact]
    public void MarkdownViewer_DefaultsToExternalHttpsImagesAndRetainsOptInConsentMode()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownViewer.xaml.cs"));
        string configurator = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Services",
            "Markdown",
            "Hosting",
            "MarkdownRendererConfigurator.cs"));

        // Per-document consent (default policy or explicit grant) and closing a
        // stale prompt on policy promotion are covered by MarkdownViewerFeedbackTests.
        Assert.Contains("DocumentSourceProperty", source, StringComparison.Ordinal);
        Assert.Contains("renderer.ImageDocumentSource = settings.DocumentSource", configurator, StringComparison.Ordinal);
        Assert.Contains("_feedback.GrantRemoteImages()", source, StringComparison.Ordinal);
        Assert.Contains("AllowThirdPartyRemoteImagesByDefaultProperty", source, StringComparison.Ordinal);
        Assert.Contains("new PropertyMetadata(true, OnRendererPropertyChanged)", source, StringComparison.Ordinal);
        Assert.Contains("AllowThirdPartyRemoteImagesByDefault = false", source, StringComparison.Ordinal);
        Assert.Contains("_feedback.AllowsThirdPartyRemoteImages);", source, StringComparison.Ordinal);
        Assert.Contains(
            "renderer.AllowThirdPartyRemoteImages = settings.AllowThirdPartyRemoteImages;",
            configurator,
            StringComparison.Ordinal);
        Assert.Contains(
            "e.Property == AllowThirdPartyRemoteImagesByDefaultProperty",
            source,
            StringComparison.Ordinal);
        Assert.Contains("viewer._feedback.OnRemoteImagePolicyChanged();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_allowRemoteImagesForDocument", source, StringComparison.Ordinal);
    }

    [Fact]
    public void JitHubMarkdownRuntime_ComposesEveryShippingRendererFeatureOnce()
    {
        string root = FindRepositoryRoot();
        string project = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "JitHub.WinUI.csproj"));
        string runtime = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Services",
            "Markdown",
            "JitHubMarkdownRuntime.cs"));
        string viewer = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownViewer.xaml.cs"));
        string configurator = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Services",
            "Markdown",
            "Hosting",
            "MarkdownRendererConfigurator.cs"));
        string shell = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "ShellPage.xaml.cs"));
        string mainWindow = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "MainWindow.xaml.cs"));

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

        Assert.Contains(".UseGitHubReadme()", runtime, StringComparison.Ordinal);
        Assert.Contains(".UseMathematics()", runtime, StringComparison.Ordinal);
        Assert.Contains(".UseMermaid()", runtime, StringComparison.Ordinal);
        Assert.Contains("new CommonTextMateGrammarProvider()", runtime, StringComparison.Ordinal);
        Assert.Contains("ownsProvider: true", runtime, StringComparison.Ordinal);
        Assert.Contains("JitHubMarkdownRuntime.CodeHighlighter", configurator, StringComparison.Ordinal);
        Assert.Contains("JitHubMarkdownRuntime.SvgRenderer", configurator, StringComparison.Ordinal);
        Assert.Contains("renderer.RasterDecoder = JitHubMarkdownRuntime.RasterDecoder;", configurator, StringComparison.Ordinal);
        Assert.Contains("internal static IMarkdownRasterDecoder RasterDecoder", runtime, StringComparison.Ordinal);
        Assert.Contains("renderer.ImageResolver = settings.ImageResolver", configurator, StringComparison.Ordinal);
        Assert.Contains("_imageResolver,", viewer, StringComparison.Ordinal);
        Assert.Contains("renderer.AllowThirdPartyRemoteImages = settings.AllowThirdPartyRemoteImages", configurator, StringComparison.Ordinal);
        Assert.Contains("new ResvgMarkdownSvgRenderer()", runtime, StringComparison.Ordinal);
        Assert.Contains("DeferredFrameAction.Schedule", shell, StringComparison.Ordinal);
        Assert.Contains("WarmUpSvgRendererAsync", shell, StringComparison.Ordinal);
        Assert.Contains("NotifyFontsChangedAsync", mainWindow, StringComparison.Ordinal);
        Assert.Contains("WmFontChange", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Task.Run(() => ShutdownCoreAsync", runtime, StringComparison.Ordinal);
        Assert.Contains("new PropertyMetadata(true, OnRendererPropertyChanged)", viewer, StringComparison.Ordinal);
        Assert.Contains("JitHubMarkdownRuntime.ShutdownForProcessExit()", File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "App.xaml.cs")), StringComparison.Ordinal);
        Assert.Contains("await JitHubMarkdownRuntime.ShutdownAsync()", mainWindow, StringComparison.Ordinal);
        Assert.Contains("THIRD-PARTY-NOTICES.md", project, StringComparison.Ordinal);
        Assert.Contains("MPL2-SOURCE.md", project, StringComparison.Ordinal);
        Assert.Contains("NATIVE_RUST_DEPENDENCIES.json", project, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Web.WebView2\\LICENSE.txt", project, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Web.WebView2\\NOTICE.txt", project, StringComparison.Ordinal);
        Assert.Contains("Microsoft.WindowsAppSDK\\Base-LICENSE.txt", project, StringComparison.Ordinal);
        Assert.Contains("Microsoft.WindowsAppSDK\\WinUI-NOTICE.txt", project, StringComparison.Ordinal);
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
        Assert.DoesNotContain("labs:TransitionHelper.Id=\"PullRequestHeaderSurface\"", xaml, StringComparison.Ordinal);
        Assert.Equal(2, xaml.Split("labs:TransitionHelper.Id=\"PullRequestHeaderChrome\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, xaml.Split("labs:TransitionHelper.Id=\"PullRequestTitle\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, xaml.Split("labs:TransitionHelper.Id=\"PullRequestSectionSelector\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("Background=\"{ThemeResource AppTransientOverlayBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ShyHeaderStartOffset", source, StringComparison.Ordinal);
        Assert.Contains("ShyHeaderRestoreOffset", source, StringComparison.Ordinal);
        Assert.Contains("ShyHeaderRevealTravel", source, StringComparison.Ordinal);
        Assert.Contains("ShyHeaderRehideTravel", source, StringComparison.Ordinal);
        Assert.Contains("ShyHeaderScrollPolicy.CanCollapse", source, StringComparison.Ordinal);
        Assert.Contains("PullRequestSectionScrollViewer_ViewChanged", source, StringComparison.Ordinal);
        Assert.Contains("RegisterPropertyChangedCallback", source, StringComparison.Ordinal);
        Assert.Contains("FindPrimaryShyHeaderScrollViewer", source, StringComparison.Ordinal);
        Assert.Contains("AttachShyHeaderScrollViewer(primary);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (ScrollViewer candidate in scrollViewers)", source, StringComparison.Ordinal);
        Assert.Contains("new TransitionHelper", source, StringComparison.Ordinal);
        Assert.Contains("TargetToggleMethod = VisualStateToggleMethod.ByIsVisible", source, StringComparison.Ordinal);
        Assert.Contains("new TransitionConfig { Id = \"PullRequestHeaderChrome\", ScaleMode = ScaleMode.Scale, EnableClipAnimation = true }", source, StringComparison.Ordinal);
        Assert.Contains("new TransitionConfig { Id = \"PullRequestSectionSelector\", ScaleMode = ScaleMode.Scale, EnableClipAnimation = true }", source, StringComparison.Ordinal);
        Assert.Contains("_headerTransition.StartAsync", source, StringComparison.Ordinal);
        Assert.Contains("_headerTransition.ReverseAsync", source, StringComparison.Ordinal);
        Assert.Contains("forceUpdateAnimatedElements: true", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryPrepareIncomingSurface", source, StringComparison.Ordinal);
        Assert.Contains("PullRequestShyHeaderSurface.Visibility = Visibility.Collapsed", source, StringComparison.Ordinal);
        Assert.Contains("RepoPullRequestPage_Unloaded", source, StringComparison.Ordinal);
        Assert.Contains("MorphTransitionSafety.TryStop(_headerTransition)", source, StringComparison.Ordinal);
        Assert.Contains("MorphTransitionSafety.TryResetVisibilityState", source, StringComparison.Ordinal);
        Assert.Contains("ui-repo-pull-request-header-morph", source, StringComparison.Ordinal);
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
        Assert.Contains("ShyHeaderScrollPolicy.CanCollapse", source, StringComparison.Ordinal);
        Assert.DoesNotContain("labs:TransitionHelper.Id=\"IssueHeaderSurface\"", xaml, StringComparison.Ordinal);
        Assert.Equal(2, xaml.Split("labs:TransitionHelper.Id=\"IssueHeaderChrome\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, xaml.Split("labs:TransitionHelper.Id=\"IssueTitle\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("Background=\"{ThemeResource AppTransientOverlayBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("new TransitionHelper", source, StringComparison.Ordinal);
        Assert.Contains("TargetToggleMethod = VisualStateToggleMethod.ByIsVisible", source, StringComparison.Ordinal);
        Assert.Contains("new TransitionConfig { Id = \"IssueHeaderChrome\", ScaleMode = ScaleMode.Scale, EnableClipAnimation = true }", source, StringComparison.Ordinal);
        Assert.Contains("_headerTransition.StartAsync", source, StringComparison.Ordinal);
        Assert.Contains("_headerTransition.ReverseAsync", source, StringComparison.Ordinal);
        Assert.Contains("forceUpdateAnimatedElements: true", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryPrepareIncomingSurface", source, StringComparison.Ordinal);
        Assert.Contains("RepoIssuesShyHeaderSurface.Visibility = Visibility.Collapsed", source, StringComparison.Ordinal);
        Assert.Contains("RepoIssueDetailPane_Unloaded", source, StringComparison.Ordinal);
        Assert.Contains("MorphTransitionSafety.TryStop(_headerTransition)", source, StringComparison.Ordinal);
        Assert.Contains("MorphTransitionSafety.TryResetVisibilityState", source, StringComparison.Ordinal);
        Assert.Contains("ui-repo-issue-header-morph", source, StringComparison.Ordinal);
        Assert.Contains("AnimateContentReflow", source, StringComparison.Ordinal);
        Assert.Contains("TranslationTransition", source, StringComparison.Ordinal);
        Assert.Contains("RepoIssuesDetailLayout.UpdateLayout()", source, StringComparison.Ordinal);
        Assert.Contains("generation != _headerTransitionGeneration", source, StringComparison.Ordinal);
        Assert.Contains("IssueCommentForm.FocusEditor()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IssueCommentForm.EffectiveEditorHeight", source, StringComparison.Ordinal);
        Assert.DoesNotContain("JITHUB_MARKDOWN_LIFECYCLE", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShyHeaderMorphs_UseSharedMediumMotionToken()
    {
        string root = FindRepositoryRoot();
        string motionTokens = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Styles",
            "Foundation",
            "Tokens.Motion.xaml"));
        string resolver = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Helpers",
            "AppMotionTokens.cs"));
        string[] shyHeaderSources =
        [
            Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "RepoPullRequestPage.xaml.cs"),
            Path.Combine(root, "JitHub.WinUI", "Views", "Controls", "Issue", "RepoIssueDetailPane.xaml.cs"),
            Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "DashboardPage.xaml.cs"),
            Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "ShellPage.xaml.cs")
        ];

        Assert.Contains("<Duration x:Key=\"AppMediumDuration\">0:0:0.18</Duration>", motionTokens, StringComparison.Ordinal);
        Assert.Contains("MediumDurationResourceKey = \"AppMediumDuration\"", resolver, StringComparison.Ordinal);
        Assert.Contains("resources.MergedDictionaries[index]", resolver, StringComparison.Ordinal);
        Assert.Contains("directValue = resources[resourceKey]", resolver, StringComparison.Ordinal);
        Assert.Contains("TimeSpan timeSpan when timeSpan > TimeSpan.Zero", resolver, StringComparison.Ordinal);
        Assert.Contains("Duration xamlDuration when xamlDuration.HasTimeSpan", resolver, StringComparison.Ordinal);
        Assert.Contains("double seconds when double.IsFinite(seconds)", resolver, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(seconds)", resolver, StringComparison.Ordinal);
        Assert.Contains("Interlocked.CompareExchange(ref _mediumDurationTicks", resolver, StringComparison.Ordinal);

        foreach (string path in shyHeaderSources)
        {
            string source = File.ReadAllText(path);
            Assert.Contains("AppMotionTokens.MediumDuration", source, StringComparison.Ordinal);
            Assert.DoesNotContain("TimeSpan.FromMilliseconds(240)", source, StringComparison.Ordinal);
            Assert.DoesNotContain("TimeSpan.FromMilliseconds(220)", source, StringComparison.Ordinal);
        }
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
        string source = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownViewer.xaml.cs"));
        XDocument viewer = XDocument.Load(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownViewer.xaml"));
        string app = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "App.xaml"));

        Assert.Contains("Styles/MarkdownRenderer.xaml", app, StringComparison.Ordinal);
        Assert.Contains("new MarkdownDocumentView().UseGitHubReadme(SharedGitHubEngine)", source, StringComparison.Ordinal);
        Assert.Contains("new MarkdownScrollView().UseGitHubReadme(SharedGitHubEngine)", source, StringComparison.Ordinal);
        Assert.Contains("if (OwnsScrollViewport)", source, StringComparison.Ordinal);
        Assert.Contains("new PropertyMetadata(false, OnViewportOwnershipChanged)", source, StringComparison.Ordinal);
        Assert.Contains("ResetOwnedViewportForNewDocument();", source, StringComparison.Ordinal);
        Assert.Contains("oldSource?.DocumentId, newSource?.DocumentId", source, StringComparison.Ordinal);
        Assert.Contains("if (!OwnsScrollViewport || _rendererSlot.Current is null)", source, StringComparison.Ordinal);
        Assert.Contains("MarkdownRendererViewport.ScrollToTop(_rendererSlot.Current);", source, StringComparison.Ordinal);
        Assert.Contains(
            "viewport.ChangeView(null, 0, null, disableAnimation: true)",
            File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Services", "Markdown", "Hosting", "MarkdownRendererViewport.cs")),
            StringComparison.Ordinal);
        Assert.DoesNotContain(viewer.Descendants(), element => element.Name.LocalName == "ScrollViewer");
        Assert.DoesNotContain("DesiredSize", source, StringComparison.Ordinal);
        // The reading-region sizing rules are covered by MarkdownReadingRegionLayoutTests.
        Assert.Contains("MarkdownReadingRegionLayout.Compute(", source, StringComparison.Ordinal);
        Assert.Contains("RendererHost.Width = region.Width", source, StringComparison.Ordinal);
        Assert.Contains("RendererHost.HorizontalAlignment = region.HorizontalAlignment", source, StringComparison.Ordinal);

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
