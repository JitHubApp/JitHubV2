# MarkdownRenderer.GitHub

`MarkdownRenderer.GitHub` is the opt-in GitHub README profile. It composes strict
GFM with GitHub alerts, footnotes, emoji shortcodes, generic attributes, and the
safe-HTML capability profile.

```csharp
var control = new MarkdownRendererControlBuilder()
    .UseGitHubReadme()
    .WithMarkdown(source)
    .BuildScrollView();
```

Use `BuildDocumentView()` when an ancestor owns the scroll viewport. The base
viewer follows WinUI/Fluent styling; the GitHub README profile is opt-in.

For a controlled Light-mode visual comparison, opt into the versioned Primer
profile on the one control being compared:

```csharp
var visualProfile = GitHubReadmeVisualStyleProfiles.PrimerLightV7;
var control = new MarkdownRendererControlBuilder()
    .UseGitHubReadme()
    .WithTheme(visualProfile.CreateTheme())
    .WithStyleSheet(visualProfile.StyleSheet)
    .WithCodeBlockChromeMode(visualProfile.CodeBlockChromeMode)
    .WithCodeBlockLineNumberMode(visualProfile.CodeBlockLineNumberMode)
    .WithCodeBlockCopyEnabled(visualProfile.IsCodeBlockCopyEnabled)
    .WithMarkdown(source)
    .BuildScrollView();
```

The profile ID is `github-primer-light`, version `7`; its fingerprint is the
SHA-256 of the canonical `Profiles/github-primer-light-v7.css`
bytes. Version 7 matches table-cell line height, relative inline-code font size,
and inline-image middle alignment to that CSS. Record that fingerprint
with visual evidence on both renderers. This profile does not alter renderer
defaults or the caller's application theme, and High Contrast continues to use
mandatory system colors. It is a stable audit fixture, not a promise that native
layout reproduces every live GitHub website detail.

It retains version 6's selective inherited foreground matching inside block
quotes and the plain code-block presentation. Versions 1–6 retain their
original identities and behavior. Inline-code chip padding remains expressed
in DIPs; relative font sizing alone does not promise CSS em-padding parity.
For another theme,
`styleSheet.WithRelativeScriptFontSize(0.75f)`
opts into the same proportional sizing without adopting this Light profile.
Its appended rules override inherited resource sizes; later explicit stylesheet
font-size rules can still override them. Nested scripts compound the factor,
with a 0.1-DIP floor for derived sizes. This sets the nominal font size; glyph
substitution and baseline positioning retain the existing font-dependent
OpenType subscript/superscript behavior. It does not establish CSS
`vertical-align` parity or full-page visual equivalence.

Safe HTML never executes script, applies CSS layout, exposes a browser DOM, or
performs direct filesystem/network access. External links and images continue to
flow through the renderer's host policies. The native safe-HTML subset parser and
painter are still incomplete in this preview.
