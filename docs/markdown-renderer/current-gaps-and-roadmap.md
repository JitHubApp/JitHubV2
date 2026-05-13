# Current gaps and roadmap

This document summarizes what remains between the current implementation and a
production-mature open-source control.

## Maturity snapshot

| Area | Estimated maturity | Main gaps |
| --- | --- | --- |
| Rendering | 85 percent | Inline images, HTML, definition lists, math, generic attributes. |
| Selection | 80 percent | Embedded controls, auto-scroll, copy-as-HTML. |
| Theming | 75 percent | Incremental restyle, auto-invalidation, state variants. |
| Accessibility | 60 percent | Text pattern, table pattern, list roles, heading levels. |
| Performance | 85 percent | Lazy layout, restyle-only rebuild, allocation reduction. |
| Packaging/API | 70 percent | XML docs, NuGet metadata, API facade, cross-arch native assets. |

## Release-blocking items

- Implement `ITextProvider` / `ITextRangeProvider`.
- Expose heading levels through UIA.
- Implement table UIA patterns.
- Fix inline image rendering.
- Add XML documentation for all public API surface.

## Before 1.0

- Incremental theme invalidation without markdown re-parse.
- Auto-invalidate `MarkdownTheme.Overrides` mutations.
- Make embedded WinUI elements participatory in selection.
- Add drag-outside-viewport auto-scroll.
- Add lazy/viewport-relative layout for large documents.
- Document and guard `IMarkdownEmbedFactory` thread safety.
- Add quick-start builder/factory APIs.
- Add ARM64/x86 SVG fallback visibility or native binaries.
- Stabilize public API surface.

## v1.1 candidates

- Definition list renderer.
- Math/LaTeX support.
- Abbreviations.
- Generic attribute styling and fragment targeting.
- Copy-as-HTML.
- List UIA roles.
- Code language hints.
- Style composition/context variants.
- Object pooling for layout allocations.

## Known technical debt

- Theme override dictionary mutation is silent until `Invalidate()` is called.
- Footnote order fallback is heuristic when Markdig does not assign order.
- Scroll anchoring can lose position if all visible blocks scroll off.
- Pointer-capture state has subtle interactions with hosted controls.
- Raw HTML policy is undecided.
- SVG is x64-only today.

## Source of truth for work tracking

The repository also contains:

- `MarkdownRenderer\TODO.md` for a checklist-style maturity backlog;
- session SQL todos for task execution within agent sessions.

When adding new maturity work, update this roadmap and the TODO file together.

