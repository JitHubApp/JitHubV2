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
var visualProfile = GitHubReadmeVisualStyleProfiles.PrimerLightV2;
var control = new MarkdownRendererControlBuilder()
    .UseGitHubReadme()
    .WithTheme(visualProfile.CreateTheme())
    .WithStyleSheet(visualProfile.StyleSheet)
    .WithMarkdown(source)
    .BuildScrollView();
```

The profile ID is `github-primer-light`, version `2`; its fingerprint is the
SHA-256 of the canonical `eng/readme-audit/profiles/github-primer-light-v2.css`
bytes. Record that fingerprint with visual evidence on both renderers. This
profile does not alter renderer defaults or the caller's application theme, and
High Contrast continues to use mandatory system colors. It is a stable audit
fixture, not a promise that native layout reproduces every live GitHub website
detail.

Safe HTML never executes script, applies CSS layout, exposes a browser DOM, or
performs direct filesystem/network access. External links and images continue to
flow through the renderer's host policies. The native safe-HTML subset parser and
painter are still incomplete in this preview.
