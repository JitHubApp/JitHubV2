using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.SyntaxHighlighting.TextMate;

/// <summary>Theme family requested from a grammar provider.</summary>
public enum TextMateGrammarThemeVariant
{
    /// <summary>Light editor colors.</summary>
    Light,

    /// <summary>Dark editor colors.</summary>
    Dark,
}
