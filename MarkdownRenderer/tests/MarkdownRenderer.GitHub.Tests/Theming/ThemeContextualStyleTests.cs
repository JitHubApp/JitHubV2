using MarkdownRenderer.GitHub.Tests.TestSupport;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.ThemeSnapshotFixture;

namespace MarkdownRenderer.GitHub.Tests.Theming;

/// <summary>
/// Checks contextual style qualifiers and their cache: language, state
/// and nesting qualifiers, compiled dispatch precedence, query sequence
/// copies, canonical keys and allocation-free warm lookups.
/// </summary>
[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class ThemeContextualStyleTests
{
    [Fact]
    public void LanguageAndStateQualifiers_AreAppliedByTheRendererSnapshot()
    {
        var baseStyle = new ElementStyle { FontSize = 13 };
        var styleSheet = new MarkdownStyleSheet(
        [
            new MarkdownStyleRule(
                new MarkdownStyleSelector(
                    MarkdownStyleRole.CodeBlock,
                    language: "csharp",
                    state: "diff"),
                new ElementStyleOverride { FontSize = 27 }),
        ]);
        var snapshot = CreateSnapshot(
            baseStyle,
            new Dictionary<string, ElementStyleOverride>(),
            isHighContrast: false,
            styleSheet);

        ElementStyle matched = snapshot.GetStyle(
            MarkdownElementKeys.CodeBlock,
            contextKeys: null,
            aliasKeys: null,
            language: "CSHARP",
            state: "diff");
        ElementStyle unmatched = snapshot.GetStyle(
            MarkdownElementKeys.CodeBlock,
            contextKeys: null,
            aliasKeys: null,
            language: "csharp",
            state: null);

        Assert.Equal(27, matched.FontSize);
        Assert.Equal(13, unmatched.FontSize);
    }

    [Fact]
    public void NestingQualifier_UsesSemanticListDepthNotContextCount()
    {
        var baseStyle = new ElementStyle { FontSize = 13 };
        var styleSheet = new MarkdownStyleSheet(
        [
            new MarkdownStyleRule(
                new MarkdownStyleSelector(
                    MarkdownStyleRole.Body,
                    minimumNestingDepth: 3,
                    maximumNestingDepth: 3),
                new ElementStyleOverride { FontSize = 31 }),
        ]);
        var snapshot = CreateSnapshot(
            baseStyle,
            new Dictionary<string, ElementStyleOverride>(),
            isHighContrast: false,
            styleSheet);

        ElementStyle actual = snapshot.GetStyle(
            MarkdownElementKeys.Body,
            [MarkdownElementKeys.Quote, MarkdownElementKeys.ListDepth(3)],
            aliasKeys: null);

        Assert.Equal(31, actual.FontSize);
    }

    [Fact]
    public void CompiledDispatch_PreservesQualifierAndLegacyOverridePrecedence()
    {
        var overrides = new Dictionary<string, ElementStyleOverride>
        {
            [MarkdownElementKeys.Class("warning")] = new() { CornerRadius = 7 },
            [MarkdownElementKeys.Context(
                MarkdownElementKeys.ListDepth(2),
                MarkdownElementKeys.Class("warning"))] = new() { BorderThickness = 3 },
        };
        var styleSheet = new MarkdownStyleSheet(
        [
            new MarkdownStyleRule(
                new MarkdownStyleSelector(
                    MarkdownStyleRole.CodeBlock,
                    minimumNestingDepth: 1),
                new ElementStyleOverride { FontSize = 17 }),
            new MarkdownStyleRule(
                new MarkdownStyleSelector(
                    MarkdownStyleRole.CodeBlock,
                    minimumNestingDepth: 2,
                    maximumNestingDepth: 2,
                    language: "csharp",
                    state: "diff",
                    className: "warning"),
                new ElementStyleOverride { FontSize = 23, BorderThickness = 5 }),
        ]);
        ThemeSnapshot snapshot = CreateSnapshot(
            new ElementStyle { FontSize = 13 },
            overrides,
            isHighContrast: false,
            styleSheet,
            textScaleFactor: 2);

        ElementStyle actual = snapshot.GetStyle(
            MarkdownElementKeys.CodeBlock,
            [MarkdownElementKeys.Quote, MarkdownElementKeys.ListDepth(2)],
            [MarkdownElementKeys.Class("warning")],
            language: " CSHARP ",
            state: " diff ");

        Assert.Equal(46, actual.FontSize);
        Assert.Equal(7, actual.CornerRadius);
        Assert.Equal(5, actual.BorderThickness);
    }

    [Fact]
    public void ContextualStyleCache_CopiesMutableQuerySequences()
    {
        var styleSheet = new MarkdownStyleSheet(
        [
            new MarkdownStyleRule(
                new MarkdownStyleSelector(
                    MarkdownStyleRole.CodeBlock,
                    minimumNestingDepth: 2,
                    maximumNestingDepth: 2,
                    className: "warning"),
                new ElementStyleOverride { FontSize = 29 }),
        ]);
        ThemeSnapshot snapshot = CreateSnapshot(
            new ElementStyle { FontSize = 13 },
            new Dictionary<string, ElementStyleOverride>(),
            isHighContrast: false,
            styleSheet);
        var contexts = new List<string> { MarkdownElementKeys.ListDepth(2) };
        var aliases = new List<string> { MarkdownElementKeys.Class("warning") };

        ElementStyle first = snapshot.GetStyle(
            MarkdownElementKeys.CodeBlock,
            contexts,
            aliases,
            language: null,
            state: null);
        contexts[0] = MarkdownElementKeys.ListDepth(1);
        aliases[0] = MarkdownElementKeys.Class("ordinary");
        ElementStyle changed = snapshot.GetStyle(
            MarkdownElementKeys.CodeBlock,
            contexts,
            aliases,
            language: null,
            state: null);
        contexts[0] = MarkdownElementKeys.ListDepth(2);
        aliases[0] = MarkdownElementKeys.Class("warning");
        ElementStyle restored = snapshot.GetStyle(
            MarkdownElementKeys.CodeBlock,
            contexts,
            aliases,
            language: null,
            state: null);

        Assert.Equal(29, first.FontSize);
        Assert.Equal(13, changed.FontSize);
        Assert.Same(first, restored);
    }

    [Fact]
    public void WarmContextualStyleLookup_DoesNotAllocate()
    {
        var styleSheet = new MarkdownStyleSheet(
        [
            new MarkdownStyleRule(
                new MarkdownStyleSelector(
                    MarkdownStyleRole.CodeBlock,
                    minimumNestingDepth: 2,
                    maximumNestingDepth: 2,
                    language: "csharp",
                    state: "diff",
                    className: "warning"),
                new ElementStyleOverride { FontSize = 23 }),
        ]);
        ThemeSnapshot snapshot = CreateSnapshot(
            new ElementStyle { FontSize = 13 },
            new Dictionary<string, ElementStyleOverride>(),
            isHighContrast: false,
            styleSheet,
            textScaleFactor: 2.25);
        string[] contexts = [MarkdownElementKeys.Quote, MarkdownElementKeys.ListDepth(2)];
        string[] aliases = [MarkdownElementKeys.Class("warning")];
        ElementStyle expected = snapshot.GetStyle(
            MarkdownElementKeys.CodeBlock,
            contexts,
            aliases,
            language: "csharp",
            state: "diff");

        for (int i = 0; i < 1_000; i++)
        {
            _ = snapshot.GetStyle(
                MarkdownElementKeys.CodeBlock,
                contexts,
                aliases,
                language: "csharp",
                state: "diff");
        }

        ElementStyle? observed = null;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            observed = snapshot.GetStyle(
                MarkdownElementKeys.CodeBlock,
                contexts,
                aliases,
                language: "csharp",
                state: "diff");
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Same(expected, observed);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ContextualStyleCache_CanonicalizesLanguageAndStateQualifiers()
    {
        var styleSheet = new MarkdownStyleSheet(
        [
            new MarkdownStyleRule(
                new MarkdownStyleSelector(
                    MarkdownStyleRole.CodeBlock,
                    language: "csharp",
                    state: "diff"),
                new ElementStyleOverride { FontSize = 23 }),
        ]);
        ThemeSnapshot snapshot = CreateSnapshot(
            new ElementStyle { FontSize = 13 },
            new Dictionary<string, ElementStyleOverride>(),
            isHighContrast: false,
            styleSheet);
        string[] contexts = [MarkdownElementKeys.ListDepth(2)];
        string[] aliases = [MarkdownElementKeys.Class("warning")];

        ElementStyle canonical = snapshot.GetStyle(
            MarkdownElementKeys.CodeBlock,
            contexts,
            aliases,
            language: "csharp",
            state: "diff");
        ElementStyle equivalent = snapshot.GetStyle(
            MarkdownElementKeys.CodeBlock,
            contexts,
            aliases,
            language: " CSHARP ",
            state: " DIFF ");

        Assert.Same(canonical, equivalent);
        Assert.Equal(23, equivalent.FontSize);
    }
}
