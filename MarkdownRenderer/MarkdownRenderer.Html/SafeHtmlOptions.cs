using System.Collections.Immutable;

namespace MarkdownRenderer.Html;

/// <summary>Determines how elements outside the native safe subset are presented.</summary>
public enum SafeHtmlUnknownElementBehavior
{
    /// <summary>Shows the exact tag markup as inert text.</summary>
    RenderLiteral,
    /// <summary>Omits unknown tags while preserving their non-suppressed text content.</summary>
    KeepTextContent,
}

/// <summary>
/// Immutable host options for the native safe subset. No option can enable scripts,
/// CSS layout, browser DOM behavior, direct network access, or filesystem access.
/// </summary>
public sealed class SafeHtmlOptions
{
    /// <summary>Creates an immutable safe-HTML policy snapshot.</summary>
    /// <param name="budgets">Optional host-lowered parser ceilings.</param>
    /// <param name="enableLinks">Whether safe links may produce host-mediated link actions.</param>
    /// <param name="enableImages">Whether safe images may use the host image resolver.</param>
    /// <param name="unknownElementBehavior">How elements outside the safe subset are rendered.</param>
    /// <param name="allowedStyleClasses">HTML class tokens allowed to reach stylesheet selectors.</param>
    public SafeHtmlOptions(
        SafeHtmlBudgets? budgets = null,
        bool enableLinks = true,
        bool enableImages = true,
        SafeHtmlUnknownElementBehavior unknownElementBehavior = SafeHtmlUnknownElementBehavior.RenderLiteral,
        IEnumerable<string>? allowedStyleClasses = null)
    {
        // The safe subset's audited defaults are absolute ceilings. Hosts may
        // lower individual values, but an options object cannot silently widen
        // parser/resource limits beyond the package's supported policy.
        Budgets = (budgets ?? SafeHtmlBudgets.Default).LoweredTo(SafeHtmlBudgets.Default);
        EnableLinks = enableLinks;
        EnableImages = enableImages;
        UnknownElementBehavior = unknownElementBehavior;
        AllowedStyleClasses = NormalizeClasses(allowedStyleClasses);
    }

    /// <summary>Gets the audited default policy.</summary>
    public static SafeHtmlOptions Default { get; } = new();

    /// <summary>Gets the parser resource ceilings.</summary>
    public SafeHtmlBudgets Budgets { get; }

    /// <summary>Links still require approval and resolution by the host link policy.</summary>
    public bool EnableLinks { get; }

    /// <summary>Images still require resolution by the host image service.</summary>
    public bool EnableImages { get; }

    /// <summary>Gets how elements outside the native safe subset are presented.</summary>
    public SafeHtmlUnknownElementBehavior UnknownElementBehavior { get; }

    /// <summary>Class tokens that may participate in host-owned stylesheet matching.</summary>
    public ImmutableHashSet<string> AllowedStyleClasses { get; }

    private static ImmutableHashSet<string> NormalizeClasses(IEnumerable<string>? classes)
    {
        if (classes is null)
        {
            return ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
        }

        var builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (string? candidate in classes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
            if (!IsSafeClassToken(candidate))
            {
                throw new ArgumentException(
                    $"'{candidate}' is not a safe HTML class token. Use ASCII letters, digits, hyphens, and underscores.",
                    nameof(classes));
            }

            builder.Add(candidate);
        }

        return builder.ToImmutable();
    }

    private static bool IsSafeClassToken(string value)
    {
        if (value.Length is 0 or > 128)
        {
            return false;
        }

        foreach (char character in value)
        {
            bool allowed = character is >= 'a' and <= 'z' or
                >= 'A' and <= 'Z' or
                >= '0' and <= '9' or
                '-' or '_';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }
}
