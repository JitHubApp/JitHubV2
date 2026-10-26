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

## 2026-10-14 — final-model-owned flowchart source provenance

Downstream correctness defect P2-004 reproduces two distinct nodes with the same
Arabic/Latin/emoji label both mapping to the first authored occurrence. The
adapter's text search cannot identify which declaration produced a rendered
node, especially after duplicate declarations, ignored edge-ID collisions, or
preprocessing. This is a source-association defect, not a font/rendering defect.
No upstream issue or pull request has been filed as part of this local repair.

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
tree plus the upstream and patched SHA-256 of all nine changed source/test
files. Its reviewed patch is `0001-final-node-source-provenance.patch`.
`Prepare-MermanSource.ps1` verifies those inputs, applies the patch to a clean
independent checkout, and revalidates cached sources before every build. It
rejects extra tracked, staged, untracked or ignored changes; it does not reset
or delete an existing checkout to make it pass. The preparation test covers
both cache reuse and tampering rejection.

Cargo's lock now records six path-sourced crates from that verified checkout;
the dependency inventory explicitly ties them back to the same manifest. The
actual 196-package metadata closure has unchanged names, versions and licenses.
Only `merman-core` and `merman-render` have source modifications. The MIT choice,
existing notices, no-ELK feature boundary, static CRT, C ABI and MMIR 1.1 schema
remain unchanged. The source patch and manifest are included in NuGet provenance.

Validation receipts and final runtime-payload hashes will be recorded below
after the rebuilt native and managed integration suites complete. This entry
does not qualify live selection, UIA, DPI, or all diagram families.

## 2026-10-14 — flowchart entity and final-literal text boundary

P2-008 reproduces authored Mermaid character entities painting as literal HTML
references. P2-009 separately reproduces the managed adapter stripping encoded
literal angle-bracket text as if it were raw formatting markup. Patch
`0002-flowchart-svg-text-entities.patch` retains a separate SVG-text source and
source mode through layout, wrapping, metrics and final emission for node,
edge, subgraph, shape-data and swimlane labels. Raw presentation wrappers and
line breaks are classified before entity decoding. HTML/Markdown routes retain
their existing sanitized source. No upstream issue or pull request is filed by
this local repair; Mermaid issue 7015 documents the upstream non-HTML-label
entity problem.

Typed SVG text is marked final for the native flattener. Legacy raw formatting
classification moves before native DirectWrite measurement and MMIR emission;
MMIR and the managed adapter then carry final literal text without another
markup/entity interpretation. The private SVG attribute does not change the
C ABI, MMIR record/schema, or public managed API. The additional retained
source buffers are charged by the existing parsed-resource ledger. The exact
21-file upstream/final byte hashes and ordered patch-set identity are in the
source manifest. Existing dependency versions, licenses, MIT choice and
no-ELK boundary are unchanged.

The immutable two-patch source preparation passes all 24 tampering/ownership
checks, and its product native suite passes 40/40 with zero ignored tests.
The static-CRT x64, x86 and ARM64 rebuilds finish successfully; PE machines are
`0x8664`, `0x014c`, and `0xaa64`. Matching x64 native-renderer tests pass
3,034/3,034. The broader Mermaid integration receipt remains red, including
known literal-marker, self-loop and default shape-data cases; this is not a
whole-feature qualification. Independent review identifies a self-loop layout-helper
identity gap, and P2-010's authored Unicode delimiter collision remains open.
Neither is waived or hidden by the new patch. Their fixes must receive a new
reviewed source snapshot and matching native rebuild before final qualification.

P2-011 is a separate managed semantic-text repair: distinct semantic records
with equal names no longer lose occurrences to a global string set, and named
semantic labels suppress their owned shaping-run fragments. Its focused 18-case
adapter/public-engine result passes; broader validation remains separate.

## 2026-10-14 — Markdown final text and self-loop label ownership

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

