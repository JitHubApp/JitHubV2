using MarkdownRenderer.Layout.Internal.Text;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Text;

/// <summary>
/// Checks which junctions the glued-token rule forbids: symbol runs and
/// UAX #14 LB24 letter/affix pairs, never spaces, hyphen-before-letter,
/// URL slashes, opening brackets or CJK.
/// </summary>
public sealed class GluedTokenBreakRuleTests
{
    [Theory]
    [InlineData('C', '+')]
    [InlineData('+', '+')]
    [InlineData('x', '+')]
    [InlineData('+', 'y')]
    [InlineData('-', '>')]
    [InlineData('-', '-')]
    [InlineData('=', '>')]
    [InlineData('.', '.')]
    [InlineData('!', '=')]
    [InlineData('|', '|')]
    [InlineData('x', '$')]
    [InlineData('a', '%')]
    public void ForbidsBreaksInsideSymbolTokens(char before, char after)
        => Assert.True(GluedTokenBreakRule.ForbidsBreakBetween(before, after));

    [Theory]
    [InlineData(' ', '+')]
    [InlineData('+', ' ')]
    [InlineData('-', 'm')]
    [InlineData('/', 'd')]
    [InlineData('/', '#')]
    [InlineData(',', '(')]
    [InlineData(')', '(')]
    [InlineData('a', 'b')]
    [InlineData('在', '汉')]
    [InlineData('汉', '+')]
    public void KeepsOrdinaryBreakOpportunities(char before, char after)
        => Assert.False(GluedTokenBreakRule.ForbidsBreakBetween(before, after));

    [Theory]
    [InlineData("plain prose without symbols", false)]
    [InlineData("a well-known e-mail at https://example.com/a/b", false)]
    [InlineData("the Terminal is a C++ application", true)]
    [InlineData("Alice->>John", true)]
    public void FindsGluedJunctions(string text, bool expected)
        => Assert.Equal(expected, GluedTokenBreakRule.ContainsGluedJunction(text));
}
