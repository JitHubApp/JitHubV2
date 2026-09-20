using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Models.GitHub;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.Services.Markdown;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.ViewModels.Pages;
using MarkdownRenderer;
using MarkdownRenderer.Controls;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Images;
using MarkdownRenderer.Theming;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.ViewManagement;

namespace JitHub.WinUI.Views.Controls.Common;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class MarkdownViewer : UserControl
{
    private static readonly Uri DefaultBaseUri = new("https://github.com/", UriKind.Absolute);
    private static MarkdownEngine SharedGitHubEngine => JitHubMarkdownRuntime.Engine;

    private static readonly string[] HostSurfaceRoles =
    [
        MarkdownElementKeys.Body,
        MarkdownElementKeys.DefinitionDescription,
        MarkdownElementKeys.Figure,
        MarkdownElementKeys.FigureCaption,
        MarkdownElementKeys.AlertNote,
        MarkdownElementKeys.AlertTip,
        MarkdownElementKeys.AlertImportant,
        MarkdownElementKeys.AlertWarning,
        MarkdownElementKeys.AlertCaution,
    ];

    private static readonly string[] StyledRoles =
    [
        MarkdownElementKeys.Body,
        MarkdownElementKeys.Heading1,
        MarkdownElementKeys.Heading2,
        MarkdownElementKeys.Heading3,
        MarkdownElementKeys.Heading4,
        MarkdownElementKeys.Heading5,
        MarkdownElementKeys.Heading6,
        MarkdownElementKeys.Link,
        MarkdownElementKeys.Strong,
        MarkdownElementKeys.Emphasis,
        MarkdownElementKeys.Strikethrough,
        MarkdownElementKeys.Subscript,
        MarkdownElementKeys.Superscript,
        MarkdownElementKeys.Inserted,
        MarkdownElementKeys.Marked,
        MarkdownElementKeys.Abbreviation,
        MarkdownElementKeys.CodeInline,
        MarkdownElementKeys.CodeBlock,
        MarkdownElementKeys.CodeBlockHeader,
        MarkdownElementKeys.CodeBlockLanguage,
        MarkdownElementKeys.CodeBlockGutter,
        MarkdownElementKeys.CodeBlockLineNumber,
        MarkdownElementKeys.Quote,
        MarkdownElementKeys.ListMarker,
        MarkdownElementKeys.ThematicBreak,
        MarkdownElementKeys.ImageCaption,
        MarkdownElementKeys.DefinitionTerm,
        MarkdownElementKeys.DefinitionDescription,
        MarkdownElementKeys.Figure,
        MarkdownElementKeys.FigureCaption,
        MarkdownElementKeys.Diagram,
        MarkdownElementKeys.Math,
        MarkdownElementKeys.Table,
        MarkdownElementKeys.TableHeader,
        MarkdownElementKeys.TableCell,
        MarkdownElementKeys.AlertNote,
        MarkdownElementKeys.AlertTip,
        MarkdownElementKeys.AlertImportant,
        MarkdownElementKeys.AlertWarning,
        MarkdownElementKeys.AlertCaution,
    ];

    private static readonly (string Role, string Token, double Fallback)[] ScalableRoleFonts =
    [
        (MarkdownElementKeys.Body, "AppMarkdownBodyFontSize", 15),
        (MarkdownElementKeys.Heading1, "AppMarkdownHeading1FontSize", 30),
        (MarkdownElementKeys.Heading2, "AppMarkdownHeading2FontSize", 24),
        (MarkdownElementKeys.Heading3, "AppMarkdownHeading3FontSize", 20),
        (MarkdownElementKeys.Heading4, "AppMarkdownHeading4FontSize", 17),
        (MarkdownElementKeys.Heading5, "AppMarkdownHeading5FontSize", 15),
        (MarkdownElementKeys.Heading6, "AppMarkdownHeading6FontSize", 14),
        (MarkdownElementKeys.Link, "AppMarkdownBodyFontSize", 15),
        (MarkdownElementKeys.Strong, "AppMarkdownBodyFontSize", 15),
        (MarkdownElementKeys.Emphasis, "AppMarkdownBodyFontSize", 15),
        (MarkdownElementKeys.Strikethrough, "AppMarkdownBodyFontSize", 15),
        (MarkdownElementKeys.Subscript, "AppMarkdownScriptFontSize", 12),
        (MarkdownElementKeys.Superscript, "AppMarkdownScriptFontSize", 12),
        (MarkdownElementKeys.Inserted, "AppMarkdownBodyFontSize", 15),
        (MarkdownElementKeys.Marked, "AppMarkdownBodyFontSize", 15),
        (MarkdownElementKeys.Abbreviation, "AppMarkdownBodyFontSize", 15),
        (MarkdownElementKeys.CodeInline, "AppMarkdownCodeFontSize", 13),
        (MarkdownElementKeys.CodeBlock, "AppMarkdownCodeFontSize", 13),
        (MarkdownElementKeys.CodeBlockHeader, "AppMarkdownMetaFontSize", 13),
        (MarkdownElementKeys.CodeBlockLanguage, "AppMarkdownMetaFontSize", 13),
        (MarkdownElementKeys.CodeBlockGutter, "AppMarkdownMetaFontSize", 13),
        (MarkdownElementKeys.CodeBlockLineNumber, "AppMarkdownMetaFontSize", 13),
        (MarkdownElementKeys.Quote, "AppMarkdownBodyFontSize", 15),
        (MarkdownElementKeys.ListMarker, "AppMarkdownBodyFontSize", 15),
        (MarkdownElementKeys.ImageCaption, "AppMarkdownMetaFontSize", 13),
        (MarkdownElementKeys.DefinitionTerm, "AppMarkdownBodyFontSize", 15),
        (MarkdownElementKeys.DefinitionDescription, "AppMarkdownBodyFontSize", 15),
        (MarkdownElementKeys.Figure, "AppMarkdownBodyFontSize", 15),
        (MarkdownElementKeys.FigureCaption, "AppMarkdownMetaFontSize", 13),
        (MarkdownElementKeys.Diagram, "AppMarkdownCodeFontSize", 13),
        (MarkdownElementKeys.Math, "AppMarkdownBodyFontSize", 15),
        (MarkdownElementKeys.Table, "AppMarkdownBodyFontSize", 15),
        (MarkdownElementKeys.TableHeader, "AppMarkdownBodyFontSize", 15),
        (MarkdownElementKeys.TableCell, "AppMarkdownBodyFontSize", 15),
        (MarkdownElementKeys.AlertNote, "AppMarkdownBodyFontSize", 15),
        (MarkdownElementKeys.AlertTip, "AppMarkdownBodyFontSize", 15),
        (MarkdownElementKeys.AlertImportant, "AppMarkdownBodyFontSize", 15),
        (MarkdownElementKeys.AlertWarning, "AppMarkdownBodyFontSize", 15),
        (MarkdownElementKeys.AlertCaution, "AppMarkdownBodyFontSize", 15),
    ];