The cumulative manifest covers 24 upstream/test files and three ordered patches.
Patch 3 SHA-256 is
`514fe9600c258e904f38b06e0a328b5c4aa57fd3c8a3b7da5264ab26ba23a569`;
the ordered patch-set SHA-256 is
`928ac1c31eefdf84982db973b251abe95259cfa2a46e0bd65b8a632867934d6a`.
Source preparation passes all 24 checks. Product/rebuilt-payload validation is
still running at this checkpoint, and independent review identifies a further
encoded-whitespace wrapping case (P2-012). No complete-feature claim is made.
No upstream PR is filed. The pinned revision, dependencies, license choices,
ELK exclusion, static CRT, C ABI and MMIR schema are unchanged.

The separate P2-011 managed copy projection now walks actual text commands in
order, emits each explicit non-root owner label at its first text command,
preserves distinct repeated owners and actual unnamed/root text, and excludes
shape-only accessibility aliases from body copy. All 23 focused managed cases
pass. This supersedes the earlier 18-case intermediate receipt, not live
clipboard or all-family qualification.

## 2026-10-14 — entity-derived whitespace and four-patch publication

Patch `0004-final-markdown-entity-whitespace.patch` fixes P2-012: an entity-derived
ASCII space must become a wrapping word boundary after Markdown classification,
not stay inside one measured token. Style and authored separator provenance
remain intact. Its SHA-256 is
`ec5b494b7c51adbe47cb233ef6d7becdd146dc14286734b08c01807dfa05dec4`.
The cumulative 24-file manifest SHA-256 is
`76d356bcc6afd42c5ceac10686f3b5e9b12c730af31012e84903054889242f22`;
the ordered four-patch identity is
`b08b9717e4d9b973165e3483fb16995458cd97f1066321356e2c5fc72661d19e`.
No upstream PR, revision/dependency/license change, ELK enablement, C ABI or MMIR
schema change is introduced.

The immutable source passes 24/24 preparation/tamper tests and 29/29 individually
discovered upstream regressions. The product native suite passes 41/41, with no
ignored tests. All three static-CRT RID builds finish through `Build-Native.ps1`.
The first publication attempt encounters a mapped-file copy failure after ARM64
compilation; a copy retry succeeds and the standard complete build rerun exits
zero. Eight compiler warnings remain visible; this is not a warning-free claim.

| RID | Bytes | SHA-256 | PE machine |
| --- | ---: | --- | --- |
| win-x64 | 16,334,848 | `f67db73dd73855e216ff62ffb02ce5af19d16abd4c802f3a38a24904c4752a16` | `0x8664` |
| win-x86 | 11,928,576 | `9520f739e9ac14fae2914c830445b4104694d2d16d4fe9dd2bc61aa3c22adddd` | `0x014c` |
| win-arm64 | 13,234,176 | `1274fa65bc039700ea455f7c0bc3db280abb900240116677e48e7ab6ebf52654` | `0xaa64` |

Matching x64 native-renderer coverage passes 3,034/3,034 with zero skips. The
unfiltered managed Mermaid suite initially records 174 PASS / 4 FAIL / 0 SKIP
from 178 cases: the independent plain/bold whitespace geometry comparisons now
pass, while the four P2-010 authored-marker collisions remain. Four additional
YAML-origin collision regressions reproduce the same defect, leaving the latest
full receipt at **174 PASS / 8 FAIL / 0 SKIP**, 182 total. The eight failures are
not waived, and no complete-feature or release qualification is claimed.
Cross-built x86/ARM64 payloads are not runtime-qualified on this x64 host.

## 2026-10-14 — explicit generated-entity ownership and final text policies

Patch `0005-generated-entity-origin-projection.patch` attempts to repair flowchart P2-010
without treating an authored Unicode spelling as private metadata. Preprocessing
records exact generated-entity ranges, carries them through source-edit mapping,
lexing and YAML label decoding, and retains a distinct typed render projection.
The public semantic label stays ordinary final text. YAML aliases retain their
identity; raw and decoded authored private-use values cannot claim ownership.
Temporary origin tokens are chosen against both raw and decoded values, use
bounded allocation, and observe cancellation. The retained-resource ledger
charges the projections and origin metadata.

