using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Html;
using MarkdownRenderer.Math;
using MarkdownRenderer.Mermaid;
using MarkdownRenderer.Svg.Resvg;
using MarkdownRenderer.SyntaxHighlighting.TextMate;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common;

namespace MarkdownRenderer.Sample.Services.Providers;

/// <summary>
/// The engine, grammar provider, hosted-control factory and optional
/// providers the window's views render with. The Providers page swaps them
/// while documents are live; the host, not a view, owns every provider, so a
/// replaced one is retired and disposed only when the window closes, since a
/// view may still hold work that started on it.
/// </summary>
internal sealed class SampleProviderSet : IDisposable
{
    private readonly MarkdownEngine _engine = CreateFullEngine();
    private readonly ResvgMarkdownSvgRenderer _svgRenderer = new();
    private readonly SampleHostedElementFactory _defaultHostedFactory = SampleHostedElementFactory.CreateDefault();
    private readonly SampleHostedElementFactory _alternateHostedFactory = SampleHostedElementFactory.CreateAlternate();
    private readonly List<IDisposable> _retiredProviders = [];
    private TextMateCodeBlockSyntaxHighlighter _textMateHighlighter = new(
        new CommonTextMateGrammarProvider(),
        options: null,
        ownsProvider: true);
    private MarkdownEngine? _replacementEngine;
    private MarkdownEngine? _minimalEngine;
    private int _engineGeneration = 1;
    private int _highlighterGeneration = 1;
    private bool _useAlternateHostedFactory;
    private bool _withoutCSharpGrammar;

    /// <summary>Whether views use strict GFM without any optional provider.</summary>
    internal bool IsMinimal { get; private set; }

    private MarkdownEngine ActiveEngine => _replacementEngine ?? _engine;

    private SampleHostedElementFactory ActiveHostedFactory => _useAlternateHostedFactory
        ? _alternateHostedFactory
        : _defaultHostedFactory;

    /// <summary>Applies the current providers to a new view.</summary>
    internal void Configure(MarkdownRendererControl renderer)
    {
        if (IsMinimal)
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
        ApplyHighlighter(renderer, _textMateHighlighter);
    }

    /// <summary>Replaces the full engine on every view; ignored with minimal providers.</summary>
    internal bool ReplaceEngine(IEnumerable<MarkdownRendererControl> views)
    {
        if (IsMinimal)
            return false;

        MarkdownEngine replacement = CreateFullEngine();
        if (_replacementEngine is { } previous)
            _retiredProviders.Add(previous);
        _replacementEngine = replacement;
        _engineGeneration++;
        foreach (MarkdownRendererControl view in views)
            ApplyFullEngine(view, replacement);
        return true;
    }

    internal bool ReplaceHighlighter(bool withoutCSharp, IEnumerable<MarkdownRendererControl> views)
    {
        if (_withoutCSharpGrammar == withoutCSharp)
            return false;

        ITextMateGrammarProvider provider = withoutCSharp
            ? new SampleGrammarProviderWithoutCSharp()
            : new CommonTextMateGrammarProvider();
        var replacement = new TextMateCodeBlockSyntaxHighlighter(provider, options: null, ownsProvider: true);
        TextMateCodeBlockSyntaxHighlighter previous = _textMateHighlighter;
        _textMateHighlighter = replacement;
        _withoutCSharpGrammar = withoutCSharp;
        _highlighterGeneration++;
        if (!IsMinimal)
        {
            foreach (MarkdownRendererControl view in views)
                ApplyHighlighter(view, replacement);
        }
        _retiredProviders.Add(previous);
        return true;
    }

    internal bool SetAlternateHostedFactory(bool alternate, IEnumerable<MarkdownRendererControl> views)
    {
        if (_useAlternateHostedFactory == alternate)
            return false;

        _useAlternateHostedFactory = alternate;
        if (!IsMinimal)
        {
            foreach (MarkdownRendererControl view in views)
                view.HostedElementFactory = ActiveHostedFactory;
        }
        return true;
    }

    /// <summary>
    /// Switches minimal providers on or off. Optional providers are
    /// configuration of a view, so the caller replaces its views afterwards.
    /// </summary>
    internal bool SetMinimal(bool minimal)
    {
        if (IsMinimal == minimal)
            return false;

        IsMinimal = minimal;
        return true;
    }

    internal string DescribeStatus(bool secondViewVisible) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"engine:{_engineGeneration};highlighter:{_highlighterGeneration};" +
        $"grammar:{(_withoutCSharpGrammar ? "no-csharp" : "common")};" +
        $"factory:{(_useAlternateHostedFactory ? "alternate" : "default")};" +
        $"minimal:{(IsMinimal ? "on" : "off")};" +
        $"second:{(secondViewVisible ? "on" : "off")}");

    /// <summary>
    /// Starts the SVG worker ahead of the first SVG. A failure is not an
    /// error: the sample keeps its accessible SVG fallbacks.
    /// </summary>
    internal async Task WarmUpSvgRendererAsync()
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

    public void Dispose()
    {
        foreach (IDisposable retired in _retiredProviders)
            retired.Dispose();
        _retiredProviders.Clear();
        _replacementEngine?.Dispose();
        _minimalEngine?.Dispose();
        _textMateHighlighter.Dispose();
        _engine.Dispose();
        _svgRenderer.Dispose();
    }

    private static MarkdownEngine CreateFullEngine()
        => new MarkdownEngineBuilder()
            .UseGitHubReadme(SafeHtmlOptions.Default)
            .UseMarkdownExtra()
            .UseMathematics()
            .UseMermaid()
            .UseExtension(new SampleHostedElementExtension())
            .Build();

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

    private static void ApplyHighlighter(
        MarkdownRendererControl renderer,
        TextMateCodeBlockSyntaxHighlighter highlighter)
    {
        switch (renderer)
        {
            case MarkdownScrollView scrollingView:
                scrollingView.UseTextMateSyntaxHighlighting(highlighter);
                break;
            case MarkdownDocumentView documentView:
                documentView.UseTextMateSyntaxHighlighting(highlighter);
                break;
        }
    }
}
