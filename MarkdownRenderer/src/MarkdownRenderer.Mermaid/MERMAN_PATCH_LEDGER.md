# Merman downstream patch ledger

Upstream Merman is consumed at the exact Git revision recorded in
`MERMAN_PROVENANCE.json`. Reviewed source patches are kept under `native/patches/`
and applied only to an independent, hash-verified build checkout. The shared
Cargo checkout is never modified, and the full upstream source is not vendored
into this repository.

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
inspection; scene availability alone is not visual parity evidence. Dashed
sequence edges still rendered solid after this change; this is not a claim of
complete SVG or all-family pixel parity.

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

MMIR emission computes the nine section sizes first and writes each section
directly into one checked, pre-sized output buffer. The former per-section byte
buffers and final payload copy were removed. This reduces peak memory while also
making `MaxSceneBytes` an admission check for every retained MMIR item rather
than only a final check.

The adapter preserves `stroke-dasharray` from SVG attributes, stylesheets,
and inline declarations. Absolute SVG dash lengths are normalized by stroke
width for Direct2D custom-dash semantics and stored through the existing MMIR
style dash range. A native serialization test and a managed sequence-diagram
integration test cover the `-->>` response path.

The managed project selects its native payload from `RuntimeIdentifier`
before consulting `Platform`. Project-reference publishes with no explicit
Platform select the correct PE machine for `win-x86`, `win-x64`, and
`win-arm64` (`0x014c`, `0x8664`, and `0xaa64`). x86 and ARM64 payloads are
cross-built and PE-validated, not executed on an x64 host. The upstream
revision, dependencies, license boundary, C ABI, and MMIR version remain
unchanged.

The build uses stable Rust 1.96.0 (`ac68faa20`), Cargo 1.96.0 (`30a34c682`),
LLVM 22.1.2, and the three `windows-msvc` targets enumerated in
`MERMAN_PROVENANCE.json`. Reproduce it from the package project directory with
`pwsh -NoProfile -File native/Build-Native.ps1`.

Adapter source hashes (SHA-256, UTF-8 with LF line endings):

| File under `native/src/` | SHA-256 |
| --- | --- |
| `lib.rs` | `e4c6cff82e1229c24729b7a033d4bf9c99c53f9084436979939f05b32d0681f0` |
| `directwrite.rs` | `7dddf7adfe94358c69dbd9c039ff87c7c22292a91de1fb7e400ea81b421d547f` |
| `scene.rs` | `180c71010fede2e976da108eccd364b94d451d2a024d3711ba5c4d62b48dd08b` |
| `scene_tests.rs` | `fc1934ab22e5ba8f4bbbfdefdd095ee5338c04533026f44c2aedfe1bda0a86e4` |

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
polygon/polyline paint passes, and an end-to-end decision diamond.