An owned generated atom is opaque during Markdown/HTML classification. Only
after classification is its Unicode value expanded. Ownership and HTML entity
interpretation are independent: ordinary helpers never expand private frames;
typed SVG text preserves unrelated authored HTML entity spellings (P2-013);
typed HTML applies its HTML entity semantics before expanding owned atoms.
Measurement, wrapping, shape recomputation, edge intersections, viewbox bounds
and emission carry the same explicit source mode. The common shape-metrics
helper takes the existing typed label state so callers cannot omit ownership.
Encoded literal tags remain text, while generated icon punctuation is considered
after expansion. The original whitespace/strong-style/SVG metric regressions
from patch 4 are retained unchanged alongside the new cases.

No upstream issue or PR is filed by this local repair. The upstream revision,
196-package dependency closure, MIT license selection/notices, excluded ELK
boundary, static CRT, C ABI and MMIR schema are unchanged. The final incremental
patch has 59 changed files and SHA-256
`8366a14fbe007dd4ba6da2f2d842a4c1bf0ce8e6533a3bfa7af6e520ec6a6e2c`.
The cumulative manifest records upstream/final byte hashes for all 61 modified
files; its SHA-256 is
`4d982714b6a45667db2697bc7222fceecd04cb8bc9e7226600c5e37a1f32e013`.
The ordered five-patch identity is
`873895d2868f1be9f40cc3f6838393e53e319664c57b8feb3a250b6e8d7ce208`.

The frozen private source passes core-lib 1,548/1,548, render-lib 1,198/1,198,
flowchart SVG integration 80/80 and label-measurement integration 3/3. Exact
changed-Rust-source formatting and diff checks pass; a workspace-wide formatting
attempt encounters Windows path-length limits in generated LALRPOP output, so
it is not described as a passing workspace formatting command. The final
source-preparation suite passes all 24 ownership/tampering checks after fixing
ordinal path ordering, without modifying the shared Cargo checkout. The
individually discovered source gate passes 67/67. Product native tests pass
41/41. The first x64 payload copy encountered a transient mapped-file error;
the unchanged cached build/copy retry succeeds without terminating any app.
The x64/x86/ARM64 static-CRT rebuild completes on that unchanged retry. This is
build evidence only; x86/ARM64 runtime behavior is not qualified on this host.

**Public-pipeline qualification fails.** The rebuilt x64 payload SHA-256 is
`f4276051eee94043adb79c52b9c843aedd1c31331c215e4cdb8b2de091091874`.
Both full, unfiltered managed configurations record **176 PASS / 30 FAIL /
0 SKIP**, 206 total. Default shape-data generated entities leak placeholder
spelling, including all eight previously passing shape-geometry guards.
Receipts `mermaid-final-origin-projection-release.trx` and
`mermaid-final-origin-projection-debug.trx` are retained under
`artifacts/phase2-20261014`; this candidate is not qualified for use or release.
The core integration Release suite separately passes 3,070/3,070 on this x64
payload; it does not override the failing optional-feature assertions.
`artifacts/phase2-20261014/merman-origin-candidate-qualification-failed.json`
records all three exact payload sizes, PE architectures and SHA-256 values.
The focused source suites do not substitute for the public facade's actual
parse/render route. Investigation must preserve the full managed assertions
and close that integration gap before a new immutable patch is registered.

This is a flowchart repair, not all-family qualification: independent public
regressions reproduce the authored-marker collision in sequence, state, mindmap
and class diagrams. Those four P2-010 cases remain open. Live UIA, selection,
clipboard, DPI and non-x64 runtime gates remain separate.

## 2026-10-14 — SVG-mode correction of the origin projection

The first five-patch candidate above is retained as failed evidence, not a
qualified runtime. Its generated-entity SVG source used legacy placeholders,
but the final Markdown consumer requires typed opaque atoms. Default HTML-label
facade tests did not exercise the production `htmlLabels=false` path. The same
defect is independently reproduced for default shape-data nodes, Markdown
edges, Markdown self-loops and Markdown subgraphs.

