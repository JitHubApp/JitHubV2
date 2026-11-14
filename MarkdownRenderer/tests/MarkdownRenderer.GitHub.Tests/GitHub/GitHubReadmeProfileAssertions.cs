using MarkdownRenderer.GitHub;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.GitHub;

/// <summary>
/// Style resolution and shared assertions for the versioned GitHub README
/// profiles that <see cref="GitHubReadmeVisualStyleProfileTests"/> checks.
/// </summary>
internal static class GitHubReadmeProfileAssertions
{
    internal static ElementStyle Resolve(
        MarkdownStyleSheet sheet,
        MarkdownStyleRole role,
        int nestingDepth = 0)
        => sheet.Resolve(new ElementStyle(), new MarkdownStyleContext(role, nestingDepth));

    internal static ElementStyle Resolve(
        MarkdownStyleSheet sheet,
        MarkdownStyleRole role,
        IEnumerable<MarkdownStyleRole> ancestorRoles,
        int nestingDepth = 0)
        => sheet.Resolve(new ElementStyle(), new MarkdownStyleContext(role, nestingDepth, ancestorRoles: ancestorRoles));

    internal static bool HasLayoutPolicy(MarkdownStyleSheet sheet, byte flag)
        => (sheet.Compile().LayoutPolicy & flag) != 0;

    internal static byte[] ReadAllBytes(Stream stream)
    {
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Asserts that a newer profile keeps the earlier profile's surface, accent and code-block presentation.</summary>
    internal static void AssertSharesPresentation(
        GitHubReadmeVisualStyleProfile previous,
        GitHubReadmeVisualStyleProfile profile)
    {
        Assert.Equal(previous.SurfaceColor, profile.SurfaceColor);
        Assert.Equal(previous.AccentColor, profile.AccentColor);
        Assert.Equal(previous.CodeBlockChromeMode, profile.CodeBlockChromeMode);
        Assert.Equal(previous.CodeBlockLineNumberMode, profile.CodeBlockLineNumberMode);
        Assert.Equal(previous.IsCodeBlockCopyEnabled, profile.IsCodeBlockCopyEnabled);
    }

    /// <summary>
    /// Asserts the PrimerLightV6 quote rule: listed descendants inherit the muted
    /// quote foreground, links and headings keep their own colors, and a later
    /// author rule still wins.
    /// </summary>
    internal static void AssertQuoteDescendantsInheritMutedForeground(
        GitHubReadmeVisualStyleProfile previous,
        GitHubReadmeVisualStyleProfile profile,
        MarkdownStyleRole[] inheritedRoles)
    {
        Color muted = Color.FromArgb(0xFF, 0x59, 0x63, 0x6E);
        Color inheritedBase = Color.FromArgb(0xFF, 0x10, 0x20, 0x30);
        foreach (MarkdownStyleRole role in inheritedRoles)
        {
            var baseStyle = new ElementStyle { Foreground = inheritedBase };
            var quoteContext = new MarkdownStyleContext(role, ancestorRoles: [MarkdownStyleRole.Quote]);
            var plainContext = new MarkdownStyleContext(role);

            Assert.Equal(muted, profile.StyleSheet.Resolve(baseStyle, quoteContext).Foreground);
            Assert.Equal(previous.StyleSheet.Resolve(baseStyle, plainContext).Foreground,
                profile.StyleSheet.Resolve(baseStyle, plainContext).Foreground);
        }

        Assert.Equal(
            Resolve(previous.StyleSheet, MarkdownStyleRole.Link).Foreground,
            Resolve(profile.StyleSheet, MarkdownStyleRole.Link, ancestorRoles: [MarkdownStyleRole.Quote]).Foreground);
        Assert.Equal(
            Resolve(previous.StyleSheet, MarkdownStyleRole.Heading1).Foreground,
            Resolve(profile.StyleSheet, MarkdownStyleRole.Heading1, ancestorRoles: [MarkdownStyleRole.Quote]).Foreground);
        Assert.Equal(
            Resolve(previous.StyleSheet, MarkdownStyleRole.Heading6).Foreground,
            Resolve(profile.StyleSheet, MarkdownStyleRole.Heading6, ancestorRoles: [MarkdownStyleRole.Quote]).Foreground);

        Color laterForeground = Color.FromArgb(0xFF, 0x77, 0x22, 0x99);
        MarkdownStyleSheet laterOverride = profile.StyleSheet.WithRule(new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.Body),
            new ElementStyleOverride { Foreground = laterForeground }));
        Assert.Equal(laterForeground,
            Resolve(laterOverride, MarkdownStyleRole.Body, ancestorRoles: [MarkdownStyleRole.Quote]).Foreground);
    }

    /// <summary>
    /// Asserts that author heading border overrides replace or keep the
    /// PrimerLightV4 bottom-only rule, and that High Contrast keeps the mandatory border.
    /// </summary>
    internal static void AssertHeadingBorderOverridesAndHighContrast(GitHubReadmeVisualStyleProfile profile)
    {
        Color customBorder = Color.FromArgb(0xFF, 0xC0, 0x20, 0x20);
        MarkdownStyleSheet customizedSheet = profile.StyleSheet.WithRule(new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.Heading1),
            new ElementStyleOverride { BorderBrush = customBorder }));
        ElementStyle customizedHeading = Resolve(customizedSheet, MarkdownStyleRole.Heading1);
        Assert.False(customizedHeading.HeadingBorderBottomOnly);
        Assert.Equal(customBorder, customizedHeading.BorderBrush);

        MarkdownStyleSheet recoloredBottomRuleSheet = profile.StyleSheet.WithRule(new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.Heading1),
            new ElementStyleOverride
            {
                BorderBrush = customBorder,
                HeadingBorderBottomOnly = true,
            }));
        ElementStyle recoloredBottomRule = Resolve(recoloredBottomRuleSheet, MarkdownStyleRole.Heading1);
        Assert.True(recoloredBottomRule.HeadingBorderBottomOnly);
        Assert.Equal(customBorder, recoloredBottomRule.BorderBrush);

        MarkdownStyleSheet customThicknessSheet = profile.StyleSheet.WithRule(new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.Heading2),
            new ElementStyleOverride { BorderThickness = 2, HeadingBorderBottomOnly = true }));
        ElementStyle customThicknessHeading = Resolve(customThicknessSheet, MarkdownStyleRole.Heading2);
        Assert.True(customThicknessHeading.HeadingBorderBottomOnly);
        Assert.Equal(2, customThicknessHeading.BorderThickness);

        Color mandatoryHighContrastBorder = Color.FromArgb(0xFF, 0xFF, 0xFF, 0x00);
        ElementStyle highContrastHeading = ThemeSnapshot.EnforceHighContrast(
            customizedHeading,
            new ElementStyle { BorderBrush = mandatoryHighContrastBorder });
        Assert.False(highContrastHeading.HeadingBorderBottomOnly);
        Assert.Equal(mandatoryHighContrastBorder, highContrastHeading.BorderBrush);
    }
}
