using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Mermaid;

/// <summary>Registers native Mermaid fenced-code rendering with a markdown engine.</summary>
public static class MermaidMarkdownExtensions
{
    private const string ExtensionId = "MarkdownRenderer.Mermaid";

    /// <summary>
    /// Enables native rendering for normalized <c>mermaid</c> fences. Other
    /// fenced code and every failed native request remain ordinary code blocks.
    /// </summary>
    public static MarkdownEngineBuilder UseMermaid(
        this MarkdownEngineBuilder builder,
        MermaidRenderOptions? options = null,
        MermaidFontCatalog? fontCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseOwnedExtensionFactory(
            ExtensionId,
            () =>
            {
                var renderer = new MermaidRenderer(options, fontCatalog);
                return new MarkdownOwnedExtension(
                    new MermaidMarkdownExtension(renderer),
                    renderer);
            });
    }

    /// <summary>
    /// Enables Mermaid using a caller-owned renderer. The caller controls its
    /// native lifetime and must keep it alive as long as the engine can parse.
    /// </summary>
    public static MarkdownEngineBuilder UseMermaid(
        this MarkdownEngineBuilder builder,
        MermaidRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(renderer);
        return builder.UseExtension(new MermaidMarkdownExtension(renderer));
    }

    private sealed class MermaidMarkdownExtension : IMarkdownExtension
    {
        private readonly MermaidRenderer _renderer;

        internal MermaidMarkdownExtension(MermaidRenderer renderer) => _renderer = renderer;

        public string Id => ExtensionId;

        public void Configure(MarkdownExtensionBuilder builder)
        {
            builder.AddFeature("MarkdownRenderer.Mermaid.Native");
            builder.RegisterBlockAsync(MarkdownSyntaxKinds.Block.FencedCode, RenderFenceAsync);
        }

        private async ValueTask RenderFenceAsync(
            MarkdownExtensionContext context,
            MarkdownContentBuilder content)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            string source = context.Node.Literal ?? string.Empty;
            context.Node.Attributes.TryGetValue("language", out string? language);
            if (!string.Equals(language, "mermaid", StringComparison.OrdinalIgnoreCase))
                return;

            MermaidRenderResult result;
            try
            {
                result = await _renderer.RenderOnWorkerAsync(source, context.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ObjectDisposedException)
            {
                ReportDiagnostic(
                    context,
                    "MMR0006",
                    MarkdownDiagnosticSeverity.Error,
                        _renderer.ResolveString(
                            MermaidStringKeys.RendererUnavailable,
                            _renderer.DiagnosticCulture,
                        "The native Mermaid renderer is unavailable."),
                    context.Node.SourceSpan);
                AddFallbackContent(context, content, source);
                return;
            }

            ReportDiagnostics(context, source, result.Diagnostics);

            if (result.ShouldUseFallback || result.Scene is null)
            {
                if (result.Diagnostics.Count == 0)
                {
                    ReportDiagnostic(
                        context,
                        "MMR0012",
                        MarkdownDiagnosticSeverity.Error,
                        _renderer.ResolveString(
                            MermaidStringKeys.DiagramRenderFailed,
                            _renderer.DiagnosticCulture,
                            "The Mermaid diagram could not be rendered."),
                        context.Node.SourceSpan);
                }

                AddFallbackContent(context, content, source);
                return;
            }

            MarkdownVectorScene scene;
            try
            {
                scene = MermaidVectorSceneAdapter.Convert(
                    result.Scene,
                    context.Node,
                    source,
                    context.CancellationToken);
            }
            catch (Exception exception) when (exception is ArgumentException or OverflowException)
            {
                ReportDiagnostic(
                    context,
                    "MMR0009",
                    MarkdownDiagnosticSeverity.Error,
                    _renderer.ResolveString(
                        MermaidStringKeys.SceneRepresentationFailed,
                        _renderer.DiagnosticCulture,
                        "The Mermaid scene could not be represented safely."),
                    context.Node.SourceSpan);
                AddFallbackContent(context, content, source);
                return;
            }

            string? accessibleName = MermaidVectorSceneAdapter.GetAccessibleName(
                result.Scene,
                context.CancellationToken);
            content.AddVectorScene(
                scene,
                context.Node.SourceSpan,
                MarkdownStyleRole.Diagram,
                MarkdownAccessibilityRole.Diagram,
                accessibleName,
                accessibilityDescription: source,
                semanticText: MermaidVectorSceneAdapter.GetSemanticText(
                    result.Scene,
                    context.CancellationToken));
        }