The corrected shared source-projection helper takes the label kind and exact
origin map for all three owners. Markdown uses opaque atoms until classification
is complete; non-Markdown and direct legacy parser callers retain their prior
projection. The node-specific quote cleanup is preserved. The facade regression
checks actual label DOM text in each HTML/SVG mode, not a string that might
appear only in metadata; the product adapter separately checks exact MMIR paint
and semantic names. All original source and public assertions remain required.

The replacement fifth patch is 375,291 bytes with SHA-256
`bafd5d2a1c73c98b97d4a30eeb5b1c74e7283acc05a54088dc98f71a6c653305`.
It changes 60 files incrementally from the immutable four-patch source. The
cumulative 62-file manifest SHA-256 is
`cc5c20f7de2b4b391ec1cb7b10ded70b914604fe0274deb75bdf0958a87a8356`;
the ordered five-patch identity is
`c1ba92288d8076d3361b1bbf4a429a592d418200d31cac1dbd8e4eb912613407`.
The upstream commit/tree and Cargo manifests/lock remain unchanged. The new
`merman-bindings-core` change is a regression test only; there is no new shipped
crate, license, native feature, C ABI, MMIR or public managed API change.

The frozen private source reports core-lib 1,549/1,549, render-lib 1,198/1,198,
flowchart SVG integration 81/81 and binding-facade SVG-lib 205/205. Prior archived
logs with 1,548/80 counts predate these last edits and do not substantiate the
new counts. The exact manifest independently passes all 24 preparation/tampering
checks without changing the shared Cargo checkout. The expanded individually
discovered source gate independently passes **70/70**, with its owned temporary
tree removed and the shared Cargo checkout unchanged. Full observed output and
machine-readable receipts are preserved under `artifacts/phase2-20261014/` as
`merman-final2-source-gate.log`, `merman-final2-source-gate.json` and
`merman-final2-preparation.json`. The product 42-test suite, all-RID static-CRT
builds and 211-case managed suite still require matching fresh receipts.

The work drive has less than 5 GB free. Fresh native build intermediates are
placed in an owned C-drive temporary directory rather than deleting existing
files. `Build-Native.ps1` now obtains Cargo's effective `target_directory` from
metadata before copying binaries, honoring both environment and Cargo config
redirection; default output behavior is unchanged. No app is launched or stopped.

The matching x64 payload is 16,372,736 bytes, SHA-256
`f3c8cdedb086fd86bd60120c10a64849eb045521827eb5fedf4f8e859143b0ef`.
Product-native tests pass **42/42**. The complete public suite in both Debug
and Release is nevertheless **206 PASS / 5 FAIL / 0 SKIP**, 211 total. The
flowchart SVG-mode regressions pass, but the shared change exposes opaque atoms
on swimlane Markdown edge labels. The four non-flowchart authored-marker
collisions remain. This candidate is not qualified. Receipts are preserved as
`mermaid-final2-release.trx`, `mermaid-final2-debug.trx` and
`merman-final2-public-qualification-failed.json` under the same artifact root.
Strengthened paint/copy assertions and four additional swimlane owner cases
give **11 PASS / 2 FAIL**: normal and self-loop Markdown edges fail; nodes,
subgraphs and shape-data labels pass. The expanded full suite has 215 cases;
none are skipped or relaxed for the next correction.

The all-RID final2 build exits zero through the effective C-drive Cargo target.
Full console output is retained as `merman-final2-native-build.log`, SHA-256
`8360b1be8d566432775978556bea8586e74ed0b03bab43c8e7dd09d666bea81c`;
`merman-final2-native-build.json` records the exact product/test scope.

| RID | Bytes | SHA-256 | PE machine |
| --- | ---: | --- | --- |
| win-x64 | 16,372,736 | `f3c8cdedb086fd86bd60120c10a64849eb045521827eb5fedf4f8e859143b0ef` | `0x8664` |
| win-x86 | 11,944,960 | `b5c9057761816f08527f49b11a6d48010e8f854f7ae87b8503630d6025adfa98` | `0x014c` |
| win-arm64 | 13,267,968 | `421b0315ce88c54271f3431922d9ff5e289a14051153b9a8f46c9cd58949bb05` | `0xaa64` |

