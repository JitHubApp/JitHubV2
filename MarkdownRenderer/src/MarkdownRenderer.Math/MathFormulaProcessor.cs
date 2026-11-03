using System.Globalization;
using MarkdownRenderer.Math.Internal;

namespace MarkdownRenderer.Math;

/// <summary>
/// Parses TeX and builds immutable vector scenes outside the caller's thread.
/// The processor is thread-safe and never creates Win2D, UI, SVG, bitmap, or
/// browser objects.
/// </summary>
internal sealed class MathFormulaProcessor
{
    private readonly IMathAccessibilityFormatter _accessibilityFormatter;
    private readonly IMathStringProvider? _strings;
    private readonly CultureInfo _localizationCulture;
    private readonly bool _hasReplacementAccessibilityFormatter;

    /// <summary>Creates a processor with optional localized accessibility services.</summary>
    public MathFormulaProcessor(
        IMathAccessibilityFormatter? accessibilityFormatter = null,
        IMathStringProvider? strings = null)
    {
        _localizationCulture = MathStringResolver.SnapshotCulture();
        _strings = strings;
        _hasReplacementAccessibilityFormatter = accessibilityFormatter is not null;
        _accessibilityFormatter = accessibilityFormatter ??
            new DefaultMathAccessibilityFormatter(strings, _localizationCulture);
    }

    /// <summary>
    /// Processes one atomic formula. Invalid, timed-out, or over-budget input
    /// returns the exact original delimited source as its fallback. Caller
    /// cancellation is propagated.
    /// </summary>
    public async ValueTask<MathFormulaResult> ProcessAsync(
        MathFormulaRequest request,
        MathProcessingOptions? options = null,
        CancellationToken cancellationToken = default)
        => await ProcessWithDeadlineAsync(
                request,
                options,
                cancellationToken,
                callerOwnsWorker: false)
            .ConfigureAwait(false);

    /// <summary>
    /// Processes a formula when the caller already owns a background worker.
    /// The callback-free path avoids a nested compiler dispatch. Configured host
    /// callbacks use bounded worker isolation so their synchronous entry cannot
    /// defeat deadline, cancellation, or fallback behavior.
    /// </summary>
    internal ValueTask<MathFormulaResult> ProcessOnWorkerAsync(
        MathFormulaRequest request,
        MathProcessingOptions? options = null,
        CancellationToken cancellationToken = default)
        => ProcessWithDeadlineAsync(request, options, cancellationToken, callerOwnsWorker: true);

    private async ValueTask<MathFormulaResult> ProcessWithDeadlineAsync(
        MathFormulaRequest request,
        MathProcessingOptions? options,
        CancellationToken cancellationToken,
        bool callerOwnsWorker)
    {
        ArgumentNullException.ThrowIfNull(request);
        options ??= MathProcessingOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();

        using var deadlineCancellation = new CancellationTokenSource(options.MaximumProcessingTime);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadlineCancellation.Token);
        CancellationToken operationToken = operationCancellation.Token;
        CultureInfo localizationCulture = options.LocalizationCulture ?? _localizationCulture;
        string languageTag = request.LanguageTag ?? MathStringResolver.GetLanguageTag(localizationCulture);

        // This payload is deliberately provider-free and fixed-size: it is
        // safe to create on the caller thread after the deadline starts, and
        // exact untrusted source is retained only by the request/copy fields.
        MathEmergencyLocalizationSnapshot emergency = MathEmergencyLocalization.CreateEnglishEmergencySnapshot(request);

