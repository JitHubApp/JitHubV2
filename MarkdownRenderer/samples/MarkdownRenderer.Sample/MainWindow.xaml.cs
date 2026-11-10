using System;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Sample.Models.Samples;
using MarkdownRenderer.Sample.Services.Automation;
using MarkdownRenderer.Sample.Services.Links;
using MarkdownRenderer.Sample.Services.Localization;
using MarkdownRenderer.Sample.Services.Providers;
using MarkdownRenderer.Sample.Services.Theming;
using MarkdownRenderer.Sample.Views.Workspace;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MarkdownRenderer.Sample;

/// <summary>
/// The feature showcase window: a navigation shell over a source editor and
/// a live preview. The window is the composition root; it builds the shell
/// layout and wires the parts that own the behavior: the preview views
/// (<see cref="SampleRendererHost"/>), the editor binding
/// (<see cref="SampleEditorSession"/>), the providers
/// (<see cref="SampleProviderSet"/>), the header commands, the diagnostics
/// InfoBar and the hidden automation probes.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly SampleStrings _strings = SampleStrings.Load();
    private readonly SampleCatalog _catalog = new();
    private readonly RendererDisposalEvidence _disposalEvidence = RendererDisposalEvidence.FromEnvironment();
    private readonly SampleProviderSet _providers = new();
    private readonly SampleAutomationStatus _status = new();
    private readonly SampleDiagnosticsPresenter _diagnostics;
    private readonly SampleRendererHost _host;
    private readonly SampleEditorSession _session;
    private readonly SampleDisplayCommands _displayCommands;
    private readonly SampleProviderCommands _providerCommands;
    private readonly Grid _shellRoot;

    public MainWindow()
    {
        Title = _strings.Get("AppTitle", "MarkdownRenderer — Feature Showcase");

        _diagnostics = new SampleDiagnosticsPresenter(_strings);
        var touchObservation = new TouchContactObservation(_status.TouchContact);
        var linkActivation = new SampleLinkActivation(_status);
        TextBox editor = CreateEditor();
        TextBlock pageTitle = CreatePageTitle();
        _host = new SampleRendererHost(
            _strings,
            _providers,
            _status,
            _disposalEvidence,
            touchObservation,
            editor,
            OnRenderCompleted,
            _diagnostics.ShowRenderFailure,
            linkActivation.OnLinkClick);
        _session = new SampleEditorSession(_strings, editor, pageTitle, _host, _status);
        _shellRoot = new Grid
        {
            Style = (Style)Application.Current.Resources["SampleShellRootStyle"],
        };
        // The application/window host remains on the startup theme. The theme
        // command gives the complete sample shell one explicit theme boundary
        // and opaque system-themed backdrop so translucent WinUI surfaces do
        // not blend dark foregrounds over the host's Light background.
        _displayCommands = new SampleDisplayCommands(
            _strings,
            _host,
            _status,
            theme => _shellRoot.RequestedTheme = theme,
            _session.ReplaceRenderer);
        _providerCommands = new SampleProviderCommands(
            _strings,
            _providers,
            _host,
            _status,
            _session.ReplaceRenderer,
            () => _session.Source);

        NavigationView navigationView = CreateNavigationView();
        navigationView.Header = CreatePageHeader(pageTitle);
        navigationView.Content = CreateWorkspace(editor);
        SampleDefinition? initialPage = AddNavigationItems(navigationView);
        navigationView.SelectionChanged += OnSampleNavigationChanged;
        _shellRoot.Children.Add(navigationView);
        Content = _shellRoot;
        navigationView.Loaded += OnNavigationLoaded;
        Closed += OnClosed;

        if (initialPage is not null)
            NavigateToSample(initialPage);
    }

    private NavigationView CreateNavigationView()
    {
        var navigationView = new NavigationView
        {
            AlwaysShowHeader = true,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false,
            PaneDisplayMode = NavigationViewPaneDisplayMode.Auto,
            PaneTitle = _strings.Get("NavigationPaneTitle", "Sample pages"),
        };
        MarkdownSystemIntegration.SetUseSystemFlowDirection(navigationView, true);
        AutomationProperties.SetAutomationId(navigationView, "SampleNavigation");
        AutomationProperties.SetName(
            navigationView,
            _strings.Get("NavigationAutomationName", "Markdown renderer feature pages"));
        return navigationView;
    }

    private TextBlock CreatePageTitle()
    {
        var pageTitle = new TextBlock
        {
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
            Text = _strings.Get("PageFullDemo", "Full demo"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(pageTitle, "SamplePageTitle");
        AutomationProperties.SetName(pageTitle, pageTitle.Text);
        AutomationProperties.SetHeadingLevel(pageTitle, AutomationHeadingLevel.Level1);
        return pageTitle;
    }

    // The title and display commands share the first row; the Providers
    // page's commands take the second.
    private Grid CreatePageHeader(TextBlock pageTitle)
    {
        var pageHeader = new Grid
        {
            Margin = (Thickness)Application.Current.Resources["SamplePageHeaderMargin"],
        };
        pageHeader.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        pageHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pageHeader.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pageHeader.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(_displayCommands.Element, 1);
        Grid.SetRow(_providerCommands.Element, 1);
        Grid.SetColumnSpan(_providerCommands.Element, 2);
        pageHeader.Children.Add(pageTitle);
        pageHeader.Children.Add(_displayCommands.Element);
        pageHeader.Children.Add(_providerCommands.Element);
        return pageHeader;
    }

    private TextBox CreateEditor()
    {
        var editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = (double)Application.Current.Resources["SampleEditorFontSize"],
            Padding = (Thickness)Application.Current.Resources["SampleEditorPadding"],
            Text = string.Empty,
        };
        AutomationProperties.SetAutomationId(editor, "MarkdownEditor");
        AutomationProperties.SetName(
            editor,
            _strings.Get("EditorAutomationName", "Markdown source editor"));
        ScrollViewer.SetHorizontalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        return editor;
    }

    // The diagnostics InfoBar sits above the editor | splitter | preview
    // split; the hidden automation probes share the split's row.
    private Grid CreateWorkspace(TextBox editor)
    {
        var contentGrid = new Grid();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(
                (double)Application.Current.Resources["SampleWorkspaceSplitterWidth"],
                GridUnitType.Pixel),
        });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        SampleExtensionResources.ApplyTo(contentGrid);

        var splitter = new Border
        {
            Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        };
        Grid previewColumn = _host.CreatePreviewColumn();
        Grid.SetColumn(editor, 0);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(previewColumn, 2);
        contentGrid.Children.Add(editor);
        contentGrid.Children.Add(splitter);
        contentGrid.Children.Add(previewColumn);

        var workspace = new Grid();
        workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        workspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_diagnostics.Element, 0);
        Grid.SetRow(contentGrid, 1);
        workspace.Children.Add(_diagnostics.Element);
        workspace.Children.Add(contentGrid);
        _status.AddTo(workspace, row: 1);
        return workspace;
    }

    // Returns the initial page, the full demo, whose item starts selected.
    private SampleDefinition? AddNavigationItems(NavigationView navigationView)
    {
        NavigationViewItem? initialItem = null;
        SampleDefinition? initialPage = null;
        foreach (SampleGroup group in _catalog.Groups)
        {
            navigationView.MenuItems.Add(new NavigationViewItemHeader
            {
                Content = _strings.Get(group.TitleResourceKey, group.FallbackTitle),
            });

            foreach (SampleDefinition page in group.Pages)
            {
                string title = _strings.Get(page.TitleResourceKey, page.FallbackTitle);
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

                if (page.Key == SamplePageKeys.FullDemo)
                {
                    initialItem = item;
                    initialPage = page;
                }
            }
        }

        navigationView.SelectedItem = initialItem;
        return initialPage;
    }

    private void OnNavigationLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement root)
            return;

        root.Loaded -= OnNavigationLoaded;
        _ = root.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => _ = _providers.WarmUpSvgRendererAsync());
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
        _diagnostics.Clear();

        bool isListsPage = string.Equals(page.Key, SamplePageKeys.Lists, StringComparison.Ordinal);
        bool isTouchSelectionPage = string.Equals(page.Key, SamplePageKeys.TouchSelection, StringComparison.Ordinal);
        bool isProgressiveScenesPage = string.Equals(page.Key, SamplePageKeys.ProgressiveScenes, StringComparison.Ordinal);
        bool isProvidersPage = string.Equals(page.Key, SamplePageKeys.Providers, StringComparison.Ordinal);
        _displayCommands.ShowForPage(
            supportsTaskEditing: isListsPage || isTouchSelectionPage,
            supportsViewportOwnership: isTouchSelectionPage || isProvidersPage);
        _providerCommands.ShowForPage(isProvidersPage);
        _host.ObserveTouch(isTouchSelectionPage);
        _session.Load(page, useProgressiveScenes: isProgressiveScenesPage);
    }

    private void OnRenderCompleted()
    {
        _diagnostics.Show(_host.Renderer.Document, _session.ActiveDeferredSceneSession);
        if (_host.Renderer.RenderedThemeVariant is { } variant)
            _status.SetRenderedTheme(variant);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _host.Dispose();
        _providers.Dispose();
        _session.Dispose();
    }
}
