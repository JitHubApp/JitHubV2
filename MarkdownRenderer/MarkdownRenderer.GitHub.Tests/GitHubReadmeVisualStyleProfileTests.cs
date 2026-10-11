using System.Security.Cryptography;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class GitHubReadmeVisualStyleProfileTests
{
    private const string FixtureResourceName =
        "MarkdownRenderer.GitHub.Profiles.github-primer-light-v1.css";
    private const string OverlayScrollbarFixtureResourceName =
        "MarkdownRenderer.GitHub.Profiles.github-primer-light-v2.css";
    private const string PlainCodeFixtureResourceName =
        "MarkdownRenderer.GitHub.Profiles.github-primer-light-v3.css";
    private const string CssGeometryFixtureResourceName =
        "MarkdownRenderer.GitHub.Profiles.github-primer-light-v4.css";

    [Fact]
    public void PrimerLightV3_BindsPlainCodePresentationToItsOwnCanonicalCss()
    {
        GitHubReadmeVisualStyleProfile profile = GitHubReadmeVisualStyleProfiles.PrimerLightV3;
        using Stream stream = typeof(GitHubReadmeVisualStyleProfiles).Assembly
            .GetManifestResourceStream(PlainCodeFixtureResourceName)!;

        Assert.Equal("github-primer-light", profile.Id);
        Assert.Equal(3, profile.Version);
        Assert.NotNull(stream);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(ReadAllBytes(stream))).ToLowerInvariant(),
            profile.Fingerprint);
        Assert.Equal(CodeBlockChromeMode.PlainMarkdown, profile.CodeBlockChromeMode);
        Assert.Equal(CodeBlockLineNumberMode.Never, profile.CodeBlockLineNumberMode);
        Assert.False(profile.IsCodeBlockCopyEnabled);
        Assert.Same(GitHubReadmeVisualStyleProfiles.PrimerLightV2.StyleSheet, profile.StyleSheet);
    }

    [Fact]
    public void PrimerLightV4_BindsCssGeometryToItsOwnCanonicalCssAndPreservesPlainCodeMode()
    {
        GitHubReadmeVisualStyleProfile profile = GitHubReadmeVisualStyleProfiles.PrimerLightV4;
        using Stream stream = typeof(GitHubReadmeVisualStyleProfiles).Assembly
            .GetManifestResourceStream(CssGeometryFixtureResourceName)!;
        using Stream previousStream = typeof(GitHubReadmeVisualStyleProfiles).Assembly
            .GetManifestResourceStream(PlainCodeFixtureResourceName)!;

        Assert.Equal("github-primer-light", profile.Id);
        Assert.Equal(4, profile.Version);
        Assert.NotNull(stream);
        byte[] cssBytes = ReadAllBytes(stream);
        string css = System.Text.Encoding.UTF8.GetString(cssBytes);
        string previousCss = System.Text.Encoding.UTF8.GetString(ReadAllBytes(previousStream));
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(cssBytes)).ToLowerInvariant(),
            profile.Fingerprint);
        Assert.Equal(
            previousCss,
            css.Replace("PrimerLightV4", "PrimerLightV3", StringComparison.Ordinal)
                .Replace("Profile version: 4", "Profile version: 3", StringComparison.Ordinal));
        Assert.Contains("Profile version: 4", css, StringComparison.Ordinal);
        Assert.Contains(".markdown-body ul,", css, StringComparison.Ordinal);
        Assert.Contains("margin: 0 0 16px;", css, StringComparison.Ordinal);
        Assert.Contains("border-left: .25em solid var(--readme-border);", css, StringComparison.Ordinal);
        Assert.Equal(CodeBlockChromeMode.PlainMarkdown, profile.CodeBlockChromeMode);
        Assert.Equal(CodeBlockLineNumberMode.Never, profile.CodeBlockLineNumberMode);
        Assert.False(profile.IsCodeBlockCopyEnabled);
        Assert.NotSame(GitHubReadmeVisualStyleProfiles.PrimerLightV3.StyleSheet, profile.StyleSheet);

        ElementStyle list = Resolve(profile.StyleSheet, MarkdownStyleRole.List);
        ElementStyle quote = Resolve(profile.StyleSheet, MarkdownStyleRole.Quote);
        ElementStyle heading1 = Resolve(profile.StyleSheet, MarkdownStyleRole.Heading1);
        ElementStyle heading2 = Resolve(profile.StyleSheet, MarkdownStyleRole.Heading2);
        Assert.Equal(new Thickness(0, 0, 0, 16), list.Margin);
        Assert.Equal(4, quote.AccentBarThickness);
        Assert.True(quote.AccentBarAffectsContentLayout);
        Assert.Equal(new Thickness(16, 0, 16, 0), quote.Padding);
        Assert.Equal(32f * 0.3f + 1f, heading1.Padding.Bottom);
        Assert.Equal(24f * 0.3f + 1f, heading2.Padding.Bottom);
        Assert.Equal(Color.FromArgb(0xFF, 0xD1, 0xD9, 0xE0), heading1.BorderBrush);
        Assert.Equal(heading1.BorderBrush, heading2.BorderBrush);
        Assert.Equal(1, heading1.BorderThickness);
        Assert.Equal(1, heading2.BorderThickness);
        Assert.True(heading1.HeadingBorderBottomOnly);
        Assert.True(heading2.HeadingBorderBottomOnly);
        Assert.False(Resolve(
            GitHubReadmeVisualStyleProfiles.PrimerLightV3.StyleSheet,
            MarkdownStyleRole.Heading1).HeadingBorderBottomOnly);
        Assert.True(HasLayoutPolicy(profile.StyleSheet, 2));
        Assert.True(HasLayoutPolicy(profile.StyleSheet, 4));
        Assert.False(HasLayoutPolicy(GitHubReadmeVisualStyleProfiles.PrimerLightV3.StyleSheet, 2));
        Assert.False(HasLayoutPolicy(GitHubReadmeVisualStyleProfiles.PrimerLightV3.StyleSheet, 4));

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

    [Fact]
    public void PrimerLightV2_IsVersionedAndMatchesCanonicalOverlayScrollbarCss()
    {
        GitHubReadmeVisualStyleProfile profile = GitHubReadmeVisualStyleProfiles.PrimerLightV2;
        using Stream stream = typeof(GitHubReadmeVisualStyleProfiles).Assembly
            .GetManifestResourceStream(OverlayScrollbarFixtureResourceName)!;

        Assert.Equal("github-primer-light", profile.Id);
        Assert.Equal(2, profile.Version);
        Assert.NotNull(stream);

        byte[] cssBytes = ReadAllBytes(stream);
        string fingerprint = Convert.ToHexString(SHA256.HashData(cssBytes)).ToLowerInvariant();
        string css = System.Text.Encoding.UTF8.GetString(cssBytes);

        Assert.Equal(fingerprint, profile.Fingerprint);
        Assert.Contains("scrollbar-width: none;", css, StringComparison.Ordinal);
        Assert.Contains("html::-webkit-scrollbar", css, StringComparison.Ordinal);
        Assert.Contains("width: 0;", css, StringComparison.Ordinal);
        Assert.Contains("height: 0;", css, StringComparison.Ordinal);
        Assert.Contains(".markdown-body {", css, StringComparison.Ordinal);
        Assert.Contains("width: 100%;", css, StringComparison.Ordinal);
        Assert.Equal(Color.FromArgb(0xFF, 0x1F, 0x23, 0x28),
            Resolve(profile.StyleSheet, MarkdownStyleRole.Body).Foreground);
        Assert.Equal(16, Resolve(profile.StyleSheet, MarkdownStyleRole.Body).FontSize);
        Assert.Equal(GitHubReadmeVisualStyleProfiles.PrimerLightV1.SurfaceColor, profile.SurfaceColor);
        Assert.Equal(GitHubReadmeVisualStyleProfiles.PrimerLightV1.AccentColor, profile.AccentColor);
        Assert.Equal(CodeBlockChromeMode.Enhanced, profile.CodeBlockChromeMode);
        Assert.Equal(CodeBlockLineNumberMode.AutoMultiline, profile.CodeBlockLineNumberMode);
        Assert.True(profile.IsCodeBlockCopyEnabled);
    }

    [Fact]
    public void PrimerLightV1_IsExplicitlyVersionedAndBoundToCanonicalCssBytes()
    {
        GitHubReadmeVisualStyleProfile profile = GitHubReadmeVisualStyleProfiles.PrimerLightV1;
        using Stream stream = typeof(GitHubReadmeVisualStyleProfiles).Assembly
            .GetManifestResourceStream(FixtureResourceName)!;

        Assert.Equal("github-primer-light", profile.Id);
        Assert.Equal(1, profile.Version);
        Assert.NotNull(stream);

        byte[] cssBytes = ReadAllBytes(stream);
        string fingerprint = Convert.ToHexString(SHA256.HashData(cssBytes)).ToLowerInvariant();
        string css = System.Text.Encoding.UTF8.GetString(cssBytes);

        Assert.Equal(fingerprint, profile.Fingerprint);
        Assert.Contains("--readme-fg: #1f2328;", css, StringComparison.Ordinal);
        Assert.Contains("--readme-bg: #ffffff;", css, StringComparison.Ordinal);
        Assert.Contains("--readme-link: #0969da;", css, StringComparison.Ordinal);
        Assert.Contains("font-size: 16px;", css, StringComparison.Ordinal);
        Assert.Contains("font-family: var(--readme-font-mono);", css, StringComparison.Ordinal);
    }

    [Fact]
    public void PrimerLightV1_ExposesImmutableSemanticRulesMatchingProfileTokens()
    {
        GitHubReadmeVisualStyleProfile profile = GitHubReadmeVisualStyleProfiles.PrimerLightV1;
        MarkdownStyleSheet sheet = profile.StyleSheet;

        ElementStyle body = Resolve(sheet, MarkdownStyleRole.Body);
        Assert.Equal("Segoe UI", body.FontFamily);
        Assert.Equal(16, body.FontSize);
        Assert.Equal(Color.FromArgb(0xFF, 0x1F, 0x23, 0x28), body.Foreground);
        Assert.Equal(1.5f, body.LineHeightMultiplier);
        Assert.Equal(new Thickness(0, 0, 0, 16), body.Margin);

        ElementStyle heading1 = Resolve(sheet, MarkdownStyleRole.Heading1);
        ElementStyle heading2 = Resolve(sheet, MarkdownStyleRole.Heading2);
        ElementStyle heading6 = Resolve(sheet, MarkdownStyleRole.Heading6);
        Assert.Equal(32, heading1.FontSize);
        Assert.Equal(24, heading2.FontSize);
        Assert.Equal(13.6f, heading6.FontSize);
        Assert.Equal(Color.FromArgb(0xFF, 0x59, 0x63, 0x6E), heading6.Foreground);
        Assert.Equal(new Thickness(0, 24, 0, 16), heading1.Margin);
        Assert.Equal(1.25f, heading1.LineHeightMultiplier);

        ElementStyle link = Resolve(sheet, MarkdownStyleRole.Link);
        Assert.Equal(Color.FromArgb(0xFF, 0x09, 0x69, 0xDA), link.Foreground);
        Assert.False(link.Underline);

        ElementStyle inlineCode = Resolve(sheet, MarkdownStyleRole.InlineCode);
        ElementStyle codeBlock = Resolve(sheet, MarkdownStyleRole.CodeBlock);
        Color codeSurface = Color.FromArgb(0xFF, 0xF6, 0xF8, 0xFA);
        Assert.Equal("Consolas", inlineCode.FontFamily);
        Assert.Equal(13.6f, inlineCode.FontSize);
        Assert.Equal(codeSurface, inlineCode.Background);
        Assert.Equal("Consolas", codeBlock.FontFamily);
        Assert.Equal(codeSurface, codeBlock.Background);
        Assert.Equal(6, codeBlock.CornerRadius);
        Assert.Equal(new Thickness(16), codeBlock.Padding);
        Assert.Equal(1.45f, codeBlock.LineHeightMultiplier);

        ElementStyle tableCell = Resolve(sheet, MarkdownStyleRole.TableCell);
        Assert.Equal(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), tableCell.Background);
        Assert.Equal(Color.FromArgb(0xFF, 0xD1, 0xD9, 0xE0), tableCell.BorderBrush);
        Assert.Equal(new Thickness(13, 6, 13, 6), tableCell.Padding);

        Assert.Equal(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), profile.SurfaceColor);
        Assert.Equal(Color.FromArgb(0xFF, 0x09, 0x69, 0xDA), profile.AccentColor);
    }

    [Fact]
    public void PrimerLightV1_HighContrastStillUsesMandatorySystemColorRoles()
    {
        GitHubReadmeVisualStyleProfile profile = GitHubReadmeVisualStyleProfiles.PrimerLightV1;
        Color systemForeground = Color.FromArgb(0xFF, 0xFF, 0xFF, 0x00);
        Color systemBackground = Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = new()
            {
                Foreground = systemForeground,
                Background = systemBackground,
            },
            [MarkdownElementKeys.Link] = new()
            {
                Foreground = systemForeground,
                Background = systemBackground,
                Underline = true,
            },
        };

        var snapshot = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(StringComparer.Ordinal),
            systemBackground,
            systemForeground,
            systemBackground,
            systemForeground,
            isDark: false,
            isHighContrast: true,
            textScaleFactor: 1,
            profile.StyleSheet);

        ElementStyle body = snapshot.GetStyle(MarkdownElementKeys.Body);
        ElementStyle link = snapshot.GetStyle(MarkdownElementKeys.Link);

        Assert.Equal(systemForeground, body.Foreground);
        Assert.Equal(systemBackground, body.Background);
        Assert.Equal(16, body.FontSize);
        Assert.Equal(systemForeground, link.Foreground);
        Assert.Equal(systemBackground, link.Background);
        Assert.True(link.Underline);
        Assert.Equal(systemBackground, snapshot.SurfaceColor);
    }

    private static ElementStyle Resolve(MarkdownStyleSheet sheet, MarkdownStyleRole role)
        => sheet.Resolve(new ElementStyle(), new MarkdownStyleContext(role));

    private static bool HasLayoutPolicy(MarkdownStyleSheet sheet, byte flag)
        => (sheet.Compile().LayoutPolicy & flag) != 0;

    private static byte[] ReadAllBytes(Stream stream)
    {
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}