        private static void AddFallbackContent(
            MarkdownExtensionContext context,
            MarkdownContentBuilder content,
            string source)
        {
            content.AddCodeBlock(source, "mermaid", context.Node.SourceSpan);
        }
    }

    internal static IReadOnlyList<MarkdownDiagnostic> ConvertDiagnostics(
        MarkdownSyntaxNode node,
        string source,
        IReadOnlyList<MermaidDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (diagnostics.Count == 0)
            return Array.Empty<MarkdownDiagnostic>();

        var converted = new MarkdownDiagnostic[diagnostics.Count];
        for (int index = 0; index < diagnostics.Count; index++)
        {
            MermaidDiagnostic diagnostic = diagnostics[index];
            converted[index] = new MarkdownDiagnostic(
                diagnostic.Code,
                diagnostic.Severity switch
                {
                    MermaidDiagnosticSeverity.Information => MarkdownDiagnosticSeverity.Information,
                    MermaidDiagnosticSeverity.Warning => MarkdownDiagnosticSeverity.Warning,
                    _ => MarkdownDiagnosticSeverity.Error,
                },
                diagnostic.Message,
                MapDiagnosticSourceSpan(node, source.Length, diagnostic.SourceRange));
        }

        return Array.AsReadOnly(converted);
    }

    private static void ReportDiagnostics(
        MarkdownExtensionContext context,
        string source,
        IReadOnlyList<MermaidDiagnostic> diagnostics)
    {
        IReadOnlyList<MarkdownDiagnostic> converted = ConvertDiagnostics(
            context.Node,
            source,
            diagnostics);
        for (int index = 0; index < converted.Count; index++)
        {
            MarkdownDiagnostic diagnostic = converted[index];
            ReportDiagnostic(
                context,
                diagnostic.Code,
                diagnostic.Severity,
                diagnostic.Message,
                diagnostic.SourceSpan);
        }
    }

    private static void ReportDiagnostic(
        MarkdownExtensionContext context,
        string code,
        MarkdownDiagnosticSeverity severity,
        string message,
        SourceSpan sourceSpan) =>
        context.ReportDiagnostic(code, severity, message, sourceSpan);

    private static SourceSpan MapDiagnosticSourceSpan(
        MarkdownSyntaxNode node,
        int sourceLength,
        MermaidSourceRange? sourceRange)
    {
        if (sourceRange is not { IsValid: true } range)
            return node.SourceSpan;

        int contentOffset = 0;
        if (node.Attributes.TryGetValue("contentOffset", out string? offsetText) &&
            (!int.TryParse(offsetText, out contentOffset) || contentOffset < 0))
        {
            return node.SourceSpan;
        }

        int relativeStart = Math.Clamp(range.Start, 0, sourceLength);
        int relativeLength = Math.Clamp(range.Length, 0, sourceLength - relativeStart);
        long absoluteStart = (long)node.SourceSpan.Start + contentOffset + relativeStart;
        if (absoluteStart < node.SourceSpan.Start || absoluteStart > node.SourceSpan.End)
            return node.SourceSpan;

        int start = (int)absoluteStart;
        return new SourceSpan(start, Math.Min(relativeLength, node.SourceSpan.End - start));
    }
}
