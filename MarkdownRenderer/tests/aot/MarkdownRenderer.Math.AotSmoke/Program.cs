using MarkdownRenderer;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Math;

// Every check goes through the public host path: an engine with
// UseMathematics parses the formula and stores its rendering, or its exact
// fallback, as the formula's extension content.

// A first formula that misses its deadline must fall back without poisoning
// the shared font catalog for the formulas that follow.
const string validMarkdown = @"before $$\frac{x^2}{\sqrt{y}}$$ after";
const string validFormula = @"$$\frac{x^2}{\sqrt{y}}$$";
const string validTex = @"\frac{x^2}{\sqrt{y}}";
using (MarkdownEngine coldDeadline = new MarkdownEngineBuilder()
    .UseMathematics(new MathProcessingOptions(maximumProcessingTime: TimeSpan.FromMilliseconds(1)))
    .Build())
{
    MarkdownDocument document = await coldDeadline.ParseAsync(validMarkdown);
    if (!document.Diagnostics.Any(diagnostic => diagnostic.Code == "MATH106") ||
        Formula(document, validMarkdown, validFormula) is not { Kind: MarkdownContentKind.Text, Text: validFormula })
    {
        return 3;
    }
}

using MarkdownEngine engine = new MarkdownEngineBuilder()
    .UseMathematics()
    .Build();

MarkdownDocument valid = await engine.ParseAsync(validMarkdown);
if (valid.HasErrors ||
    Formula(valid, validMarkdown, validFormula) is not
    {
        Kind: MarkdownContentKind.VectorScene,
        SemanticText: validTex,
        VectorScene: { Commands.Count: > 0 } scene,
    } ||
    !scene.Commands.Any(command => command.Kind == MarkdownVectorCommandKind.FillPath))
{
    return 1;
}

const string invalidMarkdown = @"prefix $\notacommand{x}$ suffix";
const string invalidFormula = @"$\notacommand{x}$";
MarkdownDocument invalid = await engine.ParseAsync(invalidMarkdown);
// MATH100 is the processor's invalid-source classification.
if (!invalid.Diagnostics.Any(diagnostic => diagnostic.Code == "MATH100") ||
    Formula(invalid, invalidMarkdown, invalidFormula) is not { Kind: MarkdownContentKind.Text, Text: invalidFormula })
{
    return 2;
}

Console.WriteLine($"Math deployment smoke passed: {scene.Commands.Count} vector commands");
return 0;

static MarkdownContent? Formula(MarkdownDocument document, string markdown, string formula)
{
    var span = new SourceSpan(markdown.IndexOf(formula, StringComparison.Ordinal), formula.Length);
    return document.TryGetExtensionContent(span, out MarkdownContentFragment? fragment) &&
        fragment!.Items is [MarkdownContent content]
        ? content
        : null;
}