Theme parity references: Mermaid 11.16.1
[default](https://github.com/mermaid-js/mermaid/blob/mermaid%4011.16.1/packages/mermaid/src/themes/theme-default.js),
[dark](https://github.com/mermaid-js/mermaid/blob/mermaid%4011.16.1/packages/mermaid/src/themes/theme-dark.js),
[flowchart styles](https://github.com/mermaid-js/mermaid/blob/mermaid%4011.16.1/packages/mermaid/src/diagrams/flowchart/styles.ts),
[sequence styles](https://github.com/mermaid-js/mermaid/blob/mermaid%4011.16.1/packages/mermaid/src/diagrams/sequence/styles.js),
and [pie styles](https://github.com/mermaid-js/mermaid/blob/mermaid%4011.16.1/packages/mermaid/src/diagrams/pie/pieStyles.ts).
Windows role pairing follows Microsoft's
[contrast-theme guidance](https://learn.microsoft.com/windows/apps/design/accessibility/high-contrast-themes).

No upstream source patch, C ABI change, MMIR version change, dependency change,
or license-boundary change was introduced. ELK remains excluded. Adapter
source hashes (SHA-256, UTF-8 with LF line endings):

| File under `native/src/` | SHA-256 |
| --- | --- |
| `lib.rs` | `9e64e2b9e7fdc084647d653063e2d13c739af467210bf08bd2c0c52f76ca4713` |
| `directwrite.rs` | `7dddf7adfe94358c69dbd9c039ff87c7c22292a91de1fb7e400ea81b421d547f` |
| `scene.rs` | `3fa0daed7c6185b143b1d11a8000e3d150b8f65289b4763cd7acfa4ae573ac89` |
| `scene_tests.rs` | `be32a84e1524bae303d37ccac973e7969fae5d28a281680102e162d19ff8e8cf` |

## 2026-10-14 — final-model-owned flowchart source provenance (patch 0001)

Two distinct nodes with the same Arabic/Latin/emoji label both mapped to the
first authored occurrence. The adapter's text search cannot identify which
declaration produced a rendered node, especially after duplicate declarations,
ignored edge-ID collisions, or preprocessing. This is a source-association
defect, not a font/rendering defect. No upstream issue or pull request has been
filed for this local repair.

- Preserve the winning merged node's label-selection span (or bare authored ID
  span) in its existing typed render context, keyed by canonical node ID.
- Remap those UTF-8 byte ranges to the original caller source in the existing
  controlled parse pipeline, using its exact preprocessing map. Inexact ranges
  and replaced shape-data labels stay absent rather than borrowing other text.
- Emit the escaped canonical node ID and optional numeric source range on the
  actual SVG node wrapper. This adds no visible geometry and does not parse
  generated SVG IDs or replay editor-fact order.
- Validate nonempty, ordered, in-bounds, scalar-boundary ranges in the adapter.
  Preserve authoritative ranges during later spatial label association. An
  explicit model node without an exact range uses the existing whole-diagram
  source fallback, not a guessed occurrence. Other diagram families retain
  their existing behavior; this repair does not claim exact provenance for
  every family.

`native/patches/MERMAN_SOURCE_PATCHES.json` records the unchanged base commit and
tree plus the upstream and patched SHA-256 of every changed source/test file.
`Prepare-MermanSource.ps1` verifies those inputs, applies the patches to a clean
independent checkout, and revalidates cached sources before every build. It
rejects extra tracked, staged, untracked or ignored changes; it does not reset
or delete an existing checkout to make it pass. The preparation test covers
both cache reuse and tampering rejection. Patch SHA-256:
`0462b167a27507aa4fc8f93c9ec19054afb84589ab50ee354551f5139137c21a`.

Cargo's lock records six path-sourced crates from that verified checkout; the
dependency inventory explicitly ties them back to the same manifest. The actual
196-package metadata closure has unchanged names, versions and licenses. Only
`merman-core` and `merman-render` have source modifications. The MIT choice,
existing notices, no-ELK feature boundary, static CRT, C ABI and MMIR 1.1 schema
remain unchanged. The source patches and manifest are included in NuGet
provenance.

## 2026-10-14 — flowchart entity and final-literal text boundary (patch 0002)

Authored Mermaid character entities painted as literal HTML references, and the
managed adapter stripped encoded literal angle-bracket text as if it were raw
formatting markup. Patch `0002-flowchart-svg-text-entities.patch` retains a
separate SVG-text source and source mode through layout, wrapping, metrics and
final emission for node, edge, subgraph, shape-data and swimlane labels. Raw
presentation wrappers and line breaks are classified before entity decoding.
HTML/Markdown routes retain their existing sanitized source. No upstream issue
or pull request is filed by this local repair; Mermaid issue 7015 documents the
upstream non-HTML-label entity problem.

Typed SVG text is marked final for the native flattener. Legacy raw formatting
classification moves before native DirectWrite measurement and MMIR emission;
MMIR and the managed adapter then carry final literal text without another
markup/entity interpretation. The private SVG attribute does not change the
C ABI, MMIR record/schema, or public managed API. The additional retained
source buffers are charged by the existing parsed-resource ledger. Patch
SHA-256: `653b355a18561801310361c64a6ba5d7e6ede9b715d4067fff68a3c5fc957fb1`.
Existing dependency versions, licenses, MIT choice and no-ELK boundary are
unchanged.

A related managed semantic-text change keeps distinct semantic records with
equal names instead of collapsing them through a global string set, and named
semantic labels suppress their owned shaping-run fragments.

## 2026-10-14 — Markdown final text and self-loop label ownership (patch 0003)

Patch `0003-final-markdown-text-and-self-loop-labels.patch` addresses the
default-Markdown shape-data path separately from plain SVG labels. Markdown
syntax and raw line breaks are classified before a one-time full entity decode
of text fragments. Measurement, wrapping and SVG emission share that final-text
mode, and the private final-text SVG marker prevents the native flattener from
reinterpreting decoded literal tags. Styled-word tokens retain whether the
source supplied a separator, including mixed delimiter runs such as adjacent
bold and italic text.

Self-loop layout helpers resolve the original model edge owner instead of
looking up their synthetic helper ID as authored SVG text provenance. The
actual edge's label therefore supplies both measured geometry and emitted text.
The adapter also keeps root SVG title metadata literal after XML decoding;
metadata is not raw presentation markup.

Patch SHA-256:
`514fe9600c258e904f38b06e0a328b5c4aa57fd3c8a3b7da5264ab26ba23a569`.
No upstream PR is filed. The pinned revision, dependencies, license choices,
ELK exclusion, static CRT, C ABI and MMIR schema are unchanged.

The managed copy projection walks actual text commands in order, emits each
explicit non-root owner label at its first text command, preserves distinct
repeated owners and actual unnamed/root text, and excludes shape-only
accessibility aliases from body copy.

## 2026-10-14 — entity-derived whitespace (patch 0004)

Patch `0004-final-markdown-entity-whitespace.patch`: an entity-derived ASCII
space must become a wrapping word boundary after Markdown classification, not
stay inside one measured token. Style and authored separator provenance remain
intact. Its SHA-256 is
`ec5b494b7c51adbe47cb233ef6d7becdd146dc14286734b08c01807dfa05dec4`.
No upstream PR, revision/dependency/license change, ELK enablement, C ABI or MMIR
schema change is introduced.

## 2026-10-14 — explicit generated-entity ownership across diagram families (patch 0005)

Patch `0005-generated-entity-origin-projection.patch` separates Mermaid-generated
entities from authored Unicode that happens to look like private marker text.
Preprocessing records exact generated-entity ranges, carries them through
source-edit mapping, lexing and YAML label decoding, and retains a distinct
typed render projection. The public semantic label stays ordinary final text.
YAML aliases retain their identity; raw and decoded authored private-use values
cannot claim ownership. Temporary origin tokens are chosen against both raw and
decoded values, use bounded allocation, and observe cancellation. The
retained-resource ledger charges the projections and origin metadata.

An owned generated atom is opaque during Markdown/HTML classification. Only
after classification is its Unicode value expanded. Ownership and HTML entity
interpretation are independent: ordinary helpers never expand private frames;
typed SVG text preserves unrelated authored HTML entity spellings; typed HTML
applies its HTML entity semantics before expanding owned atoms. Measurement,
wrapping, shape recomputation, edge intersections, viewbox bounds and emission
carry the same explicit source mode. The common shape-metrics helper takes the
existing typed label state so callers cannot omit ownership. Encoded literal
tags remain text, while generated icon punctuation is considered after
expansion.

- The shared source-projection helper takes the label kind and exact origin map
  for nodes, edges and subgraphs. Markdown (including the production
  `htmlLabels=false` path) uses opaque atoms until classification is complete;
  non-Markdown and direct legacy parser callers retain their prior projection.
- Swimlane intentionally adapts its edge label to an ordinary text node. An
  explicit internal plain-text projection mode makes measurement, wrapping,
  cached preparation and SVG emission consume the same `(source, mode)` pair,
  without enabling Markdown formatting on that label.
- Sequence, State, Mindmap and Class relation labels carry the same
  parser-owned origins and share a bounded formatting classifier with
  Flowchart. Sequence accessibility title/description slots are included.
  Origin-protected State text is sanitized before layout/Markdown so authored
  image event handlers cannot bypass sanitization.
- The `resvg_safe` foreignObject fallback propagates the final-text marker from
  SVG label ancestry to its generated overlay, decodes ordinary HTML entities
  exactly once, and preserves literal backslash-`n`. Unmarked legacy SVG keeps
  its existing placeholder/entity and newline behavior.
- The native adapter joins Class relationship paths/captions by explicit
  canonical identity, excludes `display:none` content from paint/semantics, and
  publishes literal root descriptions without mistaking descendant titles for
  root metadata.

Patches 1–4 are unchanged. Patch 5 SHA-256 is
`89431192103991613400a47b722e352a8f9e8faad30949469ae74641e3eb2ec5`.
The schema-2 manifest records upstream/final byte hashes for 129 modified files
and 3 added ordinary modules; its raw SHA-256 is
`be5cb0eef0bc11be1ae1be4c1f4db645f49a4db93cef28cde00743a8f4a0c639`.
The ordered patch-set SHA-256 is
`f32de3178414b1fcaf5f95b4d66a30bd020bb56631d0d1e59b1a11c4812613bb`.
New modules have an explicit `add` kind and null upstream hash, must be absent
at the pinned base, and may only use existing ordinary parent directories.
Exact Git status, raw-byte, patch mode and cache checks remain enforced; patch
summaries are inspected at the containing Git root so nested checkouts cannot
hide changes.

No upstream issue or PR is filed by this local repair. The upstream revision,
196-package dependency closure, MIT license selection/notices, excluded ELK
boundary, static CRT, C ABI and MMIR schema are unchanged. The
`merman-bindings-core` change is a regression test only; there is no new
shipped crate, license, native feature or public managed API change.

Build tooling: `native/Build-Native.ps1` obtains Cargo's effective
`target_directory` from metadata before copying binaries, honoring environment
and Cargo config redirection, and the preparation/build scripts accept an
optional `PreparationRoot` with the script-rooted default preserved. The
selected location goes through identical raw-hash verification. The NativeAOT
deployment smoke requires an available engine and a successful, nonempty,
non-fallback scene with no error diagnostics; the same executable without the
native DLL must return 1.

Parity evidence: the x64 payload built from this patch set passes the full
public Mermaid suite in Debug and Release with no failures or skips, plus the
private parser, renderer and source-preparation suites. x86/ARM64 are
cross-built and PE-verified, not runtime-tested. This is not live UI/DPI
qualification.

## 2026-10-26 — path-remapped rebuild

The source and patch set are unchanged. `native/Build-Native.ps1` now passes
`--remap-path-prefix` for the native checkout, the prepared Merman source, the
Cargo home and the local rust-src. Without it, panic-location metadata embedded
the build machine's user-profile and temporary paths in the shipped DLLs. The
three DLLs were rebuilt with Rust 1.96.0 and contain no machine-specific paths;
the native unit tests and the 259 Mermaid.Tests cases pass.

| RID | Bytes | SHA-256 | PE machine |
| --- | ---: | --- | --- |
| win-x64 | 16,772,608 | `1c49398444f12f6920a12f5b05297832ce3d83bc514a21d8d76b49fd22942045` | `0x8664` |
| win-x86 | 12,197,376 | `a90dd9e531882c180e43bf6d6f7242eddc1287bd85e462f41f35189961f15f1d` | `0x014c` |
| win-arm64 | 13,550,592 | `9f3d77ce0f16ab7bfab14a8996d8e841d9f37a4c78f263debb7c0236a4a38eb8` | `0xaa64` |
