# Testing and diagnostics

The renderer has unit tests, UI automation, pixel/SVG compliance tests, and
diagnostic logging for paint/selection regressions.

## Test projects

| Project | Purpose |
| --- | --- |
| `MarkdownRenderer.Tests` | Unit tests for parsing, layout, theming, selection, SVG helpers, and regressions. |
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
guessed coordinates. A selection probe must produce events such as:

- `sel-anchor`;
- `ptr-move-drag`;
- `sel-extend`;
- `sel-rect-phys`.

The Embeds shake probe also asserts that no `region` or `inline-paint` events
occur during the measured selection drag.

## ShakeLogger

`MarkdownRenderer.Diagnostics.ShakeLogger` writes diagnostic events to
`text_shaking2.log` at the repository root during sample runs.

Important markers:

- `region`: Win2D canvas region paint path ran;
- `inline-paint`: DirectWrite/Win2D text was repainted;
- `sel-anchor`: selection anchor was set;
- `ptr-move-drag`: pointer moved during a captured selection drag;
- `sel-extend`: selection range changed;
- `sel-rect-phys`: selection overlay rectangle was updated.

This log is used to catch regressions where selection/hover accidentally repaint
text and cause visible shake.

## Test gaps

Needed before maturity:

- UIA Text pattern compliance once implemented;
- table and list accessibility tests;
- mixed RTL/LTR documents;
- 10K-line and 100K-word stress documents;
- rapid theme switching;
- concurrent scroll + selection + theme changes;
- hosted embed selection participation;
- x86/ARM64 SVG fallback behavior.

