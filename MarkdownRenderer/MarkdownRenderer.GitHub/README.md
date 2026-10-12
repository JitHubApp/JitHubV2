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
var visualProfile = GitHubReadmeVisualStyleProfiles.PrimerLightV6;
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

The profile ID is `github-primer-light`, version `6`; its fingerprint is the
SHA-256 of the canonical `eng/readme-audit/profiles/github-primer-light-v6.css`
bytes. Version 6 makes quote descendants inherit the muted quote foreground
while headings and links retain their explicit colors. Record that fingerprint
with visual evidence on both renderers. This profile does not alter renderer
defaults or the caller's application theme, and High Contrast continues to use
mandatory system colors. It is a stable audit fixture, not a promise that native
layout reproduces every live GitHub website detail.

Version 6 retains version 5's geometry and plain code-block presentation while
adding selective inherited foreground matching inside block quotes. Versions
1–5 retain their original identities and behavior. For another theme,
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
