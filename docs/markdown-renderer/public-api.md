# Public API

This page summarizes the consumer-facing API. Implementation classes for layout,
painting, and source-map internals are intentionally not the consumer path.

## Packages

| Package | Purpose |
| --- | --- |
| `MarkdownRenderer` | Core WinUI control, CommonMark rendering, theming, selection, accessibility, images/SVG, hosted-control support, and document queries. |
| `MarkdownRenderer.Gfm` | GitHub-flavored markdown helpers and renderers, plus opt-in Markdown Extra helpers. |
| `MarkdownRenderer.SyntaxHighlighting.TextMate` | Optional TextMate grammar provider for broad code-block syntax highlighting. |

## Core namespaces

| Namespace | Public surface |
| --- | --- |
| `MarkdownRenderer.Controls` | `MarkdownRendererControl`, `MarkdownRendererControlBuilder`, link events, copy and rebuild entry points. |
| `MarkdownRenderer.Document` | Stable parsed-document facade and query result records. |
| `MarkdownRenderer.Hosting` | `IMarkdownEmbedFactory` for app-owned hosted WinUI controls. |
| `MarkdownRenderer.Parsing` | `MarkdownExtensionRegistry` and advanced renderer registration. |
| `MarkdownRenderer.Selection` | `MarkdownCopyOptions` and plain-text copy mode. |
| `MarkdownRenderer.Theming` | `MarkdownTheme`, `ElementStyle`, `ElementStyleOverride`, and `MarkdownElementKeys`. |
| `MarkdownRenderer.Gfm` | GFM factory and builder extension methods. |
| `MarkdownRenderer.CodeBlocks` | Code-block syntax-highlighting provider contracts and line-number options. |
| `MarkdownRenderer.SyntaxHighlighting.TextMate` | TextMate highlighter and builder/control extension methods. |

## Creating controls

CommonMark-only:

```csharp
using MarkdownRenderer.Controls;

var control = MarkdownRendererControl.CreateDefault(markdownSource);
```

Recommended GitHub-flavored setup:

```csharp
using MarkdownRenderer.Gfm;

var control = GfmMarkdownRenderer.CreateDefault(markdownSource);
```

Fluent setup:

```csharp
using MarkdownRenderer.Controls;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.SyntaxHighlighting.TextMate;

var control = new MarkdownRendererControlBuilder()
    .UseGitHubFlavoredMarkdown()
    .UseMarkdownExtra()
    .UseTextMateSyntaxHighlighting()
    .WithMarkdown(markdownSource)
    .WithTheme(theme)
    .WithEmbedFactory(embedFactory)
    .WithSelectionEnabled(true)
    .WithCodeBlockCopyEnabled(true)
    .WithCodeBlockCopyButtonLabel("Copy code")
    .WithCodeBlockCopiedButtonLabel("Copied")
    .WithCodeBlockLineNumberMode(CodeBlockLineNumberMode.AutoMultiline)
    .Build();
```

`UseGitHubFlavoredMarkdown()` is strict to GFM. `UseMarkdownExtra()` adds
definition lists, abbreviations, figures, and extra inline variants.

## MarkdownRendererControl

Common consumer properties and methods:

| Member | Purpose |
| --- | --- |
| `Markdown` | Source markdown string. Null input is treated as empty. |
| `Theme` | Optional `MarkdownTheme`; null uses the renderer default. |
| `ExtensionRegistry` | Optional parser/renderer registry. Null uses core CommonMark behavior. |
| `EmbedFactory` | Optional block-level hosted WinUI control factory. |
| `IsSelectionEnabled` | Enables pointer/keyboard text selection. |
| `IsCodeBlockCopyEnabled` | Shows always-visible native copy buttons on fenced and indented code blocks. Defaults to true. |
| `CodeBlockCopyButtonLabel` | Accessible name and tooltip for icon-only code-block copy buttons. Null uses the localized default. |
| `CodeBlockCopiedButtonLabel` | Accessible name and tooltip used briefly after a successful code-block copy. Null uses the localized default. |
| `IsCodeBlockSyntaxHighlightingEnabled` | Allows a configured highlighter provider to color code blocks. Defaults to true. |
| `CodeBlockSyntaxHighlighter` | Optional syntax-highlighting provider. Null keeps code plain. |
| `CodeBlockLineNumberMode` | Controls line-number defaults. Defaults to `AutoMultiline`. |
| `Document` | Immutable public parsed-document snapshot for queries. |
| `RequestRebuild()` | Explicitly schedules a rebuild when an advanced integration changes external state. |
| `CopySelectionToClipboard(MarkdownCopyOptions? options = null)` | Copies the current selection with source-markdown defaults and optional rendered text. |

