using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Theming;

/// <summary>
/// Selects markdown content by role and optional semantic qualifiers.
/// </summary>
public sealed class MarkdownStyleSelector
{
    /// <summary>Initializes a style selector.</summary>
    /// <param name="role">The semantic role selected by this rule.</param>
    /// <param name="minimumNestingDepth">The inclusive minimum nesting depth.</param>
    /// <param name="maximumNestingDepth">The inclusive maximum nesting depth.</param>
    /// <param name="language">The optional language qualifier.</param>
    /// <param name="state">The optional semantic or interaction state qualifier.</param>
    /// <param name="className">The optional safe HTML or extension class qualifier.</param>
    /// <param name="ancestorRole">
    /// An optional role that must occur in the containing style-context stack.
    /// </param>
    public MarkdownStyleSelector(
        MarkdownStyleRole role,
        int minimumNestingDepth = 0,
        int maximumNestingDepth = int.MaxValue,
        string? language = null,
        string? state = null,
        string? className = null,
        MarkdownStyleRole? ancestorRole = null)
    {
        if (role.IsEmpty)
            throw new ArgumentException("A style role is required.", nameof(role));
        if (minimumNestingDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumNestingDepth));
        if (maximumNestingDepth < minimumNestingDepth)
            throw new ArgumentOutOfRangeException(nameof(maximumNestingDepth));
        if (ancestorRole is { IsEmpty: true })
            throw new ArgumentException("An ancestor role cannot be empty.", nameof(ancestorRole));

        Role = role;
        MinimumNestingDepth = minimumNestingDepth;
        MaximumNestingDepth = maximumNestingDepth;
        Language = NormalizeOptional(language);
        State = NormalizeOptional(state);

        var normalizedClass = NormalizeOptional(className);
        ClassName = normalizedClass is not null && normalizedClass[0] == '.'
            ? normalizedClass.Substring(1)
            : normalizedClass;
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
