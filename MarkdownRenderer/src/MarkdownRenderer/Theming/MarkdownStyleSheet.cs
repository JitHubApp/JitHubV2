using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace MarkdownRenderer.Theming;

/// <summary>
/// An immutable, thread-safe ordered collection of semantic markdown style rules.
/// </summary>
/// <remarks>
/// Matching rules are applied in declaration order; later rules override fields
/// supplied by earlier rules. The caller remains responsible for applying
/// mandatory High Contrast substitutions after the style sheet is resolved.
/// </remarks>
public sealed class MarkdownStyleSheet
{
    private const byte CollapseMarginsFlag = 1;
    private const byte ResetTrailingMarginFlag = 2;
    private const byte ResetLeadingMarginFlag = 4;
    internal const byte CenterDefaultTableHeadersFlag = 8;
    internal const byte MiddleAlignInlineImagesFlag = 16;
    private readonly MarkdownStyleRule[] _rules;
    private readonly IReadOnlyList<MarkdownStyleRule> _rulesView;
    private readonly byte _layoutPolicy;

    /// <summary>Gets an empty style sheet.</summary>
    public static MarkdownStyleSheet Empty { get; } = new(Array.Empty<MarkdownStyleRule>());

    /// <summary>Initializes an immutable style sheet from an ordered rule sequence.</summary>
    public MarkdownStyleSheet(IEnumerable<MarkdownStyleRule> rules)
        : this(rules, layoutPolicy: 0)
    {
    }

    private MarkdownStyleSheet(
        IEnumerable<MarkdownStyleRule> rules,
        byte layoutPolicy)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var copy = new List<MarkdownStyleRule>();
        foreach (var rule in rules)
            copy.Add(rule ?? throw new ArgumentException("Style sheets cannot contain null rules.", nameof(rules)));

        _rules = copy.ToArray();
        _rulesView = new ReadOnlyCollection<MarkdownStyleRule>(_rules);
        _layoutPolicy = layoutPolicy;
    }

    /// <summary>Gets the rules in deterministic application order.</summary>
    public IReadOnlyList<MarkdownStyleRule> Rules => _rulesView;

    /// <summary>
    /// Returns a new style sheet that sizes default subscript and superscript
    /// runs relative to the containing run's font size. Nested scripts compound
    /// the factor, down to a 0.1-DIP floor for policy-derived sizes. The rules
    /// are appended after existing rules, overriding lower-precedence theme or
    /// resource script sizes. A later absolute stylesheet rule remains absolute.
    /// </summary>
    /// <param name="scale">The relative size factor, greater than zero and at most one.</param>
    /// <remarks>
    /// A new sheet, including <see cref="Empty"/>, has no relative script
    /// sizing rules. Calling this method appends subscript and superscript
    /// declarations after existing rules. These stylesheet declarations take
    /// precedence over fixed script sizes supplied by lower-precedence theme or
    /// resource styles. Calling it again appends replacement declarations; the
    /// last relative declaration wins. A later absolute stylesheet rule wins
    /// over this relative declaration. Start from a new sheet to disable the
    /// behavior. Renderer layout applies these declarations where the containing run's size is available;
    /// <see cref="Resolve(ElementStyle, MarkdownStyleContext)"/> retains the
    /// nominal <see cref="ElementStyle.FontSize"/>. Script glyph selection and
    /// baseline placement continue to use the font's existing OpenType
    /// subscript/superscript behavior. Those substitutions are font-dependent;
    /// this API does not implement CSS <c>font-size: smaller</c> or
    /// <c>vertical-align: sub|super</c> metrics or promise browser baseline parity.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="scale"/> is non-finite, not positive, or greater than one.
    /// </exception>
    public MarkdownStyleSheet WithRelativeScriptFontSize(float scale)
    {
        if (!float.IsFinite(scale) || scale <= 0f || scale > 1f)
            throw new ArgumentOutOfRangeException(nameof(scale), "The script size scale must be finite and in (0, 1].");

        var relativeOverride = new ElementStyleOverride { RelativeFontSizeScale = scale };
        var subscriptRule = new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.FromElementKey(MarkdownElementKeys.Subscript)),
            relativeOverride);
        var superscriptRule = new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.FromElementKey(MarkdownElementKeys.Superscript)),
            relativeOverride);
        return WithRule(subscriptRule).WithRule(superscriptRule);
    }

    /// <summary>
    /// Appends a profile-scoped inline-code percentage declaration. Layout resolves
    /// it against containing typography; later absolute declarations still win.
    /// </summary>
    internal MarkdownStyleSheet WithRelativeInlineCodeFontSize(float scale)
    {
        if (!float.IsFinite(scale) || scale <= 0f || scale > 1f)
            throw new ArgumentOutOfRangeException(nameof(scale), "The inline code size scale must be finite and in (0, 1].");

        return WithRule(new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.InlineCode),
            new ElementStyleOverride { RelativeFontSizeScale = scale }));
    }

    /// <summary>
    /// Returns a new style sheet that collapses adjacent positive margins on
    /// top-level normal-flow blocks to their maximum.
    /// </summary>
    public MarkdownStyleSheet WithPositiveAdjacentMarginCollapse()
        => new(_rules, (byte)(_layoutPolicy | CollapseMarginsFlag));

    /// <summary>Returns a new style sheet that removes the first top-level block's top margin.</summary>
    internal MarkdownStyleSheet WithZeroLeadingTopLevelBlockTopMargin()
        => new(_rules, (byte)(_layoutPolicy | ResetLeadingMarginFlag));

    /// <summary>Centers unaligned table header cells to match the HTML user-agent default.</summary>
    internal MarkdownStyleSheet WithCenteredDefaultTableHeaders()
        => new(_rules, (byte)(_layoutPolicy | CenterDefaultTableHeadersFlag));

    /// <summary>Uses CSS middle alignment for image objects in the opt-in source-replay profile.</summary>
    internal MarkdownStyleSheet WithCssMiddleAlignedInlineImages()
        => new(_rules, (byte)(_layoutPolicy | MiddleAlignInlineImagesFlag));

    /// <summary>
    /// Returns a new style sheet that removes the bottom margin after its last
    /// top-level block, matching CSS containers that reset their final child.
    /// This policy is independent of adjacent-margin collapse.
    /// </summary>
    public MarkdownStyleSheet WithZeroTrailingTopLevelBlockMargin()
        => new(_rules, (byte)(_layoutPolicy | ResetTrailingMarginFlag));

    /// <summary>Returns a new style sheet with a rule appended at highest precedence.</summary>
    public MarkdownStyleSheet WithRule(MarkdownStyleRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var result = new MarkdownStyleRule[_rules.Length + 1];
        Array.Copy(_rules, result, _rules.Length);
        result[^1] = rule;
        return new MarkdownStyleSheet(result, _layoutPolicy);
    }

    /// <summary>
    /// Layers matching declarations over an already resolved theme style.
    /// </summary>
    public ElementStyle Resolve(ElementStyle baseStyle, MarkdownStyleContext context)
    {
        ArgumentNullException.ThrowIfNull(baseStyle);

        var result = baseStyle;
        for (int i = 0; i < _rules.Length; i++)
        {
            var rule = _rules[i];
            if (rule.Selector.Matches(context))
                result = ThemeSnapshot.ApplyOverride(result, rule.Style);
        }

        return result;
    }

    internal CompiledMarkdownStyleSheet Compile()
        => new(_rules, _layoutPolicy);
}
