using System.Globalization;
using System.Threading.Tasks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Math.Internal;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Math;

/// <summary>Configures native dollar-delimited mathematics for an immutable engine.</summary>
public static class MathMarkdownExtensions
{
    /// <summary>
    /// Enables <c>$...$</c> and <c>$$...$$</c> parsing and native vector
    /// formula output. Backslash delimiters remain disabled for 1.0.
    /// </summary>
    public static MarkdownEngineBuilder UseMathematics(
        this MarkdownEngineBuilder builder,
        MathProcessingOptions? options = null,
        IMathAccessibilityFormatter? accessibilityFormatter = null,
        IMathStringProvider? strings = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseExtension(new MathMarkdownExtension(
            options ?? MathProcessingOptions.Default,
            accessibilityFormatter,
            strings));
    }

    private sealed class MathMarkdownExtension : IMarkdownExtension
    {
        private readonly MathProcessingOptions _options;
        private readonly MathFormulaProcessor _processor;
        private readonly IMathStringProvider? _strings;
        private readonly string _languageTag;

        internal MathMarkdownExtension(
            MathProcessingOptions options,
            IMathAccessibilityFormatter? accessibilityFormatter,
            IMathStringProvider? strings)
        {
            _options = options;
            _strings = strings;
            CultureInfo localizationCulture = MathStringResolver.SnapshotCulture(
                options.LocalizationCulture);
            _languageTag = MathStringResolver.GetLanguageTag(localizationCulture);
            _processor = new MathFormulaProcessor(accessibilityFormatter, strings);
        }

        public string Id => MathFeature.PackageId;

        public void Configure(MarkdownExtensionBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder
                .AddFeature(MathFeature.ParserFeatureId)
                .RegisterInlineAsync(MarkdownSyntaxKinds.Inline.Math, RenderInlineAsync)
                .RegisterBlockAsync(MarkdownSyntaxKinds.Block.Math, RenderBlockAsync);
        }

        private ValueTask RenderInlineAsync(MarkdownExtensionContext context, MarkdownContentBuilder content) =>
            RenderAsync(context, content, MathFormulaDisplayMode.Inline);

        private ValueTask RenderBlockAsync(MarkdownExtensionContext context, MarkdownContentBuilder content) =>
            RenderAsync(context, content, MathFormulaDisplayMode.Display);

        private async ValueTask RenderAsync(
            MarkdownExtensionContext context,
            MarkdownContentBuilder content,
            MathFormulaDisplayMode displayMode)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            string original = context.Node.Literal ?? string.Empty;
            if (!TryReadContentRange(context.Node, original.Length, out int contentOffset, out int contentLength))
            {
                string diagnosticMessage = await ResolveDiagnosticAsync(
                    MathStringKeys.ProcessingFailed,
                    context.CancellationToken).ConfigureAwait(false);
                context.CancellationToken.ThrowIfCancellationRequested();
                AddFallback(
                    context,
                    content,
                    original,
                    displayMode,
                    "MATH199",
                    diagnosticMessage,
                    context.Node.SourceSpan);
                return;
            }

            if (displayMode == MathFormulaDisplayMode.Display &&
                context.Node.Attributes.TryGetValue("closed", out string? closed) &&
                !string.Equals(closed, "true", StringComparison.Ordinal))
            {
                int delimiterLength = System.Math.Min(2, context.Node.SourceSpan.Length);
                string diagnosticMessage = await ResolveDiagnosticAsync(
                    MathStringKeys.UnmatchedDisplayDelimiter,
                    context.CancellationToken).ConfigureAwait(false);
                context.CancellationToken.ThrowIfCancellationRequested();
                AddFallback(
                    context,
                    content,
                    original,
                    displayMode,
                    "MATH002",
                    diagnosticMessage,
                    new SourceSpan(context.Node.SourceSpan.Start, delimiterLength));
                return;
            }

