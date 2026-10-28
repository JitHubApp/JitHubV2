# Testing and diagnostics

The renderer has unit tests, UI automation, pixel/SVG compliance tests, and
Windows Sandbox UI runners.

## Test projects

| Project | Purpose |
| --- | --- |
| `MarkdownRenderer.Tests` | Pure-logic unit tests for parsing, source maps, text boundaries, focus item semantics, SVG helpers, image loading, and regressions. |
| `MarkdownRenderer.GitHub.Tests` | Renderer tests that run on the WinUI/Win2D stack: layout, images, code blocks, accessibility, the GitHub README profile, and the Primer visual profiles. |
| `MarkdownRenderer.Html.Tests` | Safe-HTML contract, budget, and fuzz tests. |
| `MarkdownRenderer.Math.Tests`, `MarkdownRenderer.Mermaid.Tests`, `MarkdownRenderer.Svg.Resvg.Tests`, `MarkdownRenderer.SyntaxHighlighting.TextMate.Tests` | Tests for each feature pack. |
| `MarkdownRenderer.Conformance.Tests` | Tests of the CommonMark/GFM conformance harness itself. |
| `MarkdownRenderer.Sample` | Manual WinUI test host with feature pages. |
| `MarkdownRenderer.Sample.Automation` | FlaUI UI automation against the sample app. |
| `MarkdownRenderer.Pixel.Tests` | SVG fixture and pixel-comparison infrastructure. |
| `MarkdownRenderer.PublicApi.Tests` | Public-surface policy over every shipping assembly that the `PublicAPI.*.txt` files do not record. See [Public API](public-api.md#changing-the-public-api). |

### NativeAOT smokes

`tests/aot/` holds console consumers that publish with NativeAOT (or trimmed
single-file) and treat the trim and AOT analyzer warnings as errors:

| Project | Checks |
| --- | --- |
| `MarkdownRenderer.AotSmoke` | The `MarkdownRenderer` package: strict GFM and GitHub README engines, parse-cache identity, source map, headings, the safe-HTML capability contract, and the performance session. Prints `gfm-0.29;github-readme;safe-html-v1`. CI publishes it from project references for win-x64, and from the freshly packed package (`-p:MarkdownRendererPackageVersion=<version>`) for x86, x64, and ARM64 in NativeAOT and trimmed single-file modes. |
| `MarkdownRenderer.Math.AotSmoke`, `MarkdownRenderer.Mermaid.AotSmoke`, `MarkdownRenderer.Svg.Resvg.AotSmoke`, `MarkdownRenderer.SyntaxHighlighting.TextMate.AotSmoke` | One feature pack each. CI runs the Mermaid and resvg smokes; the per-pack release evidence scripts publish the others as package consumers. |

## Project layout and structure checks

`MarkdownRenderer/` keeps shipping libraries in `src/`, tests in `tests/`
(NativeAOT smokes in `tests/aot/`), the sample apps in `samples/`, benchmarks
and the performance harness in `perf/`, and build scripts in `eng/`. The rules are the "Code Structure Standards" in the
repository's `AGENTS.md`; two checks enforce part of them.

Every renderer project build runs `CheckMarkdownRendererProjectStructure`
(`MarkdownRenderer/Directory.Build.targets`) before it compiles:

| Code | Fails on |
| --- | --- |
| `MRS0001` | A `Compile`, `Page` or `ApplicationDefinition` item outside the project folder. Generated files in the intermediate output and sources added by NuGet packages are not checked. |
| `MRS0002` | An `InternalsVisibleTo` grant to an assembly whose name does not end in `.Tests` or `.Benchmarks`. |

`eng/Test-ProjectStructure.ps1` runs early in the preview workflow and checks
what one project build cannot see:

| Code | Fails on |
| --- | --- |
| `MRS0003` | `[assembly: InternalsVisibleTo]` in a source file. Grants are `InternalsVisibleTo` items in the project file. |
| `MRS0004` | A `.cs` file under `src`, `tests`, `samples`, `perf` or `tools` that is in no project folder and is not a declared exception. |
| `MRS0005` | A project under those folders that is missing from `MarkdownRenderer.slnx`. |
| `MRS0006` | A `MarkdownRenderer.slnx` entry that does not exist. |
| `MRS0007` | An exception `Compile` item that does not name an existing file literally. |

An item may break a rule only when it carries `ProjectStructureException`
metadata that states the reason and how the exception is removed:

```xml
<InternalsVisibleTo Include="MarkdownRenderer.ExampleTool"
                    ProjectStructureException="ExampleTool reads an internal counter; the counter moves to public diagnostics API." />
```

The project files are the only list of exceptions; search them for
`ProjectStructureException`. Run the script locally with:

```powershell
pwsh .\MarkdownRenderer\eng\Test-ProjectStructure.ps1
```

## Common commands

Build automation:

```powershell
dotnet build MarkdownRenderer\samples\MarkdownRenderer.Sample.Automation\MarkdownRenderer.Sample.Automation.csproj -p:Platform=x64 --nologo
```

Run automation:

```powershell
dotnet run --project MarkdownRenderer\samples\MarkdownRenderer.Sample.Automation\MarkdownRenderer.Sample.Automation.csproj -p:Platform=x64 --no-build -- --app-path MarkdownRenderer\samples\MarkdownRenderer.Sample\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\MarkdownRenderer.Sample.exe
```

Run unit tests:

```powershell
dotnet test MarkdownRenderer\tests\MarkdownRenderer.Tests\MarkdownRenderer.Tests.csproj -p:Platform=x64 --nologo
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
- context menu.

Selection probes do not guess coordinates. They derive target points from UIA
TextPattern bounding rectangles, perform the double-click, triple-click, or drag
gesture, and then read the selected text back through UIA. This also covers
short documents whose rendered content is vertically centered inside the
control.

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

## Correctness and evidence scripts

These are developer-run tools, not CI jobs. The Windows Sandbox runners need WinApp
CLI 0.7 or newer and write their run directories under the Git-ignored
`MarkdownRenderer/artifacts/correctness/`.

| Script (`MarkdownRenderer/eng/`) | Purpose |
| --- | --- |
| `Invoke-MarkdownCorrectnessUiTests.ps1` | Live correctness cases against the sample in Windows Sandbox. Focus a run with `-Phase2Only`, `-Phase2SystemHighContrast`, `-Phase1ClipboardOnly`, `-Phase1CasePattern`, or `-Phase2CasePattern`. |
| `MarkdownCorrectnessPhase1ClipboardCases.ps1`, `MarkdownCorrectnessPhase2*Cases.ps1` | Case files that the UI runner dot-sources for its focused modes. |
| `Invoke-MarkdownCorrectnessSandboxFollowUp.ps1` | Follow-up cases on a prior UI run: handle crossing and edge autoscroll, point-range queries, and actual Windows High Contrast. `-StressIterations` runs only the selection stress cases. |
| `MarkdownCorrectnessSandboxProbe.ps1` | Guest-side probe that the follow-up runner stages into the Sandbox. |
| `Test-SandboxSampleUi.ps1` | Builds and launches the sample in Windows Sandbox and runs a UI smoke. |
| `Test-MermaidSampleUi.ps1` | Mermaid page UI checks against a sample launched with `winapp run`. |
| `Test-TouchSelectionSampleUi.ps1` | Touch selection checks at one architecture and display scale. |
| `Test-TouchSelectionReleaseMatrix.ps1` | Validates a set of touch selection runs across architectures, scales, and contrast. |
| `Invoke-MathReleaseEvidence.ps1`, `Invoke-MermaidReleaseEvidence.ps1`, `Invoke-SvgResvgReleaseEvidence.ps1`, `Invoke-TextMateReleaseEvidence.ps1` | Per-pack release evidence, including NativeAOT consumer smokes. |
| `New-MarkdownManagedArchitectureEvidence.ps1` | Static cross-architecture build and PE-header evidence for `MarkdownRenderer.dll` and its sample and performance-harness consumers. |

Each UI run writes `run.json`, `results.json`, command logs, and screenshots.
A case that the batch cannot check is recorded as `UNVERIFIED` with a reason,
never as a pass.

`Test-MarkdownEngineeringScripts.ps1` runs the offline self-tests of the
Sandbox runners, the Sandbox sample UI configuration, the resvg
vendored-source provenance check, and the code structure check. None of them
launches an app or touches the desktop:

```powershell
pwsh .\MarkdownRenderer\eng\Test-MarkdownEngineeringScripts.ps1
```

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
- x86, x64, and ARM64 sample launch, including SVG only when the resvg provider is selected;
- package inspection for XML docs, README, icon, license metadata, source docs,
  and, for the optional resvg package, every worker, SBOM, notice, and provenance asset.
