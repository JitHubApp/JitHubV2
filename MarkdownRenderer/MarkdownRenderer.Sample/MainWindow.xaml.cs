using System;
using System.Collections.Generic;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Document;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Html;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Math;
using MarkdownRenderer.Mermaid;
using MarkdownRenderer.Svg.Resvg;
using MarkdownRenderer.SyntaxHighlighting.TextMate;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Sample;

#pragma warning disable MR1001 // The touch sample intentionally switches between both concrete viewport owners at runtime.
public sealed partial class MainWindow : Window
{
    private const int FirstDocumentBlockIndex = 1;
    private const int MaximumDisplayedDiagnostics = 50;
    private const string DiagnosticsNotificationActivityId = "RendererDiagnostics";
    private readonly MarkdownEngine _engine = new MarkdownEngineBuilder()
        .UseGitHubReadme(SafeHtmlOptions.Default)
        .UseMarkdownExtra()
        .UseMathematics()
        .UseMermaid()
        .UseExtension(SampleHostedElementExtension.Instance)
        .Build();
    private readonly ResvgMarkdownSvgRenderer _svgRenderer = new();

    private MarkdownRendererControl _renderer;
    private readonly Grid _rendererHost;
    private readonly TextBox _editor;
    private readonly TextMateCodeBlockSyntaxHighlighter _textMateHighlighter;
    private readonly TextBlock _pageTitle;
    private readonly InfoBar _diagnosticsBar;
    private readonly TextBlock _diagnosticsMessage;
    private readonly TextBlock _currentSampleStatus;
    private readonly AppBarToggleButton _taskEditingToggle;
    private readonly AppBarToggleButton _viewportOwnershipToggle;
    private string _currentPageTitle = string.Empty;
    private string _currentSampleSource = string.Empty;
    private string _lastRenderedEditorText = string.Empty;
    private string _lastAnnouncedDiagnosticSummary = string.Empty;
    private bool _isLoadingSample;
    private bool _resetPreviewScrollOnRender;
    private TextBlock? _realizedCountStatus;
    private TextBlock? _flowDirectionStatus;
    private TextBlock? _highContrastStatus;
    private TextBlock? _textScaleStatus;
    private TextBlock? _linkActivationStatus;
    private TextBlock? _themeStatus;

    public MainWindow()
    {
        Title = SampleStrings.Get("AppTitle", "MarkdownRenderer — Feature Showcase");

        var navigationView = new NavigationView
        {
            AlwaysShowHeader = true,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false,
            PaneDisplayMode = NavigationViewPaneDisplayMode.Auto,
            PaneTitle = SampleStrings.Get("NavigationPaneTitle", "Sample pages"),
        };
        MarkdownSystemIntegration.SetUseSystemFlowDirection(navigationView, true);
        AutomationProperties.SetAutomationId(navigationView, "SampleNavigation");
        AutomationProperties.SetName(
            navigationView,
            SampleStrings.Get("NavigationAutomationName", "Markdown renderer feature pages"));

        var pageHeader = new Grid
        {
            Margin = (Thickness)Application.Current.Resources["SamplePageHeaderMargin"],
        };
        pageHeader.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        pageHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _pageTitle = new TextBlock
        {
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
            Text = SampleStrings.Get("PageFullDemo", "Full demo"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(_pageTitle, "SamplePageTitle");
        AutomationProperties.SetName(_pageTitle, _pageTitle.Text);
        AutomationProperties.SetHeadingLevel(_pageTitle, AutomationHeadingLevel.Level1);

        var displayControls = new CommandBar
        {
            IsDynamicOverflowEnabled = true,
        };
        Grid.SetColumn(displayControls, 1);
        AutomationProperties.SetAutomationId(displayControls, "SampleDisplayControls");
        AutomationProperties.SetName(
            displayControls,
            SampleStrings.Get("DisplayControlsAutomationName", "Preview display controls"));

        var themeToggle = new AppBarToggleButton
        {
            Icon = new FontIcon { Glyph = "\uE706" },
            Label = SampleStrings.Get("CommandTheme", "Dark theme"),
        };
        AutomationProperties.SetAutomationId(themeToggle, "ThemeToggle");
        AutomationProperties.SetName(
            themeToggle,
            SampleStrings.Get("CommandThemeAutomationName", "Use dark theme"));
        themeToggle.Checked   += (_, _) => SetTheme(ElementTheme.Dark);
        themeToggle.Unchecked += (_, _) => SetTheme(ElementTheme.Light);
        displayControls.PrimaryCommands.Add(themeToggle);

        var rtlToggle = new AppBarToggleButton
        {
            Icon = new FontIcon { Glyph = "\uE8C0" },
            Label = SampleStrings.Get("CommandRtl", "Right-to-left"),
            Name = "RtlToggle",
        };
        AutomationProperties.SetAutomationId(rtlToggle, "RtlToggle");
        AutomationProperties.SetName(
            rtlToggle,
            SampleStrings.Get("CommandRtlAutomationName", "Use right-to-left preview flow"));
        rtlToggle.Checked   += (_, _) => { if (_renderer is not null) { _renderer.FlowDirection = FlowDirection.RightToLeft; UpdateFlowDirectionStatus(); } };
        rtlToggle.Unchecked += (_, _) => { if (_renderer is not null) { _renderer.FlowDirection = FlowDirection.LeftToRight; UpdateFlowDirectionStatus(); } };
        displayControls.PrimaryCommands.Add(rtlToggle);

        var forcedHighContrastToggle = new AppBarToggleButton
        {
            Icon = new FontIcon { Glyph = "\uE793" },
            Label = SampleStrings.Get("CommandHighContrast", "Forced contrast"),
            Name = "ForcedHighContrastToggle",
        };
        AutomationProperties.SetAutomationId(forcedHighContrastToggle, "ForcedHighContrastToggle");
        AutomationProperties.SetName(
            forcedHighContrastToggle,
            SampleStrings.Get("CommandHighContrastAutomationName", "Force high contrast preview colors"));
        forcedHighContrastToggle.Checked += (_, _) =>
        {
            MarkThemeStatusPending();
            ThemeResolver.SystemThemeProviderOverride = new ForcedMarkdownSystemThemeProvider
            {
                IsHighContrast = true,
                WindowTextColor = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                WindowColor = Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00),
                HotlightColor = Windows.UI.Color.FromArgb(0xFF, 0x00, 0xFF, 0xFF),
                HighlightColor = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0x00),
                HighlightTextColor = Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00),
            };
            if (_renderer?.Theme is { } theme)
                theme.AccentColor = Windows.UI.Color.FromArgb(0xFF, 0x80, 0x00, 0x80);
            UpdateHighContrastStatus(true);
            _renderer?.RequestRebuild();
        };
        forcedHighContrastToggle.Unchecked += (_, _) =>
        {
            MarkThemeStatusPending();
            ThemeResolver.SystemThemeProviderOverride = null;
            if (_renderer?.Theme is { } theme)
                theme.AccentColor = null;
            UpdateHighContrastStatus(false);
            _renderer?.RequestRebuild();
        };
        displayControls.PrimaryCommands.Add(forcedHighContrastToggle);

