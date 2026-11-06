using MarkdownRenderer.Theming;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Behavior that legacy theme customizations rely on: style sheets snapshot
/// their rules, element keys round-trip through style roles, and theme
/// overrides accept arbitrary legacy keys.
/// </summary>
public sealed class ThemingCompatibilityContractTests
{
    [Fact]
    public void StyleSheet_SnapshotsItsRulesAndExposesThemReadOnly()
    {
        var rule = new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.Body),
            new ElementStyleOverride());
        var source = new List<MarkdownStyleRule> { rule };
        var original = new MarkdownStyleSheet(source);

        source.Clear();
        MarkdownStyleSheet appended = original.WithRule(new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.Link),
            new ElementStyleOverride()));

        Assert.Single(original.Rules);
        Assert.Equal(2, appended.Rules.Count);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<MarkdownStyleRule>)original.Rules).Add(rule));
    }

    [Fact]
    public void StyleRole_KeepsTheElementKeyItWasCreatedFrom()
    {
        Assert.Equal("Body", MarkdownStyleRole.FromElementKey("Body").Name);
        Assert.Equal("Body", MarkdownStyleRole.Body.Name);
    }

    [Fact]
    public void ThemeOverrideStorage_PreservesArbitraryLegacyKeysThroughBothInsertionPaths()
    {
        int indexedChangedCount = 0;
        int addedChangedCount = 0;
        var indexed = new MarkdownTheme.OverrideCollection(() => indexedChangedCount++);
        var added = new MarkdownTheme.OverrideCollection(() => addedChangedCount++);
        string[] legacyKeys =
        [
            "Document",
            "Selection",
            " Extension.Widget ",
            MarkdownElementKeys.Class(new string('x', 129)),
            "Bad\u0001Role",
        ];

        foreach (string key in legacyKeys)
        {
            indexed[key] = new ElementStyleOverride { FontSize = 17 };
            added.Add(key, new ElementStyleOverride { FontSize = 18 });
        }

        Assert.Equal(legacyKeys.Length, indexed.Count);
        Assert.Equal(legacyKeys.Length, added.Count);
        Assert.Equal(legacyKeys.Length, indexedChangedCount);
        Assert.Equal(legacyKeys.Length, addedChangedCount);
        foreach (string key in legacyKeys)
        {
            Assert.Equal(17f, indexed[key].FontSize);
            Assert.Equal(18f, added[key].FontSize);
        }
    }
}
