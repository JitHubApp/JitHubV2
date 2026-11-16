namespace MarkdownRenderer.Controls.Internal.Focus;

/// <summary>
/// The viewport state a focus reveal started from, and the top it scrolls
/// toward once it has scrolled. A reveal may need several geometry retries
/// while lazy layout settles; it stays valid only while the viewport is where
/// focus left it or on the reveal's own (animated, possibly clamped) way.
/// </summary>
internal readonly record struct FocusRevealOrigin(long ViewportRequestSerial, double ViewportTop, double? RevealTop);
