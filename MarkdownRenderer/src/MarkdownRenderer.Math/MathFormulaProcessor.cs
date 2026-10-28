using System.Globalization;
using MarkdownRenderer.Math.Internal;

namespace MarkdownRenderer.Math;

/// <summary>
/// Parses TeX and builds immutable vector scenes outside the caller's thread.
/// The processor is thread-safe and never creates Win2D, UI, SVG, bitmap, or
/// browser objects.
/// </summary>
public sealed class MathFormulaProcessor
{
    private const int MaximumEmergencyTexPreviewLength = 256;
    private const int MaximumEmergencyHelpTextLength = 2048;

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
        EmergencyLocalizationSnapshot emergency = CreateEnglishEmergencySnapshot(request);

        // Host string providers are synchronous and cannot be force-cancelled.
        // Invoke them away from the caller thread, bound the await by the same
        // deadline as parsing, and observe the task in case a blocking provider
        // completes with a fault after this operation has already returned.
        try
        {
            operationToken.ThrowIfCancellationRequested();

            if (_strings is not null)
            {
                string texPreview = emergency.TexPreview;
                Task<EmergencyLocalizationSnapshot> localizationTask =
                    await MathHostCallbackGate.StartAsync(
                        token => ValueTask.FromResult(CreateLocalizedEmergencySnapshot(
                            request,
                            languageTag,
                            localizationCulture,
                            texPreview,
                            token)),
                        operationToken)
                    .ConfigureAwait(false);

                try
                {
                    emergency = await localizationTask
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
                }
            }

            bool hasHostCallbacks = _strings is not null ||
                _hasReplacementAccessibilityFormatter;
            if (hasHostCallbacks)
            {
                MathAccessibilityDescription guaranteedAccessibility = emergency.Accessibility;
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
                        emergency.Accessibility)
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
                    emergency.Accessibility)
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

    private async ValueTask<MathFormulaResult> ProcessCoreAsync(
        MathFormulaRequest request,
        MathProcessingOptions options,
        CancellationToken cancellationToken,
        string languageTag,
        CultureInfo localizationCulture,
        MathAccessibilityDescription guaranteedAccessibility)
    {
        MathFormulaDiagnostic? preflight = ValidateInput(
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

        MathScene? scene;
        try
        {
            scene = CSharpMathSceneCompiler.Compile(request, options, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MathParserRecursionLimitException)
        {
            return MathFormulaResult.Unsupported(
                request,
                Diagnostic(
                    "MATH102",
                    MathStringKeys.NestingTooDeep,
                    request.ContentRange,
                    languageTag,
                    cancellationToken),
                guaranteedAccessibility);
        }
        catch (MathSceneBudgetException exception)
            when (exception.Budget == MathSceneBudget.WorkingMemory)
        {
            return MathFormulaResult.Unsupported(
                request,
                Diagnostic(
                    "MATH105",
                    MathStringKeys.WorkingMemoryExceeded,
                    request.ContentRange,
                    languageTag,
                    cancellationToken),
                guaranteedAccessibility);
        }
        catch (MathSceneBudgetException)
        {
            return MathFormulaResult.Unsupported(
                request,
                Diagnostic(
                    "MATH103",
                    MathStringKeys.SceneTooComplex,
                    request.ContentRange,
                    languageTag,
                    cancellationToken),
                guaranteedAccessibility);
        }
        catch (MathSceneDimensionException)
        {
            return MathFormulaResult.Unsupported(
                request,
                Diagnostic(
                    "MATH104",
                    MathStringKeys.SceneTooLarge,
                    request.ContentRange,
                    languageTag,
                    cancellationToken),
                guaranteedAccessibility);
        }
        catch (Exception)
        {
            // If an unexpected compiler failure races cancellation, cancellation
            // remains the observable operation outcome. Letting the unrelated
            // compiler exception escape here would bypass the caller-cancellation
            // and deadline handling in ProcessWithDeadlineAsync.
            cancellationToken.ThrowIfCancellationRequested();
            return MathFormulaResult.Failed(
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
                    cancellationToken).ConfigureAwait(false));
        }

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

    private MathFormulaDiagnostic? ValidateInput(
        MathFormulaRequest request,
        MathProcessingOptions options,
        CancellationToken cancellationToken,
        string languageTag)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request.TexSource.Length == 0)
        {
            return Diagnostic(
                "MATH100",
                MathStringKeys.InvalidFormula,
                request.ContentRange,
                languageTag,
                cancellationToken);
        }

        if (request.TexSource.Length > options.MaximumTexLength)
        {
            return Diagnostic(
                "MATH101",
                MathStringKeys.SourceTooLong,
                request.ContentRange,
                languageTag,
                cancellationToken);
        }

        if (MathWorkingMemoryBudget.EstimateSourceBytes(request.TexSource.Length) >
            options.MaximumWorkingMemoryBytes)
        {
            return Diagnostic(
                "MATH105",
                MathStringKeys.WorkingMemoryExceeded,
                request.ContentRange,
                languageTag,
                cancellationToken);
        }

        int depth = 0;
        for (int i = 0; i < request.TexSource.Length; i++)
        {
            if ((i & 0xff) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            char value = request.TexSource[i];
            if ((value != '{' && value != '}') || IsEscaped(request.TexSource, i))
                continue;

            if (value == '{')
            {
                depth++;
                if (depth > options.MaximumNestingDepth)
                {
                    return Diagnostic(
                        "MATH102",
                        MathStringKeys.NestingTooDeep,
                        request.ContentRange,
                        languageTag,
                        cancellationToken);
                }
            }
            else
            {
                depth--;
            }
        }

        return null;
    }

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
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string message = MathStringResolver.Resolve(_strings, stringKey, languageTag);
        cancellationToken.ThrowIfCancellationRequested();
        return new MathFormulaDiagnostic(code, message, range);
    }

