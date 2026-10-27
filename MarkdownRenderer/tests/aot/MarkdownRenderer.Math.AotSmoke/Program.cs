using MarkdownRenderer.Math;

const string validMarkdown = @"before $$\frac{x^2}{\sqrt{y}}$$ after";
MathFormulaRequest validRequest = MathDelimiterScanner.Scan(validMarkdown).Formulas.Single();
var processor = new MathFormulaProcessor();
MathFormulaResult coldDeadlineResult = await processor.ProcessAsync(
    validRequest,
    new MathProcessingOptions(maximumProcessingTime: TimeSpan.FromMilliseconds(1)));
if (coldDeadlineResult.Kind != MathFormulaResultKind.Unsupported ||
    coldDeadlineResult.Diagnostic?.Code != "MATH106" ||
    coldDeadlineResult.FallbackSource != validRequest.OriginalSource)
{
    return 3;
}

// A cancelled first initialization must not poison the shared font catalog.
MathFormulaResult validResult = await processor.ProcessAsync(validRequest);

if (!validResult.IsAccepted ||
    validResult.Scene is not { Commands.Count: > 0 } scene ||
    !scene.Commands.Any(command => command.Kind == MathSceneCommandKind.FillPath) ||
    validResult.Accessibility?.CopyText != validRequest.TexSource)
{
    return 1;
}

const string invalidMarkdown = @"prefix $\notacommand{x}$ suffix";
MathFormulaRequest invalidRequest = MathDelimiterScanner.Scan(invalidMarkdown).Formulas.Single();
MathFormulaResult invalidResult = await processor.ProcessAsync(invalidRequest);

if (invalidResult.Kind != MathFormulaResultKind.InvalidSource ||
    invalidResult.Scene is not null ||
    invalidResult.FallbackSource != invalidRequest.OriginalSource ||
    invalidRequest.SourceRange.Slice(invalidMarkdown).ToString() != invalidRequest.OriginalSource)
{
    return 2;
}

Console.WriteLine($"Math deployment smoke passed: {scene.Commands.Count} vector commands");
return 0;
