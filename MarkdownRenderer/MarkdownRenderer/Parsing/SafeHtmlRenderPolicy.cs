using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Parsing;

/// <summary>
/// Internal, dependency-free snapshot of the optional HTML pack's rendering policy.
/// </summary>
internal sealed class SafeHtmlRenderPolicy
{
    private readonly HashSet<string> _allowedStyleClasses;

    internal SafeHtmlRenderPolicy(
        bool enableLinks,
        bool enableImages,
        bool renderUnknownElementsLiterally,
        IEnumerable<string>? allowedStyleClasses,
        SafeHtmlParseLimits? limits = null)
    {
        EnableLinks = enableLinks;
        EnableImages = enableImages;
        RenderUnknownElementsLiterally = renderUnknownElementsLiterally;
        Limits = limits ?? SafeHtmlParseLimits.Default;
        _allowedStyleClasses = allowedStyleClasses is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(allowedStyleClasses, StringComparer.Ordinal);
    }

    internal bool EnableLinks { get; }

    internal bool EnableImages { get; }

    internal bool RenderUnknownElementsLiterally { get; }

    internal SafeHtmlParseLimits Limits { get; }

    internal bool IsStyleClassAllowed(string className) =>
        _allowedStyleClasses.Contains(className);
}
