# Samples

`MarkdownRenderer.Sample` is the manual and automated verification host. It is
also the best place to see how production integrations should compose the
control, theme, extension registry, and embed factory.

## Running the sample

```powershell
dotnet build MarkdownRenderer\MarkdownRenderer.Sample\MarkdownRenderer.Sample.csproj -c Debug -p:Platform=x64
```

Launch the built app from Visual Studio or from the output folder. Use x86,
x64, and ARM64 builds for native SVG asset smoke.

## Pages

| Page | What it demonstrates |
| --- | --- |
| Typography | Headings, paragraphs, emphasis, inline code, links, and source-mapped selection. |
| Lists | Ordered, unordered, nested, and task-list rendering with depth-aware indents. |
| Tables | GFM pipe tables, header/cell styling, column alignment, and UIA table roles. |
| Code | Fenced and indented code blocks, language metadata, and huge-block segmentation. |
| GFM Alerts | Native alert blocks and style keys. |
| Images | Standalone images, inline images, bitmap/SVG loading, lazy loading, alt text, and selected-image visuals. |
| Embeds | Hosted WinUI controls, virtualization, focus order, and selection drag-through. |
| Markdown Extra | Definition lists, abbreviations, figures/captions, and extra inline variants. |
| Diagrams | Mermaid-style fenced-code embed sample using `IMarkdownEmbedFactory`. |
| RTL | Flow-direction and mixed-language smoke. |
| Virtualization | Long document layout, embed realization, and scroll behavior. |
| Stress | Concurrent scroll, selection, image load, theme change, and rebuild cancellation smoke. |
| Selection | Double-click, triple-click, drag, auto-scroll, source copy, rendered copy, and HTML clipboard payloads. |
| Lazy Images | Viewport-relative image loading and load-completion rebuild behavior. |
| Scroll Anchor | Scroll-position preservation across rebuilds and image intrinsic-size changes. |
| Footnotes | Deterministic footnotes, backlinks, fragments, and UIA ranges. |
| Keyboard Nav | Link focus traversal, hosted-control focus, and dismissal behavior. |
| Accessibility Lab | TextPattern, RangeFromChild, roles, attributes, high-contrast test hooks, and semantic text. |
| Full Demo | Mixed syntax page used for release smoke. |

## Automation relationship

`MarkdownRenderer.Sample.Automation` drives these pages through FlaUI. Automation
coverage is intentionally focused on behavior that is easy to regress:

- UIA tree and TextPattern semantics;
- selection diagnostics and no-shake invariants;
- image/SVG load smoke;
- embed virtualization;
- keyboard traversal and focus dismissal;
- Markdown Extra, diagrams, footnotes, stress, and RTL page load.

See [Testing and diagnostics](testing-and-diagnostics.md) for commands and log
markers.
