using System;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Theming;
using Windows.UI;

namespace MarkdownRenderer.GitHub;

/// <summary>
/// A fixed, opt-in visual profile for a bounded GitHub README comparison surface.
/// </summary>
/// <remarks>
/// A profile does not change parsing, the renderer's defaults, or a caller's
/// application-wide theme. Use <see cref="CreateTheme"/> for a new profile theme
/// instance and apply <see cref="StyleSheet"/> only to the intended markdown
/// control. The Light profile is intended for source-bound visual comparison;
/// High Contrast colors are still enforced by MarkdownRenderer.
/// </remarks>
public sealed class GitHubReadmeVisualStyleProfile
{
    internal GitHubReadmeVisualStyleProfile(
        string id,
        int version,
        string fingerprint,
        Color surfaceColor,
        Color accentColor,
        MarkdownStyleSheet styleSheet,
        CodeBlockChromeMode codeBlockChromeMode = CodeBlockChromeMode.Enhanced,
        CodeBlockLineNumberMode codeBlockLineNumberMode = CodeBlockLineNumberMode.AutoMultiline,
        bool isCodeBlockCopyEnabled = true)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        if (version <= 0)
            throw new ArgumentOutOfRangeException(nameof(version));
        Version = version;
        Fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
        SurfaceColor = surfaceColor;
        AccentColor = accentColor;
        StyleSheet = styleSheet ?? throw new ArgumentNullException(nameof(styleSheet));
        CodeBlockChromeMode = codeBlockChromeMode;
        CodeBlockLineNumberMode = codeBlockLineNumberMode;
        IsCodeBlockCopyEnabled = isCodeBlockCopyEnabled;
    }

    /// <summary>Gets the stable profile identifier.</summary>
    public string Id { get; }

    /// <summary>Gets the profile version. A visual change requires a new version.</summary>
    public int Version { get; }

    /// <summary>
    /// Gets the lowercase SHA-256 of the canonical CSS fixture, including its
    /// line endings. Native and Edge evidence must report this same fingerprint.
    /// </summary>
    public string Fingerprint { get; }

    /// <summary>Gets the immutable semantic style sheet for this profile.</summary>
    public MarkdownStyleSheet StyleSheet { get; }

    /// <summary>Gets the code-block chrome required by this visual profile.</summary>
    public CodeBlockChromeMode CodeBlockChromeMode { get; }

    /// <summary>Gets the code-block line-number policy required by this visual profile.</summary>
    public CodeBlockLineNumberMode CodeBlockLineNumberMode { get; }

    /// <summary>Gets whether this profile exposes the native code-block copy action.</summary>
    public bool IsCodeBlockCopyEnabled { get; }

    /// <summary>Gets the preferred document surface color for non-High-Contrast rendering.</summary>
    public Color SurfaceColor { get; }

    /// <summary>Gets the preferred accent color for non-High-Contrast rendering.</summary>
    public Color AccentColor { get; }

    /// <summary>
    /// Creates a fresh mutable theme with this profile's document surface and
    /// link accent. High Contrast continues to use the system Window color.
    /// </summary>
    public MarkdownTheme CreateTheme()
        => new()
        {
            SurfaceColor = SurfaceColor,
            AccentColor = AccentColor,
        };
}
