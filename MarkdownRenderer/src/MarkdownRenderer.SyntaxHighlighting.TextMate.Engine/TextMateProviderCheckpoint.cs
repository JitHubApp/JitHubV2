using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.SyntaxHighlighting.TextMate;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;

namespace MarkdownRenderer.SyntaxHighlighting.TextMate.Engine;

internal enum TextMateProviderCheckpoint
{
    BeforeGrammarLoad,
    AfterGrammarLoad,
    BeforeLineTokenization,
    AfterLineTokenizationSlice,
    AfterLineTokenization,
}
