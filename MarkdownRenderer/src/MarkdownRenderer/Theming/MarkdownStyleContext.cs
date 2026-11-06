using System;
using System.Collections.Generic;
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
    /// <param name="role">The role being matched.</param>
    /// <param name="nestingDepth">The zero-based semantic nesting depth.</param>
    /// <param name="language">The code or document language, when present.</param>
    /// <param name="state">The interaction or semantic state, when present.</param>
    /// <param name="classNames">Safe HTML or extension class names.</param>
    /// <param name="ancestorRoles">
    /// The containing semantic roles, from outermost to innermost. The context keeps
    /// a defensive snapshot.
    /// </param>
    public MarkdownStyleContext(
        MarkdownStyleRole role,
        int nestingDepth = 0,
        string? language = null,
        string? state = null,
        IEnumerable<string>? classNames = null,
        IEnumerable<MarkdownStyleRole>? ancestorRoles = null)
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
        _ancestorRoles = ancestorRoles is null ? EmptyAncestorRoles : CopyAncestorRoles(ancestorRoles);
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
