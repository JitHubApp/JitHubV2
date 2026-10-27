using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace MarkdownRenderer.Theming;

/// <summary>
/// Describes the semantic context used to match a markdown style rule.
/// </summary>
/// <remarks>
/// Ancestor roles are explicit context supplied by the caller; they are not
/// inferred from arbitrary source nesting, element aliases, or class/id names.
/// The built-in Markdown layout currently supplies block-quote ancestry while
/// representing list nesting with its separate list-depth context key. It does
/// not automatically add heading or link roles as ancestors.
/// </remarks>
public readonly struct MarkdownStyleContext
{
    private static readonly IReadOnlyList<string> EmptyClasses = Array.Empty<string>();
    private static readonly IReadOnlyList<MarkdownStyleRole> EmptyAncestorRoles = Array.Empty<MarkdownStyleRole>();
    private readonly IReadOnlyList<MarkdownStyleRole>? _ancestorRoles;

    /// <summary>Initializes a style-matching context.</summary>
    public MarkdownStyleContext(
        MarkdownStyleRole role,
        int nestingDepth = 0,
        string? language = null,
        string? state = null,
        IEnumerable<string>? classNames = null)
    {
        if (role.IsEmpty)
            throw new ArgumentException("A style role is required.", nameof(role));
        if (nestingDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(nestingDepth));

        Role = role;
        NestingDepth = nestingDepth;
        Language = NormalizeOptional(language);
        State = NormalizeOptional(state);
        ClassNames = CopyClasses(classNames);
        _ancestorRoles = EmptyAncestorRoles;
    }

    /// <summary>Initializes a context with a defensive snapshot of semantic ancestor roles.</summary>
    /// <param name="role">The role being matched.</param>
    /// <param name="ancestorRoles">The containing semantic roles, from outermost to innermost.</param>
    /// <param name="nestingDepth">The zero-based semantic nesting depth.</param>
    /// <param name="language">The code or document language, when present.</param>
    /// <param name="state">The interaction or semantic state, when present.</param>
    /// <param name="classNames">Safe HTML or extension class names.</param>
    public MarkdownStyleContext(
        MarkdownStyleRole role,
        IEnumerable<MarkdownStyleRole> ancestorRoles,
        int nestingDepth = 0,
        string? language = null,
        string? state = null,
        IEnumerable<string>? classNames = null)
        : this(role, nestingDepth, language, state, classNames)
    {
        _ancestorRoles = CopyAncestorRoles(ancestorRoles);
    }

    /// <summary>Gets the semantic role.</summary>
    public MarkdownStyleRole Role { get; }
    /// <summary>Gets the zero-based semantic nesting depth.</summary>
    public int NestingDepth { get; }
    /// <summary>Gets the normalized code or document language, when present.</summary>
    public string? Language { get; }
    /// <summary>Gets the normalized interaction or semantic state, when present.</summary>
    public string? State { get; }
    /// <summary>Gets a defensive snapshot of safe HTML or extension class names.</summary>
    public IReadOnlyList<string> ClassNames { get; }
    /// <summary>Gets a defensive snapshot of containing semantic roles, from outermost to innermost.</summary>
    public IReadOnlyList<MarkdownStyleRole> AncestorRoles => _ancestorRoles ?? EmptyAncestorRoles;

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> CopyClasses(IEnumerable<string>? values)
    {
        if (values is null)
            return EmptyClasses;

        var result = new List<string>();
        foreach (var value in values)
        {
            var normalized = NormalizeOptional(value);
            if (normalized is not null)
                result.Add(normalized[0] == '.' ? normalized.Substring(1) : normalized);
        }

        return result.Count == 0
            ? EmptyClasses
            : new ReadOnlyCollection<string>(result.ToArray());
    }

    private static IReadOnlyList<MarkdownStyleRole> CopyAncestorRoles(IEnumerable<MarkdownStyleRole> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var result = new List<MarkdownStyleRole>();
        foreach (MarkdownStyleRole role in values)
        {
            if (role.IsEmpty)
                throw new ArgumentException("Ancestor roles cannot contain an empty role.", nameof(values));
            result.Add(role);
        }

        return result.Count == 0
            ? EmptyAncestorRoles
            : new ReadOnlyCollection<MarkdownStyleRole>(result.ToArray());
    }
}

