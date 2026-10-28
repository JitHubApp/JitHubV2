using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Html;
using MarkdownRenderer.Math;
using MarkdownRenderer.Mermaid;
using MarkdownRenderer.SyntaxHighlighting.TextMate;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Sample;

// The sample switches between both concrete viewport owners through their
// shared base type, as MainWindow.xaml.cs does.

// The Providers page swaps the renderer's engine, grammar provider, hosted
// factory and optional providers while documents are live, and adds a second
// view that shares them. The Sandbox UI tests drive these commands.
public sealed partial class MainWindow
{
    private const string ProvidersPageKey = "Providers";

    // Retired providers stay alive until the window closes: a view may still
    // hold work that started on them, and the host, not the view, owns them.
    private readonly List<IDisposable> _retiredProviders = [];
    private MarkdownEngine? _replacementEngine;
    private MarkdownEngine? _minimalEngine;
    private int _engineGeneration = 1;
    private int _highlighterGeneration = 1;
    private bool _minimalProviders;
    private bool _alternateHostedFactory;
    private bool _withoutCSharpGrammar;
    private MarkdownScrollView? _secondRenderer;
    private Border? _secondViewHost;
    private RowDefinition? _secondViewRow;
    private CommandBar? _providerControls;
    private AppBarButton? _replaceEngineButton;
    private AppBarToggleButton? _withoutCSharpGrammarToggle;
    private AppBarToggleButton? _alternateFactoryToggle;
    private AppBarToggleButton? _minimalProvidersToggle;
    private AppBarToggleButton? _secondViewToggle;
    private TextBlock? _providerStatus;

    private MarkdownEngine ActiveEngine => _replacementEngine ?? _engine;

    private SampleHostedElementFactory ActiveHostedFactory => _alternateHostedFactory
        ? SampleHostedElementFactory.Alternate
        : SampleHostedElementFactory.Instance;

    private static MarkdownEngine CreateFullEngine()
        => new MarkdownEngineBuilder()
            .UseGitHubReadme(SafeHtmlOptions.Default)
            .UseMarkdownExtra()
            .UseMathematics()
            .UseMermaid()
            .UseExtension(SampleHostedElementExtension.Instance)
            .Build();

