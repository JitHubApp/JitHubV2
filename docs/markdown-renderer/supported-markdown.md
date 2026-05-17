# Supported markdown

This page documents the markdown syntax supported by the native renderer. The
renderer consumes Markdig's AST and builds WinUI/Win2D layout boxes; it does not
use Markdig's HTML renderer.

## Support levels

| Status | Meaning |
| --- | --- |
| Core | Available from the `MarkdownRenderer` package without optional helpers. |
| GFM | Available after adding `MarkdownRenderer.Gfm` and calling `UseGitHubFlavoredMarkdown()`. |
| Extra | Available after calling `UseMarkdownExtra()` from `MarkdownRenderer.Gfm`. |
| Extension sample | Supported through documented app-owned extension points, not bundled as a built-in renderer. |
| Out of scope | Intentionally not shipped in the 1.0 non-HTML/non-LaTeX scope. |

## Core CommonMark

| Syntax | Status | Notes |
| --- | --- | --- |
| Paragraphs and line breaks | Core | Uses DirectWrite wrapping, hit testing, source maps, and UIA TextPattern text. |
| ATX headings H1-H6 | Core | Exposes heading levels through UI Automation and document queries. |
| Emphasis and strong emphasis | Core | Participates in selection, clipboard HTML, and text attributes. |
| Inline code | Core | Uses code style keys and source-accurate copy. |
| Fenced and indented code blocks | Core | Rendered as native code surfaces with header metadata, copy action, always-wrapped text, multiline line numbers, highlighted-line metadata, diff markers, and huge-code segmentation. Syntax highlighting is provider-based and optional. |
| Block quotes | Core | Styled through theme background, padding, border, radius, and accent fields. |
| Ordered and unordered lists | Core | Preserves ordered-list start numbers and depth-aware indent styling. |
| Thematic breaks | Core | Native painted rule. |
| Links and autolinks | Core | Pointer, keyboard, UIA Invoke, hover/focus overlay styling, and fragment navigation. |
| Images | Core | Standalone and inline images share bitmap/SVG loading, lazy load, source maps, selection, and UIA alt text. |

## Optional syntax highlighting

Broad code-block syntax highlighting is available from
`MarkdownRenderer.SyntaxHighlighting.TextMate`:

```csharp
var control = new MarkdownRendererControlBuilder()
    .UseTextMateSyntaxHighlighting()
    .WithMarkdown(markdown)
    .Build();
```

Fence metadata supports Shiki/Nextra-style options such as
`filename="app.ts"`, `title="Example"`, `{1,3-5}`, `showLineNumbers`,
`noLineNumbers`, `startLine=10`, and `diff`.
Unsupported languages fall back to plain code.

## GitHub-flavored markdown

Enable with:

```csharp
var control = new MarkdownRendererControlBuilder()
    .UseGitHubFlavoredMarkdown()
    .WithMarkdown(markdown)
    .Build();
```

| Syntax | Status | Notes |
| --- | --- | --- |
| Pipe tables | GFM | Column alignment, UIA table/grid roles, selection, clipboard HTML, and theme keys. |
| Task lists | GFM | Rendered as native check visuals/list items and follows list depth styling. |
| Strikethrough | GFM | Uses the `Strikethrough` style key and UIA text attributes. |
| Autolinks | GFM | Rendered as links with the normal link input/accessibility surface. |
| Footnotes | GFM | Deterministic footnote registry, backlinks, source maps, fragment navigation, and document queries. |
| Emoji and smiley parsing | GFM | Resolved by Markdig before native layout. |
| Generic attributes | GFM | `id` values register fragments; `class` and `id` values participate in style aliases. |
| GitHub alerts | GFM | Native alert blocks with note/tip/important/warning/caution style keys. |

## Markdown Extra

Enable after GFM setup when an app wants non-GFM extras:

```csharp
var control = new MarkdownRendererControlBuilder()
    .UseGitHubFlavoredMarkdown()
    .UseMarkdownExtra()
    .WithMarkdown(markdown)
    .Build();
```

| Syntax | Status | Notes |
| --- | --- | --- |
| Definition lists | Extra | Native term/description rendering, style keys, selection, UIA, and document queries. |
| Abbreviations | Extra | Renders abbreviation text with hover expansion tooltips, accessible expansion metadata, and document queries. |
| Figures and captions | Extra | Rendered only where Markdig produces figure nodes; ordinary images keep normal behavior. |
| Subscript and superscript | Extra/GFM helper | Uses baseline-aware inline runs and UIA text attributes. |
| Inserted and marked text | Extra/GFM helper | Uses dedicated style keys, source maps, and clipboard HTML. |

## Extension samples

Mermaid and other diagrams are intentionally app-owned. The sample pattern is:

- detect a fenced code block or custom container from `IMarkdownEmbedFactory.CanCreate`;
- return a deterministic height from `MeasureHeight`;
- create and own the actual WinUI renderer from `CreateBlock`.

See [Extensibility API](extensibility-api.md) and [Samples](samples.md).

## Out of scope for 1.0

| Syntax | Status | Notes |
| --- | --- | --- |
| Raw HTML rendering | Out of scope | Inline HTML falls back to text where possible. HTML blocks are not rendered as HTML. A future implementation needs a sanitizer and a native rendering policy. |
| LaTeX/math | Out of scope | Tracked separately from the 1.0 non-HTML/non-LaTeX release. Apps can experiment through embeds. |
| Built-in diagram engine | Out of scope | The library documents an embed sample instead of bundling JavaScript or a sandbox. |
