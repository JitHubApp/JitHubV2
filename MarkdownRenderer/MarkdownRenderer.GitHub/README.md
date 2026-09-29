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

Safe HTML never executes script, applies CSS layout, exposes a browser DOM, or
performs direct filesystem/network access. External links and images continue to
flow through the renderer's host policies. The native safe-HTML subset parser and
painter are still incomplete in this preview.