/// <summary>Helpers for the renderer's non-role keys carried in style contexts.</summary>
internal static class MarkdownStyleContextKeys
{
    private const string ListDepthPrefix = "ListDepth";

    /// <summary>
    /// Parses the same list-depth metadata syntax used by compiled theme resolution.
    /// A false result means the key remains eligible to represent a custom semantic role.
    /// </summary>
    internal static bool TryParseListNestingDepth(string? key, out int depth)
    {
        depth = 0;
        return key is not null &&
            key.StartsWith(ListDepthPrefix, StringComparison.Ordinal) &&
            int.TryParse(
                key.AsSpan(ListDepthPrefix.Length),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out depth);
    }
}

/// <summary>
/// Selects markdown content by role and optional semantic qualifiers.
/// </summary>
public sealed class MarkdownStyleSelector
{
    /// <summary>Initializes a style selector.</summary>
    public MarkdownStyleSelector(
        MarkdownStyleRole role,
        int minimumNestingDepth = 0,
        int maximumNestingDepth = int.MaxValue,
        string? language = null,
        string? state = null,
        string? className = null)
    {
        if (role.IsEmpty)
            throw new ArgumentException("A style role is required.", nameof(role));
        if (minimumNestingDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumNestingDepth));
        if (maximumNestingDepth < minimumNestingDepth)
            throw new ArgumentOutOfRangeException(nameof(maximumNestingDepth));

        Role = role;
        MinimumNestingDepth = minimumNestingDepth;
        MaximumNestingDepth = maximumNestingDepth;
        Language = NormalizeOptional(language);
        State = NormalizeOptional(state);

