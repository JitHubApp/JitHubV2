using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Models.GitHub;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using JitHub.Services.Markdown;
using JitHub.Services.Markdown.Hosting;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.ViewModels.CodeViewer;
using JitHub.WinUI.ViewModels.Pages;
using MarkdownRenderer;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Controls;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.Storage.Streams;
using Windows.UI.ViewManagement;

namespace JitHub.WinUI.Views.Controls.Common;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class MarkdownViewer :
    UserControl,
    IMarkdownRendererShutdownParticipant,
    IMarkdownRendererSlotHost<MarkdownRendererControl>,
    IMarkdownViewerNoticePresenter,
    IMarkdownRendererLayoutGateHost
{
    private static MarkdownEngine SharedGitHubEngine => JitHubMarkdownRuntime.Engine;
    private static readonly (string Role, string Token, double Fallback)[] ScalableRoleFonts =
    [
        (MarkdownElementKeys.Body, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.Heading1, "AppMarkdownHeading1FontSize", 30),
        (MarkdownElementKeys.Heading2, "AppMarkdownHeading2FontSize", 24),
        (MarkdownElementKeys.Heading3, "AppMarkdownHeading3FontSize", 20),
        (MarkdownElementKeys.Heading4, "AppMarkdownHeading4FontSize", 17),
        (MarkdownElementKeys.Heading5, "AppMarkdownHeading5FontSize", 15),
        (MarkdownElementKeys.Heading6, "AppMarkdownHeading6FontSize", 14),
        (MarkdownElementKeys.Link, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.Strong, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.Emphasis, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.Strikethrough, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.Subscript, "AppMarkdownScriptFontSize", 12),
        (MarkdownElementKeys.Superscript, "AppMarkdownScriptFontSize", 12),
        (MarkdownElementKeys.Inserted, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.Marked, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.Abbreviation, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.CodeInline, "AppMarkdownCodeFontSize", 13),
        (MarkdownElementKeys.CodeBlock, "AppMarkdownCodeFontSize", 13),
        (MarkdownElementKeys.CodeBlockHeader, "AppMarkdownMetaFontSize", 13),
        (MarkdownElementKeys.CodeBlockLanguage, "AppMarkdownMetaFontSize", 13),
        (MarkdownElementKeys.CodeBlockGutter, "AppMarkdownMetaFontSize", 13),
        (MarkdownElementKeys.CodeBlockLineNumber, "AppMarkdownMetaFontSize", 13),
        (MarkdownElementKeys.Quote, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.ListMarker, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.ImageCaption, "AppMarkdownMetaFontSize", 13),
        (MarkdownElementKeys.DefinitionTerm, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.DefinitionDescription, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.Figure, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.FigureCaption, "AppMarkdownMetaFontSize", 13),
        (MarkdownElementKeys.Diagram, "AppMarkdownCodeFontSize", 13),
        (MarkdownElementKeys.Math, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.Table, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.TableHeader, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.TableCell, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.AlertNote, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.AlertTip, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.AlertImportant, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.AlertWarning, "AppMarkdownBodyFontSize", 16),
        (MarkdownElementKeys.AlertCaution, "AppMarkdownBodyFontSize", 16),
    ];

    private readonly MarkdownTheme _theme = new();
    private readonly IMarkdownImageResolver _imageResolver;
    private readonly IMarkdownHostObserver _hostObserver;
    private readonly MarkdownViewerFeedback _feedback;
    private readonly MarkdownLinkActivator _links;
    private MarkdownViewerDocumentPayload? _documentPayload;
    private readonly MarkdownRendererSlot<MarkdownRendererControl> _rendererSlot;
    private bool _isLoaded;
    private bool _shutdownRequested;
    private readonly MarkdownRendererInitialLayoutGate _layoutGate;
    private bool _rendererCreationQueued;
    private bool _paletteSubscribed;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _lifecycleRuntimeSettingsTimer;
    private int _lifecycleRuntimeSettingsRevision;

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
        MarkdownRendererLifetimeRegistry renderers = ((global::JitHub.WinUI.App)Application.Current).MarkdownRenderers;
        _shutdownRequested = renderers.Register(this);
        _rendererSlot = new MarkdownRendererSlot<MarkdownRendererControl>(
            renderers,
            this,
            static retirement => UiTaskGuard.Observe(retirement, "ui-markdown-retirement"));
        _layoutGate = new MarkdownRendererInitialLayoutGate(this);

        _hostObserver = ResolveService<IMarkdownHostObserver>() ?? NoMarkdownHostObserver.Instance;
        var telemetry = new MarkdownViewerTelemetry(ResolveTelemetryService(), () => HostKind);
        _feedback = new MarkdownViewerFeedback(this, _hostObserver, telemetry);
        // Links launch through Windows directly. The DI launcher can be a
        // sign-in automation substitute that must not handle document links.
        _links = new MarkdownLinkActivator(
            ResolveService<IMarkdownRouteNavigator>() ?? new ShellMarkdownRouteNavigator(Ioc.Default),
            new WindowsExternalUriLauncher(),
            _hostObserver,
            telemetry);

        // The production app defaults to trusted HTTPS image loading. Lifecycle
        // automation opts into the configurable privacy mode so that the prompt,
        // consent, and retry path remain covered as well.
        if (MarkdownLifecycleAutomationBridge.IsEnabled)
        {
            AllowThirdPartyRemoteImagesByDefault = false;
        }

        IMarkdownImageResolver imageResolver = ResolveImageResolver();
        _imageResolver = MarkdownLifecycleAutomationBridge.IsEnabled
            ? MarkdownLifecycleImageResolver.Create(imageResolver)
            : imageResolver;

        Loaded += (_, _) =>
        {
            if (_shutdownRequested)
                return;

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
            _rendererSlot.TryRetireFromLifecycle();
        };
        DataContextChanged += (_, _) =>
        {
            if (DocumentSource is null)
            {
                _feedback.ResetForHostReuse();
            }
        };
    }

    void IMarkdownRendererShutdownParticipant.PrepareForApplicationShutdown()
    {
        _shutdownRequested = true;
        _rendererCreationQueued = false;
        UnsubscribeRuntimeSettings();
        // Shutdown can begin before WinUI delivers this element's Unloaded
        // event. Explicit renderer retirement completes the viewer lifetime
        // even when that event is still queued.
        _isLoaded = false;

        _rendererSlot.Retire();
    }

    private static IMarkdownImageResolver ResolveImageResolver()
    {
        try
        {
            return Ioc.Default.GetService<IGitHubMarkdownImageResolverProvider>()?.CreateMarkdownImageResolver()
                ?? DenyAllMarkdownImageResolver.Instance;
        }
        catch
        {
            return DenyAllMarkdownImageResolver.Instance;
        }
    }

    private static void OnViewportOwnershipChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MarkdownViewer viewer || viewer._rendererSlot.Current is null)
        {
            return;
        }

        if (!viewer._rendererSlot.TryRetireFromLifecycle())
            return;

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
                    viewer._feedback.ResetForHostReuse();
                }
            }

            if (e.Property == AllowThirdPartyRemoteImagesByDefaultProperty)
            {
                viewer._feedback.OnRemoteImagePolicyChanged();
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

    private static T? ResolveService<T>()
        where T : class
    {
        try
        {
            return Ioc.Default.GetService<T>();
        }
        catch
        {
            return null;
        }
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
        if (revision > 0 && revision != _lifecycleRuntimeSettingsRevision)
        {
            _lifecycleRuntimeSettingsRevision = revision;
            QueueRendererResourceRefresh();
        }
    }

    private static ScrollViewer? FindDescendantVerticalScrollViewer(DependencyObject root)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is ScrollViewer scrollViewer &&
                scrollViewer.VerticalScrollMode != ScrollMode.Disabled)
            {
                return scrollViewer;
            }

            if (FindDescendantVerticalScrollViewer(child) is { } descendant)
                return descendant;
        }

        return null;
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

        viewer.OnDocumentSourceTransition(
            e.OldValue as MarkdownDocumentSource,
            e.NewValue as MarkdownDocumentSource);
        viewer.ApplyRendererSettings();
    }

    internal void SetDocumentPayload(MarkdownViewerDocumentPayload? payload)
    {
        if (Equals(_documentPayload, payload))
            return;

        MarkdownDocumentSource? oldSource = _documentPayload?.DocumentSource ?? DocumentSource;
        _documentPayload = payload;
        MarkdownDocumentSource? newSource = _documentPayload is null
            ? DocumentSource
            : _documentPayload.DocumentSource;
        OnDocumentSourceTransition(oldSource, newSource);
        ApplyRendererSettings();
    }

    private void OnDocumentSourceTransition(
        MarkdownDocumentSource? oldSource,
        MarkdownDocumentSource? newSource)
    {
        if (!string.Equals(oldSource?.DocumentId, newSource?.DocumentId, StringComparison.Ordinal))
        {
            ResetOwnedViewportForNewDocument();
        }

        _feedback.ActivateDocument(newSource);
    }

    private void ResetOwnedViewportForNewDocument()
    {
        // The document source is the logical navigation identity. A reused
        // README preview must start at its own top; preserving a prior repo's
        // anchor would also make lazy layout and image-readiness target the
        // wrong first viewport. Do not move an ancestor/page-owned scroller.
        if (!OwnsScrollViewport || _rendererSlot.Current is null)
            return;

        ScrollViewer? viewport = FindDescendantVerticalScrollViewer(_rendererSlot.Current);
        if (viewport is { VerticalOffset: > 0.5 })
            viewport.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void QueueRendererCreation()
    {
        if (_shutdownRequested ||
            _rendererSlot.Current is not null ||
            _rendererSlot.IsRetirementPending ||
            _rendererCreationQueued ||
            !_isLoaded)
        {
            return;
        }

        _rendererCreationQueued = true;
        var dispatcher = DispatcherQueue;
        if (dispatcher is null ||
            !dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _rendererCreationQueued = false;
                if (_shutdownRequested || !_isLoaded)
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
        if (_shutdownRequested)
            return;

        // A failed teardown can latch the underlying control's disposed state
        // before it has released every resource. Never reattach that instance.
        if (_rendererSlot.IsDisposalFaulted)
        {
            ShowRendererDisposalFailure();
            return;
        }

        // The old control is already disposed, but its published snapshot may
        // still be releasing device resources on the pool. Do not reattach it
        // if this viewer is loaded again before that retirement completes.
        if (_rendererSlot.IsRetirementPending)
            return;

        if (_rendererSlot.Current is { } current)
        {
            if (!RendererHost.Children.Contains(current))
            {
                _layoutGate.Prepare(current);
                RendererHost.Children.Add(current);
                ApplyRendererSettings();
            }

            return;
        }

        MarkdownRendererControl renderer;
        if (OwnsScrollViewport)
        {
            renderer = new MarkdownScrollView().UseGitHubReadme(SharedGitHubEngine);
        }
        else
        {
            renderer = new MarkdownDocumentView().UseGitHubReadme(SharedGitHubEngine);
        }
        _rendererSlot.Attach(renderer);
        MarkdownRendererConfigurator.ApplyDocumentStyle(renderer, _theme);
        ApplyRendererResources();
        MarkdownRendererConfigurator.ApplyInitialSettings(renderer, CreateRendererSettings());
        renderer.LinkClick += OnRendererLinkClick;
        renderer.DisclosureToggled += OnRendererDisclosureToggled;
        renderer.CopyCompleted += OnRendererCopyCompleted;
        renderer.ImageUnavailable += OnRendererImageUnavailable;
        renderer.RenderCompleted += OnRendererRenderCompleted;
        renderer.RenderFailed += OnRendererRenderFailed;
        string automationId = MarkdownHostContract.GetAutomationId(HostKind, AutomationInstanceId);
        AutomationProperties.SetName(renderer, MarkdownHostContract.GetAutomationName(HostKind));
        AutomationProperties.SetAutomationId(renderer, automationId);
        _hostObserver.OnHostReady(automationId);
        renderer.HorizontalAlignment = HorizontalAlignment.Stretch;
        renderer.VerticalAlignment = VerticalAlignment.Stretch;
        _layoutGate.Prepare(renderer);
        RendererHost.Children.Add(renderer);
        ApplyRendererSettings();
    }

    IMarkdownUiDispatcher? IMarkdownRendererSlotHost<MarkdownRendererControl>.Dispatcher =>
        DispatcherQueueMarkdownUiDispatcher.From(DispatcherQueue);

    void IMarkdownRendererSlotHost<MarkdownRendererControl>.DetachRenderer(MarkdownRendererControl renderer)
    {
        _layoutGate.Release(renderer);
        renderer.LinkClick -= OnRendererLinkClick;
        renderer.DisclosureToggled -= OnRendererDisclosureToggled;
        renderer.CopyCompleted -= OnRendererCopyCompleted;
        renderer.ImageUnavailable -= OnRendererImageUnavailable;
        renderer.RenderCompleted -= OnRendererRenderCompleted;
        renderer.RenderFailed -= OnRendererRenderFailed;
        RendererHost.Children.Remove(renderer);
    }

    void IMarkdownRendererSlotHost<MarkdownRendererControl>.OnRendererReleased()
    {
        if (_isLoaded && !_shutdownRequested && !_rendererSlot.IsDisposalFaulted)
            QueueRendererCreation();
    }

    void IMarkdownRendererSlotHost<MarkdownRendererControl>.OnRendererTeardownFaulted(Exception exception)
    {
        ShowRendererDisposalFailure();
        _hostObserver.OnRenderFailed(
            MarkdownHostContract.GetAutomationId(HostKind, AutomationInstanceId),
            exception);
    }

    string? IMarkdownViewerNoticePresenter.HostKind => HostKind;

    string IMarkdownViewerNoticePresenter.AutomationId =>
        MarkdownHostContract.GetAutomationId(HostKind, AutomationInstanceId);

    bool IMarkdownViewerNoticePresenter.AllowsThirdPartyRemoteImagesByDefault => AllowThirdPartyRemoteImagesByDefault;

    bool IMarkdownViewerNoticePresenter.IsRendererDisposalFaulted => _rendererSlot.IsDisposalFaulted;

    void IMarkdownViewerNoticePresenter.UpdateRemoteImageNotice(string title, string message, bool offerLoadImages)
    {
        RemoteImageInfoBar.Title = title;
        RemoteImageInfoBar.Message = message;
        LoadRemoteImagesButton.Visibility = offerLoadImages ? Visibility.Visible : Visibility.Collapsed;
    }

    void IMarkdownViewerNoticePresenter.OpenRemoteImageNotice() => RemoteImageInfoBar.IsOpen = true;

    void IMarkdownViewerNoticePresenter.CloseRemoteImageNotice()
    {
        if (RemoteImageInfoBar is not null)
        {
            RemoteImageInfoBar.IsOpen = false;
        }
    }

    void IMarkdownViewerNoticePresenter.SetRenderErrorNoticeOpen(bool isOpen)
    {
        if (RenderErrorInfoBar is not null)
        {
            RenderErrorInfoBar.IsOpen = isOpen;
        }
    }

    void IMarkdownViewerNoticePresenter.ApplyRemoteImageGrant()
    {
        if (_rendererSlot.Current is { } renderer)
        {
            renderer.AllowThirdPartyRemoteImages = true;
            renderer.RequestRebuild();
        }
    }

    void IMarkdownViewerNoticePresenter.RequestRebuild() => _rendererSlot.Current?.RequestRebuild();

    private void ShowRendererDisposalFailure()
    {
        RenderErrorInfoBar.Message = LocalizedResourceText.GetString(
            "Markdown.RendererDisposalFailure.Message",
            "This Markdown view could not restart safely. Close and reopen JitHub to try again.");
        RetryRenderButton.Visibility = Visibility.Collapsed;
        RenderErrorInfoBar.IsOpen = true;
    }

    MarkdownRendererControl? IMarkdownRendererLayoutGateHost.CurrentRenderer => _rendererSlot.Current;

    bool IMarkdownRendererLayoutGateHost.IsActive => !_shutdownRequested && _isLoaded;

    void IMarkdownRendererLayoutGateHost.OnInitialLayoutReady() => ApplyRendererSettings();

    private void UpdateHostLayout()
    {
        MarkdownReadingRegionSize region = MarkdownReadingRegionLayout.Compute(
            ActualWidth,
            ContentPadding.Left,
            ContentPadding.Right,
            ContentMaxWidth,
            ContentHorizontalAlignment);
        RendererHost.Width = region.Width;
        RendererHost.MaxWidth = region.MaxWidth;
        RendererHost.HorizontalAlignment = region.HorizontalAlignment;
    }

    private MarkdownRendererSettings CreateRendererSettings()
    {
        MarkdownViewerDocumentPayload? documentPayload = _documentPayload;
        return new MarkdownRendererSettings(
            IsSelectionEnabled,
            IsCodeBlockCopyEnabled,
            IsSyntaxHighlightingEnabled,
            _imageResolver,
            MarkdownRendererConfigurator.ResolveImageBaseUri(documentPayload is null ? BaseUrl : documentPayload.BaseUrl),
            documentPayload is null ? DocumentPath : documentPayload.DocumentPath,
            documentPayload is null ? DocumentSource : documentPayload.DocumentSource,
            _feedback.AllowsThirdPartyRemoteImages);
    }
    private void ApplyRendererSettings()
    {
        if (_rendererSlot.IsDisposalFaulted)
            return;

        if (_rendererSlot.Current is not { } renderer)
        {
            QueueRendererCreation();
            return;
        }

        if (!renderer.IsLoaded || !_layoutGate.IsReady)
        {
            // A source tick is meaningful only when Markdown assignment can
            // request the pipeline at its measured width. The one-shot
            // LayoutUpdated handler runs after the initial SizeChanged events,
            // so the exact source will not be superseded by the first measure.
            return;
        }

        MarkdownViewerDocumentPayload? documentPayload = _documentPayload;
        string markdown = GetEffectiveMarkdown(documentPayload is null ? Text : documentPayload.Markdown);
        bool markdownChanged = !string.Equals(renderer.Markdown, markdown, StringComparison.Ordinal);
        if (markdownChanged)
        {
            _feedback.OnMarkdownChanged();
        }

        MarkdownRendererConfigurator.ApplySettings(renderer, CreateRendererSettings(), markdown);
        AutomationProperties.SetName(renderer, MarkdownHostContract.GetAutomationName(HostKind));
        AutomationProperties.SetAutomationId(
            renderer,
            MarkdownHostContract.GetAutomationId(HostKind, AutomationInstanceId));
    }
    private string GetEffectiveMarkdown(string? sourceMarkdown)
    {
        string markdown = sourceMarkdown ?? string.Empty;
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

    private void OnRendererImageUnavailable(object? sender, MarkdownImageUnavailableEventArgs e) =>
        _feedback.OnImageUnavailable(e.Source, e.Reason);

    private void RemoteImageInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) =>
        _feedback.OnRemoteImageNoticeClosed(args.Reason == InfoBarCloseReason.CloseButton);

    private void OnRendererRenderCompleted(object? sender, EventArgs e) =>
        _feedback.OnRenderCompleted();

    private void OnRendererDisclosureToggled(object? sender, MarkdownDisclosureToggledEventArgs e) =>
        _feedback.OnDisclosureToggled(e.IsExpanded);

    private void OnRendererCopyCompleted(object? sender, MarkdownCopyCompletedEventArgs e) =>
        _feedback.OnCopyCompleted(e.Kind == MarkdownCopyKind.CodeBlock, e.Succeeded);

    private void OnRendererRenderFailed(object? sender, MarkdownRenderFailedEventArgs e) =>
        _feedback.OnRenderFailed(e.Exception);

    private void RetryRenderButton_Click(object sender, RoutedEventArgs e) =>
        _feedback.Retry();

    private void LoadRemoteImagesButton_Click(object sender, RoutedEventArgs e) =>
        _feedback.GrantRemoteImages();
    private Uri GetEffectiveBaseUri()
    {
        MarkdownViewerDocumentPayload? documentPayload = _documentPayload;
        return MarkdownRendererConfigurator.ResolveImageBaseUri(documentPayload is null ? BaseUrl : documentPayload.BaseUrl);
    }

    private MarkdownDocumentSource? GetEffectiveDocumentSource()
    {
        MarkdownViewerDocumentPayload? documentPayload = _documentPayload;
        return documentPayload is null ? DocumentSource : documentPayload.DocumentSource;
    }

    private void OnRendererLinkClick(object? sender, MarkdownLinkClickEventArgs e) =>
        UiTaskGuard.Run(
            () => _links.ActivateAsync(
                e.Url,
                GetEffectiveBaseUri(),
                GetEffectiveDocumentSource(),
                MarkdownHostContract.GetAutomationId(HostKind, AutomationInstanceId)),
            "ui-markdown-viewer");
    private void ApplyRendererResources()
    {
        if (_rendererSlot.Current is null)
        {
            return;
        }

        ResourceDictionary resources = _rendererSlot.Current.Resources;
        MarkdownRendererResources.RemoveLocalOverrides(resources);
        MarkdownRendererResources.ApplyHostSurface(resources, SurfaceColorToken, HostKind);

        if (MarkdownLifecycleAutomationBridge.GetTextScaleFactor() is double requestedScale)
        {
            double tokenScale = GetLifecycleTokenScaleFactor(requestedScale);
            foreach ((string role, string token, double fallback) in ScalableRoleFonts)
            {
                resources[MarkdownResourceKeys.ForRole(
                    MarkdownStyleRole.FromElementKey(role),
                    MarkdownStyleProperty.FontSize)] =
                    MarkdownRendererResources.ResolveDouble(token, fallback) * tokenScale;
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

        foreach (string roleName in MarkdownRendererResources.StyledRoles)
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

        if (MarkdownRendererResources.TryResolveAppResource("AppHighContrastMonoFontFamily", out object? monoFont))
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
}
