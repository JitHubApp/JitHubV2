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

- automation tree shape;
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
- RTL flow toggle;
- sample button discoverability;
- embed virtualization bounded realization;
- images sample load;
- lazy image sample load;
- scroll anchoring sample load;
- footnotes sample load;
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

## Test gaps

Needed before maturity:

- more exhaustive mixed RTL/LTR documents;
- real Narrator smoke across built-in and customized Windows contrast themes;
- optional system language / MRT layout-direction smoke;
- 10K-line and 100K-word stress documents;
- rapid theme switching;
- concurrent scroll + selection + theme changes;
- hosted embed selection participation;
- x86/ARM64 SVG fallback behavior.

