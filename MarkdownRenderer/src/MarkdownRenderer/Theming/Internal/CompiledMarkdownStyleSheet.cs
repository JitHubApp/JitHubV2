using System;
using System.Collections.Generic;
using System.Collections.Frozen;

namespace MarkdownRenderer.Theming.Internal;

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