        var normalizedClass = NormalizeOptional(className);
        ClassName = normalizedClass is not null && normalizedClass[0] == '.'
            ? normalizedClass.Substring(1)
            : normalizedClass;
        AncestorRole = null;
    }

    /// <summary>Initializes a selector qualified by a containing semantic role.</summary>
    /// <param name="role">The semantic role selected by this rule.</param>
    /// <param name="ancestorRole">A role that must occur in the containing style-context stack.</param>
    /// <param name="minimumNestingDepth">The inclusive minimum nesting depth.</param>
    /// <param name="maximumNestingDepth">The inclusive maximum nesting depth.</param>
    /// <param name="language">The optional language qualifier.</param>
    /// <param name="state">The optional semantic or interaction state qualifier.</param>
    /// <param name="className">The optional safe HTML or extension class qualifier.</param>
    public MarkdownStyleSelector(
        MarkdownStyleRole role,
        MarkdownStyleRole ancestorRole,
        int minimumNestingDepth = 0,
        int maximumNestingDepth = int.MaxValue,
        string? language = null,
        string? state = null,
        string? className = null)
        : this(role, minimumNestingDepth, maximumNestingDepth, language, state, className)
    {
        if (ancestorRole.IsEmpty)
            throw new ArgumentException("An ancestor role is required.", nameof(ancestorRole));

        AncestorRole = ancestorRole;
    }

    /// <summary>Gets the required semantic role.</summary>
    public MarkdownStyleRole Role { get; }
    /// <summary>Gets the inclusive minimum nesting depth.</summary>
    public int MinimumNestingDepth { get; }
    /// <summary>Gets the inclusive maximum nesting depth.</summary>
    public int MaximumNestingDepth { get; }
    /// <summary>Gets the optional language qualifier.</summary>
    public string? Language { get; }
    /// <summary>Gets the optional semantic or interaction state qualifier.</summary>
    public string? State { get; }
    /// <summary>Gets the optional safe HTML or extension class qualifier.</summary>
    public string? ClassName { get; }
    /// <summary>Gets the optional semantic role that must occur in the ancestor context.</summary>
    /// <remarks>
    /// The selector only matches ancestors explicitly supplied by the caller.
    /// Built-in Markdown layout does not implicitly add heading or link roles,
    /// and aliases are not interpreted as ancestor roles.
    /// </remarks>
    public MarkdownStyleRole? AncestorRole { get; }

    /// <summary>Returns whether this selector matches a semantic style context.</summary>
    public bool Matches(MarkdownStyleContext context)
    {
        if (Role != context.Role ||
            context.NestingDepth < MinimumNestingDepth ||
            context.NestingDepth > MaximumNestingDepth ||
            !MatchesOptional(Language, context.Language) ||
            !MatchesOptional(State, context.State))
        {
            return false;
        }

        if (AncestorRole is MarkdownStyleRole ancestorRole)
        {
            bool found = false;
            IReadOnlyList<MarkdownStyleRole> ancestorRoles = context.AncestorRoles;
            for (int i = 0; i < ancestorRoles.Count; i++)
            {
                if (ancestorRoles[i] == ancestorRole)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
                return false;
        }

        if (ClassName is null)
            return true;

        for (int i = 0; i < context.ClassNames.Count; i++)
        {
            if (string.Equals(ClassName, context.ClassNames[i], StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool MatchesOptional(string? selector, string? actual)
        => selector is null || string.Equals(selector, actual, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A selector and immutable partial style declaration.</summary>
public sealed class MarkdownStyleRule
{
    /// <summary>Initializes a style rule.</summary>
    public MarkdownStyleRule(MarkdownStyleSelector selector, ElementStyleOverride style)
    {
        Selector = selector ?? throw new ArgumentNullException(nameof(selector));
        Style = style ?? throw new ArgumentNullException(nameof(style));
        if (style.RelativeFontSizeScale is float relativeScale)
        {
            if (!float.IsFinite(relativeScale) || relativeScale <= 0f || relativeScale > 1f)
                throw new ArgumentOutOfRangeException(nameof(style), "A relative font-size scale must be finite and in (0, 1].");
            if (style.FontSize.HasValue)
                throw new ArgumentException("A style rule cannot declare both absolute and relative font size.", nameof(style));
            if (selector.Role.Name is not (MarkdownElementKeys.Subscript or MarkdownElementKeys.Superscript or MarkdownElementKeys.CodeInline))
                throw new ArgumentException("Relative font size is supported only for subscript, superscript, and inline code roles.", nameof(selector));
        }
    }

    /// <summary>Gets the selector.</summary>
    public MarkdownStyleSelector Selector { get; }
    /// <summary>Gets the partial style declaration.</summary>
    public ElementStyleOverride Style { get; }
}

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

/// <summary>
/// Immutable role-indexed representation used by a <see cref="ThemeSnapshot"/>.
/// It avoids scanning unrelated rules and matches qualifiers directly without
/// materializing a <see cref="MarkdownStyleContext"/> or a class-name list.
/// </summary>
internal sealed class CompiledMarkdownStyleSheet
{
    private readonly FrozenDictionary<string, CompiledRule[]> _rulesByRole;
    internal readonly byte LayoutPolicy;
    internal readonly bool HasRelativeScriptFontSizeRules;
    internal readonly bool HasRelativeInlineCodeFontSizeRules;
    internal readonly bool CenterDefaultTableHeaders;
    internal readonly bool MiddleAlignInlineImages;

    internal CompiledMarkdownStyleSheet(
        IReadOnlyList<MarkdownStyleRule> rules,
        byte layoutPolicy)
    {
        LayoutPolicy = layoutPolicy;
        CenterDefaultTableHeaders = (layoutPolicy & MarkdownStyleSheet.CenterDefaultTableHeadersFlag) != 0;
        MiddleAlignInlineImages = (layoutPolicy & MarkdownStyleSheet.MiddleAlignInlineImagesFlag) != 0;
        if (rules.Count == 0)
        {
            _rulesByRole = FrozenDictionary<string, CompiledRule[]>.Empty;
            return;
        }

        var groupedRules = new Dictionary<string, List<CompiledRule>>(StringComparer.Ordinal);
        bool hasRelativeScriptFontSizeRules = false;
        bool hasRelativeInlineCodeFontSizeRules = false;
        for (int i = 0; i < rules.Count; i++)
        {
            MarkdownStyleRule rule = rules[i];
            string roleName = rule.Selector.Role.Name;
            if (rule.Style.RelativeFontSizeScale.HasValue)
            {
                hasRelativeScriptFontSizeRules |= roleName is MarkdownElementKeys.Subscript or MarkdownElementKeys.Superscript;
                hasRelativeInlineCodeFontSizeRules |= roleName == MarkdownElementKeys.CodeInline;
            }
            if (!groupedRules.TryGetValue(roleName, out List<CompiledRule>? roleRules))
            {
                roleRules = new List<CompiledRule>();
                groupedRules.Add(roleName, roleRules);
            }

            roleRules.Add(new CompiledRule(rule.Selector, rule.Style));
        }

        var compiled = new Dictionary<string, CompiledRule[]>(groupedRules.Count, StringComparer.Ordinal);
        foreach ((string roleName, List<CompiledRule> roleRules) in groupedRules)
            compiled.Add(roleName, roleRules.ToArray());

        _rulesByRole = compiled.ToFrozenDictionary(StringComparer.Ordinal);
        HasRelativeScriptFontSizeRules = hasRelativeScriptFontSizeRules;
        HasRelativeInlineCodeFontSizeRules = hasRelativeInlineCodeFontSizeRules;
    }

    internal bool HasRules => _rulesByRole.Count != 0;


    internal IEnumerable<string> RoleNames => _rulesByRole.Keys;

    internal ElementStyle Resolve(
        ElementStyle baseStyle,
        string roleName,
        int nestingDepth,
        string? language,
        string? state,
        IReadOnlyList<string>? contextKeys,
        IReadOnlyList<string>? aliases)
    {
        if (!_rulesByRole.TryGetValue(roleName, out CompiledRule[]? rules))
        {
            string normalizedRole = roleName.Trim();
            if (normalizedRole.Length == 0 ||
                !_rulesByRole.TryGetValue(normalizedRole, out rules))
            {
                return baseStyle;
            }
        }

        ElementStyle result = baseStyle;
        for (int i = 0; i < rules.Length; i++)
        {
            ref readonly CompiledRule rule = ref rules[i];
            if (rule.Matches(nestingDepth, language, state, contextKeys, aliases))
                result = ThemeSnapshot.ApplyOverride(result, rule.Style);
        }

        return result;
    }

    private readonly struct CompiledRule
    {
        private readonly int _minimumNestingDepth;
        private readonly int _maximumNestingDepth;
        private readonly string? _language;
        private readonly string? _state;
        private readonly string? _className;
        private readonly string? _ancestorRoleName;

        internal CompiledRule(MarkdownStyleSelector selector, ElementStyleOverride style)
        {
            _minimumNestingDepth = selector.MinimumNestingDepth;
            _maximumNestingDepth = selector.MaximumNestingDepth;
            _language = selector.Language;
            _state = selector.State;
            _className = selector.ClassName;
            _ancestorRoleName = selector.AncestorRole?.Name;
            Style = style;
        }

        internal readonly ElementStyleOverride Style;

        internal bool Matches(
            int nestingDepth,
            string? language,
            string? state,
            IReadOnlyList<string>? contextKeys,
            IReadOnlyList<string>? aliases)
        {
            if (nestingDepth < _minimumNestingDepth ||
                nestingDepth > _maximumNestingDepth ||
                !MatchesOptional(_language, language) ||
                !MatchesOptional(_state, state))
            {
                return false;
            }

            if (_ancestorRoleName is not null)
            {
                bool found = false;
                if (contextKeys is not null)
                {
                    for (int i = 0; i < contextKeys.Count; i++)
                    {
                        if (!MarkdownStyleContextKeys.TryParseListNestingDepth(contextKeys[i], out _) &&
                            string.Equals(_ancestorRoleName, contextKeys[i], StringComparison.Ordinal))
                        {
                            found = true;
                            break;
                        }
                    }
                }

                if (!found)
                    return false;
            }

            if (_className is null)
                return true;

            if (aliases is null)
                return false;

            for (int i = 0; i < aliases.Count; i++)
            {
                string? alias = aliases[i];
                if (string.IsNullOrEmpty(alias) || alias[0] != '.')
                    continue;

                ReadOnlySpan<char> candidate = Trim(alias.AsSpan(1));
                if (!candidate.IsEmpty && candidate[0] == '.')
                    candidate = candidate[1..];

                if (candidate.Equals(_className.AsSpan(), StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static bool MatchesOptional(string? selector, string? actual)
        {
            if (selector is null)
                return true;
            if (actual is null)
                return false;

            return Trim(actual.AsSpan()).Equals(selector.AsSpan(), StringComparison.OrdinalIgnoreCase);
        }

        private static ReadOnlySpan<char> Trim(ReadOnlySpan<char> value)
        {
            int start = 0;
            while (start < value.Length && char.IsWhiteSpace(value[start]))
                start++;

            int end = value.Length;
            while (end > start && char.IsWhiteSpace(value[end - 1]))
                end--;

            return value[start..end];
        }
    }
}