            string tex = original.Substring(contentOffset, contentLength);
            var request = new MathFormulaRequest(
                original,
                tex,
                new MathSourceRange(context.Node.SourceSpan.Start, context.Node.SourceSpan.Length),
                new MathSourceRange(
                    checked(context.Node.SourceSpan.Start + contentOffset),
                    contentLength),
                displayMode,
                _languageTag);

            MathFormulaResult result = await _processor
                .ProcessOnWorkerAsync(request, _options, context.CancellationToken)
                .ConfigureAwait(false);
            context.CancellationToken.ThrowIfCancellationRequested();

            if (!result.IsAccepted || result.Scene is null || result.Accessibility is null)
            {
                MathFormulaDiagnostic diagnostic = result.Diagnostic ?? new MathFormulaDiagnostic(
                    "MATH199",
                    MathStringResolver.GetEnglishString(MathStringKeys.ProcessingFailed),
                    request.ContentRange);
                AddFallback(
                    context,
                    content,
                    result.FallbackSource ?? original,
                    displayMode,
                    diagnostic.Code,
                    diagnostic.Message,
                    ToSourceSpan(diagnostic.SourceRange));
                return;
            }

            MathAccessibilityDescription accessibility = result.Accessibility;
            string accessibleName = string.IsNullOrWhiteSpace(accessibility.StructuralSpeech)
                ? accessibility.AutomationName
                : string.Concat(
                    accessibility.AutomationName,
                    ", ",
                    accessibility.StructuralSpeech);
            content.AddVectorScene(
                ConvertScene(result.Scene, _options.FontSize),
                context.Node.SourceSpan,
                MarkdownStyleRole.Math,
                MarkdownAccessibilityRole.Math,
                accessibleName,
                accessibility.HelpText,
                accessibility.CopyText);
        }

        private async ValueTask<string> ResolveDiagnosticAsync(
            string stringKey,
            CancellationToken cancellationToken)
        {
            string fallback = MathStringResolver.GetEnglishString(stringKey);
            if (_strings is null)
                return fallback;

            cancellationToken.ThrowIfCancellationRequested();
            using var deadlineCancellation = new CancellationTokenSource(
                _options.MaximumProcessingTime);
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadlineCancellation.Token);
            CancellationToken operationToken = operationCancellation.Token;

            try
            {
                Task<string> callbackTask = await MathHostCallbackGate.StartAsync(
                    token =>
                    {
                        token.ThrowIfCancellationRequested();
                        string value = MathStringResolver.Resolve(
                            _strings,
                            stringKey,
                            _languageTag);
                        token.ThrowIfCancellationRequested();
                        return ValueTask.FromResult(value);
                    },
                    operationToken).ConfigureAwait(false);
                string result = await callbackTask
                    .WaitAsync(operationToken)
                    .ConfigureAwait(false);
                operationToken.ThrowIfCancellationRequested();
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (OperationCanceledException) when (deadlineCancellation.IsCancellationRequested)
            {
                return fallback;
            }
            catch
            {
                cancellationToken.ThrowIfCancellationRequested();
                return fallback;
            }
        }

        private static bool TryReadContentRange(
            MarkdownSyntaxNode node,
            int originalLength,
            out int offset,
            out int length)
        {
            offset = 0;
            length = 0;
            if (!node.Attributes.TryGetValue("contentOffset", out string? offsetText) ||
                !node.Attributes.TryGetValue("contentLength", out string? lengthText) ||
                !int.TryParse(offsetText, NumberStyles.None, CultureInfo.InvariantCulture, out offset) ||
                !int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out length) ||
                offset < 0 || length < 0 || offset > originalLength - length)
            {
                return false;
            }

