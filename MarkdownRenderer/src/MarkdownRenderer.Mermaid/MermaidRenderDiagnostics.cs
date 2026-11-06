using System.Globalization;

namespace MarkdownRenderer.Mermaid;

/// <summary>
/// Creates the localized failure results and diagnostics a
/// <see cref="MermaidRenderer"/> returns, through its configured string provider.
/// </summary>
internal sealed class MermaidRenderDiagnostics
{
    private readonly MermaidRenderOptions _options;

    internal MermaidRenderDiagnostics(MermaidRenderOptions options)
    {
        _options = options;
    }
    internal MermaidRenderResult SourceTooLargeResult(string source, CultureInfo diagnosticCulture) =>
        Result(
            MermaidRenderStatus.BudgetExceeded,
            source,
            "MMR0004",
            MermaidStringKeys.SourceTooLarge,
            diagnosticCulture,
            "The Mermaid source exceeds MaxSourceBytes ({0} UTF-8 bytes).",
            _options.Budgets.MaxSourceBytes);

    internal MermaidRenderResult OverloadedResult(string source, CultureInfo diagnosticCulture) =>
        Result(
            MermaidRenderStatus.Overloaded,
            source,
            "MMR0013",
            MermaidStringKeys.RenderOverloaded,
            diagnosticCulture,
            "The Mermaid renderer admission limit is full; retry after an outstanding render completes.");

    internal MermaidRenderResult CooperativeCancellationResult(
        string source,
        CancellationToken producerCancellation,
        CancellationToken deadlineCancellation,
        CultureInfo diagnosticCulture)
    {
        bool timedOut = deadlineCancellation.IsCancellationRequested &&
            !producerCancellation.IsCancellationRequested;
        return Result(
            timedOut ? MermaidRenderStatus.TimedOut : MermaidRenderStatus.Cancelled,
            source,
            timedOut ? "MMR0007" : "MMR0005",
            timedOut ? MermaidStringKeys.RenderTimedOut : MermaidStringKeys.RenderCancelled,
            diagnosticCulture,
            timedOut
                ? "The Mermaid render exceeded its end-to-end deadline."
                : "The Mermaid render was cancelled.");
    }

    internal MermaidRenderResult NativeFailure(
        NativeStatus status,
        string source,
        CultureInfo diagnosticCulture) => status switch
    {
        NativeStatus.InvalidInput => Result(
            MermaidRenderStatus.InvalidInput,
            source,
            "MMR0010",
            MermaidStringKeys.NativeSourceRejected,
            diagnosticCulture,
            "The native Mermaid engine rejected the source."),
        NativeStatus.BudgetExceeded => Result(
            MermaidRenderStatus.BudgetExceeded,
            source,
            "MMR0011",
            MermaidStringKeys.NativeBudgetExceeded,
            diagnosticCulture,
            "The native Mermaid engine exhausted a configured budget."),
        NativeStatus.UnsupportedLayout => Result(
            MermaidRenderStatus.UnsupportedLayout,
            source,
            "MMR0002",
            MermaidStringKeys.NativeLayoutUnsupported,
            diagnosticCulture,
            "The requested native layout is unsupported."),
        NativeStatus.TimedOut => TimedOutResult(source, diagnosticCulture),
        NativeStatus.Cancelled => Result(
            MermaidRenderStatus.Cancelled,
            source,
            "MMR0005",
            MermaidStringKeys.RenderCancelled,
            diagnosticCulture,
            "The Mermaid render was cancelled."),
        NativeStatus.IncompatibleAbi => Result(
            MermaidRenderStatus.EngineUnavailable,
            source,
            "MMR0006",
            MermaidStringKeys.EngineIncompatibleAbi,
            diagnosticCulture,
            "The installed native Mermaid engine has an incompatible ABI."),
        NativeStatus.InvalidScene => Result(
            MermaidRenderStatus.InvalidScene,
            source,
            "MMR0009",
            MermaidStringKeys.SceneInvalid,
            diagnosticCulture,
            "The native Mermaid engine produced a scene that could not be represented safely."),
        _ => Result(
            MermaidRenderStatus.NativeFailure,
            source,
            "MMR0012",
            MermaidStringKeys.NativeFailure,
            diagnosticCulture,
            "The native Mermaid engine failed without producing a scene."),
    };

    internal MermaidRenderResult TimedOutResult(string source, CultureInfo diagnosticCulture) =>
        Result(
            MermaidRenderStatus.TimedOut,
            source,
            "MMR0007",
            MermaidStringKeys.RenderTimedOut,
            diagnosticCulture,
            "The Mermaid render exceeded its end-to-end deadline.");

    internal MermaidRenderResult Result(
        MermaidRenderStatus status,
        string source,
        string code,
        string resourceKey,
        CultureInfo diagnosticCulture,
        string englishFormat,
        params object?[] arguments) =>
        new(
            status,
            null,
            [new MermaidDiagnostic(
                code,
                MermaidDiagnosticSeverity.Error,
                ResolveString(resourceKey, diagnosticCulture, englishFormat, arguments))],
            source);

    internal string ResolveString(
        string resourceKey,
        CultureInfo diagnosticCulture,
        string englishFormat,
        params object?[] arguments) =>
        MermaidStringResolver.Resolve(
            _options.StringProvider,
            resourceKey,
            diagnosticCulture,
            englishFormat,
            arguments);

    internal MermaidScene LocalizeSceneDiagnostics(
        MermaidScene scene,
        CultureInfo diagnosticCulture)
    {
        if (_options.StringProvider is null || scene.Diagnostics.Count == 0)
            return scene;

        var localized = new MermaidDiagnostic[scene.Diagnostics.Count];
        for (int index = 0; index < localized.Length; index++)
        {
            MermaidDiagnostic diagnostic = scene.Diagnostics[index];
            localized[index] = diagnostic with
            {
                Message = ResolveString(
                    MermaidStringKeys.NativeDiagnostic,
                    diagnosticCulture,
                    "{1}",
                    diagnostic.Code,
                    diagnostic.Message),
            };
        }

        return scene.WithDiagnostics(localized);
    }
}