This is reproducible failed-candidate evidence, not feature qualification.
x86/ARM64 are cross-built and PE-verified only. A product-native swimlane
edge/self-loop regression is added after this frozen 42-test build; its new
count must not be backdated into these receipts.

## 2026-10-14 — Swimlane plain-text projection correction

P2-014 is a product-visible regression: a parser-owned Markdown label uses
opaque entity atoms, but Swimlane intentionally adapts its edge label to an
ordinary text node. Measurement used a sanitized projection while painting
sent opaque text through the legacy placeholder decoder. The normal edge and
self-loop expose the private atom instead of the intended character.

The repair adds an explicit internal plain-text projection mode. Measurement,
wrapping, cached preparation and SVG emission consume the same `(source, mode)`
pair. Generated atoms are finalized once without rescanning decoded values;
authored private frames remain escaped literal text. The mode does not enable
Markdown formatting on the intentionally plain Swimlane label. Authored HTML
entity spelling, generated Mermaid entities and the existing HTML-label branch
have separate exact regressions. No public API, FFI, MMIR, dependency, license,
upstream pin or ELK boundary changes.

The corrected fifth patch is 400,190 bytes, SHA-256
`a241bdcf8c99d49668d7db79478d3a43ec5442726680398ae234730b07bc80ce`.
It changes 61 paths from the immutable four-patch source; the cumulative
63-file manifest SHA-256 is
`50db012299dc570a07f938fe551f216ed93e477547c4628eec1ba0b659d7fb69`,
and the ordered patch-set identity is
`15775d7cda6edb55af33333343c2678e45a426d279b5e875308388c720243c58`.
Root independently verifies clean application, all 63 raw upstream/final source
hashes and the unchanged base HEAD. The frozen private renderer library passes
1,203/1,203 and the unfiltered flowchart SVG integration target passes 83/83;
full raw logs are `marker-origin-swimlane-final-render-lib.log` and
`marker-origin-swimlane-final-flowchart-svg-test.log` in the October 14 artifact
root. Direct rustfmt checks of changed non-generated Rust and `git diff --check`
pass; whole-workspace fmt still cannot traverse Windows-overlong fixture paths.

Preparation independently passes all **24/24** ownership/tampering checks for
this exact identity. The source gate retains every original case and adds seven
explicit projection/metrics/SVG regressions, raising the required count to **77**.
Its fresh result, product-native **43** tests and unfiltered public **215** cases
are now observed: **77/77** source cases (including every original 70 identity),
**43/43** product-native cases and **211 PASS / 4 FAIL / 0 SKIP** in each full
Debug/Release x64 public suite. All 13 Flowchart/Swimlane paint/semantic-copy
routes pass, including both formerly failing Swimlane Markdown edge routes.
The remaining failures are P2-010's Sequence, State, Mindmap and Class authored
marker collisions; this is not whole-feature qualification.

All three static-CRT native builds finish with exit 0 and PE-verified payloads:

| RID | Bytes | SHA-256 | PE machine |
| --- | ---: | --- | --- |
| win-x64 | 16,397,824 | `c0c7bbed9ca0503b0ff59e66ea5ee0d43d9e50630ae23c68a9e0c53358720c45` | `0x8664` |
| win-x86 | 11,968,512 | `6c97e260a4eb88e25c5110a9c0bdc438c8fced323d423c7659abac84ea8e66cc` | `0x014c` |
| win-arm64 | 13,292,544 | `f5b0863ae332f7ff453b4c4b96872be28e9ce4d33503a9c3699f4aa406b74a40` | `0xaa64` |

x86/ARM64 are cross-built only. The actually executed x64 public test output
directories contain the matching `c0c7bbed...` DLL; the stale nested `win-x64`
publish output is not an executed candidate. A fresh full Release core renderer
suite passes **3,070/3,070** with this payload. Exact TRX hashes, failed case
identities, build logs and non-claims are preserved in
`artifacts/phase2-20261014/merman-swimlane-product-qualification.json`.
A source-only or partial-suite pass is not promoted to whole-product success.