    private CommandBar CreateProviderControls()
    {
        var controls = new CommandBar
        {
            IsDynamicOverflowEnabled = false,
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(controls, "ProviderControls");
        AutomationProperties.SetName(
            controls,
            SampleStrings.Get("ProviderControlsAutomationName", "Provider configuration controls"));

        _replaceEngineButton = new AppBarButton
        {
            Icon = new FontIcon { Glyph = "" },
            Label = SampleStrings.Get("CommandReplaceEngine", "Replace engine"),
        };
        AutomationProperties.SetAutomationId(_replaceEngineButton, "ProviderReplaceEngine");
        _replaceEngineButton.Click += (_, _) => ReplaceEngine();
        controls.PrimaryCommands.Add(_replaceEngineButton);

        _withoutCSharpGrammarToggle = new AppBarToggleButton
        {
            Icon = new FontIcon { Glyph = "" },
            Label = SampleStrings.Get("CommandWithoutCSharpGrammar", "No C# grammar"),
        };
        AutomationProperties.SetAutomationId(_withoutCSharpGrammarToggle, "ProviderWithoutCSharpGrammar");
        AutomationProperties.SetName(
            _withoutCSharpGrammarToggle,
            SampleStrings.Get("CommandWithoutCSharpGrammarAutomationName", "Replace the grammar provider with one that has no C# grammar"));
        _withoutCSharpGrammarToggle.Checked += (_, _) => ReplaceHighlighter(withoutCSharp: true);
        _withoutCSharpGrammarToggle.Unchecked += (_, _) => ReplaceHighlighter(withoutCSharp: false);
        controls.PrimaryCommands.Add(_withoutCSharpGrammarToggle);

        _alternateFactoryToggle = new AppBarToggleButton
        {
            Icon = new FontIcon { Glyph = "" },
            Label = SampleStrings.Get("CommandAlternateFactory", "Alternate controls"),
        };
        AutomationProperties.SetAutomationId(_alternateFactoryToggle, "ProviderAlternateFactory");
        AutomationProperties.SetName(
            _alternateFactoryToggle,
            SampleStrings.Get("CommandAlternateFactoryAutomationName", "Replace the hosted control factory"));
        _alternateFactoryToggle.Checked += (_, _) => SetAlternateHostedFactory(true);
        _alternateFactoryToggle.Unchecked += (_, _) => SetAlternateHostedFactory(false);
        controls.PrimaryCommands.Add(_alternateFactoryToggle);

        _minimalProvidersToggle = new AppBarToggleButton
        {
            Icon = new FontIcon { Glyph = "" },
            Label = SampleStrings.Get("CommandMinimalProviders", "Minimal providers"),
        };
        AutomationProperties.SetAutomationId(_minimalProvidersToggle, "ProviderMinimal");
        AutomationProperties.SetName(
            _minimalProvidersToggle,
            SampleStrings.Get("CommandMinimalProvidersAutomationName", "Use strict GFM without optional providers"));
        _minimalProvidersToggle.Checked += (_, _) => SetMinimalProviders(true);
        _minimalProvidersToggle.Unchecked += (_, _) => SetMinimalProviders(false);
        controls.PrimaryCommands.Add(_minimalProvidersToggle);

        _secondViewToggle = new AppBarToggleButton
        {
            Icon = new FontIcon { Glyph = "" },
            Label = SampleStrings.Get("CommandSecondView", "Second view"),
        };
        AutomationProperties.SetAutomationId(_secondViewToggle, "ProviderSecondView");
        AutomationProperties.SetName(
            _secondViewToggle,
            SampleStrings.Get("CommandSecondViewAutomationName", "Show a second view that shares the providers"));
        _secondViewToggle.Checked += (_, _) => SetSecondView(true);
        _secondViewToggle.Unchecked += (_, _) => SetSecondView(false);
        controls.PrimaryCommands.Add(_secondViewToggle);

        _providerStatus = new TextBlock
        {
            Opacity = 0,
            IsHitTestVisible = false,
            Width = 1,
            Height = 1,
        };
        AutomationProperties.SetAutomationId(_providerStatus, "ProviderStatus");
        AutomationProperties.SetAccessibilityView(_providerStatus, AccessibilityView.Raw);
        UpdateProviderStatus();

        _providerControls = controls;
        return controls;
    }

    private Grid CreatePreviewColumn()
    {
        var column = new Grid();
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _secondViewRow = new RowDefinition { Height = new GridLength(0) };
        column.RowDefinitions.Add(_secondViewRow);
        _secondViewHost = new Border
        {
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = (Thickness)Application.Current.Resources["SampleSecondViewBorderThickness"],
            Visibility = Visibility.Collapsed,
        };
        Grid.SetRow(_rendererHost, 0);
        Grid.SetRow(_secondViewHost, 1);
        column.Children.Add(_rendererHost);
        column.Children.Add(_secondViewHost);
        return column;
    }

    private IEnumerable<MarkdownRendererControl> ProviderViews()
    {
        if (_renderer is not null)
            yield return _renderer;
        if (_secondRenderer is not null)
            yield return _secondRenderer;
    }

    private void ConfigureRendererProviders(MarkdownRendererControl renderer)
    {
        if (_minimalProviders)
        {
            // Strict GFM with no HTML, Math, Mermaid, SVG, raster, TextMate or
            // hosted-control providers: optional content must keep its
            // documented safe-text or original-source fallback.
            MarkdownEngine minimal = _minimalEngine ??= new MarkdownEngineBuilder()
                .UseGitHubFlavoredMarkdown()
                .Build();
            renderer.SvgRenderer = null;
            renderer.RasterDecoder = null;
            renderer.HostedElementFactory = null;
            switch (renderer)
            {
                case MarkdownScrollView scrollingView:
                    scrollingView.UseGitHubFlavoredMarkdown(minimal);
                    break;
                case MarkdownDocumentView documentView:
                    documentView.UseGitHubFlavoredMarkdown(minimal);
                    break;
            }
            return;
        }

        renderer.SvgRenderer = _svgRenderer;
        // The resvg provider also decodes WebP in its isolated worker, as JitHub
        // configures it, so the sample does not depend on an OS WebP codec.
        renderer.RasterDecoder = _svgRenderer;
        renderer.HostedElementFactory = ActiveHostedFactory;
        ApplyFullEngine(renderer, ActiveEngine);
        switch (renderer)
        {
            case MarkdownScrollView scrollingView:
                scrollingView.UseTextMateSyntaxHighlighting(_textMateHighlighter);
                break;
            case MarkdownDocumentView documentView:
                documentView.UseTextMateSyntaxHighlighting(_textMateHighlighter);
                break;
        }
    }

    private static void ApplyFullEngine(MarkdownRendererControl renderer, MarkdownEngine engine)
    {
        switch (renderer)
        {
            case MarkdownScrollView scrollingView:
                scrollingView
                    .UseGitHubFlavoredMarkdown(engine)
                    .UseMarkdownExtra(engine)
                    .UseSafeHtml(SafeHtmlOptions.Default);
                break;
            case MarkdownDocumentView documentView:
                documentView
                    .UseGitHubFlavoredMarkdown(engine)
                    .UseMarkdownExtra(engine)
                    .UseSafeHtml(SafeHtmlOptions.Default);
                break;
        }
    }

    private void ReplaceEngine()
    {
        if (_minimalProviders)
            return;

        MarkdownEngine replacement = CreateFullEngine();
        if (_replacementEngine is { } previous)
            _retiredProviders.Add(previous);
        _replacementEngine = replacement;
        _engineGeneration++;
        foreach (MarkdownRendererControl view in ProviderViews())
            ApplyFullEngine(view, replacement);
        UpdateProviderStatus();
    }

    private void ReplaceHighlighter(bool withoutCSharp)
    {
        if (_withoutCSharpGrammar == withoutCSharp)
            return;

        ITextMateGrammarProvider provider = withoutCSharp
            ? new SampleGrammarProviderWithoutCSharp()
            : new CommonTextMateGrammarProvider();
        var replacement = new TextMateCodeBlockSyntaxHighlighter(provider, options: null, ownsProvider: true);
        TextMateCodeBlockSyntaxHighlighter previous = _textMateHighlighter;
        _textMateHighlighter = replacement;
        _withoutCSharpGrammar = withoutCSharp;
        _highlighterGeneration++;
        if (!_minimalProviders)
        {
            foreach (MarkdownRendererControl view in ProviderViews())
            {
                switch (view)
                {
                    case MarkdownScrollView scrollingView:
                        scrollingView.UseTextMateSyntaxHighlighting(replacement);
                        break;
                    case MarkdownDocumentView documentView:
                        documentView.UseTextMateSyntaxHighlighting(replacement);
                        break;
                }
            }
        }
        _retiredProviders.Add(previous);
        UpdateProviderStatus();
    }

    private void SetAlternateHostedFactory(bool alternate)
    {
        if (_alternateHostedFactory == alternate)
            return;

        _alternateHostedFactory = alternate;
        if (!_minimalProviders)
        {
            foreach (MarkdownRendererControl view in ProviderViews())
                view.HostedElementFactory = ActiveHostedFactory;
        }
        UpdateProviderStatus();
    }

    private void SetMinimalProviders(bool minimal)
    {
        if (_minimalProviders == minimal)
            return;

        _minimalProviders = minimal;
        if (_replaceEngineButton is not null)
            _replaceEngineButton.IsEnabled = !minimal;
        // Optional providers are configuration of a view, so the sample swaps
        // the whole view, the way an app changes its feature set.
        ReplaceRenderer(ownsViewport: _renderer is MarkdownScrollView);
        UpdateProviderStatus();
    }

    private void SetSecondView(bool visible)
    {
        if (_secondViewHost is null || _secondViewRow is null)
            return;

        if (visible && _secondRenderer is null)
        {
            var second = (MarkdownScrollView)CreateRenderer(ownsViewport: true);
            AutomationProperties.SetAutomationId(second, "SecondMarkdownRenderer");
            AutomationProperties.SetName(
                second,
                SampleStrings.Get("SecondRendererAutomationName", "Second rendered markdown preview"));
            second.Theme = _renderer?.Theme ?? new MarkdownTheme();
            second.FlowDirection = _renderer?.FlowDirection ?? FlowDirection.LeftToRight;
            _secondRenderer = second;
            _secondViewHost.Child = second;
            _secondViewHost.Visibility = Visibility.Visible;
            _secondViewRow.Height = new GridLength(1, GridUnitType.Star);
            second.Markdown = _editor.Text ?? string.Empty;
        }
        else if (!visible && _secondRenderer is { } existing)
        {
            _secondRenderer = null;
            _secondViewHost.Child = null;
            _secondViewHost.Visibility = Visibility.Collapsed;
            _secondViewRow.Height = new GridLength(0);
            existing.Dispose();
        }
        UpdateProviderStatus();
    }

    private void UpdateProviderPage(bool isProvidersPage)
    {
        if (_providerControls is not null)
            _providerControls.Visibility = isProvidersPage ? Visibility.Visible : Visibility.Collapsed;
        if (isProvidersPage)
            return;

        // Provider swaps belong to the Providers page; every other page shows
        // the standard configuration.
        if (_secondViewToggle is not null) _secondViewToggle.IsChecked = false;
        if (_alternateFactoryToggle is not null) _alternateFactoryToggle.IsChecked = false;
        if (_withoutCSharpGrammarToggle is not null) _withoutCSharpGrammarToggle.IsChecked = false;
        if (_minimalProvidersToggle is not null) _minimalProvidersToggle.IsChecked = false;
    }

    private void UpdateProviderStatus()
    {
        if (_providerStatus is null)
            return;

        string status = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"engine:{_engineGeneration};highlighter:{_highlighterGeneration};" +
            $"grammar:{(_withoutCSharpGrammar ? "no-csharp" : "common")};" +
            $"factory:{(_alternateHostedFactory ? "alternate" : "default")};" +
            $"minimal:{(_minimalProviders ? "on" : "off")};" +
            $"second:{(_secondRenderer is null ? "off" : "on")}");
        _providerStatus.Text = status;
        AutomationProperties.SetName(_providerStatus, status);
    }

    private void DisposeProviderResources()
    {
        _secondRenderer?.Dispose();
        _secondRenderer = null;
        foreach (IDisposable retired in _retiredProviders)
            retired.Dispose();
        _retiredProviders.Clear();
        _replacementEngine?.Dispose();
        _minimalEngine?.Dispose();
    }

    private sealed class SampleGrammarProviderWithoutCSharp : ITextMateGrammarProvider, IDisposable
    {
        private readonly CommonTextMateGrammarProvider _inner = new();

        public int Revision => 1;

        public ValueTask<TextMateGrammarHighlightResult?> HighlightAsync(
            TextMateGrammarHighlightRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            return IsCSharp(request.LanguageId)
                ? ValueTask.FromResult<TextMateGrammarHighlightResult?>(TextMateGrammarHighlightResult.Empty)
                : _inner.HighlightAsync(request, cancellationToken);
        }

        public void Dispose() => _inner.Dispose();

        private static bool IsCSharp(string languageId)
            => string.Equals(languageId, "csharp", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(languageId, "cs", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(languageId, "c#", StringComparison.OrdinalIgnoreCase);
    }
}
