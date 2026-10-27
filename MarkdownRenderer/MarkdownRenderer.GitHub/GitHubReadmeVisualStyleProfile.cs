using System;
using System.Collections.Generic;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;
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

/// <summary>Versioned, opt-in visual profiles for GitHub README rendering.</summary>
public static class GitHubReadmeVisualStyleProfiles
{
    /// <summary>
    /// Gets the fixed Light profile backed by
    /// <c>MarkdownRenderer.GitHub/Profiles/github-primer-light-v1.css</c>.
    /// </summary>
    /// <remarks>
    /// The profile uses a white surface, GitHub's light foreground and link
    /// colors, 16-DIP body typography, and the corresponding code/table tokens.
    /// It reproduces the pinned Primer CSS source for GitHub-faithful README
    /// rendering and is not the default MarkdownRenderer or JitHub theme.
    /// </remarks>
    public static GitHubReadmeVisualStyleProfile PrimerLightV1 { get; } = CreatePrimerLightV1();

    /// <summary>
    /// Gets the Light profile used by the same-byte source replay. Version 2
    /// matches WinUI's overlay-scrollbar content width in the Edge oracle while
    /// retaining the same Markdown colors and typography as version 1.
    /// </summary>
    /// <remarks>
    /// The profile is opt-in and does not change the default JitHub theme.
    /// </remarks>
    public static GitHubReadmeVisualStyleProfile PrimerLightV2 { get; } = CreatePrimerLightV2();

    /// <summary>
    /// Gets the Light source-replay profile with browser-style plain code
    /// blocks and no renderer-added header, line numbers, or copy action.
    /// </summary>
    public static GitHubReadmeVisualStyleProfile PrimerLightV3 { get; } = CreatePrimerLightV3();

    /// <summary>
    /// Gets the source-replay profile with plain code blocks, CSS list margins,
    /// and a four-DIP quote border outside the quote's inner padding.
    /// </summary>
    public static GitHubReadmeVisualStyleProfile PrimerLightV4 { get; } = CreatePrimerLightV4();

    /// <summary>
    /// Gets the source-replay profile with proportional subscript and
    /// superscript sizing, centered default table headers, and marginless
    /// nested lists. Earlier profile identities and generic renderer defaults
    /// remain unchanged.
    /// </summary>
    public static GitHubReadmeVisualStyleProfile PrimerLightV5 { get; } = CreatePrimerLightV5();

    /// <summary>
    /// Gets the source-replay profile with explicit semantic inheritance for
    /// foregrounds inside block quotes. Heading and link colors remain explicit.
    /// </summary>
    public static GitHubReadmeVisualStyleProfile PrimerLightV6 { get; } = CreatePrimerLightV6();

    /// <summary>
    /// Gets the source-replay profile with table-cell line height and relative
    /// inline-code typography and middle-aligned images matching its canonical CSS.
    /// </summary>
    public static GitHubReadmeVisualStyleProfile PrimerLightV7 { get; } = CreatePrimerLightV7();

    private static GitHubReadmeVisualStyleProfile CreatePrimerLightV7()
    {
        GitHubReadmeVisualStyleProfile previous = PrimerLightV6;
        MarkdownStyleSheet styleSheet = previous.StyleSheet
            .WithCssMiddleAlignedInlineImages()
            .WithRelativeInlineCodeFontSize(0.85f)
            .WithRule(Rule(MarkdownStyleRole.TableHeader,
                new ElementStyleOverride { LineHeightMultiplier = 1.5f }))
            .WithRule(Rule(MarkdownStyleRole.TableCell,
                new ElementStyleOverride { LineHeightMultiplier = 1.5f }));

        return new GitHubReadmeVisualStyleProfile(
            id: previous.Id,
            version: 7,
            fingerprint: "882f63ac35c8f0854485ba797fda1bc8690cbc97bfd5d0fd9c0c961471cb17c4",
            surfaceColor: previous.SurfaceColor,
            accentColor: previous.AccentColor,
            styleSheet: styleSheet,
            codeBlockChromeMode: previous.CodeBlockChromeMode,
            codeBlockLineNumberMode: previous.CodeBlockLineNumberMode,
            isCodeBlockCopyEnabled: previous.IsCodeBlockCopyEnabled);
    }

