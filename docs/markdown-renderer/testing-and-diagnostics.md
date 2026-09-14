# Testing and diagnostics

The renderer has unit tests, UI automation, pixel/SVG compliance tests, and
diagnostic logging for paint/selection regressions.

## Test projects

| Project | Purpose |
| --- | --- |
| `MarkdownRenderer.Tests` | Pure-logic unit tests for parsing, source maps, text boundaries, focus item semantics, SVG helpers, image loading, and regressions. |
| `MarkdownRenderer.Sample` | Manual WinUI test host with feature pages. |
| `MarkdownRenderer.Sample.Automation` | FlaUI UI automation against the sample app. |
| `MarkdownRenderer.PixelTests` | SVG fixture and pixel-comparison infrastructure. |

## Common commands

Build automation:

```powershell
dotnet build MarkdownRenderer\MarkdownRenderer.Sample.Automation\MarkdownRenderer.Sample.Automation.csproj -p:Platform=x64 --nologo
```

Run automation:

```powershell
dotnet run --project MarkdownRenderer\MarkdownRenderer.Sample.Automation\MarkdownRenderer.Sample.Automation.csproj -p:Platform=x64 --no-build -- --app-path MarkdownRenderer\MarkdownRenderer.Sample\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\MarkdownRenderer.Sample.exe
```

Run unit tests:

```powershell
dotnet test MarkdownRenderer\MarkdownRenderer.Tests\MarkdownRenderer.Tests.csproj -p:Platform=x64 --nologo
```

## UI automation coverage

Current automation checks include:

- sample-shell discovery through `SampleNavigation`;
- all grouped destinations through stable `SampleNav_<page-key>` automation IDs
  and `SelectionItem` semantics;
- navigation completion through test-only raw-UIA `CurrentSamplePage` values of
  `page:<page-key>`; raw status probes are excluded from the assistive-technology
  control/content views;
- independent page navigation resetting the preview viewport to the top;
- automation tree shape;
- the Safe HTML page's native rendering and security boundary, including
  TextPattern content, native table and link peers, disclosure
  `ExpandCollapsePattern`, collapse/expand text updates, inert literal fallback
  for script and an unknown element, and suppression of frame and form content;
- the sample host's URI-scheme allowlist, including rejection of custom protocols;
- the Math page's three native formula peers and zero-diagnostic clean render;
- the Audit matrix's exact invalid-Math and unsupported-ELK fallbacks, selectable
  visible `MATH100` and `MMR0002` diagnostics with half-open source spans, and an
  accessibility notification when the diagnostic page is left;
- Accessibility Lab TextPattern text/ranges/bounds;
- Accessibility Lab `RangeFromChild` for hyperlinks, images, and hosted WinUI
  embedded controls;
- Accessibility Lab UIA text attributes for links, code, read-only text, and
  flow/color attributes;
- Accessibility Lab semantic roles for headings, links, lists, tables, cells,
  images, hosted buttons, and task checkboxes;
- deterministic forced high-contrast palette resolution through the sample's
  `ForcedHighContrastToggle`;
- Accessibility Lab keyboard order from painted links into hosted WinUI controls;
- pointer-dismiss resume within the markdown focus order;
- selection dismissal when clicking another app control or a hosted WinUI
  control inside the markdown surface;
- RTL flow toggle on the active page;
- sample navigation discoverability;
- embed virtualization bounded realization;
- images sample load;
- lazy image sample load;
- scroll anchoring sample load;
- footnotes sample load;
- a clean primary Mermaid page with native-scene semantics, pointer/UIA/keyboard
  link activation, vector text scaling, forced-high-contrast pixel/text-attribute
  evidence, and zero diagnostics;
- keyboard Tab traversal;
- focus-ring dismissal on click;
- double-click word selection;
- triple-click line selection;
- context menu;
- hover text-shake regression;
- Embeds-page selection text-shake regression.

Selection automation validates actual selection diagnostics instead of relying on
guessed coordinates. The current probes derive target points from UIA
TextPattern bounding rectangles, validate that the point produces a `sel-anchor`
event, and only then perform the target double-click, triple-click, or drag
gesture. This also covers short documents whose rendered canvas content is
vertically centered inside the control. A selection probe must produce events
such as:

- `sel-anchor`;
- `ptr-move-drag`;
- `sel-extend`;
- `sel-rect-phys`.

The Embeds shake probe also asserts that no `region` or `inline-paint` events
occur during the measured selection drag.

### Sample navigation contracts

Automation selects feature pages by stable identity rather than localized labels.
The helper first locates `SampleNav_<page-key>`, scrolls the item into view when
`ScrollItemPattern` is available, and selects it through `SelectionItemPattern`
with invoke/click fallbacks. It then waits for `CurrentSamplePage` to publish
`page:<page-key>` before inspecting the persistent `MarkdownRenderer` surface.
`CurrentSamplePage` is deliberately a raw-tree test hook, not user-facing UIA
content; automation clients must search the raw tree for it.

The renderer, editor, and display-command contracts remain stable across page
selection: `MarkdownRenderer`, `MarkdownEditor`, `ThemeToggle`, `RtlToggle`,
`ForcedHighContrastToggle`, and `TextScaleToggle`. Tests should not locate a
destination by its localized `NavigationViewItem.Content` or assume it exposes
the Button control type.

The visible `SampleDiagnostics` region reports committed
`MarkdownDocument.Diagnostics` without reparsing. `SampleDiagnosticsMessage` is
selectable and uses polite live-region semantics; each line exposes localized
severity, diagnostic code, message, and a half-open UTF-16 source span.
During editor-driven replacement renders, the previous region remains associated
with the displayed snapshot until a new render commits. Page navigation clears the
previous destination's region immediately. Clearing an open region emits an
`ActionCompleted` notification before it closes so assistive technology can announce
that the diagnostic no longer applies.

## ShakeLogger

`MarkdownRenderer.Diagnostics.ShakeLogger` is off by default. Enable it only
when collecting paint/selection diagnostics by setting either
`MARKDOWN_RENDERER_DIAGNOSTICS=1` or `MARKDOWN_RENDERER_SHAKE_LOG=1`, or by
launching the sample with `--markdown-renderer-diagnostics`. When enabled it
writes diagnostic events to `text_shaking2.log` at the repository root.

Important markers:

- `region`: Win2D canvas region paint path ran;
- `inline-paint`: DirectWrite/Win2D text was repainted;
- `sel-anchor`: selection anchor was set;
- `ptr-move-drag`: pointer moved during a captured selection drag;
- `sel-extend`: selection range changed;
- `sel-rect-phys`: selection adorner rectangle was updated.
- `sel-adorner-draw`: selection adorner rendered at least one selected frame.

The UI automation harness enables diagnostics for the probes that read this log.
Normal sample and app runs stay quiet so paint and pointer paths do not pay the
logging cost.

## Release smoke checklist

Run these before declaring a 1.0 package ready:

- Narrator phrasing for headings, tables, lists, links, images, embeds,
  footnotes, definitions, abbreviations, subscript/superscript, figures, and
  fragments;
- every built-in Windows contrast theme plus one customized contrast theme;
- system language / RTL smoke, including mixed LTR/RTL paragraphs;
- rapid theme switching during scroll, image/SVG load completion, and selection;
- concurrent scroll + selection + hosted-control drag-through;
- monitor disconnect/reconnect or graphics-device reset smoke;
- x86, x64, and ARM64 sample launch, including SVG only when the ThorVG pack is selected;
- package inspection for XML docs, README, icon, license metadata, source docs,
  and, for the optional ThorVG package, all native runtime assets.
