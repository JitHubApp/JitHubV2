using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Markdown;
using JitHub.Markdown.Hosting;
using JitHub.Markdown.Images;
using JitHub.Markdown.Lifetime;
using JitHub.Markdown.Links;
using JitHub.Markdown.Notices;
using JitHub.Markdown.Runtime;
using JitHub.Markdown.Telemetry;
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
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace JitHub.WinUI.Views.Controls.Common;

/// <summary>
/// JitHub's one Markdown host control. It owns a renderer slot and forwards the
/// renderer's events to the feedback, link and layout owners.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because a WinUI control's fifteen dependency
/// properties, their CLR wrappers and its explicit implementations of five host
/// interfaces must be declared on the control itself. The behavior behind them
/// already lives in <see cref="MarkdownViewerFeedback"/>, <see cref="MarkdownLinkActivator"/>,
/// <see cref="MarkdownRendererSlot{TRenderer}"/>, <see cref="MarkdownRendererAttachment{TRenderer}"/>
/// and <see cref="MarkdownRendererInitialLayoutGate"/>.
/// </remarks>
[WinRT.GeneratedBindableCustomProperty]
public sealed partial class MarkdownViewer :
    UserControl,
    IMarkdownRendererShutdownParticipant,
    IMarkdownRendererSlotHost<MarkdownRendererControl>,
    IMarkdownRendererAttachmentHost<MarkdownRendererControl>,
    IMarkdownViewerNoticePresenter,
    IMarkdownRendererLayoutGateHost
{
    private readonly JitHubMarkdownRuntime _runtime;
    private readonly RuntimeEventSubscription _runtimeEvents;
    private readonly MarkdownTheme _theme = new();
    private readonly IMarkdownImageResolver _imageResolver;
    private readonly MarkdownViewerFeedback _feedback;
    private readonly MarkdownLinkActivator _links;
    private MarkdownViewerDocumentPayload? _documentPayload;
    private readonly MarkdownRendererSlot<MarkdownRendererControl> _rendererSlot;
    private bool _isLoaded;
    private bool _shutdownRequested;
    private readonly MarkdownRendererInitialLayoutGate _layoutGate;
    private readonly MarkdownRendererAttachment<MarkdownRendererControl> _attachment;
    private bool _paletteSubscribed;

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
        global::JitHub.WinUI.App app = (global::JitHub.WinUI.App)Application.Current;
        _runtime = app.MarkdownRuntime;
        _runtimeEvents = app.MarkdownRuntimeEvents;
        MarkdownRendererLifetimeRegistry renderers = app.MarkdownRenderers;
        _shutdownRequested = renderers.Register(this);
        _rendererSlot = new MarkdownRendererSlot<MarkdownRendererControl>(
            renderers,
            this,
            static retirement => UiTaskGuard.Observe(retirement, "ui-markdown-retirement"));
        _layoutGate = new MarkdownRendererInitialLayoutGate(this);
        _attachment = new MarkdownRendererAttachment<MarkdownRendererControl>(_rendererSlot, this);

        var telemetry = new MarkdownViewerTelemetry(ResolveTelemetryService(), () => HostKind);
        _feedback = new MarkdownViewerFeedback(
            this,
            telemetry,
            LocalizedMarkdownResourceText.Instance);
        // Links launch through Windows directly. The DI launcher can be a
        // sign-in automation substitute that must not handle document links.
        _links = new MarkdownLinkActivator(
            ResolveService<IMarkdownRouteNavigator>() ?? new ShellMarkdownRouteNavigator(Ioc.Default),
            new WindowsExternalUriLauncher(),
            telemetry);

        _imageResolver = ResolveImageResolver();

        Loaded += (_, _) =>
        {
            if (_shutdownRequested)
                return;

            _isLoaded = true;
            SubscribeRuntimeSettings();
            UpdateHostLayout();
            _attachment.EnsureRenderer();
            ApplyRendererSettings();
        };
        Unloaded += (_, _) =>
        {
            _isLoaded = false;
            UnsubscribeRuntimeSettings();
            _attachment.CancelQueuedCreation();
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
        _attachment.CancelQueuedCreation();
        UnsubscribeRuntimeSettings();
        // Shutdown can begin before WinUI delivers this element's Unloaded
        // event. Explicit renderer retirement completes the viewer lifetime
        // even when that event is still queued.
        _isLoaded = false;

        _rendererSlot.Retire();
    }

    private static IMarkdownImageResolver ResolveImageResolver() =>
        DenyAllMarkdownImageResolver.CreateOrDenyAll(static () =>
            Ioc.Default.GetService<IGitHubMarkdownImageResolverProvider>()?.CreateMarkdownImageResolver());

    private static void OnViewportOwnershipChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MarkdownViewer viewer || viewer._rendererSlot.Current is null)
        {
            return;
        }

        if (!viewer._rendererSlot.TryRetireFromLifecycle())
            return;

        viewer._attachment.QueueCreation();
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
            _paletteSubscribed = _runtimeEvents.TrySubscribe(
                () => ThemePaletteRuntime.PaletteChanged += ThemePaletteRuntime_PaletteChanged,
                nameof(ThemePaletteRuntime.PaletteChanged));
        }
    }

    private void UnsubscribeRuntimeSettings()
    {
        _runtimeEvents.TryUnsubscribe(
            () => ThemePaletteRuntime.PaletteChanged -=ThemePaletteRuntime_PaletteChanged,
            _paletteSubscribed,
            nameof(ThemePaletteRuntime.PaletteChanged));

        _paletteSubscribed = false;
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

        MarkdownRendererViewport.ScrollToTop(_rendererSlot.Current);
    }

    bool IMarkdownRendererAttachmentHost<MarkdownRendererControl>.IsShutdownRequested => _shutdownRequested;

    bool IMarkdownRendererAttachmentHost<MarkdownRendererControl>.IsLoaded => _isLoaded;

    bool IMarkdownRendererAttachmentHost<MarkdownRendererControl>.TryEnqueueLowPriority(Action callback) =>
        DispatcherQueue is { } dispatcher &&
        dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => callback());

    bool IMarkdownRendererAttachmentHost<MarkdownRendererControl>.IsRendererInTree(MarkdownRendererControl renderer) =>
        RendererHost.Children.Contains(renderer);

    MarkdownRendererControl IMarkdownRendererAttachmentHost<MarkdownRendererControl>.CreateRenderer()
    {
        if (OwnsScrollViewport)
        {
            return new MarkdownScrollView().UseGitHubReadme(_runtime.Engine);
        }

        return new MarkdownDocumentView().UseGitHubReadme(_runtime.Engine);
    }

    void IMarkdownRendererAttachmentHost<MarkdownRendererControl>.ConfigureNewRenderer(MarkdownRendererControl renderer)
    {
        MarkdownRendererConfigurator.ApplyDocumentStyle(renderer, _theme);
        ApplyRendererResources();
        MarkdownRendererConfigurator.ApplyInitialSettings(renderer, CreateRendererSettings(), _runtime);
        renderer.LinkClick += OnRendererLinkClick;
        renderer.DisclosureToggled += OnRendererDisclosureToggled;
        renderer.CopyCompleted += OnRendererCopyCompleted;
        renderer.ImageUnavailable += OnRendererImageUnavailable;
        renderer.RenderCompleted += OnRendererRenderCompleted;
        renderer.RenderFailed += OnRendererRenderFailed;
        string automationId = MarkdownHostContract.GetAutomationId(HostKind, AutomationInstanceId);
        AutomationProperties.SetName(renderer, MarkdownHostContract.GetAutomationName(HostKind));
        AutomationProperties.SetAutomationId(renderer, automationId);
        renderer.HorizontalAlignment = HorizontalAlignment.Stretch;
        renderer.VerticalAlignment = VerticalAlignment.Stretch;
    }

    void IMarkdownRendererAttachmentHost<MarkdownRendererControl>.AddRendererToTree(MarkdownRendererControl renderer)
    {
        _layoutGate.Prepare(renderer);
        RendererHost.Children.Add(renderer);
    }

    void IMarkdownRendererAttachmentHost<MarkdownRendererControl>.ApplyRendererSettings() => ApplyRendererSettings();

    void IMarkdownRendererAttachmentHost<MarkdownRendererControl>.ShowRendererDisposalFailure() =>
        ShowRendererDisposalFailure();
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

    void IMarkdownRendererSlotHost<MarkdownRendererControl>.OnRendererReleased() =>
        _attachment.OnRendererReleased();

    void IMarkdownRendererSlotHost<MarkdownRendererControl>.OnRendererTeardownFaulted(Exception exception)
    {
        ShowRendererDisposalFailure();
    }

    string? IMarkdownViewerNoticePresenter.HostKind => HostKind;

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

    /// <summary>
    /// Returns the document this viewer shows: the atomic payload when a host set
    /// one, otherwise the bound dependency properties. Rendering and link activation
    /// both read it, so a link always resolves against the rendered document's context.
    /// </summary>
    private MarkdownViewerDocumentPayload CurrentDocument() =>
        _documentPayload ?? new MarkdownViewerDocumentPayload(Text ?? string.Empty, BaseUrl, DocumentPath, DocumentSource);

    private MarkdownRendererSettings CreateRendererSettings()
    {
        MarkdownViewerDocumentPayload document = CurrentDocument();
        return new MarkdownRendererSettings(
            IsSelectionEnabled,
            IsCodeBlockCopyEnabled,
            IsSyntaxHighlightingEnabled,
            _imageResolver,
            MarkdownRendererConfigurator.ResolveImageBaseUri(document.BaseUrl),
            document.DocumentPath,
            document.DocumentSource,
            _feedback.AllowsThirdPartyRemoteImages);
    }
    private void ApplyRendererSettings()
    {
        if (_rendererSlot.IsDisposalFaulted)
            return;

        if (_rendererSlot.Current is not { } renderer)
        {
            _attachment.QueueCreation();
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

        string markdown = CurrentDocument().Markdown ?? string.Empty;
        bool markdownChanged = !string.Equals(renderer.Markdown, markdown, StringComparison.Ordinal);
        if (markdownChanged)
        {
            _feedback.OnMarkdownChanged();
        }

        MarkdownRendererConfigurator.ApplySettings(renderer, CreateRendererSettings(), markdown, _runtime);
        AutomationProperties.SetName(renderer, MarkdownHostContract.GetAutomationName(HostKind));
        AutomationProperties.SetAutomationId(
            renderer,
            MarkdownHostContract.GetAutomationId(HostKind, AutomationInstanceId));
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
    private void OnRendererLinkClick(object? sender, MarkdownLinkClickEventArgs e)
    {
        MarkdownViewerDocumentPayload document = CurrentDocument();
        UiTaskGuard.Run(
            () => _links.ActivateAsync(
                e.Url,
                MarkdownRendererConfigurator.ResolveImageBaseUri(document.BaseUrl),
                document.DocumentSource),
            "ui-markdown-viewer");
    }
    private void ApplyRendererResources()
    {
        if (_rendererSlot.Current is null)
        {
            return;
        }

        ResourceDictionary resources = _rendererSlot.Current.Resources;
        MarkdownRendererResources.RemoveLocalOverrides(resources);
        MarkdownRendererResources.ApplyHostSurface(resources, SurfaceColorToken, HostKind);

        // The renderer recompiles one immutable style snapshot. Its shared
        // environment monitor remains the sole owner of real theme, accent,
        // High Contrast, text scale, language, flow direction, and DPI events.
        _theme.Invalidate();
    }
}