    private static GitHubReadmeVisualStyleProfile CreatePrimerLightV6()
    {
        GitHubReadmeVisualStyleProfile previous = PrimerLightV5;
        Color inheritedForeground = Color.FromArgb(0xFF, 0x59, 0x63, 0x6E);
        MarkdownStyleSheet styleSheet = previous.StyleSheet;
        MarkdownStyleRole[] inheritedRoles =
        [
            MarkdownStyleRole.Body,
            MarkdownStyleRole.Strong,
            MarkdownStyleRole.Emphasis,
            MarkdownStyleRole.InlineCode,
            MarkdownStyleRole.CodeBlock,
            MarkdownStyleRole.ListMarker,
            MarkdownStyleRole.Table,
            MarkdownStyleRole.TableHeader,
            MarkdownStyleRole.TableCell,
        ];

        for (int i = 0; i < inheritedRoles.Length; i++)
        {
            styleSheet = styleSheet.WithRule(new MarkdownStyleRule(
                new MarkdownStyleSelector(inheritedRoles[i], MarkdownStyleRole.Quote),
                new ElementStyleOverride { Foreground = inheritedForeground }));
        }

        return new GitHubReadmeVisualStyleProfile(
            id: previous.Id,
            version: 6,
            fingerprint: "2efa4379622ab3d526084ee64ef006c4b5c76257f61a1d6122f64e73128afbc3",
            surfaceColor: previous.SurfaceColor,
            accentColor: previous.AccentColor,
            styleSheet: styleSheet,
            codeBlockChromeMode: previous.CodeBlockChromeMode,
            codeBlockLineNumberMode: previous.CodeBlockLineNumberMode,
            isCodeBlockCopyEnabled: previous.IsCodeBlockCopyEnabled);
    }

    private static GitHubReadmeVisualStyleProfile CreatePrimerLightV5()
    {
        GitHubReadmeVisualStyleProfile previous = PrimerLightV4;
        return new GitHubReadmeVisualStyleProfile(
            id: previous.Id,
            version: 5,
            fingerprint: "3af666058cf4044b4e037801db6903f2104f6a6808f32c25ecda3159eecc2824",
            surfaceColor: previous.SurfaceColor,
            accentColor: previous.AccentColor,
            styleSheet: previous.StyleSheet
                .WithRelativeScriptFontSize(0.75f)
                .WithCenteredDefaultTableHeaders()
                .WithRule(new MarkdownStyleRule(
                    new MarkdownStyleSelector(MarkdownStyleRole.List, minimumNestingDepth: 2),
                    new ElementStyleOverride { Margin = new Thickness(0) })),
            codeBlockChromeMode: previous.CodeBlockChromeMode,
            codeBlockLineNumberMode: previous.CodeBlockLineNumberMode,
            isCodeBlockCopyEnabled: previous.IsCodeBlockCopyEnabled);
    }

    private static GitHubReadmeVisualStyleProfile CreatePrimerLightV4()
    {
        GitHubReadmeVisualStyleProfile previous = PrimerLightV3;
        MarkdownStyleSheet styleSheet = previous.StyleSheet
            .WithRule(Rule(MarkdownStyleRole.List, new ElementStyleOverride
            {
                Margin = new Thickness(0, 0, 0, 16),
            }))
            .WithRule(Rule(MarkdownStyleRole.Quote, new ElementStyleOverride
            {
                AccentBarThickness = 4,
                AccentBarAffectsContentLayout = true,
            }))
            .WithRule(Rule(MarkdownStyleRole.Heading1, new ElementStyleOverride
            {
                // CSS uses .3em of bottom padding followed by a 1-DIP rule.
                // Store both in the reserved bottom inset so block height and
                // hit testing include the complete CSS border box.
                Padding = new Thickness(0, 0, 0, 32f * 0.3f + 1f),
                BorderBrush = Color.FromArgb(0xFF, 0xD1, 0xD9, 0xE0),
                BorderThickness = 1,
                HeadingBorderBottomOnly = true,
            }))
            .WithRule(Rule(MarkdownStyleRole.Heading2, new ElementStyleOverride
            {
                Padding = new Thickness(0, 0, 0, 24f * 0.3f + 1f),
                BorderBrush = Color.FromArgb(0xFF, 0xD1, 0xD9, 0xE0),
                BorderThickness = 1,
                HeadingBorderBottomOnly = true,
            }))
            .WithZeroLeadingTopLevelBlockTopMargin()
            .WithZeroTrailingTopLevelBlockMargin();
        return new GitHubReadmeVisualStyleProfile(
            id: previous.Id,
            version: 4,
            fingerprint: "d1f276efd42d802835c33480c90cd54544afab7a20eae1be00d8b0a7f881f8f1",
            surfaceColor: previous.SurfaceColor,
            accentColor: previous.AccentColor,
            styleSheet: styleSheet,
            codeBlockChromeMode: previous.CodeBlockChromeMode,
            codeBlockLineNumberMode: previous.CodeBlockLineNumberMode,
            isCodeBlockCopyEnabled: previous.IsCodeBlockCopyEnabled);
    }

