using System;

namespace MarkdownRenderer.Images.Svg;

/// <summary>Immutable inputs used to open one sanitized SVG.</summary>
/// <param name="SanitizedSvgBytes">
/// Sanitized SVG bytes admitted by the host. Providers must repeat all
/// authoritative security and resource checks rather than trusting preflight,
/// and must snapshot the bytes before the asynchronous <c>OpenAsync</c>
/// operation completes. The caller must keep the backing memory unchanged and
/// alive until that operation has completed (including cancellation or failure),
/// and may release or reuse it afterwards. Returning a pending
/// <c>ValueTask</c> does not complete the operation.
/// </param>
/// <param name="Locale">Optional BCP-47 locale used for SVG text and generic-family fallback.</param>
/// <param name="ColorScheme">Resolved light or dark color scheme.</param>
/// <param name="SemanticColor">Optional semantic color used to resolve <c>currentColor</c>.</param>
/// <param name="Priority">Scheduling priority for the open operation.</param>
public sealed record MarkdownSvgOpenRequest(
    ReadOnlyMemory<byte> SanitizedSvgBytes,
    string? Locale = null,
    MarkdownSvgColorScheme ColorScheme = MarkdownSvgColorScheme.Light,
    MarkdownSvgColor? SemanticColor = null,
    MarkdownSvgRenderPriority Priority = MarkdownSvgRenderPriority.Visible);
