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
    private const string RelativeScriptFixtureResourceName =
        "MarkdownRenderer.GitHub.Profiles.github-primer-light-v5.css";
    private const string InheritedQuoteColorFixtureResourceName =
        "MarkdownRenderer.GitHub.Profiles.github-primer-light-v6.css";

    [Fact]
    public void PrimerLightV6_BindsSelectiveQuoteForegroundInheritanceToCanonicalCss()
    {
        GitHubReadmeVisualStyleProfile previous = GitHubReadmeVisualStyleProfiles.PrimerLightV5;
        GitHubReadmeVisualStyleProfile profile = GitHubReadmeVisualStyleProfiles.PrimerLightV6;
        using Stream stream = typeof(GitHubReadmeVisualStyleProfiles).Assembly
            .GetManifestResourceStream(InheritedQuoteColorFixtureResourceName)!;
        using Stream previousStream = typeof(GitHubReadmeVisualStyleProfiles).Assembly
            .GetManifestResourceStream(RelativeScriptFixtureResourceName)!;
        Assert.NotNull(stream);
        Assert.NotNull(previousStream);
        byte[] cssBytes = ReadAllBytes(stream);
        string css = System.Text.Encoding.UTF8.GetString(cssBytes);
        string previousCss = System.Text.Encoding.UTF8.GetString(ReadAllBytes(previousStream));
        const string inheritedForegroundDeclaration =
            "\n/*\n" +
            " * These descendants have no CSS foreground of their own: they inherit the\n" +
            " * muted blockquote color. Headings and links deliberately remain outside this\n" +
            " * group because their rules set their own colors.\n" +
            " */\n" +
            ".markdown-body blockquote p,\n" +
            ".markdown-body blockquote strong,\n" +
            ".markdown-body blockquote em,\n" +
            ".markdown-body blockquote code,\n" +
            ".markdown-body blockquote pre,\n" +
            ".markdown-body blockquote li::marker,\n" +
            ".markdown-body blockquote table,\n" +
            ".markdown-body blockquote th,\n" +
            ".markdown-body blockquote td {\n" +
            "  color: inherit;\n" +
            "}\n";

        Assert.Equal(previous.Id, profile.Id);
        Assert.Equal(6, profile.Version);
        Assert.Equal("2efa4379622ab3d526084ee64ef006c4b5c76257f61a1d6122f64e73128afbc3", profile.Fingerprint);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(cssBytes)).ToLowerInvariant(), profile.Fingerprint);
        Assert.Equal(previousCss.Replace("PrimerLightV5", "PrimerLightV6", StringComparison.Ordinal)
            .Replace("Profile version: 5", "Profile version: 6", StringComparison.Ordinal) +
            inheritedForegroundDeclaration, css);
        Assert.Equal(previous.SurfaceColor, profile.SurfaceColor);
        Assert.Equal(previous.AccentColor, profile.AccentColor);
        Assert.Equal(previous.CodeBlockChromeMode, profile.CodeBlockChromeMode);
        Assert.Equal(previous.CodeBlockLineNumberMode, profile.CodeBlockLineNumberMode);
        Assert.Equal(previous.IsCodeBlockCopyEnabled, profile.IsCodeBlockCopyEnabled);

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
        Assert.Equal(previous.StyleSheet.Rules.Count + inheritedRoles.Length, profile.StyleSheet.Rules.Count);
        for (int index = 0; index < previous.StyleSheet.Rules.Count; index++)
            Assert.Same(previous.StyleSheet.Rules[index], profile.StyleSheet.Rules[index]);

        Color muted = Color.FromArgb(0xFF, 0x59, 0x63, 0x6E);
        Color inheritedBase = Color.FromArgb(0xFF, 0x10, 0x20, 0x30);
        foreach (MarkdownStyleRole role in inheritedRoles)
        {
            var baseStyle = new ElementStyle { Foreground = inheritedBase };
            var quoteContext = new MarkdownStyleContext(role, [MarkdownStyleRole.Quote]);
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

    [Fact]
    public void PrimerLightV5_BindsScriptHeaderAndNestedListPoliciesToCanonicalProfile()
    {
        GitHubReadmeVisualStyleProfile previous = GitHubReadmeVisualStyleProfiles.PrimerLightV4;
        GitHubReadmeVisualStyleProfile profile = GitHubReadmeVisualStyleProfiles.PrimerLightV5;
        using Stream stream = typeof(GitHubReadmeVisualStyleProfiles).Assembly
            .GetManifestResourceStream(RelativeScriptFixtureResourceName)!;
        using Stream previousStream = typeof(GitHubReadmeVisualStyleProfiles).Assembly
            .GetManifestResourceStream(CssGeometryFixtureResourceName)!;
        Assert.NotNull(stream);
        Assert.NotNull(previousStream);
        byte[] cssBytes = ReadAllBytes(stream);
        string css = System.Text.Encoding.UTF8.GetString(cssBytes);
        string previousCss = System.Text.Encoding.UTF8.GetString(ReadAllBytes(previousStream));
        const string nestedListDeclaration =
            "\n/* Only the outer list carries block spacing; nested lists stay inside their item. */\n" +
            ".markdown-body ul ul,\n.markdown-body ul ol,\n.markdown-body ol ul,\n.markdown-body ol ol { margin: 0; }\n";
        const string scriptDeclaration =
            "\n/* Explicit proportional script sizes, independent of the UA's smaller keyword. */\n" +
            ".markdown-body sup,\n.markdown-body sub { font-size: 75%; }\n";

        Assert.Equal(previous.Id, profile.Id);
        Assert.Equal(5, profile.Version);
        Assert.Equal("3af666058cf4044b4e037801db6903f2104f6a6808f32c25ecda3159eecc2824", profile.Fingerprint);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(cssBytes)).ToLowerInvariant(), profile.Fingerprint);
        Assert.Equal(previousCss.Replace("PrimerLightV4", "PrimerLightV5", StringComparison.Ordinal)
            .Replace("Profile version: 4", "Profile version: 5", StringComparison.Ordinal) +
            nestedListDeclaration + scriptDeclaration, css);
        Assert.Equal(previous.SurfaceColor, profile.SurfaceColor);
        Assert.Equal(previous.AccentColor, profile.AccentColor);
        Assert.Equal(previous.CodeBlockChromeMode, profile.CodeBlockChromeMode);
        Assert.Equal(previous.CodeBlockLineNumberMode, profile.CodeBlockLineNumberMode);
        Assert.Equal(previous.IsCodeBlockCopyEnabled, profile.IsCodeBlockCopyEnabled);
        Assert.Equal(
            (byte)(previous.StyleSheet.Compile().LayoutPolicy | MarkdownStyleSheet.CenterDefaultTableHeadersFlag),
            profile.StyleSheet.Compile().LayoutPolicy);
        Assert.False(previous.StyleSheet.Compile().CenterDefaultTableHeaders);
        Assert.True(profile.StyleSheet.Compile().CenterDefaultTableHeaders);
        MarkdownStyleSheet withRule = profile.StyleSheet.WithRule(new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.Body),
            new ElementStyleOverride { FontSize = 16 }));
        Assert.True(withRule.Compile().CenterDefaultTableHeaders);
        Assert.True(profile.StyleSheet.WithRelativeScriptFontSize(0.5f).Compile().CenterDefaultTableHeaders);
        Assert.Equal(previous.StyleSheet.Rules.Count + 3, profile.StyleSheet.Rules.Count);
        for (int index = 0; index < previous.StyleSheet.Rules.Count; index++)
            Assert.Same(previous.StyleSheet.Rules[index], profile.StyleSheet.Rules[index]);

        Assert.False(previous.StyleSheet.Compile().HasRelativeScriptFontSizeRules);
        Assert.True(profile.StyleSheet.Compile().HasRelativeScriptFontSizeRules);
        Assert.Equal(0.75f, Resolve(profile.StyleSheet,
            MarkdownStyleRole.FromElementKey(MarkdownElementKeys.Subscript)).RelativeFontSizeScale);
        Assert.Equal(0.75f, Resolve(profile.StyleSheet,
            MarkdownStyleRole.FromElementKey(MarkdownElementKeys.Superscript)).RelativeFontSizeScale);

        MarkdownStyleRole listRole = MarkdownStyleRole.FromElementKey(MarkdownElementKeys.List);
        Assert.Equal(new Thickness(0, 0, 0, 16), Resolve(profile.StyleSheet, listRole, nestingDepth: 1).Margin);
        Assert.Equal(new Thickness(0), Resolve(profile.StyleSheet, listRole, nestingDepth: 2).Margin);
        Assert.Equal(new Thickness(0), Resolve(profile.StyleSheet, listRole, nestingDepth: 3).Margin);
    }

    [Fact]
    public void PrimerLightV5_LaterNestedListRuleOverridesOnlyNestedMargin()
    {
        MarkdownStyleRole listRole = MarkdownStyleRole.FromElementKey(MarkdownElementKeys.List);
        MarkdownStyleSheet customized = GitHubReadmeVisualStyleProfiles.PrimerLightV5.StyleSheet.WithRule(
            new MarkdownStyleRule(
                new MarkdownStyleSelector(listRole, minimumNestingDepth: 2),
                new ElementStyleOverride { Margin = new Thickness(0, 0, 0, 24) }));

        Assert.Equal(new Thickness(0, 0, 0, 16), Resolve(customized, listRole, nestingDepth: 1).Margin);
        Assert.Equal(new Thickness(0, 0, 0, 24), Resolve(customized, listRole, nestingDepth: 2).Margin);
        Assert.Equal(new Thickness(0, 0, 0, 24), Resolve(customized, listRole, nestingDepth: 3).Margin);
    }

    [Fact]
    public void PrimerLightV5_OverridesInheritedScriptSizeButPreservesLaterAbsoluteRulesAndHighContrast()
    {
        MarkdownStyleSheet sheet = GitHubReadmeVisualStyleProfiles.PrimerLightV5.StyleSheet;
        Color inheritedForeground = Color.FromArgb(0xFF, 0, 0, 0xFF);
        Color systemForeground = Color.FromArgb(0xFF, 0xFF, 0xFF, 0);
        Color systemBackground = Color.FromArgb(0xFF, 0, 0, 0);
        var inherited = new ElementStyle { FontSize = 12, Foreground = inheritedForeground };
        var context = new MarkdownStyleContext(MarkdownStyleRole.FromElementKey(MarkdownElementKeys.Superscript));
        ElementStyle relative = sheet.Resolve(inherited, context);
        Assert.Equal(0.75f, relative.RelativeFontSizeScale);
        // Standalone resolution retains the nominal size; layout supplies the parent size.
        Assert.Equal(12, relative.FontSize);
        ElementStyle explicitSize = sheet.WithRule(new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.FromElementKey(MarkdownElementKeys.Superscript)),
            new ElementStyleOverride { FontSize = 13 })).Resolve(inherited, context);
        Assert.Null(explicitSize.RelativeFontSizeScale);
        Assert.Equal(13, explicitSize.FontSize);

        ElementStyle highContrast = ThemeSnapshot.EnforceHighContrast(relative,
            new ElementStyle { Foreground = systemForeground, Background = systemBackground });
        Assert.Equal(systemForeground, highContrast.Foreground);
        Assert.Equal(systemBackground, highContrast.Background);
        Assert.Equal(0.75f, highContrast.RelativeFontSizeScale);
    }

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
        Assert.Equal(new Thickness(5.44, 2.72, 5.44, 2.72), inlineCode.Padding);
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

    private static ElementStyle Resolve(
        MarkdownStyleSheet sheet,
        MarkdownStyleRole role,
        int nestingDepth = 0)
        => sheet.Resolve(new ElementStyle(), new MarkdownStyleContext(role, nestingDepth));

    private static ElementStyle Resolve(
        MarkdownStyleSheet sheet,
        MarkdownStyleRole role,
        IEnumerable<MarkdownStyleRole> ancestorRoles,
        int nestingDepth = 0)
        => sheet.Resolve(new ElementStyle(), new MarkdownStyleContext(role, ancestorRoles, nestingDepth));

    private static bool HasLayoutPolicy(MarkdownStyleSheet sheet, byte flag)
        => (sheet.Compile().LayoutPolicy & flag) != 0;

    private static byte[] ReadAllBytes(Stream stream)
    {
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}
