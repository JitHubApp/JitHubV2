using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.SyntaxHighlighting.TextMate;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.All;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common;

Environment.SetEnvironmentVariable(
    "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY",
    AppContext.BaseDirectory);

using var provider = new CommonTextMateGrammarProvider();
var highlighter = new TextMateCodeBlockSyntaxHighlighter(provider);

CodeBlockHighlightResult? highlighted = await highlighter.HighlightAsync(
    new CodeBlockHighlightRequest(
        "csharp",
        "public sealed class NativeAotSmoke { }",
        CodeBlockThemeVariant.Dark,
        CancellationToken.None));

if (highlighted is not { Spans.Count: > 0 })
    return 1;

CodeBlockHighlightResult? unsupported = await highlighter.HighlightAsync(
    new CodeBlockHighlightRequest(
        "pascal",
        "program Demo; begin end.",
        CodeBlockThemeVariant.Dark,
        CancellationToken.None));

if (!ReferenceEquals(unsupported, CodeBlockHighlightResult.Empty))
    return 2;

using var allProvider = new AllTextMateGrammarProvider();
var allHighlighter = new TextMateCodeBlockSyntaxHighlighter(allProvider);
CodeBlockHighlightResult? allHighlighted = await allHighlighter.HighlightAsync(
    new CodeBlockHighlightRequest(
        "pascal",
        "program Demo; begin writeln('hello'); end.",
        CodeBlockThemeVariant.Dark,
        CancellationToken.None));

if (allHighlighted is not { Spans.Count: > 0 })
    return 3;

Console.WriteLine(
    $"TextMate deployment smoke passed: common={highlighted.Spans.Count}, all={allHighlighted.Spans.Count} spans");
return 0;
