# Samples

`MarkdownRenderer.Sample` is the manual and automated verification host. It is
also the best place to see viewport ownership, immutable engine/document reuse,
Fluent theming, optional feature packs, and host services composed together.

## Running the sample

```powershell
dotnet build MarkdownRenderer\MarkdownRenderer.Sample\MarkdownRenderer.Sample.csproj -c Debug -p:Platform=x64
```

Launch the built app from Visual Studio or from the output folder. Use x86,
x64, and ARM64 builds for native SVG asset smoke.

## Shell and navigation

The sample uses a left, responsive WinUI `NavigationView`. Its pane is expanded
when space permits and collapses at narrower window widths without allowing the
selected sample to determine workspace width. Destinations are grouped as
Overview, Markdown, Profiles, Feature packs, Interaction, and Diagnostics and
scale.

Page identity is independent of localized display text. The navigation root has
automation ID `SampleNavigation`, and every destination uses
`SampleNav_<stable-page-key>`. Selecting a destination updates the hidden,
test-only `CurrentSamplePage` automation status to `page:<stable-page-key>`.
That status and the other test probes live only in the raw UIA view, so they do
not pollute the assistive-technology control/content views.

All destinations share one persistent editor/renderer workspace. Navigation
changes its sample source rather than constructing another renderer, so engine,
highlighter, cache, viewport, and disposal ownership stay stable. Editing the
source updates the preview live and marks the current page title as modified.

The page header contains a responsive `CommandBar` for dark theme, preview RTL,
forced high contrast, and 200% text scale. Dynamic command overflow keeps these
display controls reachable at narrow widths.

Recoverable parser and feature-pack failures appear in a localized diagnostics
`InfoBar` above the workspace. Its selectable, polite-live-region text includes
severity, stable code, message, and the half-open UTF-16 source span. The region
is capped to 50 displayed entries so hostile input cannot consume the workspace;
full diagnostics remain available through the renderer document API. Diagnostics
stay paired with the currently committed preview during editor-driven replacement
renders. Selecting another page clears the previous page's diagnostics immediately.
Whenever an open region clears, the sample raises a localized accessibility
notification before it closes. The sample host launches only absolute HTTP, HTTPS,
and `mailto` links; relative and custom-scheme links are rejected at the host boundary.

## Pages

| Group | Page | Stable automation ID | What it demonstrates |
| --- | --- | --- | --- |
| Overview | Full demo | `SampleNav_FullDemo` | Mixed syntax and optional packs composed in one release-smoke document. |
| Markdown | Typography | `SampleNav_Typography` | Headings, paragraphs, emphasis, inline code, links, and source-mapped selection. |
| Markdown | Lists | `SampleNav_Lists` | Ordered, unordered, nested, and task-list rendering with depth-aware indents. |
| Markdown | Tables | `SampleNav_Tables` | GFM pipe tables, header/cell styling, column alignment, local overflow, and UIA table roles. |
| Markdown | Code and TextMate | `SampleNav_Code` | Fenced and indented code, metadata, line numbers, highlighted ranges, copy actions, TextMate highlighting, and local horizontal scrolling. |
| Markdown | Images and SVG | `SampleNav_Images` | Standalone bitmap and data-URI/remote SVG loading, alt text, captions, failure fallback, and selection. |
| Profiles | GitHub alerts | `SampleNav_GitHubAlerts` | Native Note, Tip, Important, Warning, and Caution alerts from the GitHub README profile. |
| Profiles | Footnotes | `SampleNav_Footnotes` | Footnote definitions, backlinks, fragments, keyboard navigation, and UIA ranges. |
| Profiles | Markdown Extra | `SampleNav_MarkdownExtra` | Definition lists, abbreviations, and figures/captions enabled separately from strict GFM. |
| Profiles | Safe HTML | `SampleNav_Html` | Native block and inline HTML, lists, table semantics, disclosure interaction, host-mediated links/images, inert unknown tags, and suppression of executable content. |
| Feature packs | Math | `SampleNav_Math` | Clean inline and display TeX examples, selectable/accessibility-atomic formulas, and literal unsupported delimiters. |
| Feature packs | Mermaid | `SampleNav_Mermaid` | Clean native linked flowchart, sequence, and pie scenes plus selection, UIA, text scaling, and theme/contrast behavior. |
| Feature packs | Diagram pipeline | `SampleNav_Diagrams` | Mermaid extension claiming and native rendering alongside an unclaimed diagram fence that remains exact code. |
| Feature packs | Hosted elements | `SampleNav_Embeds` | Hosted WinUI controls, viewport realization, focus order, read-only task semantics, and selection drag-through. |
| Interaction | Selection and copy | `SampleNav_Selection` | Double-click, triple-click, drag, auto-scroll, source copy, rendered copy, and HTML clipboard payloads. |
| Interaction | Keyboard navigation | `SampleNav_KeyboardNav` | Link and hosted-control focus traversal, activation, selection commands, and dismissal behavior. |
| Interaction | Right-to-left | `SampleNav_Rtl` | Preview flow direction, mirrored block/list/table layout, and mixed-language shaping. |
| Interaction | Lazy images | `SampleNav_LazyImages` | Viewport-relative image loading and load-completion rebuild behavior. |
| Interaction | Scroll anchoring | `SampleNav_ScrollAnchor` | Reading-position preservation across rebuilds and image intrinsic-size changes. |
| Diagnostics and scale | Virtualization | `SampleNav_Virtualization` | Long document layout, bounded hosted-element realization, and scroll behavior. |
| Diagnostics and scale | Stress document | `SampleNav_Stress` | Large mixed-syntax layout, hosted-element realization, scrolling, selection, theme changes, and rebuild-cancellation smoke. |
| Diagnostics and scale | Accessibility lab | `SampleNav_AccessibilityLab` | TextPattern, RangeFromChild, semantic roles, attributes, high-contrast hooks, and hosted-element focus order. |
| Diagnostics and scale | Audit matrix | `SampleNav_AuditMatrix` | Deterministic hostile/malformed input, recoverable Math and unsupported-Mermaid fallback diagnostics, overflow, selection, clipboard, and viewport-containment checks. |

## Automation relationship

`MarkdownRenderer.Sample.Automation` drives these pages through FlaUI. Automation
coverage is intentionally focused on behavior that is easy to regress:

- complete `NavigationView` destination discovery and `SelectionItem` semantics
  through the stable `SampleNav_*` IDs;
- page navigation resetting the independent document viewport to the top;
- UIA tree and TextPattern semantics;
- selection diagnostics and no-shake invariants;
- image/SVG load smoke;
- native safe-HTML table/link/disclosure semantics, disclosure state changes,
  inert script/unknown-element fallback, and suppression of frame/form content;
- rejection of a non-allowlisted custom URI by the sample host;
- a clean primary Math page with three native formula peers;
- exact invalid-formula and unsupported-ELK fallback plus visible `MATH100` and
  `MMR0002` diagnostic details in the Audit matrix, including an accessible
  resolution notification;
- hosted-element virtualization;
- keyboard traversal and focus dismissal;
- audit-matrix hostile content, footnote loading, image/lazy-image loading,
  scroll anchoring, virtualization, and keyboard-navigation behavior;
- a clean primary Mermaid page with native hyperlink invocation, semantic ranges,
  text scaling, forced-high-contrast paint evidence, and zero diagnostics.

See [Testing and diagnostics](testing-and-diagnostics.md) for commands and log
markers.
