using System;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Models.GitHub;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.Services.Markdown;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.ViewModels.Pages;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.Images;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace JitHub.WinUI.Views.Controls.Common;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class MarkdownViewer : UserControl
{
    private static readonly Uri DefaultBaseUri = new("https://github.com/", UriKind.Absolute);
    private static readonly Lazy<MarkdownExtensionRegistry> SharedGfmRegistry = new(CreateGfmRegistry);

    private readonly MarkdownTheme _theme = new();
    private readonly IMarkdownImageResolver _imageResolver;
    private readonly MarkdownRemoteContentConsent _remoteContentConsent = new();
    private readonly UISettings? _uiSettings = RuntimeEventSubscription.TryCreate(
        static () => new UISettings(),
        nameof(UISettings));
    private readonly AccessibilitySettings? _accessibilitySettings = RuntimeEventSubscription.TryCreate(
        static () => new AccessibilitySettings(),
        nameof(AccessibilitySettings));
    private MarkdownRendererControl? _renderer;
    private bool _isLoaded;
    private bool _rendererCreationQueued;
    private bool _colorValuesSubscribed;
    private bool _textScaleSubscribed;
    private bool _highContrastSubscribed;
    private double _appliedTextScaleFactor = 1;
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
        new PropertyMetadata(false, OnRendererPropertyChanged));

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

    public MarkdownViewer()
    {
        InitializeComponent();

        IMarkdownImageResolver imageResolver = ResolveImageResolver();
        _imageResolver = MarkdownLifecycleAutomationBridge.IsEnabled
            ? new MarkdownLifecycleImageResolver(imageResolver)
            : imageResolver;
        ApplyTheme();

        Loaded += (_, _) =>
        {
            _isLoaded = true;
            SubscribeRuntimeSettings();
            ApplyTheme();
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
        ActualThemeChanged += MarkdownViewer_ActualThemeChanged;
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

    private static MarkdownExtensionRegistry CreateGfmRegistry()
        => new MarkdownExtensionRegistry().UseGitHubFlavoredMarkdown();

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
            viewer.ApplyTheme();
        }
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double textScaleFactor = GetTextScaleFactor();
        if (Math.Abs(textScaleFactor - _appliedTextScaleFactor) > 0.001 ||
            MarkdownLifecycleAutomationBridge.IsEnabled)
        {
            ApplyTheme();
            ApplyRendererSettings();
        }

        UpdateHostLayout();
    }

    private void SubscribeRuntimeSettings()
    {
        if (_uiSettings is not null && !_colorValuesSubscribed)
        {
            _colorValuesSubscribed = RuntimeEventSubscription.TrySubscribe(
                () => _uiSettings.ColorValuesChanged += UISettings_ColorValuesChanged,
                nameof(UISettings.ColorValuesChanged));
        }
        if (_uiSettings is not null && !_textScaleSubscribed)
        {
            _textScaleSubscribed = RuntimeEventSubscription.TrySubscribe(
                () => _uiSettings.TextScaleFactorChanged += UISettings_TextScaleFactorChanged,
                nameof(UISettings.TextScaleFactorChanged));
        }
        if (_accessibilitySettings is not null && !_highContrastSubscribed)
        {
            _highContrastSubscribed = RuntimeEventSubscription.TrySubscribe(
                () => _accessibilitySettings.HighContrastChanged += AccessibilitySettings_HighContrastChanged,
                nameof(AccessibilitySettings.HighContrastChanged));
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
        if (_uiSettings is not null)
        {
            RuntimeEventSubscription.TryUnsubscribe(
                () => _uiSettings.ColorValuesChanged -= UISettings_ColorValuesChanged,
                _colorValuesSubscribed,
                nameof(UISettings.ColorValuesChanged));
            RuntimeEventSubscription.TryUnsubscribe(
                () => _uiSettings.TextScaleFactorChanged -= UISettings_TextScaleFactorChanged,
                _textScaleSubscribed,
                nameof(UISettings.TextScaleFactorChanged));
        }

        if (_accessibilitySettings is not null)
        {
            RuntimeEventSubscription.TryUnsubscribe(
                () => _accessibilitySettings.HighContrastChanged -= AccessibilitySettings_HighContrastChanged,
                _highContrastSubscribed,
                nameof(AccessibilitySettings.HighContrastChanged));
        }

        _colorValuesSubscribed = false;
        _textScaleSubscribed = false;
        _highContrastSubscribed = false;
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
        QueueRuntimeThemeRefresh();
    }

    private void MarkdownViewer_ActualThemeChanged(FrameworkElement sender, object args) =>
        QueueRuntimeThemeRefresh();

    private void UISettings_ColorValuesChanged(UISettings sender, object args) =>
        QueueRuntimeThemeRefresh();

    private void UISettings_TextScaleFactorChanged(UISettings sender, object args) =>
        QueueRuntimeThemeRefresh();

    private void AccessibilitySettings_HighContrastChanged(AccessibilitySettings sender, object args) =>
        QueueRuntimeThemeRefresh();

    private void QueueRuntimeThemeRefresh()
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

            ApplyTheme();
            ApplyRendererSettings();
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
        if (viewer.RemoteImageInfoBar is not null)
        {
            viewer.RemoteImageInfoBar.IsOpen = false;
        }
        viewer.ApplyRendererSettings();
    }

    private void ResetRemoteContentConsent()
    {
        _remoteContentConsent.ResetForHostReuse();
        if (RemoteImageInfoBar is not null)
        {
            RemoteImageInfoBar.IsOpen = false;
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

        _renderer = new MarkdownRendererControlBuilder()
            .WithExtensionRegistry(SharedGfmRegistry.Value)
            .WithTheme(_theme)
            .WithSelectionEnabled(IsSelectionEnabled)
            .WithCodeBlockCopyEnabled(IsCodeBlockCopyEnabled)
            .WithImageResolver(_imageResolver)
            .WithImageBaseUri(GetBaseUri())
            .WithImageDocumentPath(DocumentPath)
            .WithImageDocumentSource(DocumentSource)
            .WithThirdPartyRemoteImagesAllowed(_remoteContentConsent.IsGranted)
            .Build();
        _renderer.LinkClick += OnRendererLinkClick;
        _renderer.ImageUnavailable += OnRendererImageUnavailable;
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
        _renderer.ImageUnavailable -= OnRendererImageUnavailable;
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

        _renderer.Markdown = GetEffectiveMarkdown();
        _renderer.IsSelectionEnabled = IsSelectionEnabled;
        _renderer.IsCodeBlockCopyEnabled = IsCodeBlockCopyEnabled;
        // Keep JitHub's production markdown surfaces off TextMate/Onig for now.
        // The native Onig runtime can fail-fast during packaged WinUI shutdown,
        // and the package does not expose a safe registry/scanner disposal path.
        _renderer.IsCodeBlockSyntaxHighlightingEnabled = false;

        _renderer.ImageResolver = _imageResolver;
        _renderer.ImageBaseUri = GetBaseUri();
        _renderer.ImageDocumentPath = DocumentPath;
        _renderer.ImageDocumentSource = DocumentSource;
        _renderer.AllowThirdPartyRemoteImages = _remoteContentConsent.IsGranted;
        AutomationProperties.SetName(_renderer, MarkdownHostContract.GetAutomationName(HostKind));
        AutomationProperties.SetAutomationId(_renderer, MarkdownHostContract.GetAutomationId(HostKind, AutomationInstanceId));
    }

    private string GetEffectiveMarkdown()
    {
        string markdown = Text ?? string.Empty;
        if (!string.Equals(
                Environment.GetEnvironmentVariable("JITHUB_MARKDOWN_LIFECYCLE_FIXTURE"),
                "1",
                StringComparison.Ordinal))
        {
            return markdown;
        }

        string? targetHost = Environment.GetEnvironmentVariable("JITHUB_MARKDOWN_LIFECYCLE_HOST");
        if (!string.IsNullOrWhiteSpace(targetHost) &&
            !MarkdownHostContract.GetAutomationId(HostKind, AutomationInstanceId)
                .StartsWith(targetHost, StringComparison.Ordinal))
        {
            return markdown;
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
        MarkdownLifecycleAutomationBridge.RecordImageUnavailable(
            MarkdownHostContract.GetAutomationId(HostKind, AutomationInstanceId),
            e.Source,
            e.Reason);

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

    private void LoadRemoteImagesButton_Click(object sender, RoutedEventArgs e)
    {
        _remoteContentConsent.Grant();
        RemoteImageInfoBar.IsOpen = false;
        if (_renderer is not null)
        {
            _renderer.AllowThirdPartyRemoteImages = true;
            _renderer.RequestRebuild();
        }
    }

    private Uri GetBaseUri()
    {
        return Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri? baseUri)
            ? baseUri
            : DefaultBaseUri;
    }

    private async void OnRendererLinkClick(object? sender, MarkdownLinkClickEventArgs e)
    {
        if (!TryCreateLaunchUri(e.Url, out Uri? uri, out bool mayNavigateInternally) || uri is null)
        {
            return;
        }

        MarkdownGitHubRoute route = MarkdownLinkNavigationPolicy.ClassifyGitHubRoute(uri);
        string disposition = mayNavigateInternally && route.Kind is (
            MarkdownGitHubRouteKind.User or
            MarkdownGitHubRouteKind.Repository or
            MarkdownGitHubRouteKind.Issue or
            MarkdownGitHubRouteKind.PullRequest)
                ? route.Kind.ToString().ToLowerInvariant()
                : "external-browser";
        string automationId = MarkdownHostContract.GetAutomationId(HostKind, AutomationInstanceId);
        if (MarkdownLifecycleAutomationBridge.RecordLinkRoute(automationId, uri, disposition))
        {
            return;
        }

        if (mayNavigateInternally && TryOpenInternalGitHubRoute(uri))
        {
            return;
        }

        await Launcher.LaunchUriAsync(uri);
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

    private void ApplyTheme()
    {
        var colors = ResolveThemeColors();
        double textScaleFactor = GetTextScaleFactor();
        _appliedTextScaleFactor = textScaleFactor;
        using (_theme.BeginUpdate())
        {
            _theme.AccentColor = colors.Accent;
            _theme.SurfaceColor = colors.MarkdownSurface;
            _theme.Overrides.Clear();

            _theme.Overrides[MarkdownElementKeys.Body] = new ElementStyleOverride
            {
                FontFamily = "Segoe UI",
                FontSize = ScaleFont(15, textScaleFactor),
                Foreground = colors.Ink,
                Background = colors.MarkdownSurface,
                LineHeightMultiplier = 1.42f,
                Margin = new Thickness(0, 0, 0, 8),
            };
            _theme.Overrides[MarkdownElementKeys.Heading1] = Heading(colors.Ink, ScaleFont(30, textScaleFactor), new Thickness(0, 16, 0, 8));
            _theme.Overrides[MarkdownElementKeys.Heading2] = Heading(colors.Ink, ScaleFont(24, textScaleFactor), new Thickness(0, 14, 0, 6));
            _theme.Overrides[MarkdownElementKeys.Heading3] = Heading(colors.Ink, ScaleFont(20, textScaleFactor), new Thickness(0, 12, 0, 4));
            _theme.Overrides[MarkdownElementKeys.Heading4] = Heading(colors.Ink, ScaleFont(17, textScaleFactor), new Thickness(0, 10, 0, 4));
            _theme.Overrides[MarkdownElementKeys.Heading5] = Heading(colors.Ink, ScaleFont(15, textScaleFactor), new Thickness(0, 8, 0, 2));
            _theme.Overrides[MarkdownElementKeys.Heading6] = Heading(colors.InkSubtle, ScaleFont(14, textScaleFactor), new Thickness(0, 6, 0, 2));
            _theme.Overrides[MarkdownElementKeys.Link] = new ElementStyleOverride
            {
                Foreground = colors.Accent,
                HoverForeground = colors.AccentHover,
                FocusForeground = colors.AccentHover,
                Underline = true,
            };
            _theme.Overrides[MarkdownElementKeys.Strong] = new ElementStyleOverride
            {
                FontFamily = "Segoe UI",
                FontSize = ScaleFont(15, textScaleFactor),
                FontWeight = FontWeights.SemiBold,
                Foreground = colors.Ink,
            };
            _theme.Overrides[MarkdownElementKeys.CodeInline] = new ElementStyleOverride
            {
                FontFamily = "Consolas",
                FontSize = ScaleFont(13, textScaleFactor),
                Foreground = colors.Ink,
                Background = colors.CodeInlineBackground,
                CornerRadius = 4,
                Padding = new Thickness(3, 0, 3, 0),
            };
            _theme.Overrides[MarkdownElementKeys.CodeBlock] = new ElementStyleOverride
            {
                FontFamily = "Consolas",
                FontSize = ScaleFont(13, textScaleFactor),
                Foreground = colors.Ink,
                Background = colors.CodeBlockBackground,
                BorderBrush = colors.Outline,
                BorderThickness = 1,
                CornerRadius = 6,
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 4, 0, 10),
            };
            _theme.Overrides[MarkdownElementKeys.CodeBlockHeader] = new ElementStyleOverride
            {
                Foreground = colors.InkSubtle,
                Background = colors.SurfaceSubtle,
                BorderBrush = colors.Outline,
            };
            _theme.Overrides[MarkdownElementKeys.CodeBlockLanguage] = new ElementStyleOverride
            {
                Foreground = colors.InkSubtle,
                FontWeight = FontWeights.SemiBold,
            };
            _theme.Overrides[MarkdownElementKeys.CodeBlockGutter] = new ElementStyleOverride
            {
                Foreground = colors.InkSubtle,
                Background = colors.SurfaceSubtle,
            };
            _theme.Overrides[MarkdownElementKeys.CodeBlockLineNumber] = new ElementStyleOverride
            {
                Foreground = colors.InkSubtle,
            };
            _theme.Overrides[MarkdownElementKeys.Quote] = new ElementStyleOverride
            {
                Foreground = colors.InkMuted,
                AccentBar = colors.Accent,
                Padding = new Thickness(12, 2, 8, 2),
                Margin = new Thickness(0, 4, 0, 8),
            };
            _theme.Overrides[MarkdownElementKeys.ListMarker] = new ElementStyleOverride
            {
                Foreground = colors.InkSubtle,
            };
            _theme.Overrides[MarkdownElementKeys.ThematicBreak] = new ElementStyleOverride
            {
                Foreground = colors.Outline,
            };
            _theme.Overrides[MarkdownElementKeys.ImageCaption] = new ElementStyleOverride
            {
                Foreground = colors.InkSubtle,
            };
            _theme.Overrides[MarkdownElementKeys.Table] = new ElementStyleOverride
            {
                Foreground = colors.Ink,
                Background = colors.Surface,
                BorderBrush = colors.Outline,
                BorderThickness = 1,
                CornerRadius = 6,
            };
            _theme.Overrides[MarkdownElementKeys.TableHeader] = new ElementStyleOverride
            {
                Foreground = colors.Ink,
                Background = colors.SurfaceSubtle,
                BorderBrush = colors.Outline,
                FontWeight = FontWeights.SemiBold,
            };
            _theme.Overrides[MarkdownElementKeys.TableCell] = new ElementStyleOverride
            {
                Foreground = colors.Ink,
                Background = colors.Surface,
                BorderBrush = colors.Outline,
            };
        }
    }

    private static float ScaleFont(float fontSize, double textScaleFactor) =>
        (float)(fontSize * textScaleFactor);

    private double GetTextScaleFactor()
    {
        double? lifecycleScale = MarkdownLifecycleAutomationBridge.GetTextScaleFactor();
        if (lifecycleScale is not null)
        {
            return lifecycleScale.Value;
        }

        if (string.Equals(
                Environment.GetEnvironmentVariable("JITHUB_MARKDOWN_LIFECYCLE_FIXTURE"),
                "1",
                StringComparison.Ordinal) &&
            double.TryParse(
                Environment.GetEnvironmentVariable("JITHUB_AUTOMATION_TEXT_SCALE_FACTOR"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double automationScale))
        {
            return Math.Clamp(automationScale, 1, 3);
        }

        try
        {
            return Math.Clamp(_uiSettings?.TextScaleFactor ?? 1, 1, 3);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return 1;
        }
    }

    private static ElementStyleOverride Heading(Color foreground, float fontSize, Thickness margin)
    {
        return new ElementStyleOverride
        {
            FontFamily = "Segoe UI",
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = foreground,
            Margin = margin,
            LineHeightMultiplier = 1.25f,
        };
    }

    private MarkdownThemeColors ResolveThemeColors()
    {
        if (MarkdownLifecycleAutomationBridge.IsHighContrastEnabled)
        {
            Color window = Colors.Black;
            Color text = Colors.White;
            Color link = Colors.Yellow;
            return new MarkdownThemeColors(
                text,
                text,
                text,
                window,
                window,
                window,
                window,
                text,
                link,
                Colors.Cyan,
                window,
                window);
        }

        bool dark = ActualTheme == ElementTheme.Dark;
        Color ink = ResolveColor("AppInk", dark ? "#F0F2EA" : "#1B1B1B");
        Color inkMuted = ResolveColor("AppInkMuted", dark ? "#C7CDBF" : "#4F4F4F");
        Color inkSubtle = ResolveColor("AppInkSubtle", dark ? "#99A294" : "#6B6B6B");
        Color markdownSurface = ResolveColor(
            MarkdownHostContract.GetSurfaceColorToken(HostKind),
            MarkdownHostContract.GetSurfaceFallback(HostKind, dark));
        Color surface = ResolveColor("AppSurface", dark ? "#212621" : "#FAFAFA");
        Color surfaceSubtle = ResolveColor("AppSurfaceSubtle", dark ? "#252B25" : "#F0F0F0");
        Color canvasInset = ResolveColor("AppCanvasInset", dark ? "#11130F" : "#EDEDED");
        Color outline = ResolveColor("AppOutline", dark ? "#3C463E" : "#D2D2D2");
        Color accent = ResolveColor("AppAccent", dark ? "#77B59A" : "#256B52");
        Color accentHover = ResolveColor("AppAccentHover", dark ? "#8BC2AA" : "#2F7C60");
        Color codeInlineBackground = ResolveColor("AppCanvasInset", dark ? "#303830" : "#E9E9E9");
        Color codeBlockBackground = ResolveColor("AppCanvasInset", dark ? "#1C221C" : "#EDEDED");

        return new MarkdownThemeColors(
            ink,
            inkMuted,
            inkSubtle,
            markdownSurface,
            surface,
            surfaceSubtle,
            canvasInset,
            outline,
            accent,
            accentHover,
            codeInlineBackground,
            codeBlockBackground);
    }

    private static Color ResolveColor(string tokenName, string fallbackHex)
    {
        if (Application.Current?.Resources is { } resources)
        {
            if (TryResolveResourceColor(resources, tokenName + "Brush", out Color brushColor))
            {
                return brushColor;
            }

            if (TryResolveResourceColor(resources, tokenName + "Color", out Color color))
            {
                return color;
            }
        }

        return ParseColor(fallbackHex);
    }

    private static bool TryResolveResourceColor(ResourceDictionary resources, string key, out Color color)
    {
        color = default;
        if (!resources.TryGetValue(key, out object value))
        {
            return false;
        }

        switch (value)
        {
            case Color resourceColor:
                color = resourceColor;
                return true;
            case SolidColorBrush brush:
                color = brush.Color;
                return true;
            default:
                return false;
        }
    }

    private static Color ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        return Color.FromArgb(
            0xFF,
            Convert.ToByte(hex[0..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16));
    }

    private readonly record struct MarkdownThemeColors(
        Color Ink,
        Color InkMuted,
        Color InkSubtle,
        Color MarkdownSurface,
        Color Surface,
        Color SurfaceSubtle,
        Color CanvasInset,
        Color Outline,
        Color Accent,
        Color AccentHover,
        Color CodeInlineBackground,
        Color CodeBlockBackground);
}