    private EmergencyLocalizationSnapshot CreateLocalizedEmergencySnapshot(
        MathFormulaRequest request,
        string languageTag,
        CultureInfo localizationCulture,
        string texPreview,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string automationName = MathStringResolver.Resolve(
            _strings,
            MathStringKeys.AutomationName,
            languageTag);

        // Every synchronous callback has its own preceding checkpoint. In
        // particular, a provider blocked in one call cannot start the next call
        // after the deadline has been observed.
        cancellationToken.ThrowIfCancellationRequested();
        string helpText = MathStringResolver.FormatOriginalTex(
            _strings,
            languageTag,
            texPreview,
            localizationCulture,
            MaximumEmergencyHelpTextLength,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        string timeoutMessage = MathStringResolver.Resolve(
            _strings,
            MathStringKeys.ProcessingTimedOut,
            languageTag);
        cancellationToken.ThrowIfCancellationRequested();

        return new EmergencyLocalizationSnapshot(
            new MathAccessibilityDescription(
                automationName,
                texPreview,
                helpText,
                request.TexSource),
            new MathFormulaDiagnostic(
                "MATH106",
                timeoutMessage,
                request.ContentRange),
            texPreview);
    }

    private static EmergencyLocalizationSnapshot CreateEnglishEmergencySnapshot(
        MathFormulaRequest request)
    {
        string texPreview = CreateBoundedText(
            request.TexSource,
            MaximumEmergencyTexPreviewLength);
        string helpText = CreateBoundedText(
            "Original TeX: " + texPreview,
            MaximumEmergencyHelpTextLength);
        return new EmergencyLocalizationSnapshot(
            new MathAccessibilityDescription(
                MathStringResolver.GetEnglishString(MathStringKeys.AutomationName),
                texPreview,
                helpText,
                request.TexSource),
            new MathFormulaDiagnostic(
                "MATH106",
                MathStringResolver.GetEnglishString(MathStringKeys.ProcessingTimedOut),
                request.ContentRange),
            texPreview);
    }

    private static string CreateBoundedText(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
            return value;

        int prefixLength = maximumLength - 1;
        if (prefixLength > 0 &&
            char.IsHighSurrogate(value[prefixLength - 1]) &&
            char.IsLowSurrogate(value[prefixLength]))
        {
            prefixLength--;
        }

        return string.Create(
            prefixLength + 1,
            (Value: value, PrefixLength: prefixLength),
            static (destination, state) =>
            {
                state.Value.AsSpan(0, state.PrefixLength).CopyTo(destination);
                destination[^1] = '\u2026';
            });
    }

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

    private readonly record struct EmergencyLocalizationSnapshot(
        MathAccessibilityDescription Accessibility,
        MathFormulaDiagnostic TimeoutDiagnostic,
        string TexPreview);

    private static bool IsEscaped(string source, int index)
    {
        int slashCount = 0;
        for (int cursor = index - 1; cursor >= 0 && source[cursor] == '\\'; cursor--)
            slashCount++;
        return (slashCount & 1) != 0;
    }
}
