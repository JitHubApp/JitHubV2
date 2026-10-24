# Overview and philosophy

MarkdownRenderer provides a native WinUI markdown experience without a browser
DOM or WebView. Its default presentation follows WinUI/Fluent conventions, while
GitHub-specific parsing and presentation are deliberate opt-ins.

## Design principles

- **Immutable input model.** A `MarkdownEngineBuilder` freezes parsing and
  extension configuration into a thread-safe `MarkdownEngine`. Parsing produces
  reusable immutable `MarkdownDocument` snapshots.
- **Explicit viewport ownership.** `MarkdownScrollView` owns scrolling;
  `MarkdownDocumentView` participates in an ancestor-owned effective viewport.
- **Lean by default.** The `MarkdownRenderer` package contains Core plus the
  native viewer. GFM, GitHub, safe HTML, Math, Mermaid, isolated resvg, TextMate, and
  grammar resources are optional packs.
- **Declarative extensibility.** Extensions emit semantic content and source
  spans. They do not construct or inherit viewer layout objects.
- **Host-controlled capabilities.** Images, commands, localization, code
  highlighting, and hosted WinUI elements cross stable service boundaries.
- **Accessible, native interaction.** Selection, focus, links, UI Automation,
  high contrast, RTL, text scale, and DPI behavior are part of the design.
- **Honest fallback.** Optional native engines must fail safely and preserve
  usable source content.

## Clipboard philosophy

Normal Copy behaves like a document viewer: it writes rendered semantic text and
formatted `CF_HTML`. Copy Markdown is a distinct, explicit source-oriented action
available through `CopySelectionAsMarkdown()` or `MarkdownCopyOptions`.

## Non-goals

- browser DOM, script execution, CSS layout, or unrestricted HTML;
- bundling all grammars and native payloads into the lean package;
- exposing internal layout and paint infrastructure as an extension API;
- cross-platform rendering; this viewer is Windows/WinUI-specific.

Native formula processing, the selected-RID Mermaid engine and the safe-HTML
subset are implemented. The broader 1.0 release gates remain open; implemented
capabilities are not claims of full syntax parity or release certification.