The work drive is nearly full after a test build mistakenly used an E: cache.
No cache or source is deleted. Native preparation/build scripts now accept an
optional `PreparationRoot` and preserve the existing script-rooted default;
the selected C: location still goes through identical raw-hash verification.
Both prepared source and Cargo intermediates for fresh verification are on C:.
No app launch, display change, physical input or live DPI pass is implied.

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

## October 14 four-family correctness follow-up — qualification in progress

The fifth downstream patch now carries parser-owned entity origins through
Sequence, State, Mindmap and Class relation labels, sharing a bounded formatting
classifier with Flowchart. Authored marker-shaped Unicode remains literal;
generated entities are finalized after syntax classification, and layout and SVG
consume the same owned projection. Sequence accessibility title/description
slots are included, with a separate legacy serialization control.

Patches 1–4 remain byte-identical. The replacement fifth patch SHA-256 is
`89431192103991613400a47b722e352a8f9e8faad30949469ae74641e3eb2ec5`.
The schema-2 manifest has **129 modified files and 3 added ordinary modules**;
its raw SHA-256 is
`be5cb0eef0bc11be1ae1be4c1f4db645f49a4db93cef28cde00743a8f4a0c639`.
The ordered patch-set SHA-256 is
`f32de3178414b1fcaf5f95b4d66a30bd020bb56631d0d1e59b1a11c4812613bb`.
New modules have an explicit `add` kind and null upstream hash, must be absent
at the pinned base, and may only use existing ordinary parent directories.
Exact Git status, raw-byte, patch mode and cache checks remain enforced.

The main native adapter also joins Class relationship paths/captions by explicit
canonical identity, excludes `display:none` content from paint/semantics, and
publishes literal root descriptions without mistaking descendant titles for
root metadata. Its pre-rebuild component suite passes 54/54. The private parser
suite passes 1,570/1,570; combined math/Cytoscape renderer tests pass 1,287/1,287,
Sequence SVG tests pass 53 with one existing 3-pixel root-width residual ignored,
and the corrected XML-text Mindmap assertions pass all 10 feature-enabled cases.
That ignored browser-width comparison is not a new waiver or a correctness
acceptance criterion. The focused source gate retains all previous 77 package
and test identities in order and adds 40 exact regressions. Its final isolated
run passes **117/117**, with zero failures or ignored cases across eight exact
discovery targets. The shared checkout is unchanged and the verified GUID-owned
temporary tree is removed; root independently verifies its absence and the
retained log/result hashes.

The first replacement x64 payload (`dec15047...`, patch-set `f61ee8b1...`)
improves the full public suite from 211/254 to **244/254**, with ten failures
retained. The follow-up corrects the final Mindmap/MMIR boundary marker and
State path/caption identity, and sanitizes origin-protected State text before
layout/Markdown so authored image event handlers cannot bypass sanitization.
Generated markup entities remain literal text. Private Mindmap SVG/library
tests pass 10/10, 25/25 with optional features and 23/23 with default features;
State SVG and label-unit tests pass 22/22 and 3/3 on an explicit C: target.
XML attribute-order-only assertions are replaced with exact decoded text
checks; visible content and configuration expectations are retained. Two new
public simple/composite State copy cases reproduce the old payload's missing
named Edge owner. The next rebuilt x64 payload (`c3fa1473...`, patch-set
`681006a1...`) passes **251/256** full public cases, including both new State
ownership controls. The five remaining failures are all Mindmap final-text
cases; that failed candidate and its complete TRX are retained.

The actual product uses the `resvg_safe` foreignObject fallback, whose root
overlay discarded the original final-text marker and reinterpreted finalized
entities. The final correction propagates that marker from SVG label ancestry
to the generated overlay, decodes ordinary HTML entities exactly once, and
preserves literal backslash-`n`. Unmarked legacy SVG keeps its existing
placeholder/entity and newline behavior. Generated output stays escaped and
under the same budget/checkpoint checks. The new actual-pipeline source test
passes with all 11 Mindmap integration cases; 41 fallback unit cases and two
SVG entity cases also pass. Three additional public controls first reproduce
literal `&quot;`, `<b>` and backslash-`n` corruption on the preceding DLL.
The final registered x64 payload now passes all **259/259 public cases** in both
Debug and Release, with zero failures or skips. Root verifies that every prior
254 test name remains and that both executed output directories contain the
matching native DLL. Private checks are not substituted for this runtime gate.