        try
        {
            operationToken.ThrowIfCancellationRequested();

            if (_strings is not null)
            {
                emergency = await LocalizeEmergencySnapshotAsync(
                        request,
                        languageTag,
                        localizationCulture,
                        emergency,
                        operationToken)
                    .ConfigureAwait(false);
            }

            return await DispatchCoreAsync(
                    request,
                    options,
                    operationToken,
                    languageTag,
                    localizationCulture,
                    emergency.Accessibility,
                    callerOwnsWorker)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException) when (deadlineCancellation.IsCancellationRequested)
        {
            return MathFormulaResult.Unsupported(
                request,
                emergency.TimeoutDiagnostic,
                emergency.Accessibility);
        }
    }

    /// <summary>
    /// Resolves the emergency (timeout) strings through the host provider.
    /// Host string providers are synchronous and cannot be force-cancelled, so
    /// they run away from the caller thread, the await is bounded by the
    /// operation deadline, and any other failure keeps the English snapshot.
    /// </summary>
    private async ValueTask<MathEmergencyLocalizationSnapshot> LocalizeEmergencySnapshotAsync(
        MathFormulaRequest request,
        string languageTag,
        CultureInfo localizationCulture,
        MathEmergencyLocalizationSnapshot english,
        CancellationToken operationToken)
    {
        string texPreview = english.TexPreview;
        Task<MathEmergencyLocalizationSnapshot> localizationTask =
            await MathHostCallbackGate.StartAsync(
                token => ValueTask.FromResult(MathEmergencyLocalization.CreateLocalizedEmergencySnapshot(
                    _strings,
                    request,
                    languageTag,
                    localizationCulture,
                    texPreview,
                    token)),
                operationToken)
            .ConfigureAwait(false);

        try
        {
            return await localizationTask
                .WaitAsync(operationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The English snapshot is a guaranteed boundary even for an
            // unexpected failure outside the provider resolver's guard.
            return english;
        }
    }

    /// <summary>
    /// Runs the core on the right worker: bounded host-callback isolation when
    /// host callbacks are configured, one thread-pool dispatch for public
    /// callback-free calls, and inline when the caller already owns a worker.
    /// </summary>
    private async ValueTask<MathFormulaResult> DispatchCoreAsync(
        MathFormulaRequest request,
        MathProcessingOptions options,
        CancellationToken operationToken,
        string languageTag,
        CultureInfo localizationCulture,
        MathAccessibilityDescription guaranteedAccessibility,
        bool callerOwnsWorker)
    {
        bool hasHostCallbacks = _strings is not null ||
            _hasReplacementAccessibilityFormatter;
        if (hasHostCallbacks)
        {
            Task<MathFormulaResult> coreTask =
                await MathHostCallbackGate.StartAsync(
                    token => ProcessCoreAsync(
                        request,
                        options,
                        token,
                        languageTag,
                        localizationCulture,
                        guaranteedAccessibility),
                    operationToken)
                .ConfigureAwait(false);
            return await coreTask
                .WaitAsync(operationToken)
                .ConfigureAwait(false);
        }

        // Public callback-free processing owns exactly one worker dispatch
        // for compilation. The internal worker path stays entirely inline.
        if (!callerOwnsWorker)
        {
            Task<MathFormulaResult> coreTask = Task.Run(
                async () => await ProcessCoreAsync(
                    request,
                    options,
                    operationToken,
                    languageTag,
                    localizationCulture,
                    guaranteedAccessibility)
                .ConfigureAwait(false),
                CancellationToken.None);
            ObserveFault(coreTask);
            return await coreTask
                .WaitAsync(operationToken)
                .ConfigureAwait(false);
        }

        return await ProcessCoreAsync(
                request,
                options,
                operationToken,
                languageTag,
                localizationCulture,
                guaranteedAccessibility)
            .ConfigureAwait(false);
    }

    private async ValueTask<MathFormulaResult> ProcessCoreAsync(
        MathFormulaRequest request,
        MathProcessingOptions options,
        CancellationToken cancellationToken,
        string languageTag,
        CultureInfo localizationCulture,
        MathAccessibilityDescription guaranteedAccessibility)
    {
        MathFormulaDiagnostic? preflight = MathInputPreflight.ValidateInput(
            _strings,
            request,
            options,
            cancellationToken,
            languageTag);
        if (preflight is not null)
        {
            if (preflight.Code != "MATH100")
                return MathFormulaResult.Unsupported(request, preflight, guaranteedAccessibility);

            MathAccessibilityDescription accessibility =
                await FormatAccessibilityAsync(
                    request,
                    languageTag,
                    localizationCulture,
                    guaranteedAccessibility,
                    cancellationToken).ConfigureAwait(false);
            return MathFormulaResult.InvalidSource(request, preflight, accessibility);
        }

        (MathScene? scene, MathFormulaResult? compileFailure) = await CompileSceneAsync(
                request,
                options,
                cancellationToken,
                languageTag,
                localizationCulture,
                guaranteedAccessibility)
            .ConfigureAwait(false);
        if (compileFailure is not null)
            return compileFailure;

        if (scene is null)
        {
            return MathFormulaResult.InvalidSource(
                request,
                Diagnostic(
                    "MATH100",
                    MathStringKeys.InvalidFormula,
                    request.ContentRange,
                    languageTag,
                    cancellationToken),
                await FormatAccessibilityAsync(
                    request,
                    languageTag,
                    localizationCulture,
                    guaranteedAccessibility,
                    cancellationToken).ConfigureAwait(false));
        }

        MathAccessibilityDescription acceptedAccessibility =
            await FormatAccessibilityAsync(
                request,
                languageTag,
                localizationCulture,
                guaranteedAccessibility,
                cancellationToken).ConfigureAwait(false);
        return MathFormulaResult.Accepted(request, scene, acceptedAccessibility);
    }

    /// <summary>
    /// Compiles the scene (null for invalid TeX). Compiler limits and unexpected
    /// compiler failures are returned as the failure result instead.
    /// </summary>
    private async ValueTask<(MathScene? Scene, MathFormulaResult? Failure)> CompileSceneAsync(
        MathFormulaRequest request,
        MathProcessingOptions options,
        CancellationToken cancellationToken,
        string languageTag,
        CultureInfo localizationCulture,
        MathAccessibilityDescription guaranteedAccessibility)
    {
        try
        {
            return (CSharpMathSceneCompiler.Compile(request, options, cancellationToken), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MathParserRecursionLimitException)
        {
            return (null, CompilerLimitResult(
                request, "MATH102", MathStringKeys.NestingTooDeep, languageTag, guaranteedAccessibility, cancellationToken));
        }
        catch (MathSceneBudgetException exception)
            when (exception.Budget == MathSceneBudget.WorkingMemory)
        {
            return (null, CompilerLimitResult(
                request, "MATH105", MathStringKeys.WorkingMemoryExceeded, languageTag, guaranteedAccessibility, cancellationToken));
        }
        catch (MathSceneBudgetException)
        {
            return (null, CompilerLimitResult(
                request, "MATH103", MathStringKeys.SceneTooComplex, languageTag, guaranteedAccessibility, cancellationToken));
        }
        catch (MathSceneDimensionException)
        {
            return (null, CompilerLimitResult(
                request, "MATH104", MathStringKeys.SceneTooLarge, languageTag, guaranteedAccessibility, cancellationToken));
        }
        catch (Exception)
        {
            // If an unexpected compiler failure races cancellation, cancellation
            // remains the observable operation outcome. Letting the unrelated
            // compiler exception escape here would bypass the caller-cancellation
            // and deadline handling in ProcessWithDeadlineAsync.
            cancellationToken.ThrowIfCancellationRequested();
            return (null, MathFormulaResult.Failed(
                request,
                Diagnostic(
                    "MATH199",
                    MathStringKeys.ProcessingFailed,
                    request.ContentRange,
                    languageTag,
                    cancellationToken),
                await FormatAccessibilityAsync(
                    request,
                    languageTag,
                    localizationCulture,
                    guaranteedAccessibility,
                    cancellationToken).ConfigureAwait(false)));
        }
    }

    private MathFormulaResult CompilerLimitResult(
        MathFormulaRequest request,
        string code,
        string stringKey,
        string languageTag,
        MathAccessibilityDescription guaranteedAccessibility,
        CancellationToken cancellationToken) =>
        MathFormulaResult.Unsupported(
            request,
            Diagnostic(code, stringKey, request.ContentRange, languageTag, cancellationToken),
            guaranteedAccessibility);

    private async ValueTask<MathAccessibilityDescription> FormatAccessibilityAsync(
        MathFormulaRequest request,
        string languageTag,
        CultureInfo localizationCulture,
        MathAccessibilityDescription guaranteedAccessibility,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValueTask<MathAccessibilityDescription> pending =
                _accessibilityFormatter is DefaultMathAccessibilityFormatter defaultFormatter
                    ? defaultFormatter.FormatAsync(request, languageTag, cancellationToken)
                    : _accessibilityFormatter.FormatAsync(request, cancellationToken);
            MathAccessibilityDescription? result;
            if (pending.IsCompletedSuccessfully)
            {
                result = pending.Result;
            }
            else
            {
                Task<MathAccessibilityDescription> formatterTask = pending.AsTask();
                ObserveFault(formatterTask);
                // The caller-facing core wait enforces the deadline. Await the
                // formatter's actual completion here so the host-callback
                // admission lease cannot retire while ignored work still runs.
                result = await formatterTask.ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return result ?? guaranteedAccessibility;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await new DefaultMathAccessibilityFormatter(_strings, localizationCulture)
                    .FormatAsync(request, languageTag, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return guaranteedAccessibility;
            }
        }
    }

    private MathFormulaDiagnostic Diagnostic(
        string code,
        string stringKey,
        MathSourceRange range,
        string? languageTag,
        CancellationToken cancellationToken) =>
        MathFormulaDiagnostics.Create(_strings, code, stringKey, range, languageTag, cancellationToken);
    private static void ObserveFault(Task task)
    {
        if (task.IsCompleted)
        {
            _ = task.Exception;
            return;
        }

        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously |
                TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}