    private readonly MarkdownTheme _theme = new();
    private readonly IMarkdownImageResolver _imageResolver;
    private readonly ITelemetryService? _telemetryService;
    private readonly MarkdownRemoteContentConsent _remoteContentConsent = new();
    private readonly HashSet<MarkdownImageUnavailableReason> _reportedImageUnavailableReasons = [];
#pragma warning disable MR1001 // Common storage for the two explicit viewport control types; never constructed directly.
    private MarkdownRendererControl? _renderer;
#pragma warning restore MR1001
    private bool _isLoaded;
    private bool _rendererCreationQueued;
    private bool _paletteSubscribed;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _lifecycleRuntimeSettingsTimer;
    private int _lifecycleRuntimeSettingsRevision;
    private string? _lastAppliedMarkdown;
    private bool _renderFailureReportedForDocument;
    private bool _retryRenderPending;
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(MarkdownViewer),
        new PropertyMetadata(string.Empty, OnRendererPropertyChanged));

    public static readonly DependencyProperty BaseUrlProperty = DependencyProperty.Register(
        nameof(BaseUrl),
        typeof(string),
        typeof(MarkdownViewer),
        new PropertyMetadata(null, OnRendererPropertyChanged));

    public static readonly DependencyProperty DocumentPathProperty = DependencyProperty.Register(
        nameof(DocumentPath),
        typeof(string),
        typeof(MarkdownViewer),
        new PropertyMetadata(null, OnRendererPropertyChanged));

    public static readonly DependencyProperty ContentPaddingProperty = DependencyProperty.Register(
        nameof(ContentPadding),
        typeof(Thickness),
        typeof(MarkdownViewer),
        new PropertyMetadata(new Thickness(0), OnLayoutPropertyChanged));

    public static readonly DependencyProperty ContentMaxWidthProperty = DependencyProperty.Register(
        nameof(ContentMaxWidth),
        typeof(double),
        typeof(MarkdownViewer),
        new PropertyMetadata(double.PositiveInfinity, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ContentHorizontalAlignmentProperty = DependencyProperty.Register(
        nameof(ContentHorizontalAlignment),
        typeof(HorizontalAlignment),
        typeof(MarkdownViewer),
        new PropertyMetadata(HorizontalAlignment.Stretch, OnLayoutPropertyChanged));

    public static readonly DependencyProperty HostKindProperty = DependencyProperty.Register(
        nameof(HostKind),
        typeof(string),
        typeof(MarkdownViewer),
        new PropertyMetadata(MarkdownHostContract.Conversation, OnThemePropertyChanged));

    public static readonly DependencyProperty SurfaceColorTokenProperty = DependencyProperty.Register(
        nameof(SurfaceColorToken),
        typeof(string),
        typeof(MarkdownViewer),
        new PropertyMetadata(null, OnThemePropertyChanged));

    public static readonly DependencyProperty AutomationInstanceIdProperty = DependencyProperty.Register(
        nameof(AutomationInstanceId),
        typeof(string),
        typeof(MarkdownViewer),
        new PropertyMetadata(null, OnRendererPropertyChanged));

    public static readonly DependencyProperty DocumentSourceProperty = DependencyProperty.Register(
        nameof(DocumentSource),
        typeof(MarkdownDocumentSource),
        typeof(MarkdownViewer),
        new PropertyMetadata(null, OnDocumentSourceChanged));

    public static readonly DependencyProperty IsSelectionEnabledProperty = DependencyProperty.Register(
        nameof(IsSelectionEnabled),
        typeof(bool),
        typeof(MarkdownViewer),
        new PropertyMetadata(true, OnRendererPropertyChanged));

    public static readonly DependencyProperty IsCodeBlockCopyEnabledProperty = DependencyProperty.Register(
        nameof(IsCodeBlockCopyEnabled),
        typeof(bool),
        typeof(MarkdownViewer),
        new PropertyMetadata(true, OnRendererPropertyChanged));

    public static readonly DependencyProperty IsSyntaxHighlightingEnabledProperty = DependencyProperty.Register(
        nameof(IsSyntaxHighlightingEnabled),
        typeof(bool),
        typeof(MarkdownViewer),
        new PropertyMetadata(true, OnRendererPropertyChanged));

    public static readonly DependencyProperty AllowThirdPartyRemoteImagesByDefaultProperty = DependencyProperty.Register(
        nameof(AllowThirdPartyRemoteImagesByDefault),
        typeof(bool),
        typeof(MarkdownViewer),
        new PropertyMetadata(true, OnRendererPropertyChanged));

    public static readonly DependencyProperty OwnsScrollViewportProperty = DependencyProperty.Register(
        nameof(OwnsScrollViewport),
        typeof(bool),
        typeof(MarkdownViewer),
        new PropertyMetadata(false, OnViewportOwnershipChanged));

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? BaseUrl
    {
        get => (string?)GetValue(BaseUrlProperty);
        set => SetValue(BaseUrlProperty, value);
    }

    public string? DocumentPath
    {
        get => (string?)GetValue(DocumentPathProperty);
        set => SetValue(DocumentPathProperty, value);
    }

    public Thickness ContentPadding
    {
        get => (Thickness)GetValue(ContentPaddingProperty);
        set => SetValue(ContentPaddingProperty, value);
    }

    public double ContentMaxWidth
    {
        get => (double)GetValue(ContentMaxWidthProperty);
        set => SetValue(ContentMaxWidthProperty, value);
    }

    public HorizontalAlignment ContentHorizontalAlignment
    {
        get => (HorizontalAlignment)GetValue(ContentHorizontalAlignmentProperty);
        set => SetValue(ContentHorizontalAlignmentProperty, value);
    }

    public string HostKind
    {
        get => (string)GetValue(HostKindProperty);
        set => SetValue(HostKindProperty, value);
    }

    public string? SurfaceColorToken
    {
        get => (string?)GetValue(SurfaceColorTokenProperty);
        set => SetValue(SurfaceColorTokenProperty, value);
    }

    /// <summary>Stable identity and repository context for the logical document.</summary>
    public MarkdownDocumentSource? DocumentSource
    {
        get => (MarkdownDocumentSource?)GetValue(DocumentSourceProperty);
        set => SetValue(DocumentSourceProperty, value);
    }

    public string? AutomationInstanceId
    {
        get => (string?)GetValue(AutomationInstanceIdProperty);
        set => SetValue(AutomationInstanceIdProperty, value);
    }

    public bool IsSelectionEnabled
    {
        get => (bool)GetValue(IsSelectionEnabledProperty);
        set => SetValue(IsSelectionEnabledProperty, value);
    }

    public bool IsCodeBlockCopyEnabled
    {
        get => (bool)GetValue(IsCodeBlockCopyEnabledProperty);
        set => SetValue(IsCodeBlockCopyEnabledProperty, value);
    }

    public bool IsSyntaxHighlightingEnabled
    {
        get => (bool)GetValue(IsSyntaxHighlightingEnabledProperty);
        set => SetValue(IsSyntaxHighlightingEnabledProperty, value);
    }

    /// <summary>
    /// Gets or sets whether secure third-party HTTPS images load without asking
    /// for per-document consent. JitHub enables this because repository content
    /// commonly depends on external badges and screenshots. Setting it to false
    /// restores the privacy prompt. Legacy HTTP references are never sent over
    /// plaintext: the production resolver attempts the same origin over HTTPS
    /// and fails closed when TLS is unavailable.
    /// </summary>
    public bool AllowThirdPartyRemoteImagesByDefault
    {
        get => (bool)GetValue(AllowThirdPartyRemoteImagesByDefaultProperty);
        set => SetValue(AllowThirdPartyRemoteImagesByDefaultProperty, value);
    }

    /// <summary>
    /// Gets or sets whether this viewer owns its vertical scrolling surface.
    /// Page and conversation shells leave this false and provide the viewport;
    /// standalone previews opt in explicitly.
    /// </summary>
    public bool OwnsScrollViewport
    {
        get => (bool)GetValue(OwnsScrollViewportProperty);
        set => SetValue(OwnsScrollViewportProperty, value);
    }

    public MarkdownViewer()
    {
        InitializeComponent();

        // The production app defaults to trusted HTTPS image loading. Lifecycle
        // automation opts into the configurable privacy mode so that the prompt,
        // consent, and retry path remain covered as well.
        if (MarkdownLifecycleAutomationBridge.IsEnabled)
        {
            AllowThirdPartyRemoteImagesByDefault = false;
        }

        _telemetryService = ResolveTelemetryService();
        IMarkdownImageResolver imageResolver = ResolveImageResolver();
        _imageResolver = MarkdownLifecycleAutomationBridge.IsEnabled
            ? new MarkdownLifecycleImageResolver(imageResolver)
            : imageResolver;
        if (MarkdownLifecycleAutomationBridge.IsEvidenceEnabled)
        {
            _imageResolver = new MarkdownAuditImageResolver(_imageResolver);
        }

        Loaded += (_, _) =>
        {
            _isLoaded = true;
            SubscribeRuntimeSettings();
            UpdateHostLayout();
            EnsureRenderer();
            ApplyRendererSettings();
        };
        Unloaded += (_, _) =>
        {
            _isLoaded = false;
            UnsubscribeRuntimeSettings();
            _rendererCreationQueued = false;
            DisposeRenderer();
        };
        DataContextChanged += (_, _) =>
        {
            if (DocumentSource is null)
            {
                ResetRemoteContentConsent();
            }
        };
    }

    private static IMarkdownImageResolver ResolveImageResolver()
    {
        try
        {
            return Ioc.Default.GetService<IGitHubService>() as IMarkdownImageResolver
                ?? DenyAllMarkdownImageResolver.Instance;
        }
        catch
        {
            return DenyAllMarkdownImageResolver.Instance;
        }
    }

    private static void OnViewportOwnershipChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MarkdownViewer viewer || viewer._renderer is null)
        {
            return;
        }

        viewer.DisposeRenderer();
        viewer.QueueRendererCreation();
    }

    private static void OnRendererPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownViewer viewer)
        {
            if (e.Property == TextProperty || e.Property == BaseUrlProperty || e.Property == DocumentPathProperty)
            {
                if (viewer.DocumentSource is null)
                {
                    viewer.ResetRemoteContentConsent();
                }
            }

            if (e.Property == AllowThirdPartyRemoteImagesByDefaultProperty &&
                viewer.ShouldAllowThirdPartyRemoteImages())
            {
                // A host can promote its policy at runtime. Do not leave a stale
                // consent prompt visible while the renderer rebuilds its images.
                viewer.RemoteImageInfoBar.IsOpen = false;
            }

            viewer.ApplyRendererSettings();
        }
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownViewer viewer)
        {
            viewer.Bindings.Update();
            viewer.UpdateHostLayout();
        }
    }

    private static void OnThemePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownViewer viewer)
        {
            viewer.ApplyRendererResources();
        }
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateHostLayout();
    }

    private static ITelemetryService? ResolveTelemetryService()
    {
        try
        {
            ITelemetryService? telemetry = Ioc.Default.GetService<ITelemetryService>();
            return telemetry is null ? null : SafeTelemetryService.Wrap(telemetry);
        }
        catch
        {
            return null;
        }
    }

    private void SubscribeRuntimeSettings()
    {
        if (!_paletteSubscribed)
        {
            _paletteSubscribed = RuntimeEventSubscription.TrySubscribe(
                () => ThemePaletteRuntime.PaletteChanged += ThemePaletteRuntime_PaletteChanged,
                nameof(ThemePaletteRuntime.PaletteChanged));
        }
        if (MarkdownLifecycleAutomationBridge.IsEnabled && _lifecycleRuntimeSettingsTimer is null)
        {
            _lifecycleRuntimeSettingsRevision = MarkdownLifecycleAutomationBridge.GetRuntimeSettingsRevision();
            _lifecycleRuntimeSettingsTimer = DispatcherQueue.CreateTimer();
            _lifecycleRuntimeSettingsTimer.Interval = TimeSpan.FromMilliseconds(100);
            _lifecycleRuntimeSettingsTimer.IsRepeating = true;
            _lifecycleRuntimeSettingsTimer.Tick += LifecycleRuntimeSettingsTimer_Tick;
            _lifecycleRuntimeSettingsTimer.Start();
        }
    }

    private void UnsubscribeRuntimeSettings()
    {
        RuntimeEventSubscription.TryUnsubscribe(
            () => ThemePaletteRuntime.PaletteChanged -= ThemePaletteRuntime_PaletteChanged,
            _paletteSubscribed,
            nameof(ThemePaletteRuntime.PaletteChanged));

        _paletteSubscribed = false;
        if (_lifecycleRuntimeSettingsTimer is not null)
        {
            _lifecycleRuntimeSettingsTimer.Stop();
            _lifecycleRuntimeSettingsTimer.Tick -= LifecycleRuntimeSettingsTimer_Tick;
            _lifecycleRuntimeSettingsTimer = null;
        }
    }

    private void LifecycleRuntimeSettingsTimer_Tick(
        Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
        object args)
    {
        int revision = MarkdownLifecycleAutomationBridge.GetRuntimeSettingsRevision();
        if (revision <= 0 || revision == _lifecycleRuntimeSettingsRevision)
        {
            return;
        }

        _lifecycleRuntimeSettingsRevision = revision;
        QueueRendererResourceRefresh();
    }

    private void ThemePaletteRuntime_PaletteChanged(
        object? sender,
        ThemePaletteChangedEventArgs args) =>
        QueueRendererResourceRefresh();

    private void QueueRendererResourceRefresh()
    {
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = DispatcherQueue;
        if (dispatcher is null)
        {
            return;
        }

        void Refresh()
        {
            if (!_isLoaded)
            {
                return;
            }

            ApplyRendererResources();
        }

        if (dispatcher.HasThreadAccess)
        {
            Refresh();
        }
        else
        {
            dispatcher.TryEnqueue(Refresh);
        }
    }

    private static void OnDocumentSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MarkdownViewer viewer)
        {
            return;
        }

        viewer._remoteContentConsent.Activate(e.NewValue as MarkdownDocumentSource);
        viewer._reportedImageUnavailableReasons.Clear();
        viewer._renderFailureReportedForDocument = false;
        viewer._retryRenderPending = false;
        if (viewer.RemoteImageInfoBar is not null)
        {
            viewer.RemoteImageInfoBar.IsOpen = false;
        }
        if (viewer.RenderErrorInfoBar is not null)
        {
            viewer.RenderErrorInfoBar.IsOpen = false;
        }
        viewer.ApplyRendererSettings();
    }

    private void ResetRemoteContentConsent()
    {
        _remoteContentConsent.ResetForHostReuse();
        _reportedImageUnavailableReasons.Clear();
        _renderFailureReportedForDocument = false;
        _retryRenderPending = false;
        if (RemoteImageInfoBar is not null)
        {
            RemoteImageInfoBar.IsOpen = false;
        }
        if (RenderErrorInfoBar is not null)
        {
            RenderErrorInfoBar.IsOpen = false;
        }
    }

    private void QueueRendererCreation()
    {
        if (_renderer is not null || _rendererCreationQueued || !_isLoaded)
        {
            return;
        }

        _rendererCreationQueued = true;
        var dispatcher = DispatcherQueue;
        if (dispatcher is null ||
            !dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _rendererCreationQueued = false;
                if (!_isLoaded)
                {
                    return;
                }

                EnsureRenderer();
            }))
        {
            _rendererCreationQueued = false;
            EnsureRenderer();
        }
    }

    private void EnsureRenderer()
    {
        if (_renderer is not null)
        {
            if (!RendererHost.Children.Contains(_renderer))
            {
                RendererHost.Children.Add(_renderer);
            }

            return;
        }

        if (OwnsScrollViewport)
        {
            _renderer = new MarkdownScrollView().UseGitHubReadme(SharedGitHubEngine);
        }
        else
        {
            _renderer = new MarkdownDocumentView().UseGitHubReadme(SharedGitHubEngine);
        }
        _renderer.Theme = _theme;
        _renderer.StringProvider = JitHubMarkdownStringProvider.Instance;
        ApplyRendererResources();
        _renderer.IsSelectionEnabled = IsSelectionEnabled;
        _renderer.IsCodeBlockCopyEnabled = IsCodeBlockCopyEnabled;
        _renderer.ImageResolver = _imageResolver;
        _renderer.SvgRenderer = JitHubMarkdownRuntime.SvgRenderer;
        _renderer.ImageBaseUri = GetBaseUri();
        _renderer.ImageDocumentPath = DocumentPath;
        _renderer.ImageDocumentSource = DocumentSource;
        _renderer.AllowThirdPartyRemoteImages = ShouldAllowThirdPartyRemoteImages();
        _renderer.LinkClick += OnRendererLinkClick;
        _renderer.DisclosureToggled += OnRendererDisclosureToggled;
        _renderer.CopyCompleted += OnRendererCopyCompleted;
        _renderer.ImageUnavailable += OnRendererImageUnavailable;
        _renderer.RenderCompleted += OnRendererRenderCompleted;
        _renderer.RenderFailed += OnRendererRenderFailed;
        string automationId = MarkdownHostContract.GetAutomationId(HostKind, AutomationInstanceId);
        AutomationProperties.SetName(_renderer, MarkdownHostContract.GetAutomationName(HostKind));
        AutomationProperties.SetAutomationId(_renderer, automationId);
        MarkdownLifecycleAutomationBridge.SignalHostReady(automationId);
        _renderer.HorizontalAlignment = HorizontalAlignment.Stretch;
        _renderer.VerticalAlignment = VerticalAlignment.Stretch;
        RendererHost.Children.Add(_renderer);
        ApplyRendererSettings();
    }

    private void DisposeRenderer()
    {
        if (_renderer is null)
        {
            return;
        }

        _renderer.LinkClick -= OnRendererLinkClick;
        _renderer.DisclosureToggled -= OnRendererDisclosureToggled;
        _renderer.CopyCompleted -= OnRendererCopyCompleted;
        _renderer.ImageUnavailable -= OnRendererImageUnavailable;
        _renderer.RenderCompleted -= OnRendererRenderCompleted;
        _renderer.RenderFailed -= OnRendererRenderFailed;
        RendererHost.Children.Remove(_renderer);
        _renderer.Dispose();
        _renderer = null;
    }

    private void UpdateHostLayout()
    {
        double availableWidth = ActualWidth - ContentPadding.Left - ContentPadding.Right;
        if (availableWidth <= 0 || double.IsNaN(availableWidth) || double.IsInfinity(availableWidth))
        {
            RendererHost.Width = double.NaN;
            RendererHost.MaxWidth = double.PositiveInfinity;
            RendererHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            return;
        }

        bool hasFiniteMaxWidth = ContentMaxWidth > 0
            && !double.IsNaN(ContentMaxWidth)
            && !double.IsInfinity(ContentMaxWidth);

        if (hasFiniteMaxWidth && availableWidth > ContentMaxWidth)
        {
            RendererHost.Width = ContentMaxWidth;
            RendererHost.MaxWidth = ContentMaxWidth;
            RendererHost.HorizontalAlignment = ContentHorizontalAlignment == HorizontalAlignment.Stretch
                ? HorizontalAlignment.Center
                : ContentHorizontalAlignment;
            return;
        }

        RendererHost.Width = double.NaN;
        RendererHost.MaxWidth = hasFiniteMaxWidth ? ContentMaxWidth : double.PositiveInfinity;
        RendererHost.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    private void ApplyRendererSettings()
    {
        if (_renderer is null)
        {
            QueueRendererCreation();
            return;
        }

        string markdown = GetEffectiveMarkdown();
        if (!string.Equals(_lastAppliedMarkdown, markdown, StringComparison.Ordinal))
        {
            _lastAppliedMarkdown = markdown;
            _renderFailureReportedForDocument = false;
            _retryRenderPending = false;
            RenderErrorInfoBar.IsOpen = false;
        }

        _renderer.Markdown = markdown;
        _renderer.IsSelectionEnabled = IsSelectionEnabled;
        _renderer.IsCodeBlockCopyEnabled = IsCodeBlockCopyEnabled;
        _renderer.CodeBlockCopyButtonStyle = TryResolveResource("AppToolbarButtonStyle", out object? copyStyle)
            ? copyStyle as Style
            : null;
        _renderer.CodeHighlighter = IsSyntaxHighlightingEnabled
            ? JitHubMarkdownRuntime.CodeHighlighter
            : null;
        _renderer.IsCodeBlockSyntaxHighlightingEnabled = IsSyntaxHighlightingEnabled;

        _renderer.ImageResolver = _imageResolver;
        _renderer.SvgRenderer = JitHubMarkdownRuntime.SvgRenderer;
        _renderer.ImageBaseUri = GetBaseUri();
        _renderer.ImageDocumentPath = DocumentPath;
        _renderer.ImageDocumentSource = DocumentSource;
        _renderer.AllowThirdPartyRemoteImages = ShouldAllowThirdPartyRemoteImages();
        AutomationProperties.SetName(_renderer, MarkdownHostContract.GetAutomationName(HostKind));
        AutomationProperties.SetAutomationId(_renderer, MarkdownHostContract.GetAutomationId(HostKind, AutomationInstanceId));
    }

    private string GetEffectiveMarkdown()
    {
        string markdown = Text ?? string.Empty;
        if (!MarkdownLifecycleAutomationBridge.IsEnabled)
        {
            return markdown;
        }

        string? targetHost = MarkdownLifecycleAutomationBridge.TargetHost;
        if (!string.IsNullOrWhiteSpace(targetHost) &&
            !MarkdownHostContract.GetAutomationId(HostKind, AutomationInstanceId)
                .StartsWith(targetHost, StringComparison.Ordinal))
        {
            return markdown;
        }

        if (TryReadAutomationCorpus(out string corpus))
        {
            return corpus;
        }

        const string marker = "Markdown host lifecycle fixture";
        if (markdown.Contains(marker, StringComparison.Ordinal))
        {
            return markdown;
        }

        string fixture = BuildLifecycleFixtureMarkdown();
        if (string.Equals(
                Environment.GetEnvironmentVariable("JITHUB_MARKDOWN_SECURITY_LIVE_FIXTURE"),
                "1",
                StringComparison.Ordinal))
        {
            fixture += SecurityLifecycleFixture.Value;
        }

        return fixture + markdown;
    }

    private static bool TryReadAutomationCorpus(out string corpus)
    {
        corpus = string.Empty;
        string? path = Program.CurrentLaunchOptions.MarkdownCorpusPath ??
            Environment.GetEnvironmentVariable("JITHUB_MARKDOWN_CORPUS_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            string extension = Path.GetExtension(fullPath);
            if (!extension.Equals(".md", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            FileInfo file = new(fullPath);
            const int maxCorpusBytes = 4 * 1024 * 1024;
            if (!file.Exists || file.Length is <= 0 or > maxCorpusBytes)
            {
                return false;
            }

            corpus = File.ReadAllText(fullPath, Encoding.UTF8);
            return corpus.Length > 0;
        }
        catch (Exception)
        {
            corpus = string.Empty;
            return false;
        }
    }

    private static readonly Lazy<string> SecurityLifecycleFixture = new(BuildSecurityLifecycleFixtureMarkdown);

    private static string BuildLifecycleFixtureMarkdown()
    {
        StringBuilder fixture = new("""


            ---

            # Markdown audit selection marker

            Markdown host lifecycle fixture covers the canonical host and includes a [keyboard link](https://github.com/JitHubApp/JitHubV2).

            Route checks: [internal repository route](https://github.com/JitHubApp/JitHubV2),
            [internal user route](https://github.com/JitHubApp), and
            [external browser route](https://example.com/jithub-markdown-audit).

            Markdown audit pointer selection starts here on the first line.
            Markdown audit pointer selection ends here on the second line.

            - [x] Lifecycle task list complete
            - [ ] Lifecycle task list pending

            | Feature | State |
            | --- | --- |
            | Selection | Ready |
            | Images | Protected |

            > Lifecycle quote level one
            >> Lifecycle quote level two
            >>> Lifecycle quote level three

            ```csharp
            public static string LifecycleCode() => "ready";
            ```

            Relative image fixture: ![Lifecycle relative image](docs/images/lifecycle-relative.png)

            Malformed inline HTML remains contained: `<svg><text>unfinished`

            ![Lifecycle malformed SVG](data:image/svg+xml;utf8,%3Csvg%3E%3Ctext%3Eunfinished)

            ![Lifecycle inline SVG](data:image/svg+xml;utf8,%3Csvg%20xmlns%3D%27http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%27%20width%3D%2732%27%20height%3D%2732%27%20viewBox%3D%270%200%2032%2032%27%3E%3Crect%20width%3D%2732%27%20height%3D%2732%27%20fill%3D%27%2377B59A%27%2F%3E%3C%2Fsvg%3E)

            ![Lifecycle animated image](data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH/C05FVFNDQVBFMi4wAwEAAAAh+QQFCgAAACwAAAAAAQABAAACAkQBADs=)

            ![Lifecycle blocked remote image](https://example.invalid/jithub-markdown-lifecycle.png)

            """);
        for (int index = 1; index <= 60; index++)
        {
            fixture.AppendLine($"Lifecycle long document paragraph {index}: stable scrolling, selection, and reading order.");
            fixture.AppendLine();
        }

        fixture.AppendLine("Lifecycle long document final marker.");
        return fixture.ToString();
    }

    private static string BuildSecurityLifecycleFixtureMarkdown()
    {
        const int oversizedSvgBytes = (2 * 1024 * 1024) + 1;
        string hostileSvg = "<svg xmlns='http://www.w3.org/2000/svg'><text font-size='999999'>blocked</text></svg>";
        string deepSvg = "<svg xmlns='http://www.w3.org/2000/svg'>" +
            string.Concat(Enumerable.Repeat("<g>", 70)) +
            "<rect width='1' height='1'/>" +
            string.Concat(Enumerable.Repeat("</g>", 70)) +
            "</svg>";
        string oversizedSvg = "<svg xmlns='http://www.w3.org/2000/svg'>" +
            new string(' ', oversizedSvgBytes) +
            "</svg>";

        static string SvgDataUri(string svg) =>
            "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));

        return $"""

            ## Security resource-budget fixture

            ![Lifecycle hostile font SVG]({SvgDataUri(hostileSvg)})

            ![Lifecycle hostile depth SVG]({SvgDataUri(deepSvg)})

            ![Lifecycle oversized SVG]({SvgDataUri(oversizedSvg)})

            ![Lifecycle insecure remote image](http://example.invalid/insecure.png)

            ![Lifecycle redirect-policy remote image](https://example.invalid/redirect.png)

            Lifecycle security fixture final marker.
            """;
    }

    private void OnRendererImageUnavailable(object? sender, MarkdownImageUnavailableEventArgs e)
    {
        if (e.Reason == MarkdownImageUnavailableReason.RemoteContentBlocked &&
            ShouldAllowThirdPartyRemoteImages())
        {
            return;
        }

        MarkdownLifecycleAutomationBridge.RecordImageUnavailable(
            MarkdownHostContract.GetAutomationId(HostKind, AutomationInstanceId),
            e.Source,
            e.Reason);

        if (_reportedImageUnavailableReasons.Add(e.Reason))
        {
            TrackMarkdownEvent(
                "markdown.resource.unavailable",
                action: null,
                result: e.Reason switch
                {
                    MarkdownImageUnavailableReason.RemoteContentBlocked or
                    MarkdownImageUnavailableReason.InsecureRemoteContent => TelemetryTaxonomy.Results.Rejected,
                    MarkdownImageUnavailableReason.Offline or
                    MarkdownImageUnavailableReason.MeteredConnection => TelemetryTaxonomy.Results.Deferred,
                    _ => TelemetryTaxonomy.Results.Unavailable,
                },
                resource: "remote_image",
                policy: e.Reason switch
                {
                    MarkdownImageUnavailableReason.Offline or
                    MarkdownImageUnavailableReason.MeteredConnection => "deferred",
                    MarkdownImageUnavailableReason.RemoteContentBlocked or
                    MarkdownImageUnavailableReason.InsecureRemoteContent => "suppressed",
                    _ => null,
                });
        }

        if (e.Reason is not (MarkdownImageUnavailableReason.RemoteContentBlocked or
            MarkdownImageUnavailableReason.InsecureRemoteContent or
            MarkdownImageUnavailableReason.Offline or
            MarkdownImageUnavailableReason.MeteredConnection))
        {
            return;
        }

        bool compactNotice = MarkdownHostContract.Parse(HostKind) == MarkdownHostKind.EditorPreview;
        string message = e.Reason switch
        {
            MarkdownImageUnavailableReason.InsecureRemoteContent =>
                LocalizedResourceText.GetString(
                    "Markdown.RemoteImage.InsecureMessage",
                    "An image used insecure HTTP and cannot be loaded."),
            MarkdownImageUnavailableReason.Offline =>
                LocalizedResourceText.GetString(
                    "Markdown.RemoteImage.OfflineMessage",
                    "An image is not cached and cannot be loaded while offline."),
            MarkdownImageUnavailableReason.MeteredConnection =>
                LocalizedResourceText.GetString(
                    "Markdown.RemoteImage.MeteredMessage",
                    "Automatic image loading is paused on this metered connection."),
            _ => LocalizedResourceText.GetString(
                "Markdown.RemoteImage.PrivacyMessage",
                "External images can reveal your IP address and request timing to another site."),
        };
        RemoteImageInfoBar.Title = compactNotice
            ? e.Reason switch
            {
                MarkdownImageUnavailableReason.Offline => LocalizedResourceText.GetString(
                    "Markdown.RemoteImage.OfflineCompactTitle",
                    "Images unavailable offline"),
                MarkdownImageUnavailableReason.MeteredConnection => LocalizedResourceText.GetString(
                    "Markdown.RemoteImage.MeteredCompactTitle",
                    "Images paused on this connection"),
                MarkdownImageUnavailableReason.InsecureRemoteContent => LocalizedResourceText.GetString(
                    "Markdown.RemoteImage.InsecureCompactTitle",
                    "Insecure image blocked"),
                _ => LocalizedResourceText.GetString(
                    "Markdown.RemoteImage.BlockedCompactTitle",
                    "External images blocked"),
            }
            : LocalizedResourceText.GetString(
                "Markdown.RemoteImage.ProtectedTitle",
                "Remote images are protected");
        RemoteImageInfoBar.Message = compactNotice ? string.Empty : message;
        LoadRemoteImagesButton.Visibility = e.Reason is MarkdownImageUnavailableReason.RemoteContentBlocked or
            MarkdownImageUnavailableReason.MeteredConnection
            ? Visibility.Visible
            : Visibility.Collapsed;
        RemoteImageInfoBar.IsOpen = true;
    }

    private void OnRendererRenderCompleted(object? sender, EventArgs e)
    {
        MarkdownLifecycleAutomationBridge.RecordRenderComplete(
            MarkdownHostContract.GetAutomationId(HostKind, AutomationInstanceId));
        RenderErrorInfoBar.IsOpen = false;
        if (!_retryRenderPending)
        {
            return;
        }

        _retryRenderPending = false;
        TrackMarkdownEvent(
            "markdown.action.executed",
            TelemetryTaxonomy.Actions.Retry,
            TelemetryTaxonomy.Results.Success);
    }

    private void OnRendererDisclosureToggled(object? sender, MarkdownDisclosureToggledEventArgs e) =>
        TrackMarkdownEvent(
            "markdown.action.executed",
            TelemetryTaxonomy.Actions.ToggleDetails,
            e.IsExpanded ? TelemetryTaxonomy.Results.Expanded : TelemetryTaxonomy.Results.Collapsed);

    private void OnRendererCopyCompleted(object? sender, MarkdownCopyCompletedEventArgs e) =>
        TrackMarkdownEvent(
            "markdown.action.executed",
            e.Kind == MarkdownCopyKind.CodeBlock
                ? TelemetryTaxonomy.Actions.CopyCode
                : TelemetryTaxonomy.Actions.CopySelection,
            e.Succeeded ? TelemetryTaxonomy.Results.Success : TelemetryTaxonomy.Results.Error);

    private void OnRendererRenderFailed(object? sender, MarkdownRenderFailedEventArgs e)
    {
        MarkdownLifecycleAutomationBridge.RecordRenderFailure(
            MarkdownHostContract.GetAutomationId(HostKind, AutomationInstanceId),
            e.Exception);
        RenderErrorInfoBar.IsOpen = true;
        bool wasRetry = _retryRenderPending;
        _retryRenderPending = false;
        if (_renderFailureReportedForDocument && !wasRetry)
        {
            return;
        }

        _renderFailureReportedForDocument = true;
        TrackMarkdownEvent(
            "markdown.error",
            wasRetry ? TelemetryTaxonomy.Actions.Retry : TelemetryTaxonomy.Actions.Render,
            TelemetryTaxonomy.Results.Failed,
            errorKind: TelemetryTaxonomy.ErrorKinds.Unexpected,
            source: "background");
    }

    private void RetryRenderButton_Click(object sender, RoutedEventArgs e)
    {
        RenderErrorInfoBar.IsOpen = false;
        _retryRenderPending = true;
        TrackMarkdownEvent(
            "markdown.action.executed",
            TelemetryTaxonomy.Actions.Retry,
            TelemetryTaxonomy.Results.Queued);
        _renderer?.RequestRebuild();
    }

    private void LoadRemoteImagesButton_Click(object sender, RoutedEventArgs e)
    {
        _remoteContentConsent.Grant();
        RemoteImageInfoBar.IsOpen = false;
        if (_renderer is not null)
        {
            _renderer.AllowThirdPartyRemoteImages = true;
            _renderer.RequestRebuild();
        }

        TrackMarkdownEvent(
            "markdown.action.executed",
            TelemetryTaxonomy.Actions.LoadRemoteImages,
            "allowed",
            resource: "remote_image");
    }

    private bool ShouldAllowThirdPartyRemoteImages() =>
        AllowThirdPartyRemoteImagesByDefault || _remoteContentConsent.IsGranted;

    private Uri GetBaseUri()
    {
        return Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri? baseUri)
            ? baseUri
            : DefaultBaseUri;
    }

    private void OnRendererLinkClick(object? sender, MarkdownLinkClickEventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            if (!TryCreateLaunchUri(e.Url, out Uri? uri, out bool mayNavigateInternally) || uri is null)
            {
                TrackMarkdownEvent("markdown.action.executed", TelemetryTaxonomy.Actions.OpenLink, TelemetryTaxonomy.Results.Rejected, resource: "link");
                return;
            }

            MarkdownGitHubRoute route = MarkdownLinkNavigationPolicy.ClassifyGitHubRoute(uri);
            string disposition = mayNavigateInternally && route.Kind is (MarkdownGitHubRouteKind.User or MarkdownGitHubRouteKind.Repository or MarkdownGitHubRouteKind.Issue or MarkdownGitHubRouteKind.PullRequest) ? route.Kind.ToString().ToLowerInvariant() : "external-browser";
            string automationId = MarkdownHostContract.GetAutomationId(HostKind, AutomationInstanceId);
            if (MarkdownLifecycleAutomationBridge.RecordLinkRoute(automationId, uri, disposition))
            {
                TrackMarkdownEvent("markdown.action.executed", TelemetryTaxonomy.Actions.OpenLink, TelemetryTaxonomy.Results.Success, resource: "link");
                return;
            }

            if (mayNavigateInternally && TryOpenInternalGitHubRoute(uri))
            {
                TrackMarkdownEvent("markdown.action.executed", TelemetryTaxonomy.Actions.OpenLink, TelemetryTaxonomy.Results.Success, resource: "link");
                return;
            }

            try
            {
                bool launched = await Launcher.LaunchUriAsync(uri);
                TrackMarkdownEvent("markdown.action.executed", TelemetryTaxonomy.Actions.OpenLink, launched ? TelemetryTaxonomy.Results.Launched : TelemetryTaxonomy.Results.Failed, resource: "link");
            }
            catch (Exception)
            {
                TrackMarkdownEvent("markdown.error", TelemetryTaxonomy.Actions.OpenLink, TelemetryTaxonomy.Results.Failed, resource: "link", errorKind: TelemetryTaxonomy.ErrorKinds.Launch);
            }
        }, "ui-markdown-viewer");
    }

    private void TrackMarkdownEvent(
        string eventName,
        string? action,
        string result,
        string? resource = null,
        string? policy = null,
        string? errorKind = null,
        string source = TelemetryTaxonomy.Sources.User)
    {
        if (_telemetryService is null)
        {
            return;
        }

        var properties = new Dictionary<string, string?>
        {
            ["feature"] = "markdown",
            ["section"] = MarkdownHostContract.GetTelemetrySection(HostKind),
            ["source"] = source,
            ["action"] = action,
            ["result"] = result,
            ["resource"] = resource,
            ["policy"] = policy,
            ["error_kind"] = errorKind,
        };
        _telemetryService.TrackEvent(eventName, properties);
    }

    private static bool TryOpenInternalGitHubRoute(Uri uri)
    {
        MarkdownGitHubRoute route = MarkdownLinkNavigationPolicy.ClassifyGitHubRoute(uri);
        if (route.Kind is not (
            MarkdownGitHubRouteKind.User or
            MarkdownGitHubRouteKind.Repository or
            MarkdownGitHubRouteKind.Issue or
            MarkdownGitHubRouteKind.PullRequest))
        {
            return false;
        }

        ShellPageViewModel? shell = Ioc.Default.GetService<ShellPageViewModel>();
        if (shell is null)
        {
            return false;
        }

        ShellWorkspaceTabIdentity expectedRoute;
        if (route.Kind == MarkdownGitHubRouteKind.User)
        {
            expectedRoute = ShellWorkspaceTabIdentity.Profile(route.Owner!);
            shell.OpenUserProfile(route.Owner!, "markdown");
        }
        else if (route.Kind == MarkdownGitHubRouteKind.Repository)
        {
            expectedRoute = ShellWorkspaceTabIdentity.Repository(
                $"{route.Owner}/{route.Repository}",
                RepoPageType.CodePage,
                "main");
            shell.OpenRepositoryPage($"{route.Owner}/{route.Repository}", "code", null);
        }
        else
        {
            GitHubRepository repository = new()
            {
                FullName = $"{route.Owner}/{route.Repository}",
                Name = route.Repository!,
                DefaultBranch = "main",
                Owner = new GitHubRepositoryOwner { Login = route.Owner! }
            };
            if (route.Kind == MarkdownGitHubRouteKind.Issue)
            {
                expectedRoute = ShellWorkspaceTabIdentity.Repository(
                    repository,
                    RepoPageType.IssuePage,
                    repository.DefaultBranch);
                shell.OpenRepositoryTarget(
                    repository,
                    RepoPageType.IssuePage,
                    new IssueNavArg(repository, route.Number ?? 0));
            }
            else
            {
                expectedRoute = ShellWorkspaceTabIdentity.Repository(
                    repository,
                    RepoPageType.PullRequestPage,
                    repository.DefaultBranch);
                shell.OpenRepositoryTarget(
                    repository,
                    RepoPageType.PullRequestPage,
                    new PullRequestPageNavArg(repository, route.Number ?? 0));
            }
        }

        return shell.IsCurrentRoute(expectedRoute);
    }

    private bool TryCreateLaunchUri(
        string? url,
        out Uri? uri,
        out bool mayNavigateInternally)
        => MarkdownLinkNavigationPolicy.TryResolveLaunchUri(
            url,
            GetBaseUri(),
            DocumentSource,
            out uri,
            out mayNavigateInternally);

    private void ApplyRendererResources()
    {
        if (_renderer is null)
        {
            return;
        }

        ResourceDictionary resources = _renderer.Resources;
        RemoveLocalRendererResources(resources);

        if (TryResolveHostSurfaceBrush(out SolidColorBrush? hostSurface))
        {
            resources[MarkdownResourceKeys.DocumentSurfaceBrush] = hostSurface;
            foreach (string role in HostSurfaceRoles)
            {
                resources[MarkdownResourceKeys.ForRole(
                    MarkdownStyleRole.FromElementKey(role),
                    MarkdownStyleProperty.BackgroundBrush)] = hostSurface;
            }
        }

        if (MarkdownLifecycleAutomationBridge.GetTextScaleFactor() is double requestedScale)
        {
            double tokenScale = GetLifecycleTokenScaleFactor(requestedScale);
            foreach ((string role, string token, double fallback) in ScalableRoleFonts)
            {
                resources[MarkdownResourceKeys.ForRole(
                    MarkdownStyleRole.FromElementKey(role),
                    MarkdownStyleProperty.FontSize)] =
                    ResolveDouble(token, fallback) * tokenScale;
            }
        }

        if (MarkdownLifecycleAutomationBridge.IsHighContrastEnabled)
        {
            ApplyLifecycleHighContrastResources(resources);
        }

        // The renderer recompiles one immutable style snapshot. Its shared
        // environment monitor remains the sole owner of real theme, accent,
        // High Contrast, text scale, language, flow direction, and DPI events.
        _theme.Invalidate();
    }

    private static void RemoveLocalRendererResources(ResourceDictionary resources)
    {
        resources.Remove(MarkdownResourceKeys.DocumentSurfaceBrush);
        resources.Remove(MarkdownResourceKeys.SelectionBackgroundBrush);
        resources.Remove(MarkdownResourceKeys.SelectionForegroundBrush);
        resources.Remove(MarkdownResourceKeys.FocusVisualBrush);
        resources.Remove(MarkdownResourceKeys.OverflowIndicatorBrush);

        foreach (string roleName in StyledRoles)
        {
            MarkdownStyleRole role = MarkdownStyleRole.FromElementKey(roleName);
            resources.Remove(MarkdownResourceKeys.ForRole(role, MarkdownStyleProperty.ForegroundBrush));
            resources.Remove(MarkdownResourceKeys.ForRole(role, MarkdownStyleProperty.HoverForegroundBrush));
            resources.Remove(MarkdownResourceKeys.ForRole(role, MarkdownStyleProperty.FocusForegroundBrush));
            resources.Remove(MarkdownResourceKeys.ForRole(role, MarkdownStyleProperty.BackgroundBrush));
            resources.Remove(MarkdownResourceKeys.ForRole(role, MarkdownStyleProperty.AccentBrush));
            resources.Remove(MarkdownResourceKeys.ForRole(role, MarkdownStyleProperty.BorderBrush));
            resources.Remove(MarkdownResourceKeys.ForRole(role, MarkdownStyleProperty.FontFamily));
            resources.Remove(MarkdownResourceKeys.ForRole(role, MarkdownStyleProperty.FontSize));
        }
    }

    private bool TryResolveHostSurfaceBrush(out SolidColorBrush? brush)
    {
        string token = string.IsNullOrWhiteSpace(SurfaceColorToken)
            ? MarkdownHostContract.GetSurfaceColorToken(HostKind)
            : SurfaceColorToken.Trim();

        if (TryResolveResource(token + "Brush", out object? value) &&
            value is SolidColorBrush resolvedBrush)
        {
            brush = resolvedBrush;
            return true;
        }

        if (TryResolveResource(token + "Color", out value) &&
            value is Windows.UI.Color color)
        {
            brush = new SolidColorBrush(color);
            return true;
        }

        brush = null;
        return false;
    }

    private static double GetLifecycleTokenScaleFactor(double requestedScale)
    {
        // The lifecycle fixture asks for a final effective scale. Divide out
        // the real Windows scale because MarkdownEnvironmentMonitor applies it
        // after resolving these test-only font resources.
        double systemScale = 1;
        UISettings? settings = RuntimeEventSubscription.TryCreate(
            static () => new UISettings(),
            nameof(UISettings));
        try
        {
            double observedScale = settings?.TextScaleFactor ?? 1;
            systemScale = double.IsFinite(observedScale) && observedScale > 0
                ? Math.Clamp(observedScale, 1, 3)
                : 1;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            systemScale = 1;
        }

        return Math.Clamp(requestedScale, 1, 3) / systemScale;
    }

    private static void ApplyLifecycleHighContrastResources(ResourceDictionary resources)
    {
        SolidColorBrush window = new(Colors.Black);
        SolidColorBrush text = new(Colors.White);
        SolidColorBrush link = new(Colors.Yellow);
        SolidColorBrush focus = new(Colors.Cyan);

        resources[MarkdownResourceKeys.DocumentSurfaceBrush] = window;
        resources[MarkdownResourceKeys.SelectionBackgroundBrush] = link;
        resources[MarkdownResourceKeys.SelectionForegroundBrush] = window;
        resources[MarkdownResourceKeys.FocusVisualBrush] = focus;
        resources[MarkdownResourceKeys.OverflowIndicatorBrush] = text;

        foreach (string roleName in StyledRoles)
        {
            resources[MarkdownResourceKeys.ForRole(
                MarkdownStyleRole.FromElementKey(roleName),
                MarkdownStyleProperty.ForegroundBrush)] = text;
        }

        foreach (string roleName in new[]
        {
            MarkdownElementKeys.Body,
            MarkdownElementKeys.CodeInline,
            MarkdownElementKeys.CodeBlock,
            MarkdownElementKeys.CodeBlockHeader,
            MarkdownElementKeys.CodeBlockGutter,
            MarkdownElementKeys.Marked,
            MarkdownElementKeys.DefinitionDescription,
            MarkdownElementKeys.Figure,
            MarkdownElementKeys.FigureCaption,
            MarkdownElementKeys.Diagram,
            MarkdownElementKeys.Table,
            MarkdownElementKeys.TableHeader,
            MarkdownElementKeys.TableCell,
            MarkdownElementKeys.AlertNote,
            MarkdownElementKeys.AlertTip,
            MarkdownElementKeys.AlertImportant,
            MarkdownElementKeys.AlertWarning,
            MarkdownElementKeys.AlertCaution,
        })
        {
            resources[MarkdownResourceKeys.ForRole(
                MarkdownStyleRole.FromElementKey(roleName),
                MarkdownStyleProperty.BackgroundBrush)] = window;
        }

        foreach (string roleName in new[]
        {
            MarkdownElementKeys.CodeBlock,
            MarkdownElementKeys.CodeBlockHeader,
            MarkdownElementKeys.Diagram,
            MarkdownElementKeys.Table,
            MarkdownElementKeys.TableHeader,
            MarkdownElementKeys.TableCell,
        })
        {
            resources[MarkdownResourceKeys.ForRole(
                MarkdownStyleRole.FromElementKey(roleName),
                MarkdownStyleProperty.BorderBrush)] = text;
        }

        if (TryResolveResource("AppHighContrastMonoFontFamily", out object? monoFont))
        {
            foreach (string roleName in new[]
            {
                MarkdownElementKeys.CodeInline,
                MarkdownElementKeys.CodeBlock,
                MarkdownElementKeys.CodeBlockGutter,
                MarkdownElementKeys.CodeBlockLineNumber,
                MarkdownElementKeys.Diagram,
            })
            {
                resources[MarkdownResourceKeys.ForRole(
                    MarkdownStyleRole.FromElementKey(roleName),
                    MarkdownStyleProperty.FontFamily)] = monoFont;
            }
        }

        MarkdownStyleRole linkRole = MarkdownStyleRole.FromElementKey(MarkdownElementKeys.Link);
        resources[MarkdownResourceKeys.ForRole(linkRole, MarkdownStyleProperty.ForegroundBrush)] = link;
        resources[MarkdownResourceKeys.ForRole(linkRole, MarkdownStyleProperty.HoverForegroundBrush)] = link;
        resources[MarkdownResourceKeys.ForRole(linkRole, MarkdownStyleProperty.FocusForegroundBrush)] = focus;

        foreach (string roleName in new[]
        {
            MarkdownElementKeys.Quote,
            MarkdownElementKeys.AlertNote,
            MarkdownElementKeys.AlertTip,
            MarkdownElementKeys.AlertImportant,
            MarkdownElementKeys.AlertWarning,
            MarkdownElementKeys.AlertCaution,
        })
        {
            resources[MarkdownResourceKeys.ForRole(
                MarkdownStyleRole.FromElementKey(roleName),
                MarkdownStyleProperty.AccentBrush)] = link;
        }
    }

    private static double ResolveDouble(string tokenName, double fallback)
    {
        if (TryResolveResource(tokenName, out object? value))
        {
            return value switch
            {
                double number => number,
                float number => number,
                int number => number,
                _ => fallback,
            };
        }

        return fallback;
    }

    private static bool TryResolveResource(string tokenName, out object? value)
    {
        value = null;
        return Application.Current?.Resources is { } resources &&
            resources.TryGetValue(tokenName, out value);
    }

}