The first native rebuild exposed Git's subdirectory-prefix filtering of patch
summaries. Inspection now runs at the containing Git root (or in the standalone
export directory); a nested-checkout regression is added to the preparation
suite. The earlier 48-check receipt precedes this final 49-check revision. The
final registered source passes **49/49** preparation checks, preserves the shared
checkout and removes only its verified, GUID-owned test directory.

The product-native suite passes **54/54**, with no ignored cases. All three
static-CRT builds complete successfully and the resulting PE machines match:

| RID | Bytes | SHA-256 | PE machine |
| --- | ---: | --- | --- |
| win-x64 | 16,796,160 | `384aa86f0e8bb8a2db74d054b03e547397670d21b760d199d775b041d2ad0c47` | `0x8664` |
| win-x86 | 12,222,976 | `6fd53fcaaa361a242217e168db961041172dddd983a1607c257a34498b52ef52` | `0x014c` |
| win-arm64 | 13,598,720 | `f94894ff69ecc471c463fc94e082d0e2cee2407e4abf5ad7577210ed3d744284` | `0xaa64` |

x86/ARM64 are cross-built, not runtime-tested. A fresh headless run parses all
500 frozen READMEs with zero source/invariant/disposal failures and all 19
Mermaid scenes present; its two existing rank-340 unsupported-math fallbacks
still yield exit 1. Independent byte/identity verification covers all
18,023,117 source bytes. That runner does not perform layout, image loading,
selection, clipboard or navigation checks.

The full Debug and Release managed integration suites each pass **3,070/3,070**
with zero skips after providing the actual workflow source to the C:-isolated test directory.
The preceding 3,069-pass/one-missing-fixture result remains retained; no assertion
or product source changes are needed for that setup correction.

The matching Release/x64 sample builds with zero warnings/errors and is not
launched. NativeAOT publishes without IL/trim warnings, produces an actual
native PE without CLR metadata, and renders eight vector commands. Its former
deployment smoke accepted an unavailable engine or any render result once the
DLL loaded. The smoke now requires an available engine and successful,
nonempty, non-fallback scene with no error diagnostics. An identical executable
in a fresh directory without the native DLL returns 1 as required; the valid
publish returns 0. The original publish is not modified for this control.

This section records **source and x64 public-pipeline verification**, not release
or live UI/DPI qualification. Exact
payload/TRX/log hashes and retained failed candidates are recorded in
`artifacts/phase2-20261014/merman-four-family-product-verification.json`.
No C ABI, MMIR version, public API, package version, dependency version, or
license-boundary change is introduced.

## Path-remapped rebuild (October 26, 2026)

The source and patch set are unchanged. `native/Build-Native.ps1` now passes
`--remap-path-prefix` for the native checkout, the prepared Merman source, the
Cargo home and the local rust-src. Without it, panic-location metadata embedded
the build machine's user-profile and temporary paths in the shipped DLLs. The
three DLLs were rebuilt with Rust 1.96.0, and the native unit tests pass. The
DLLs now contain no machine-specific paths. Mermaid.Tests pass 259/259, and
GitHub.Tests pass 3,324/3,324.

| RID | Bytes | SHA-256 | PE machine |
| --- | ---: | --- | --- |
| win-x64 | 16,772,608 | `1c49398444f12f6920a12f5b05297832ce3d83bc514a21d8d76b49fd22942045` | `0x8664` |
| win-x86 | 12,197,376 | `a90dd9e531882c180e43bf6d6f7242eddc1287bd85e462f41f35189961f15f1d` | `0x014c` |
| win-arm64 | 13,550,592 | `9f3d77ce0f16ab7bfab14a8996d8e841d9f37a4c78f263debb7c0236a4a38eb8` | `0xaa64` |
