namespace MarkdownRenderer.Layout.Internal.Text;

/// <summary>
/// Decides where browsers never wrap inside a symbol token attached to a
/// word, such as <c>C++</c>, <c>a++</c>, <c>x+y</c>, <c>-&gt;</c>,
/// <c>--&gt;</c> or <c>=&gt;</c>, but DirectWrite's line breaker reports a
/// break opportunity.
/// </summary>
/// <remarks>
/// DirectWrite's line-break classes predate UAX #14 rule LB24, so it allows a
/// break between a letter and a following prefix or postfix numeric symbol
/// (<c>C|+</c>, <c>x|$</c>) and between two such symbols (<c>+|+</c>). It also
/// breaks after a hyphen before an operator (<c>-|&gt;</c>), which browsers
/// keep together. This rule names only those junctions. Whitespace, CJK,
/// letter-after-hyphen (<c>e-|mail</c>), opening brackets and the junction
/// after a slash in a URL are never forbidden, so their break opportunities
/// stay exactly as DirectWrite reports them.
/// </remarks>
internal static class GluedTokenBreakRule
{
    /// <summary>
    /// Returns whether a line must not wrap between <paramref name="before"/>
    /// and <paramref name="after"/>.
    /// </summary>
    /// <param name="before">The last UTF-16 unit before the candidate break.</param>
    /// <param name="after">The first UTF-16 unit after the candidate break.</param>
    /// <returns>True when a browser keeps the two characters on one line.</returns>
    internal static bool ForbidsBreakBetween(char before, char after)
    {
        if (IsOperatorSymbol(before) && IsOperatorSymbol(after))
            return true;

        // UAX #14 LB24: (AL | HL) × (PR | PO) and (PR | PO) × (AL | HL).
        return (char.IsAsciiLetterOrDigit(before) && IsNumericAffix(after)) ||
            (IsNumericAffix(before) && char.IsAsciiLetterOrDigit(after));
    }

    /// <summary>
    /// Returns whether <paramref name="text"/> contains any junction this rule
    /// forbids, so a paragraph without one skips line inspection entirely.
    /// </summary>
    /// <param name="text">The native layout text.</param>
    /// <returns>True when at least one adjacent pair is glued.</returns>
    internal static bool ContainsGluedJunction(string text)
    {
        for (int index = 1; index < text.Length; index++)
        {
            if (ForbidsBreakBetween(text[index - 1], text[index]))
                return true;
        }

        return false;
    }

    // ASCII prefix-numeric (PR) and postfix-numeric (PO) symbols.
    private static bool IsNumericAffix(char value) => value is '+' or '$' or '%' or '\\';

    // ASCII punctuation that forms operator and symbol runs. Brackets, quotes
    // and the slash are left out: browsers break before an opening bracket and
    // after a URL path slash, and DirectWrite already agrees with them there.
    private static bool IsOperatorSymbol(char value) => value is
        '!' or '#' or '$' or '%' or '&' or '*' or '+' or ',' or '-' or '.' or
        ':' or ';' or '<' or '=' or '>' or '?' or '@' or '\\' or '^' or '_' or
        '`' or '|' or '~';
}
