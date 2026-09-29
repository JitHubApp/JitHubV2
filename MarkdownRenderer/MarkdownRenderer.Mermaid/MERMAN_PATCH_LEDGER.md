# Merman downstream patch ledger

Upstream Merman is consumed at the exact Git revision recorded in
`MERMAN_PROVENANCE.json`; its source is not modified or vendored into this repository.

The downstream work is isolated in `native/` as a thin product adapter:

- a private, fixed-width C ABI with opaque handles and panic containment;
- a no-callback DirectWrite text measurer whose immutable family catalog is transferred at engine creation;
- strict resource, cancellation, deadline, and concurrency enforcement;
- an SVG-presentation-to-MMIR scene flattener; and
- an explicit rejection boundary for every ELK request.

Any future Merman revision or local source patch must add a dated entry with rationale,
upstream issue or pull request, changed-file hashes, license review, and parity evidence before
the provenance pin may move.

## 2026-09-08 — native chart geometry and presentation

Reproduced displaced node labels and origin-painted arrowheads in the WinUI
sample. Changes are confined to the downstream adapter; the upstream revision,
dependency lock, license boundary, C ABI, and MMIR format remain unchanged.

- Set top-level `htmlLabels: false` as well as the legacy flowchart setting.
  The top-level setting takes precedence; native text must not pass through
  detached foreignObject fallback overlays.
- Report DirectWrite horizontal extents relative to the centered SVG anchor,
  not the left-aligned layout origin.
- Lay out styled text runs with measured advances, inherited positioning,
  font-relative lengths, whole-chunk anchors, and central baselines.
- Retain font, text-anchor, baseline, marker, and background declarations in
  the CSS property filter instead of discarding them before scene conversion.
- Skip non-rendered definitions and instantiate referenced start/end markers
  at their path endpoints, honoring orientation, reference points, viewport
  scale, and marker units. Marker recursion remains depth-bounded.
- Use the existing pinned SVG parser's absolute/simplified path commands to
  retain smooth curves and elliptical arcs; flatten curves with bounded
  subdivision instead of substituting straight chords.
- Preserve the SVG canvas background so its palette stays readable when the
  same immutable scene is embedded in a different application theme.

Regression coverage includes native geometry assertions, DirectWrite anchor
metrics, managed DLL-to-scene assertions, LF/CRLF/CR markdown integration, and
`eng/Test-MermaidSampleUi.ps1` for flowcharts, links, sequence charts, pie charts,
and deliberate ELK fallback. UIA success must be accompanied by screenshot
inspection; scene availability alone is not visual parity evidence.

Verified 16 native release tests, 55 managed tests in both Debug and Release,
and four live x64 sample scenarios with individually inspected screenshots.
All three native runtime payloads were rebuilt; x86/ARM64 were cross-built,
not runtime-tested. Dashed sequence edges still render solid in the current
adapter; this evidence is not a claim of complete SVG or all-family pixel parity.