            return true;
        }

        private static void AddFallback(
            MarkdownExtensionContext context,
            MarkdownContentBuilder content,
            string exactSource,
            MathFormulaDisplayMode displayMode,
            string diagnosticCode,
            string diagnosticMessage,
            SourceSpan diagnosticSpan)
        {
            context.ReportDiagnostic(
                diagnosticCode,
                diagnosticCode is "MATH100" or "MATH199"
                    ? MarkdownDiagnosticSeverity.Error
                    : MarkdownDiagnosticSeverity.Warning,
                diagnosticMessage,
                diagnosticSpan);

            if (displayMode == MathFormulaDisplayMode.Display)
            {
                content.AddCodeBlock(
                    exactSource,
                    language: "tex",
                    context.Node.SourceSpan,
                    MarkdownStyleRole.CodeBlock);
            }
            else
            {
                content.AddText(
                    exactSource,
                    context.Node.SourceSpan,
                    MarkdownStyleRole.InlineCode,
                    MarkdownAccessibilityRole.Code);
            }
        }

        private static MarkdownVectorScene ConvertScene(MathScene source, float referenceFontSize)
        {
            var commands = new MarkdownVectorCommand[source.Commands.Count];
            for (int i = 0; i < commands.Length; i++)
            {
                MathSceneCommand command = source.Commands[i];
                commands[i] = command.Kind switch
                {
                    MathSceneCommandKind.FillPath => MarkdownVectorCommand.FillPath(
                        ConvertPath(command.Path),
                        command.ColorArgb),
                    MathSceneCommandKind.StrokePath => MarkdownVectorCommand.StrokePath(
                        ConvertPath(command.Path),
                        command.StrokeWidth,
                        command.ColorArgb),
                    MathSceneCommandKind.FillRectangle => MarkdownVectorCommand.FillRectangle(
                        ConvertRectangle(command.Rectangle),
                        command.ColorArgb),
                    MathSceneCommandKind.StrokeRectangle => MarkdownVectorCommand.StrokeRectangle(
                        ConvertRectangle(command.Rectangle),
                        command.StrokeWidth,
                        command.ColorArgb),
                    MathSceneCommandKind.StrokeLine => MarkdownVectorCommand.StrokeLine(
                        ConvertPoint(command.Start),
                        ConvertPoint(command.End),
                        command.StrokeWidth,
                        command.ColorArgb),
                    _ => throw new InvalidOperationException("Unknown formula scene command."),
                };
            }

            return new MarkdownVectorScene(
                source.Width,
                source.Height,
                source.Baseline,
                commands,
                referenceFontSize);
        }

        private static IReadOnlyList<MarkdownVectorPathOperation> ConvertPath(
            IReadOnlyList<MathPathOperation> source)
        {
            var operations = new MarkdownVectorPathOperation[source.Count];
            for (int i = 0; i < operations.Length; i++)
            {
                MathPathOperation operation = source[i];
                operations[i] = operation.Kind switch
                {
                    MathPathOperationKind.Move => MarkdownVectorPathOperation.MoveTo(
                        ConvertPoint(operation.Point1)),
                    MathPathOperationKind.Line => MarkdownVectorPathOperation.LineTo(
                        ConvertPoint(operation.Point1)),
                    MathPathOperationKind.QuadraticBezier => MarkdownVectorPathOperation.QuadraticBezierTo(
                        ConvertPoint(operation.Point1),
                        ConvertPoint(operation.Point2)),
                    MathPathOperationKind.CubicBezier => MarkdownVectorPathOperation.CubicBezierTo(
                        ConvertPoint(operation.Point1),
                        ConvertPoint(operation.Point2),
                        ConvertPoint(operation.Point3)),
                    MathPathOperationKind.Close => MarkdownVectorPathOperation.Close(),
                    _ => throw new InvalidOperationException("Unknown formula path operation."),
                };
            }
            return operations;
        }

        private static MarkdownVectorPoint ConvertPoint(MathPoint point) =>
            new(point.X, point.Y);

        private static MarkdownVectorRectangle ConvertRectangle(MathRectangle rectangle) =>
            new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);

        private static SourceSpan ToSourceSpan(MathSourceRange range) =>
            new(range.Start, range.Length);
    }
}