    private static GitHubReadmeVisualStyleProfile CreatePrimerLightV3()
    {
        GitHubReadmeVisualStyleProfile previous = PrimerLightV2;
        return new GitHubReadmeVisualStyleProfile(
            id: previous.Id,
            version: 3,
            fingerprint: "dfe6217758d10d1a815f2d8e408e1f4d0bc1dd4d60ff426cf2cb5c618b86392d",
            surfaceColor: previous.SurfaceColor,
            accentColor: previous.AccentColor,
            styleSheet: previous.StyleSheet,
            codeBlockChromeMode: CodeBlockChromeMode.PlainMarkdown,
            codeBlockLineNumberMode: CodeBlockLineNumberMode.Never,
            isCodeBlockCopyEnabled: false);
    }

    private static GitHubReadmeVisualStyleProfile CreatePrimerLightV2()
    {
        GitHubReadmeVisualStyleProfile previous = PrimerLightV1;
        return new GitHubReadmeVisualStyleProfile(
            id: previous.Id,
            version: 2,
            fingerprint: "29488337f9d5884c9fb9e64bfab118d78dae4c86badcbffbe2e003bdf659b008",
            surfaceColor: previous.SurfaceColor,
            accentColor: previous.AccentColor,
            styleSheet: previous.StyleSheet);
    }

