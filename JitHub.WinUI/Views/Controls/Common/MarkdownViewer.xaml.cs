using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Services;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.Images;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.SyntaxHighlighting.TextMate;
using MarkdownRenderer.Theming;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace JitHub.WinUI.Views.Controls.Common;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class MarkdownViewer : UserControl
{
    private static readonly Uri DefaultBaseUri = new("https://github.com/", UriKind.Absolute);
    private static readonly Lazy<MarkdownExtensionRegistry> SharedGfmRegistry = new(CreateGfmRegistry);
    private static readonly TextMateCodeBlockSyntaxHighlighter SharedSyntaxHighlighter = new();

    private readonly MarkdownTheme _theme = new();
    private readonly IMarkdownImageResolver? _imageResolver;
    private MarkdownRendererControl? _renderer;
    private bool _isLoaded;
    private bool _rendererCreationQueued;

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

    public static readonly DependencyProperty SurfaceColorTokenProperty = DependencyProperty.Register(
        nameof(SurfaceColorToken),
        typeof(string),
        typeof(MarkdownViewer),
        new PropertyMetadata("AppSurface", OnThemePropertyChanged));

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

    public string? SurfaceColorToken
    {
        get => (string?)GetValue(SurfaceColorTokenProperty);
        set => SetValue(SurfaceColorTokenProperty, value);
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

        _imageResolver = ResolveImageResolver();
        ApplyTheme();

        Loaded += (_, _) =>
        {
            _isLoaded = true;
            ApplyTheme();
            UpdateHostLayout();
            QueueRendererCreation();
        };
        Unloaded += (_, _) =>
        {
            _isLoaded = false;
            _rendererCreationQueued = false;
        };
        ActualThemeChanged += (_, _) => ApplyTheme();
    }

    private static IMarkdownImageResolver? ResolveImageResolver()
    {
        try
        {
            return Ioc.Default.GetService<IGitHubService>() as IMarkdownImageResolver;
        }
        catch
        {
            return null;
        }
    }

    private static MarkdownExtensionRegistry CreateGfmRegistry()
        => new MarkdownExtensionRegistry().UseGitHubFlavoredMarkdown();

    private static void OnRendererPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownViewer viewer)
        {
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
        => UpdateHostLayout();

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
            return;
        }

        _renderer = new MarkdownRendererControlBuilder()
            .WithExtensionRegistry(SharedGfmRegistry.Value)
            .UseTextMateSyntaxHighlighting(SharedSyntaxHighlighter)
            .WithTheme(_theme)
            .WithSelectionEnabled(IsSelectionEnabled)
            .WithCodeBlockCopyEnabled(IsCodeBlockCopyEnabled)
            .WithImageResolver(_imageResolver)
            .WithImageBaseUri(GetBaseUri())
            .WithImageDocumentPath(DocumentPath)
            .Build();
        _renderer.LinkClick += OnRendererLinkClick;
        _renderer.HorizontalAlignment = HorizontalAlignment.Stretch;
        _renderer.VerticalAlignment = VerticalAlignment.Stretch;
        RendererHost.Children.Add(_renderer);
        ApplyRendererSettings();
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

        _renderer.Markdown = Text ?? string.Empty;
        _renderer.IsSelectionEnabled = IsSelectionEnabled;
        _renderer.IsCodeBlockCopyEnabled = IsCodeBlockCopyEnabled;
        _renderer.IsCodeBlockSyntaxHighlightingEnabled = IsSyntaxHighlightingEnabled;
        if (IsSyntaxHighlightingEnabled && _renderer.CodeBlockSyntaxHighlighter is null)
        {
            _renderer.UseTextMateSyntaxHighlighting(SharedSyntaxHighlighter);
        }

        _renderer.ImageResolver = _imageResolver;
        _renderer.ImageBaseUri = GetBaseUri();
        _renderer.ImageDocumentPath = DocumentPath;
    }

    private Uri GetBaseUri()
    {
        return Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri? baseUri)
            ? baseUri
            : DefaultBaseUri;
    }

    private async void OnRendererLinkClick(object? sender, MarkdownLinkClickEventArgs e)
    {
        if (!TryCreateLaunchUri(e.Url, out Uri? uri))
        {
            return;
        }

        await Launcher.LaunchUriAsync(uri);
    }

    private bool TryCreateLaunchUri(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith("#", StringComparison.Ordinal))
        {
            return false;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out uri))
        {
            return uri.Scheme is "http" or "https" or "mailto";
        }

        return Uri.TryCreate(GetBaseUri(), url, out uri);
    }

    private void ApplyTheme()
    {
        var colors = ResolveThemeColors();
        using (_theme.BeginUpdate())
        {
            _theme.AccentColor = colors.Accent;
            _theme.SurfaceColor = colors.MarkdownSurface;
            _theme.Overrides.Clear();

            _theme.Overrides[MarkdownElementKeys.Body] = new ElementStyleOverride
            {
                FontFamily = "Segoe UI",
                FontSize = 15,
                Foreground = colors.Ink,
                Background = colors.MarkdownSurface,
                LineHeightMultiplier = 1.42f,
                Margin = new Thickness(0, 0, 0, 8),
            };
            _theme.Overrides[MarkdownElementKeys.Heading1] = Heading(colors.Ink, 30, new Thickness(0, 16, 0, 8));
            _theme.Overrides[MarkdownElementKeys.Heading2] = Heading(colors.Ink, 24, new Thickness(0, 14, 0, 6));
            _theme.Overrides[MarkdownElementKeys.Heading3] = Heading(colors.Ink, 20, new Thickness(0, 12, 0, 4));
            _theme.Overrides[MarkdownElementKeys.Heading4] = Heading(colors.Ink, 17, new Thickness(0, 10, 0, 4));
            _theme.Overrides[MarkdownElementKeys.Heading5] = Heading(colors.Ink, 15, new Thickness(0, 8, 0, 2));
            _theme.Overrides[MarkdownElementKeys.Heading6] = Heading(colors.InkSubtle, 14, new Thickness(0, 6, 0, 2));
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
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = colors.Ink,
            };
            _theme.Overrides[MarkdownElementKeys.CodeInline] = new ElementStyleOverride
            {
                FontFamily = "Consolas",
                FontSize = 13,
                Foreground = colors.Ink,
                Background = colors.CodeInlineBackground,
                CornerRadius = 4,
                Padding = new Thickness(3, 0, 3, 0),
            };
            _theme.Overrides[MarkdownElementKeys.CodeBlock] = new ElementStyleOverride
            {
                FontFamily = "Consolas",
                FontSize = 13,
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
        bool dark = ActualTheme == ElementTheme.Dark;
        Color ink = ResolveColor("AppInk", dark ? "#F0F2EA" : "#223127");
        Color inkMuted = ResolveColor("AppInkMuted", dark ? "#C7CDBF" : "#4B5E52");
        Color inkSubtle = ResolveColor("AppInkSubtle", dark ? "#99A294" : "#6D7A70");
        Color markdownSurface = ResolveColor(GetSurfaceColorToken(), GetSurfaceFallback(dark));
        Color surface = ResolveColor("AppSurface", dark ? "#212621" : "#FFFDFC");
        Color surfaceSubtle = ResolveColor("AppSurfaceSubtle", dark ? "#252B25" : "#F7F0E1");
        Color canvasInset = ResolveColor("AppCanvasInset", dark ? "#11130F" : "#EEE7D9");
        Color outline = ResolveColor("AppOutline", dark ? "#3C463E" : "#D5CBB7");
        Color accent = ResolveColor("AppAccent", dark ? "#77B59A" : "#3E7B64");
        Color accentHover = ResolveColor("AppAccentHover", dark ? "#8BC2AA" : "#4A8D73");

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
            dark ? Color.FromArgb(0xFF, 0x30, 0x38, 0x30) : Color.FromArgb(0xFF, 0xE8, 0xE3, 0xD8),
            dark ? Color.FromArgb(0xFF, 0x1C, 0x22, 0x1C) : Color.FromArgb(0xFF, 0xED, 0xE8, 0xDD));
    }

    private string GetSurfaceColorToken()
        => string.IsNullOrWhiteSpace(SurfaceColorToken)
            ? "AppSurface"
            : SurfaceColorToken.Trim();

    private string GetSurfaceFallback(bool dark)
    {
        return GetSurfaceColorToken() switch
        {
            "AppCanvas" => dark ? "#171914" : "#F5F1E7",
            "AppCanvasRaised" => dark ? "#1C211C" : "#FBF7EE",
            "AppCanvasInset" => dark ? "#11130F" : "#EEE7D9",
            "AppSurfaceSubtle" => dark ? "#252B25" : "#F7F0E1",
            _ => dark ? "#212621" : "#FFFDFC",
        };
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