Link activation is surfaced through `LinkClick`. Internal fragments and footnote
backlinks are handled by the control when possible.

## Document facade

`MarkdownRenderer.Document.MarkdownDocument` exposes stable queries that do not
require consumers to inspect layout boxes:

```csharp
var document = control.Document;

var headings = document.GetHeadings();
var links = document.GetLinks();
var codeBlocks = document.GetCodeBlocks();
var images = document.GetImages();
var footnotes = document.GetFootnotes();
var definitions = document.GetDefinitionItems();
var abbreviations = document.GetAbbreviations();
var fragments = document.GetFragments();
```

Query records include display text, source span, block index, and syntax-specific
metadata such as heading level, URL/title, code language, image source/alt text,
footnote label/order, definition marker, abbreviation expansion, and fragment id.

The facade is a snapshot. Read it again after `Markdown` or registry changes
commit a rebuild.

## Clipboard API

Default keyboard and context-menu copy writes:

- plain text: exact selected markdown source;
- HTML: formatted clipboard payload.

Apps that want semantic rendered text as the plain-text payload can opt in:

```csharp
using MarkdownRenderer.Selection;

control.CopySelectionToClipboard(new MarkdownCopyOptions
{
    PlainTextMode = MarkdownPlainTextCopyMode.RenderedText,
    IncludeHtml = true,
});
```

Use `MarkdownPlainTextCopyMode.SourceMarkdown` when the markdown source remains
the document of record.

## Theme API

`MarkdownTheme` contains an observable `Overrides` dictionary. Direct indexer,
add, remove, and clear operations raise `Changed` and trigger theme invalidation
when assigned to a control.

```csharp
using MarkdownRenderer.Theming;

theme.Overrides[MarkdownElementKeys.Link] = new ElementStyleOverride
{
    Foreground = Colors.DodgerBlue,
    HoverForeground = Colors.DeepSkyBlue,
    Underline = false,
};
```

Style resolution order is deterministic:

1. Win11/light/dark/high-contrast defaults;
2. semantic element key;
3. ancestor/context keys;
4. generic attribute class aliases such as `.warning`;
5. generic attribute id aliases such as `#intro`.

See [Theming and customization](theming-and-customization.md).

## Extension author API

Advanced extension authors can register Markdig pipeline mutations and native
node renderers through `MarkdownExtensionRegistry`. These callbacks run during
background parse/layout and must not touch WinUI dispatcher-affine state.

Use `IMarkdownEmbedFactory` for real WinUI controls. Its threading contract is:

| Method | Thread | Requirement |
| --- | --- | --- |
| `CanCreate` | Background layout thread | Pure, thread-safe, WinUI-free. |
| `MeasureHeight` | Background layout thread | Pure, deterministic, WinUI-free. |
| `CreateBlock` | UI thread | Create the hosted `FrameworkElement`. |
| `RecycleBlock` | UI thread | Detach handlers and release app resources. |

See [Extensibility API](extensibility-api.md) and [Native integration and hosted controls](native-integration-and-hosted-controls.md).

## Versioning

The library is in pre-1.0 cleanup while the public surface is being finalized.
Source-breaking changes are allowed before 1.0 when they remove accidental public
internals or clarify the extension boundary. Starting at 1.0, public APIs follow
semantic versioning.