    private static GitHubReadmeVisualStyleProfile CreatePrimerLightV1()
    {
        Color foreground = Color.FromArgb(0xFF, 0x1F, 0x23, 0x28);
        Color surface = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        Color muted = Color.FromArgb(0xFF, 0x59, 0x63, 0x6E);
        Color border = Color.FromArgb(0xFF, 0xD1, 0xD9, 0xE0);
        Color link = Color.FromArgb(0xFF, 0x09, 0x69, 0xDA);
        Color codeSurface = Color.FromArgb(0xFF, 0xF6, 0xF8, 0xFA);
        const string sans = "Segoe UI";
        const string mono = "Consolas";
        Windows.UI.Text.FontWeight semibold = new() { Weight = 600 };

        var rules = new List<MarkdownStyleRule>
        {
            Rule(MarkdownStyleRole.Body, new ElementStyleOverride
            {
                FontFamily = sans,
                FontSize = 16,
                Foreground = foreground,
                LineHeightMultiplier = 1.5f,
                Margin = new Thickness(0, 0, 0, 16),
                ListIndent = 32,
            }),
            Rule(MarkdownStyleRole.Heading1, Heading(sans, foreground, 32, semibold)),
            Rule(MarkdownStyleRole.Heading2, Heading(sans, foreground, 24, semibold)),
            Rule(MarkdownStyleRole.Heading3, Heading(sans, foreground, 20, semibold)),
            Rule(MarkdownStyleRole.Heading4, Heading(sans, foreground, 16, semibold)),
            Rule(MarkdownStyleRole.Heading5, Heading(sans, foreground, 14, semibold)),
            Rule(MarkdownStyleRole.Heading6, Heading(sans, muted, 13.6f, semibold)),
            Rule(MarkdownStyleRole.Link, new ElementStyleOverride
            {
                FontFamily = sans,
                FontSize = 16,
                Foreground = link,
                HoverForeground = link,
                FocusForeground = link,
                Underline = false,
            }),
            Rule(MarkdownStyleRole.Strong, new ElementStyleOverride
            {
                FontFamily = sans,
                FontSize = 16,
                FontWeight = semibold,
                Foreground = foreground,
            }),
            Rule(MarkdownStyleRole.Emphasis, new ElementStyleOverride
            {
                FontFamily = sans,
                FontSize = 16,
                Foreground = foreground,
            }),
            Rule(MarkdownStyleRole.InlineCode, new ElementStyleOverride
            {
                FontFamily = mono,
                FontSize = 13.6f,
                Foreground = foreground,
                Background = codeSurface,
                CornerRadius = 6,
                Padding = new Thickness(5.44, 2.72, 5.44, 2.72),
            }),
            Rule(MarkdownStyleRole.CodeBlock, new ElementStyleOverride
            {
                FontFamily = mono,
                FontSize = 13.6f,
                Foreground = foreground,
                Background = codeSurface,
                BorderThickness = 0,
                CornerRadius = 6,
                Padding = new Thickness(16),
                LineHeightMultiplier = 1.45f,
                Margin = new Thickness(0, 0, 0, 16),
            }),
            Rule(MarkdownStyleRole.CodeBlockHeader, new ElementStyleOverride
            {
                FontFamily = sans,
                FontSize = 12,
                Foreground = muted,
                Background = codeSurface,
                BorderThickness = 0,
            }),
            Rule(MarkdownStyleRole.CodeBlockLanguage, new ElementStyleOverride
            {
                FontFamily = sans,
                FontSize = 12,
                Foreground = muted,
            }),
            Rule(MarkdownStyleRole.CodeBlockGutter, new ElementStyleOverride
            {
                FontFamily = mono,
                FontSize = 12,
                Foreground = muted,
                Background = codeSurface,
            }),
            Rule(MarkdownStyleRole.CodeBlockLineNumber, new ElementStyleOverride
            {
                FontFamily = mono,
                FontSize = 12,
                Foreground = muted,
            }),
            Rule(MarkdownStyleRole.Quote, new ElementStyleOverride
            {
                FontFamily = sans,
                FontSize = 16,
                Foreground = muted,
                AccentBar = border,
                Padding = new Thickness(16, 0, 16, 0),
                Margin = new Thickness(0, 0, 0, 16),
            }),
            Rule(MarkdownStyleRole.Table, new ElementStyleOverride
            {
                FontFamily = sans,
                FontSize = 16,
                Foreground = foreground,
                Background = surface,
                BorderBrush = border,
                BorderThickness = 1,
                CornerRadius = 0,
                Margin = new Thickness(0, 0, 0, 16),
            }),
            Rule(MarkdownStyleRole.TableHeader, new ElementStyleOverride
            {
                FontFamily = sans,
                FontSize = 16,
                FontWeight = semibold,
                Foreground = foreground,
                Background = surface,
                BorderBrush = border,
                Padding = new Thickness(13, 6, 13, 6),
            }),
            Rule(MarkdownStyleRole.TableCell, new ElementStyleOverride
            {
                FontFamily = sans,
                FontSize = 16,
                Foreground = foreground,
                Background = surface,
                BorderBrush = border,
                Padding = new Thickness(13, 6, 13, 6),
            }),
            Rule(MarkdownStyleRole.ListMarker, new ElementStyleOverride
            {
                FontFamily = sans,
                FontSize = 16,
                Foreground = foreground,
                ListIndent = 32,
            }),
            Rule(MarkdownStyleRole.ThematicBreak, new ElementStyleOverride
            {
                Foreground = border,
                Margin = new Thickness(0, 12, 0, 12),
            }),
        };

        return new GitHubReadmeVisualStyleProfile(
            id: "github-primer-light",
            version: 1,
            fingerprint: "af720fa86fc6902ba4984dbe7e41a3303a83f1359a4bf26c2dc3536d7147436e",
            surfaceColor: surface,
            accentColor: link,
            styleSheet: new MarkdownStyleSheet(rules).WithPositiveAdjacentMarginCollapse());
    }

    private static MarkdownStyleRule Rule(MarkdownStyleRole role, ElementStyleOverride style)
        => new(new MarkdownStyleSelector(role), style);

    private static ElementStyleOverride Heading(
        string fontFamily,
        Color foreground,
        float fontSize,
        Windows.UI.Text.FontWeight fontWeight)
        => new()
        {
            FontFamily = fontFamily,
            FontSize = fontSize,
            FontWeight = fontWeight,
            Foreground = foreground,
            Margin = new Thickness(0, 24, 0, 16),
            LineHeightMultiplier = 1.25f,
        };
}