        var textScaleToggle = new AppBarToggleButton
        {
            Icon = new FontIcon { Glyph = "\uE8E9" },
            Label = SampleStrings.Get("CommandTextScale", "200% text"),
            Name = "TextScaleToggle",
        };
        AutomationProperties.SetAutomationId(textScaleToggle, "TextScaleToggle");
        AutomationProperties.SetName(
            textScaleToggle,
            SampleStrings.Get("CommandTextScaleAutomationName", "Use two hundred percent preview text scale"));
        textScaleToggle.Checked += (_, _) => ApplyTextScale(2f);
        textScaleToggle.Unchecked += (_, _) => ApplyTextScale(1f);
        displayControls.PrimaryCommands.Add(textScaleToggle);

        _taskEditingToggle = new AppBarToggleButton
        {
            Icon = new FontIcon { Glyph = "\uE73E" },
            Label = SampleStrings.Get("CommandTaskEditing", "Edit tasks"),
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(_taskEditingToggle, "TaskEditingToggle");
        AutomationProperties.SetName(
            _taskEditingToggle,
            SampleStrings.Get("CommandTaskEditingAutomationName", "Enable task-list editing"));
        _taskEditingToggle.Checked += (_, _) =>
        {
            if (_renderer is not null)
                _renderer.IsTaskListEditingEnabled = true;
        };
        _taskEditingToggle.Unchecked += (_, _) =>
        {
            if (_renderer is not null)
                _renderer.IsTaskListEditingEnabled = false;
        };
        displayControls.PrimaryCommands.Add(_taskEditingToggle);

        _viewportOwnershipToggle = new AppBarToggleButton
        {
            Icon = new FontIcon { Glyph = "\uE7F8" },
            Label = SampleStrings.Get("CommandAncestorViewport", "Ancestor viewport"),
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(_viewportOwnershipToggle, "TouchViewportOwnershipToggle");
        AutomationProperties.SetName(
            _viewportOwnershipToggle,
            SampleStrings.Get(
                "CommandAncestorViewportAutomationName",
                "Use a page-owned ScrollViewer for the touch sample"));
        _viewportOwnershipToggle.Checked += (_, _) => ReplaceRenderer(ownsViewport: false);
        _viewportOwnershipToggle.Unchecked += (_, _) => ReplaceRenderer(ownsViewport: true);
        displayControls.PrimaryCommands.Add(_viewportOwnershipToggle);

        pageHeader.Children.Add(_pageTitle);
        pageHeader.Children.Add(displayControls);
        navigationView.Header = pageHeader;

        _diagnosticsMessage = new TextBlock
        {
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(_diagnosticsMessage, "SampleDiagnosticsMessage");

        var diagnosticsScroller = new ScrollViewer
        {
            Content = _diagnosticsMessage,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = (double)Application.Current.Resources["SampleDiagnosticsMaxHeight"],
        };
        AutomationProperties.SetName(
            diagnosticsScroller,
            SampleStrings.Get("DiagnosticsDetailsAutomationName", "Diagnostic details"));

        _diagnosticsBar = new InfoBar
        {
            Content = diagnosticsScroller,
            IsClosable = false,
            IsOpen = false,
            Margin = (Thickness)Application.Current.Resources["SampleDiagnosticsMargin"],
            Severity = InfoBarSeverity.Informational,
        };
        AutomationProperties.SetAutomationId(_diagnosticsBar, "SampleDiagnostics");
        AutomationProperties.SetName(
            _diagnosticsBar,
            SampleStrings.Get("DiagnosticsAutomationName", "Renderer diagnostics"));
        AutomationProperties.SetLiveSetting(_diagnosticsBar, AutomationLiveSetting.Polite);

        // ── editor + renderer split ─────────────────────────────────────────────
        var contentGrid = new Grid();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(
                (double)Application.Current.Resources["SampleWorkspaceSplitterWidth"],
                GridUnitType.Pixel),
        });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Exercise the public resource contract from an ancestor rather than
        // assigning a MarkdownTheme override directly to the renderer. The
        // non-built-in role must be discovered from both the Default theme
        // dictionary and the selected High Contrast theme dictionary.
        var extensionStyleRole = new MarkdownStyleRole(SampleHostedElementExtension.StyleRoleName);
        var extensionResources = new ResourceDictionary
        {
            [MarkdownResourceKeys.ForRole(extensionStyleRole, MarkdownStyleProperty.ForegroundBrush)] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x8A, 0x2B, 0xE2)),
            [MarkdownResourceKeys.ForRole(extensionStyleRole, MarkdownStyleProperty.FontSize)] = 19d,
            [MarkdownResourceKeys.ForRole(MarkdownStyleRole.Diagram, MarkdownStyleProperty.FontFamily)] =
                new FontFamily("Cascadia Mono"),
        };
        var extensionHighContrastResources = new ResourceDictionary
        {
            // Deliberately non-system authored color: the renderer must map it
            // back to WindowText while retaining the HC-specific typography.
            [MarkdownResourceKeys.ForRole(extensionStyleRole, MarkdownStyleProperty.ForegroundBrush)] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x00, 0xFF)),
            [MarkdownResourceKeys.ForRole(extensionStyleRole, MarkdownStyleProperty.FontSize)] = 23d,
            [MarkdownResourceKeys.ForRole(MarkdownStyleRole.Diagram, MarkdownStyleProperty.FontFamily)] =
                new FontFamily("Cascadia Mono"),
        };
        contentGrid.Resources.ThemeDictionaries["Default"] = extensionResources;
        contentGrid.Resources.ThemeDictionaries["HighContrast"] = extensionHighContrastResources;

        _editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = (double)Application.Current.Resources["SampleEditorFontSize"],
            Padding = (Thickness)Application.Current.Resources["SampleEditorPadding"],
            Text = string.Empty,
        };
        AutomationProperties.SetAutomationId(_editor, "MarkdownEditor");
        AutomationProperties.SetName(
            _editor,
            SampleStrings.Get("EditorAutomationName", "Markdown source editor"));
        ScrollViewer.SetHorizontalScrollBarVisibility(_editor, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_editor, ScrollBarVisibility.Auto);
        _editor.TextChanged += (_, _) =>
        {
            string editorText = _editor.Text ?? string.Empty;
            if (!_isLoadingSample &&
                _renderer is not null &&
                !string.Equals(editorText, _lastRenderedEditorText, StringComparison.Ordinal))
            {
                _lastRenderedEditorText = editorText;
                _renderer.Markdown = editorText;
            }

            if (!_isLoadingSample && _currentPageTitle.Length > 0)
                UpdatePageTitle(!string.Equals(
                    editorText,
                    _currentSampleSource,
                    StringComparison.Ordinal));
        };

        var splitter = new Border
        {
            Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        };

        _textMateHighlighter = new TextMateCodeBlockSyntaxHighlighter(
            new CommonTextMateGrammarProvider(),
            options: null,
            ownsProvider: true);

        _renderer = CreateRenderer(ownsViewport: true);
        _rendererHost = new Grid();
        AttachRendererToHost(_renderer, ownsViewport: true);
        Closed += (_, _) =>
        {
            ThemeResolver.SystemThemeProviderOverride = null;
            _renderer.Dispose();
            _textMateHighlighter.Dispose();
            _engine.Dispose();
            _svgRenderer.Dispose();
        };

        // Hidden status TextBlock that mirrors RealizedEmbedCount so UI
        // automation tests can verify virtualisation without polluting the
        // renderer's own UIA surface (HelpText is read aloud by Narrator).
        _realizedCountStatus = new TextBlock
        {
            Text = "realized:0",
            Opacity = 0,
            IsHitTestVisible = false,
            Width = 1,
            Height = 1,
        };
        AutomationProperties.SetAutomationId(_realizedCountStatus, "RealizedEmbedCount");
        AutomationProperties.SetName(_realizedCountStatus, "realized:0");
        AutomationProperties.SetAccessibilityView(_realizedCountStatus, AccessibilityView.Raw);

        _flowDirectionStatus = new TextBlock
        {
            Text = "flow:ltr",
            Opacity = 0,
            IsHitTestVisible = false,
            Width = 1,
            Height = 1,
        };
        AutomationProperties.SetAutomationId(_flowDirectionStatus, "FlowDirectionStatus");
        AutomationProperties.SetName(_flowDirectionStatus, "flow:ltr");
        AutomationProperties.SetAccessibilityView(_flowDirectionStatus, AccessibilityView.Raw);

        _highContrastStatus = new TextBlock
        {
            Text = "hc:off",
            Opacity = 0,
            IsHitTestVisible = false,
            Width = 1,
            Height = 1,
        };
        AutomationProperties.SetAutomationId(_highContrastStatus, "HighContrastStatus");
        AutomationProperties.SetName(_highContrastStatus, "hc:off");
        AutomationProperties.SetAccessibilityView(_highContrastStatus, AccessibilityView.Raw);

        _textScaleStatus = new TextBlock
        {
            Text = "text-scale:1",
            Opacity = 0,
            IsHitTestVisible = false,
            Width = 1,
            Height = 1,
        };
        AutomationProperties.SetAutomationId(_textScaleStatus, "TextScaleStatus");
        AutomationProperties.SetName(_textScaleStatus, "text-scale:1");
        AutomationProperties.SetAccessibilityView(_textScaleStatus, AccessibilityView.Raw);

        _linkActivationStatus = new TextBlock
        {
            Text = "link:none",
            Opacity = 0,
            IsHitTestVisible = false,
            Width = 1,
            Height = 1,
        };
        AutomationProperties.SetAutomationId(_linkActivationStatus, "LinkActivationStatus");
        AutomationProperties.SetName(_linkActivationStatus, "link:none");
        AutomationProperties.SetAccessibilityView(_linkActivationStatus, AccessibilityView.Raw);

        _themeStatus = new TextBlock
        {
            Text = "theme:pending",
            Opacity = 0,
            IsHitTestVisible = false,
            Width = 1,
            Height = 1,
        };
        AutomationProperties.SetAutomationId(_themeStatus, "ThemeStatus");
        AutomationProperties.SetName(_themeStatus, "theme:pending");
        AutomationProperties.SetAccessibilityView(_themeStatus, AccessibilityView.Raw);

        _currentSampleStatus = new TextBlock
        {
            Text = "page:pending",
            Opacity = 0,
            IsHitTestVisible = false,
            Width = 1,
            Height = 1,
        };
        AutomationProperties.SetAutomationId(_currentSampleStatus, "CurrentSamplePage");
        AutomationProperties.SetName(_currentSampleStatus, "page:pending");
        AutomationProperties.SetAccessibilityView(_currentSampleStatus, AccessibilityView.Raw);

        Grid.SetColumn(_editor, 0);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(_rendererHost, 2);
        contentGrid.Children.Add(_editor);
        contentGrid.Children.Add(splitter);
        contentGrid.Children.Add(_rendererHost);

        var workspace = new Grid();
        workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        workspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_diagnosticsBar, 0);
        Grid.SetRow(contentGrid, 1);
        Grid.SetRow(_realizedCountStatus, 1);
        Grid.SetRow(_flowDirectionStatus, 1);
        Grid.SetRow(_highContrastStatus, 1);
        Grid.SetRow(_textScaleStatus, 1);
        Grid.SetRow(_linkActivationStatus, 1);
        Grid.SetRow(_themeStatus, 1);
        Grid.SetRow(_currentSampleStatus, 1);
        workspace.Children.Add(_diagnosticsBar);
        workspace.Children.Add(contentGrid);
        workspace.Children.Add(_realizedCountStatus);
        workspace.Children.Add(_flowDirectionStatus);
        workspace.Children.Add(_highContrastStatus);
        workspace.Children.Add(_textScaleStatus);
        workspace.Children.Add(_linkActivationStatus);
        workspace.Children.Add(_themeStatus);
        workspace.Children.Add(_currentSampleStatus);
        navigationView.Content = workspace;

        NavigationViewItem? initialItem = null;
        SampleDefinition? initialPage = null;
        foreach (SampleGroup group in SampleCatalog.Groups)
        {
            navigationView.MenuItems.Add(new NavigationViewItemHeader
            {
                Content = SampleStrings.Get(group.TitleResourceKey, group.FallbackTitle),
            });

            foreach (SampleDefinition page in group.Pages)
            {
                string title = SampleStrings.Get(page.TitleResourceKey, page.FallbackTitle);
                var item = new NavigationViewItem
                {
                    Content = title,
                    Icon = new FontIcon { Glyph = page.IconGlyph },
                    SelectsOnInvoked = true,
                    Tag = page,
                };
                AutomationProperties.SetAutomationId(item, "SampleNav_" + page.Key);
                AutomationProperties.SetName(item, title);
                ToolTipService.SetToolTip(item, title);
                navigationView.MenuItems.Add(item);

                if (page.Key == "FullDemo")
                {
                    initialItem = item;
                    initialPage = page;
                }
            }
        }

        navigationView.SelectedItem = initialItem;
        navigationView.SelectionChanged += OnSampleNavigationChanged;
        Content = navigationView;
        navigationView.Loaded += OnNavigationLoaded;

        if (initialPage is not null)
            NavigateToSample(initialPage);
    }

    private void OnNavigationLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement root)
            return;

        root.Loaded -= OnNavigationLoaded;
        _ = root.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            WarmSvgRenderer);
    }

    private void WarmSvgRenderer() => _ = WarmSvgRendererCoreAsync();

    private async System.Threading.Tasks.Task WarmSvgRendererCoreAsync()
    {
        try
        {
            await _svgRenderer.WarmUpAsync();
        }
        catch (Exception)
        {
            // The sample keeps its accessible SVG fallbacks. Opening a sample
            // must never fail because an optional architecture worker is absent.
        }
    }

    private MarkdownRendererControl CreateRenderer(bool ownsViewport)
    {
        MarkdownRendererControl renderer = ownsViewport
            ? new MarkdownScrollView()
            : new MarkdownDocumentView();
        renderer.CommandProvider = new SampleTaskCommandProvider(
            () => _editor.Text ?? string.Empty,
            value => _editor.Text = value);
        renderer.Engine = _engine;
        renderer.SvgRenderer = _svgRenderer;
        renderer.HostedElementFactory = SampleHostedElementFactory.Instance;
        renderer.Markdown = string.Empty;
        renderer.Theme = new MarkdownTheme();
        renderer.Margin = new Thickness(0);
        renderer.Padding = (Thickness)Application.Current.Resources["SampleRendererPadding"];
        if (renderer is MarkdownScrollView scrollingView)
        {
            scrollingView
                .UseGitHubFlavoredMarkdown(_engine)
                .UseMarkdownExtra(_engine)
                .UseSafeHtml(SafeHtmlOptions.Default);
            scrollingView.UseTextMateSyntaxHighlighting(_textMateHighlighter);
        }
        else if (renderer is MarkdownDocumentView documentView)
        {
            documentView
                .UseGitHubFlavoredMarkdown(_engine)
                .UseMarkdownExtra(_engine)
                .UseSafeHtml(SafeHtmlOptions.Default);
            documentView.UseTextMateSyntaxHighlighting(_textMateHighlighter);
        }
        RendererDisposalEvidence.Attach(renderer);
        MathRegressionEvidence.Attach(renderer);
        AutomationProperties.SetAutomationId(renderer, "MarkdownRenderer");
        AutomationProperties.SetName(
            renderer,
            SampleStrings.Get("RendererAutomationName", "Rendered markdown preview"));
        renderer.LinkClick += OnRendererLinkClick;
        renderer.EmbedsRealizationChanged += (_, _) =>
        {
            if (!ReferenceEquals(renderer, _renderer) || _realizedCountStatus is null)
                return;

            int n = renderer.RealizedEmbedCount;
            string s = "realized:" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _realizedCountStatus.Text = s;
            AutomationProperties.SetName(_realizedCountStatus, s);
        };
        renderer.RenderCompleted += (_, _) =>
        {
            if (!ReferenceEquals(renderer, _renderer))
                return;

            if (_resetPreviewScrollOnRender)
            {
                _resetPreviewScrollOnRender = false;
                renderer.ScrollToBlock(FirstDocumentBlockIndex);
            }

            UpdateDiagnostics();
            UpdateThemeStatus();
        };
        renderer.RenderFailed += (_, e) =>
        {
            if (ReferenceEquals(renderer, _renderer))
                ShowRenderFailure(e.Exception);
        };
        return renderer;
    }

    private void AttachRendererToHost(MarkdownRendererControl renderer, bool ownsViewport)
    {
        _rendererHost.Children.Clear();
        if (ownsViewport)
        {
            _rendererHost.Children.Add(renderer);
            return;
        }

        var pageViewport = new ScrollViewer
        {
            Content = renderer,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            ZoomMode = ZoomMode.Disabled,
        };
        AutomationProperties.SetAutomationId(pageViewport, "TouchSelectionAncestorViewport");
        AutomationProperties.SetName(
            pageViewport,
            SampleStrings.Get("AncestorViewportAutomationName", "Page-owned markdown viewport"));
        _rendererHost.Children.Add(pageViewport);
    }

    private void ReplaceRenderer(bool ownsViewport)
    {
        if (_rendererHost is null || _renderer is null || _isLoadingSample)
            return;

        string source = _editor.Text ?? string.Empty;
        FlowDirection flowDirection = _renderer.FlowDirection;
        MarkdownTheme theme = _renderer.Theme ?? new MarkdownTheme();
        bool taskEditing = _renderer.IsTaskListEditingEnabled;
        var previous = _renderer;
        var replacement = CreateRenderer(ownsViewport);
        replacement.FlowDirection = flowDirection;
        replacement.Theme = theme;
        replacement.IsTaskListEditingEnabled = taskEditing;
        _renderer = replacement;
        AttachRendererToHost(replacement, ownsViewport);
        previous.Dispose();
        _lastRenderedEditorText = source;
        _resetPreviewScrollOnRender = true;
        replacement.Markdown = source;
        UpdateFlowDirectionStatus();
        MarkThemeStatusPending();
    }

    private void OnSampleNavigationChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (!args.IsSettingsSelected && args.SelectedItemContainer?.Tag is SampleDefinition page)
            NavigateToSample(page);
    }

    private void NavigateToSample(SampleDefinition page)
    {
        // Diagnostics belong to the page that produced them. Hide them as soon
        // as navigation starts so they cannot be mistaken for failures in the
        // next destination while its replacement document is rendering.
        ClearDiagnostics();

        bool isListsPage = string.Equals(page.Key, "Lists", StringComparison.Ordinal);
        bool isTouchSelectionPage = string.Equals(page.Key, "TouchSelection", StringComparison.Ordinal);
        bool supportsTaskEditing = isListsPage || isTouchSelectionPage;
        _taskEditingToggle.Visibility = supportsTaskEditing ? Visibility.Visible : Visibility.Collapsed;
        _viewportOwnershipToggle.Visibility = isTouchSelectionPage ? Visibility.Visible : Visibility.Collapsed;
        if (!supportsTaskEditing)
            _taskEditingToggle.IsChecked = false;
        if (!isTouchSelectionPage && _viewportOwnershipToggle.IsChecked == true)
            _viewportOwnershipToggle.IsChecked = false;

        _currentPageTitle = SampleStrings.Get(page.TitleResourceKey, page.FallbackTitle);
        UpdatePageTitle(modified: false);

        string status = "page:" + page.Key;
        _currentSampleStatus.Text = status;
        AutomationProperties.SetName(_currentSampleStatus, status);

        string source = page.SourceFactory();
        _isLoadingSample = true;
        try
        {
            _editor.Text = source;
            // TextBox normalizes line endings. Use its stored value as the clean
            // baseline so a deferred TextChanged event does not mark navigation
            // loads as user edits.
            _currentSampleSource = _editor.Text ?? string.Empty;
            _lastRenderedEditorText = _currentSampleSource;
            _editor.SelectionStart = 0;
            _editor.SelectionLength = 0;
            // Sample destinations are independent documents. Reset the old
            // viewport before replacing the source so document-level scroll
            // anchoring cannot carry a position from one page into another.
            _renderer.ScrollToBlock(FirstDocumentBlockIndex);
            _resetPreviewScrollOnRender = true;
            _renderer.Markdown = source;
        }
        finally
        {
            _isLoadingSample = false;
        }
    }

    private void UpdatePageTitle(bool modified)
    {
        string title = modified
            ? _currentPageTitle + SampleStrings.Get("ModifiedSuffix", " — modified")
            : _currentPageTitle;
        _pageTitle.Text = title;
        AutomationProperties.SetName(_pageTitle, title);
    }

    private void ClearDiagnostics()
    {
        bool hadAnnouncedDiagnostics = _diagnosticsBar.IsOpen &&
            !string.IsNullOrEmpty(_lastAnnouncedDiagnosticSummary);
        if (hadAnnouncedDiagnostics)
        {
            string notification = SampleStrings.Get(
                "DiagnosticsClearedNotification",
                "No renderer diagnostics.");
            AutomationPeer? peer =
                FrameworkElementAutomationPeer.FromElement(_diagnosticsBar) ??
                FrameworkElementAutomationPeer.CreatePeerForElement(_diagnosticsBar);
            peer?.RaiseNotificationEvent(
                AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.MostRecent,
                notification,
                DiagnosticsNotificationActivityId);
        }

        _diagnosticsBar.IsOpen = false;
        _diagnosticsBar.Title = string.Empty;
        _diagnosticsMessage.Text = string.Empty;
        _lastAnnouncedDiagnosticSummary = string.Empty;
        AutomationProperties.SetName(_diagnosticsMessage, string.Empty);
        AutomationProperties.SetName(
            _diagnosticsBar,
            SampleStrings.Get("DiagnosticsAutomationName", "Renderer diagnostics"));
    }

    private async void OnRendererLinkClick(object? sender, MarkdownLinkClickEventArgs e)
    {
        string activation = string.Concat(
            "link:",
            e.InputKind.ToString(),
            ":",
            e.Url,
            ":",
            e.Action ?? string.Empty);

        if (string.Equals(e.Url, "https://example.invalid/mermaid-node", StringComparison.Ordinal) ||
            string.Equals(e.Url, "https://example.invalid/touch-link", StringComparison.Ordinal))
        {
            e.Handled = true;
            UpdateLinkActivationStatus(activation + ":sample-handled");
            return;
        }

        if (!IsAllowedExternalUri(e.Uri))
        {
            e.Handled = true;
            UpdateLinkActivationStatus(activation + ":blocked");
            return;
        }

        e.Handled = true;
        try
        {
            bool launched = await Windows.System.Launcher.LaunchUriAsync(e.Uri);
            UpdateLinkActivationStatus(activation + (launched ? ":launched" : ":launch-failed"));
        }
        catch
        {
            UpdateLinkActivationStatus(activation + ":launch-failed");
        }
    }

    private static bool IsAllowedExternalUri(Uri? uri)
    {
        if (uri is not { IsAbsoluteUri: true })
            return false;

        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Scheme, Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateLinkActivationStatus(string status)
    {
        if (_linkActivationStatus is null)
            return;

        _linkActivationStatus.Text = status;
        AutomationProperties.SetName(_linkActivationStatus, status);
    }

    private void UpdateDiagnostics()
    {
        IReadOnlyList<MarkdownDiagnostic> diagnostics =
            _renderer.Document?.Diagnostics ?? Array.Empty<MarkdownDiagnostic>();
        if (diagnostics.Count == 0)
        {
            ClearDiagnostics();
            return;
        }

        MarkdownDiagnosticSeverity highestSeverity = MarkdownDiagnosticSeverity.Information;
        int highestSeverityIndex = 0;
        var message = new System.Text.StringBuilder();
        for (int index = 0; index < diagnostics.Count; index++)
        {
            if (diagnostics[index].Severity > highestSeverity)
            {
                highestSeverity = diagnostics[index].Severity;
                highestSeverityIndex = index;
            }
        }

        int displayedCount = System.Math.Min(diagnostics.Count, MaximumDisplayedDiagnostics);
        for (int index = 0; index < displayedCount; index++)
        {
            int diagnosticIndex = diagnostics.Count > MaximumDisplayedDiagnostics &&
                                  highestSeverityIndex >= MaximumDisplayedDiagnostics &&
                                  index == displayedCount - 1
                ? highestSeverityIndex
                : index;
            MarkdownDiagnostic diagnostic = diagnostics[diagnosticIndex];
            if (index > 0)
                message.AppendLine();
            message.Append(FormatDiagnosticLine(diagnostic));
        }

        if (diagnostics.Count > displayedCount)
        {
            message.AppendLine()
                .Append(string.Format(
                    System.Globalization.CultureInfo.CurrentUICulture,
                    SampleStrings.Get("DiagnosticsRemainingFormat", "Additional diagnostics not shown: {0}"),
                    diagnostics.Count - displayedCount));
        }

        string title = string.Format(
            System.Globalization.CultureInfo.CurrentUICulture,
            SampleStrings.Get("DiagnosticsTitleFormat", "Renderer diagnostics ({0})"),
            diagnostics.Count);
        _diagnosticsBar.Title = title;
        _diagnosticsBar.Severity = highestSeverity switch
        {
            MarkdownDiagnosticSeverity.Error => InfoBarSeverity.Error,
            MarkdownDiagnosticSeverity.Warning => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational,
        };
        string messageText = message.ToString();
        _diagnosticsMessage.Text = messageText;
        AutomationProperties.SetName(_diagnosticsMessage, messageText);
        MarkdownDiagnostic summaryDiagnostic = diagnostics[highestSeverityIndex];

        OpenDiagnostics(string.Format(
            System.Globalization.CultureInfo.CurrentUICulture,
            SampleStrings.Get("DiagnosticsAutomationSummaryFormat", "{0}. {1}"),
            title,
            FormatDiagnosticLine(summaryDiagnostic)));
    }

    private void ShowRenderFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string title = SampleStrings.Get("RenderFailedTitle", "Render failed");
        _diagnosticsBar.Title = title;
        _diagnosticsBar.Severity = InfoBarSeverity.Error;
        string message = string.Format(
            System.Globalization.CultureInfo.CurrentUICulture,
            SampleStrings.Get("RenderFailureMessageFormat", "{0}: {1}"),
            exception.GetType().Name,
            exception.Message);
        _diagnosticsMessage.Text = message;
        AutomationProperties.SetName(_diagnosticsMessage, message);
        OpenDiagnostics(string.Format(
            System.Globalization.CultureInfo.CurrentUICulture,
            SampleStrings.Get("DiagnosticsAutomationSummaryFormat", "{0}. {1}"),
            title,
            message));
    }

    private void OpenDiagnostics(string accessibleSummary)
    {
        AutomationProperties.SetName(_diagnosticsBar, accessibleSummary);
        _diagnosticsBar.IsOpen = true;
        if (string.Equals(
                accessibleSummary,
                _lastAnnouncedDiagnosticSummary,
                StringComparison.Ordinal))
        {
            return;
        }

        _lastAnnouncedDiagnosticSummary = accessibleSummary;
        AutomationPeer? peer =
            FrameworkElementAutomationPeer.FromElement(_diagnosticsBar) ??
            FrameworkElementAutomationPeer.CreatePeerForElement(_diagnosticsBar);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private static string FormatDiagnosticLine(MarkdownDiagnostic diagnostic) => string.Format(
        System.Globalization.CultureInfo.CurrentUICulture,
        SampleStrings.Get("DiagnosticLineFormat", "{0} {1} [{2}..{3}): {4}"),
        GetDiagnosticSeverityLabel(diagnostic.Severity),
        diagnostic.Code,
        diagnostic.SourceSpan.Start.ToString(System.Globalization.CultureInfo.InvariantCulture),
        diagnostic.SourceSpan.End.ToString(System.Globalization.CultureInfo.InvariantCulture),
        diagnostic.Message);

    private static string GetDiagnosticSeverityLabel(MarkdownDiagnosticSeverity severity) => severity switch
    {
        MarkdownDiagnosticSeverity.Error => SampleStrings.Get("DiagnosticsError", "Error"),
        MarkdownDiagnosticSeverity.Warning => SampleStrings.Get("DiagnosticsWarning", "Warning"),
        _ => SampleStrings.Get("DiagnosticsInformation", "Information"),
    };

    private void UpdateThemeStatus()
    {
        if (_themeStatus is null || _renderer.CurrentThemeSnapshot is not { } snapshot)
            return;

        string status = snapshot.IsHighContrast
            ? "theme:high-contrast"
            : snapshot.IsDark ? "theme:dark" : "theme:light";
        _themeStatus.Text = status;
        AutomationProperties.SetName(_themeStatus, status);
    }

    private void UpdateHighContrastStatus(bool enabled)
    {
        if (_highContrastStatus is null) return;
        string s = enabled
            ? "hc:on;fg:#FFFFFF;bg:#000000;hotlight:#00FFFF;highlight:#FFFF00;highlightText:#000000"
            : "hc:off";
        _highContrastStatus.Text = s;
        AutomationProperties.SetName(_highContrastStatus, s);
    }

    private void UpdateFlowDirectionStatus()
    {
        if (_flowDirectionStatus is null || _renderer is null) return;
        string s = _renderer.FlowDirection == FlowDirection.RightToLeft ? "flow:rtl" : "flow:ltr";
        _flowDirectionStatus.Text = s;
        AutomationProperties.SetName(_flowDirectionStatus, s);
    }

    private void ApplyTextScale(float scale)
    {
        if (_renderer?.Theme is not { } theme)
            return;

        MarkThemeStatusPending();
        var sizes = new Dictionary<string, float>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Heading1] = 32,
            [MarkdownElementKeys.Heading2] = 26,
            [MarkdownElementKeys.Heading3] = 22,
            [MarkdownElementKeys.Heading4] = 18,
            [MarkdownElementKeys.Heading5] = 15,
            [MarkdownElementKeys.Heading6] = 14,
            [MarkdownElementKeys.Body] = 14,
            [MarkdownElementKeys.CodeInline] = 12,
            [MarkdownElementKeys.CodeBlock] = 13,
            [MarkdownElementKeys.CodeBlockLanguage] = 12,
            [MarkdownElementKeys.CodeBlockLineNumber] = 12,
            [MarkdownElementKeys.Link] = 14,
            [MarkdownElementKeys.Strong] = 14,
            [MarkdownElementKeys.Emphasis] = 14,
            [MarkdownElementKeys.Strikethrough] = 14,
            [MarkdownElementKeys.ListMarker] = 14,
            [MarkdownElementKeys.TableHeader] = 14,
            [MarkdownElementKeys.TableCell] = 14,
            [MarkdownElementKeys.ImageCaption] = 12,
            [MarkdownElementKeys.Diagram] = 13,
            [MarkdownElementKeys.Math] = 14,
        };

        using (theme.BeginUpdate())
        {
            foreach (var (key, baseSize) in sizes)
                theme.Overrides[key] = new ElementStyleOverride { FontSize = baseSize * scale };
        }

        if (_textScaleStatus is not null)
        {
            string status = $"text-scale:{scale:0.#}";
            _textScaleStatus.Text = status;
            AutomationProperties.SetName(_textScaleStatus, status);
        }
    }

    private void SetTheme(ElementTheme theme)
    {
        MarkThemeStatusPending();
        if (Content is FrameworkElement fe) fe.RequestedTheme = theme;
    }

    private void MarkThemeStatusPending()
    {
        if (_themeStatus is null)
            return;

        const string status = "theme:pending";
        _themeStatus.Text = status;
        AutomationProperties.SetName(_themeStatus, status);
    }

    private sealed class SampleTaskCommandProvider(
        Func<string> getSource,
        Action<string> setSource) : IMarkdownCommandProvider
    {
        private readonly ICommand _command = new SampleTaskCommand(getSource, setSource);

        public ICommand? GetCommand(MarkdownCommandContext context) =>
            context.Kind == MarkdownCommandKind.ToggleTask ? _command : null;
    }

    private sealed class SampleTaskCommand(
        Func<string> getSource,
        Action<string> setSource) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) =>
            parameter is MarkdownCommandContext context &&
            TryCreateReplacement(getSource(), context, out _);

        public void Execute(object? parameter)
        {
            if (parameter is not MarkdownCommandContext context)
                return;

            string source = getSource();
            if (!TryCreateReplacement(source, context, out string replacement))
                return;

            setSource(string.Concat(
                source.AsSpan(0, context.SourceRange.Start),
                replacement,
                source.AsSpan(context.SourceRange.End)));
        }

        private static bool TryCreateReplacement(
            string source,
            MarkdownCommandContext context,
            out string replacement)
        {
            replacement = context.Target switch
            {
                "checked" => "[x]",
                "unchecked" => "[ ]",
                _ => string.Empty,
            };
            SourceSpan range = context.SourceRange;
            return replacement.Length == 3 &&
                   range.Length == 3 &&
                   range.Start >= 0 &&
                   range.End <= source.Length &&
                   source[range.Start] == '[' &&
                   source[range.End - 1] == ']';
        }
    }
}
#pragma warning restore MR1001