SVG rules consulted: [text positioning](https://www.w3.org/TR/SVG2/text.html)
and [markers](https://www.w3.org/TR/svg-markers/).

No upstream source patch or new dependency was introduced; the existing MIT
license and no-ELK dependency review still apply. Changed adapter source hashes
(SHA-256, UTF-8 with LF line endings):

| File under `native/src/` | SHA-256 |
| --- | --- |
| `lib.rs` | `d3471c7bc51730b20d35d9c5dc4628dc9ab67f7ed781a0fff81656591f2b791e` |
| `directwrite.rs` | `7dddf7adfe94358c69dbd9c039ff87c7c22292a91de1fb7e400ea81b421d547f` |
| `scene.rs` | `64a09bc67d90a948730414e8dff131e0160c933bb52091c0445cc379059aaa8a` |
| `scene_tests.rs` | `a71cf89070ba517369ed6c5a90a05f240a6526143c7e9d1d1e35c7c457f29af4` |

## 2026-09-09 — bounded scene conversion and dashed strokes

The SVG-to-MMIR adapter now carries the same Merman `OperationControl` through
post-processing and emission. It checks requested cancellation and monotonic
deadlines throughout element scanning, traversal, path subdivision, text
measurement, semantic association, and serialization. Adapter-owned element,
work, retained-memory, and encoded-scene ledgers reject work before vector/string
growth or final-buffer allocation. Curve subdivision consumes both work and
memory budget at every branch.

MMIR emission now computes the nine section sizes first and writes each section
directly into one checked, pre-sized output buffer. The former per-section byte
buffers and final payload copy were removed. This reduces peak memory while also
making `MaxSceneBytes` an admission check for every retained MMIR item rather
than only a final check.

The adapter now preserves `stroke-dasharray` from SVG attributes, stylesheets,
and inline declarations. Absolute SVG dash lengths are normalized by stroke
width for Direct2D custom-dash semantics and stored through the existing MMIR
style dash range. A native serialization test and a managed sequence-diagram
integration test cover the `-->>` response path.

The managed project now selects its native payload from `RuntimeIdentifier`
before consulting `Platform`. Project-reference publishes with no explicit
Platform selected the correct PE machine for `win-x86`, `win-x64`, and
`win-arm64` (`0x014c`, `0x8664`, and `0xaa64`).

Verified 19 native tests in Debug and optimized Release, and 64 managed Mermaid
tests in Release/x64. All three native payloads were rebuilt through
`native/Build-Native.ps1`; x86 and ARM64 were cross-built and PE-validated, not
executed on the x64 host. The upstream revision, dependencies, license boundary,
C ABI, and MMIR version remain unchanged.

The recorded build used stable Rust 1.96.0 (`ac68faa20`), Cargo 1.96.0
(`30a34c682`), LLVM 22.1.2, and the three `windows-msvc` targets enumerated in
`MERMAN_PROVENANCE.json`. Reproduce it from the package project directory with
`pwsh -NoProfile -File native/Build-Native.ps1`.

Current adapter source hashes (SHA-256, UTF-8 with LF line endings):

| File under `native/src/` | SHA-256 |
| --- | --- |
| `lib.rs` | `e4c6cff82e1229c24729b7a033d4bf9c99c53f9084436979939f05b32d0681f0` |
| `directwrite.rs` | `7dddf7adfe94358c69dbd9c039ff87c7c22292a91de1fb7e400ea81b421d547f` |
| `scene.rs` | `180c71010fede2e976da108eccd364b94d451d2a024d3711ba5c4d62b48dd08b` |
| `scene_tests.rs` | `fc1934ab22e5ba8f4bbbfdefdd095ee5338c04533026f44c2aedfe1bda0a86e4` |

Rebuilt runtime payload hashes:

| RID | Bytes | SHA-256 |
| --- | ---: | --- |
| `win-x86` | 11,617,280 | `a3ebb25070524cc53a0a913fd2808d0fab75147e1298a48ef2fcbfa7c52222c3` |
| `win-x64` | 15,950,848 | `48510b6e69b70810b42db155d824d65ad59df6d530487085d4140da7eb92fb5c` |
| `win-arm64` | 12,911,104 | `64b2a88c90ef87137f09ebd221b67c436feb66c7c51b37ff2dc0909e019feeb3` |

## 2026-09-11 — official Mermaid palettes and Windows contrast themes

Reproduced official-theme color drift, invisible dark-theme edges, and false
`MMR0002` results on otherwise valid diagrams. The problems were confined to
the downstream adapter: a Merman editor presentation preset overrode Mermaid's
theme variables, the SVG color bridge recognized only a small named-color
subset, and the ELK boundary scanned arbitrary source text instead of consuming
the renderer's typed capability result.

- Start every request from Merman's pinned Mermaid defaults with an exact site
  configuration and the documented `classic` look. Do not inject EditorLight or
  EditorDark presentation variables into Mermaid's `default`, `dark`, `forest`,
  or `neutral` themes.
- Materialize the selected theme's root canvas. In particular, the pinned dark
  theme uses `#333`; default, forest, and neutral retain a white canvas. The
  explicit pack High Contrast theme keeps its black canvas.
- Parse the complete CSS3/SVG named-color set through the already-pinned
  `svgtypes` dependency. This preserves official `lightgrey` flowchart edges and
  arrowheads as well as colors such as Gantt `navy`.
- Cascade `fill`, `stroke`, `fill-opacity`, and `stroke-opacity` independently,
  retain presentation-attribute opacity, and combine it with authored RGBA
  alpha regardless of declaration order.
- Reject ELK through the managed layout option or Merman's typed
  `RenderCapability::LayoutElk` error. Mermaid labels containing strings such as
  `layout: elk` or `{"layout":"elk"}` are ordinary content and render normally.
- Preserve authored Mermaid colors in normal Windows themes. The managed vector
  scene carries separate High Contrast roles so surfaces resolve to the user's
  system `Window` color and text, outlines, edges, and arrowheads resolve to
  `WindowText`; linked foregrounds resolve to `Hotlight`.
- Use explicit transparent fills for stroke-only paths, keep edge arrowheads as
  High Contrast foreground, and reuse immutable converted styles for equivalent
  commands.
- Emit distinct fill and stroke passes for filled polygon and polyline geometry
  so decision-node outlines survive the managed scene's opcode-specific paint
  restriction.

Regression coverage includes exact pinned default node/edge/text colors, exact
dark canvas/node/text/edge/arrow colors, default sequence-note colors, distinct
pie categories, High Contrast semantic roles, full named-color parsing,
independent opacity cascading, typed ELK rejection, ELK-like label text, paired
polygon/polyline paint passes, and an end-to-end decision diamond. Verified 23
optimized native tests and 103 managed Mermaid tests in both Debug
and Release on x64. All three native payloads were rebuilt through
`native/Build-Native.ps1`; x86 and ARM64 were cross-built and PE-validated, not
executed on the x64 host. PE machine values were re-verified as `0x014c` (x86),
`0x8664` (x64), and `0xaa64` (ARM64).

Theme parity references: Mermaid 11.16.1
[default](https://github.com/mermaid-js/mermaid/blob/mermaid%4011.16.1/packages/mermaid/src/themes/theme-default.js),
[dark](https://github.com/mermaid-js/mermaid/blob/mermaid%4011.16.1/packages/mermaid/src/themes/theme-dark.js),
[flowchart styles](https://github.com/mermaid-js/mermaid/blob/mermaid%4011.16.1/packages/mermaid/src/diagrams/flowchart/styles.ts),
[sequence styles](https://github.com/mermaid-js/mermaid/blob/mermaid%4011.16.1/packages/mermaid/src/diagrams/sequence/styles.js),
and [pie styles](https://github.com/mermaid-js/mermaid/blob/mermaid%4011.16.1/packages/mermaid/src/diagrams/pie/pieStyles.ts).
Windows role pairing follows Microsoft's
[contrast-theme guidance](https://learn.microsoft.com/windows/apps/design/accessibility/high-contrast-themes).

No upstream source patch, C ABI change, MMIR version change, dependency change,
or license-boundary change was introduced. ELK remains excluded. Current adapter
source hashes (SHA-256, UTF-8 with LF line endings):

| File under `native/src/` | SHA-256 |
| --- | --- |
| `lib.rs` | `9e64e2b9e7fdc084647d653063e2d13c739af467210bf08bd2c0c52f76ca4713` |
| `directwrite.rs` | `7dddf7adfe94358c69dbd9c039ff87c7c22292a91de1fb7e400ea81b421d547f` |
| `scene.rs` | `3fa0daed7c6185b143b1d11a8000e3d150b8f65289b4763cd7acfa4ae573ac89` |
| `scene_tests.rs` | `be32a84e1524bae303d37ccac973e7969fae5d28a281680102e162d19ff8e8cf` |

Rebuilt runtime payload hashes:

| RID | Bytes | SHA-256 |
| --- | ---: | --- |
| `win-x86` | 11,582,976 | `919c3c7efa18adf76ef78490ae21cee0a8a77ab5c8a3568e3279a20f15b10ec4` |
| `win-x64` | 15,899,136 | `f2ed10ac1e67efad0f5b4a8c0173b532a0543528d9d8c6ef664a4779a613db0d` |
| `win-arm64` | 12,870,656 | `666c91c1c6d5bfe446fa254a22530ed2a3f722c8d0bb78f1d26209e8ae18b952` |
