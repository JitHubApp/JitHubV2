using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace MarkdownRenderer.Theming;

/// <summary>
/// Describes the semantic context used to match a markdown style rule.
/// </summary>
public readonly struct MarkdownStyleContext
{
    private static readonly IReadOnlyList<string> EmptyClasses = Array.Empty<string>();

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
    private readonly MarkdownStyleRule[] _rules;
    private readonly IReadOnlyList<MarkdownStyleRule> _rulesView;

    /// <summary>Gets an empty style sheet.</summary>
    public static MarkdownStyleSheet Empty { get; } = new(Array.Empty<MarkdownStyleRule>());

    /// <summary>Initializes an immutable style sheet from an ordered rule sequence.</summary>
    public MarkdownStyleSheet(IEnumerable<MarkdownStyleRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var copy = new List<MarkdownStyleRule>();
        foreach (var rule in rules)
            copy.Add(rule ?? throw new ArgumentException("Style sheets cannot contain null rules.", nameof(rules)));

        _rules = copy.ToArray();
        _rulesView = new ReadOnlyCollection<MarkdownStyleRule>(_rules);
    }

    /// <summary>Gets the rules in deterministic application order.</summary>
    public IReadOnlyList<MarkdownStyleRule> Rules => _rulesView;

    /// <summary>Returns a new style sheet with a rule appended at highest precedence.</summary>
    public MarkdownStyleSheet WithRule(MarkdownStyleRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var result = new MarkdownStyleRule[_rules.Length + 1];
        Array.Copy(_rules, result, _rules.Length);
        result[^1] = rule;
        return new MarkdownStyleSheet(result);
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
        => new(_rules);
}

/// <summary>
/// Immutable role-indexed representation used by a <see cref="ThemeSnapshot"/>.
/// It avoids scanning unrelated rules and matches qualifiers directly without
/// materializing a <see cref="MarkdownStyleContext"/> or a class-name list.
/// </summary>
internal sealed class CompiledMarkdownStyleSheet
{
    private readonly FrozenDictionary<string, CompiledRule[]> _rulesByRole;

    internal CompiledMarkdownStyleSheet(IReadOnlyList<MarkdownStyleRule> rules)
    {
        if (rules.Count == 0)
        {
            _rulesByRole = FrozenDictionary<string, CompiledRule[]>.Empty;
            return;
        }

        var groupedRules = new Dictionary<string, List<CompiledRule>>(StringComparer.Ordinal);
        for (int i = 0; i < rules.Count; i++)
        {
            MarkdownStyleRule rule = rules[i];
            string roleName = rule.Selector.Role.Name;
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
    }

    internal bool HasRules => _rulesByRole.Count != 0;

    internal IEnumerable<string> RoleNames => _rulesByRole.Keys;

    internal ElementStyle Resolve(
        ElementStyle baseStyle,
        string roleName,
        int nestingDepth,
        string? language,
        string? state,
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
            if (rule.Matches(nestingDepth, language, state, aliases))
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

        internal CompiledRule(MarkdownStyleSelector selector, ElementStyleOverride style)
        {
            _minimumNestingDepth = selector.MinimumNestingDepth;
            _maximumNestingDepth = selector.MaximumNestingDepth;
            _language = selector.Language;
            _state = selector.State;
            _className = selector.ClassName;
            Style = style;
        }

        internal ElementStyleOverride Style { get; }

        internal bool Matches(
            int nestingDepth,
            string? language,
            string? state,
            IReadOnlyList<string>? aliases)
        {
            if (nestingDepth < _minimumNestingDepth ||
                nestingDepth > _maximumNestingDepth ||
                !MatchesOptional(_language, language) ||
                !MatchesOptional(_state, state))
            {
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
