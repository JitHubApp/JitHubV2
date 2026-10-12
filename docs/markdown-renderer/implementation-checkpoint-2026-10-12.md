# MarkdownRenderer implementation checkpoint and architecture assessment

Updated October 12, 2026. MarkdownRenderer remains a preview. The implementation
has advanced substantially, but the performance, full-page fidelity, and complete
release qualification goals are still open. This checkpoint preserves the work
and its known limitations so development can resume from a concrete state.

The detailed chronology, immutable evidence locations, and original performance
requirements are in the [progressive performance plan](progressive-performance-plan.md).
This checkpoint is the concise current handoff; historical passing results do not
certify subsequent source changes.

## Repository state

- Branch: `codex/markdown-renderer-1-0-finalize`.
- Source parent before this checkpoint: `5b653588`.
- Latest app and runner exercised together in Sandbox: `4e961abe`.
- Approved before-change performance reference:
  `1c00c83ddfd2e8b65e05cb57e359a3f092b37271`, the September 28 version.
- [PR 99](https://github.com/JitHubApp/JitHubV2/pull/99) remains open. This local
  checkpoint does not push changes or update the remote PR.
- Scheduled continuation was canceled at the user's request. Further work runs
  locally when requested; release completion has not been declared.

## Implemented capabilities

These are implemented directions and capabilities, not a claim that every syntax,
device, or release combination has passed qualification.

| Area | Implemented behavior |
| --- | --- |
| Document model | Immutable engines and documents, preserved source spans, bounded parse admission and caching, cancellation, and declarative extensions. |
| Viewport ownership | `MarkdownScrollView` owns scrolling; `MarkdownDocumentView` uses an ancestor's viewport. |
| Markdown and GitHub | CommonMark/GFM, tables, task lists, alerts, footnotes, emoji, attributes, and an explicit GitHub README profile. |
| Safe HTML | A bounded native subset with inline styles and cross-block scopes; active web content is rejected. |
| Math | CSharpMath-based vector scenes, inline/display formulas, bounded processing, and accessible source fallback. |
| Mermaid | Native scenes from the selected-RID Merman engine, validation, theme/accessibility behavior, and original-code fallback. |
| Syntax highlighting | Optional TextMate integration with separately selected grammar packages. |
| Images | Host-controlled resolution, security partitioning, asynchronous preparation, caching, aspect-ratio containment, and image-style selection. |
| SVG | Explicitly registered resvg 0.48.1 provider, isolated Rust workers, shared-memory source/raster transport, typed failures, resource limits, and crash containment. |
| WebP | An engine-neutral raster-decoder contract and bounded isolated decoding through the existing worker pool; animated first-frame composition remains open. |
| Progressive work | Opt-in shared performance sessions, priority/admission controls, prefetch and display-sized preparation, stale-generation rejection, and bounded resource ownership. |
| Reflow | Completed image roots can be measured privately and published through immutable placement generations. General fallbacks and document-scale generation work still require optimization. |
| Interaction | Mouse, keyboard, pen, touch selection, native task checkboxes, native menus, clipboard formats, and selection handles. |
| Accessibility | Semantic text, source mappings, UI Automation selection, atomic image/vector content, and hosted-control integration. |
| Packaging | Lean Core/WinUI packages plus explicit optional feature packs, static registration, trimming/AOT checks, pinned native dependencies, and license inventories. |

See [supported syntax](supported-markdown.md), [public API](public-api.md),
[selection](selection-and-clipboard.md), and [package structure](packaging-and-distribution.md)
for consumer-facing detail.

## Runtime architecture and ownership

The WinUI host owns UI integration, device resources, presentation, and UI
Automation. Win2D/DirectWrite performs native text shaping, layout, drawing, and
hit testing. The immutable document and semantic models preserve the source and
selection relationships across lazy realization and asynchronous preparation.

The application explicitly registers optional services. The controls borrow
`SvgRenderer`, `RasterDecoder`, and `PerformanceSession`; the host owns their
lifetime. The builder exposes `WithSvgRenderer`, `WithRasterDecoder`, and
`WithPerformanceSession`. Document-specific work is canceled and retired when
the document or control is replaced. There is no reflection-based provider
discovery.

The Rust worker contains resvg and the bounded WebP decoder. Fixed protocol
messages control shared-memory transactions. Worker failures must produce typed
fallbacks in the application, with no in-process native fallback. Isolation,
resource limits, request identity, cancellation, cache ownership, and disposal
remain part of correctness, even when optimizing throughput.

The worker's pinned source includes reviewed changes to resvg, usvg, tiny-skia,
tiny-skia-path, and image-webp. These changes create an ongoing maintenance and
qualification obligation; an upstream version number alone does not describe
the shipped implementation.

## Changes included in this checkpoint

Primer Light V7 preserves V1 through V6 and gives the new behavior a distinct
version and canonical CSS fingerprint:
`882f63ac35c8f0854485ba797fda1bc8690cbc97bfd5d0fd9c0c961471cb17c4`.
Its table-cell line-height multiplier is 1.5. Inline code uses 85 percent of its
containing typography, including heading, nested link, and script contexts;
later absolute declarations retain precedence. Inline-code chip padding remains
expressed in DIPs and does not yet establish CSS em-padding equivalence.

The opt-in profile aligns inline images around the adjacent font's lowercase-x
middle. Measurement captures per-run x-height and line-baseline scalars. Paint,
selection, and image geometry consume the same captured placement instead of
performing font analysis or repeatedly scanning all line metrics. The ordinary
profile retains its existing placement behavior. The pinned Win2D analyzer's
native object does not support the projected close operation; analysis keeps
the projection local rather than invoking that failing cast.

JitHub selects V7 only in the controlled same-byte audit mode. Corpus generation,
readiness contracts, embedded CSS resources, staging scripts, and tests use the
same version/fingerprint. V6 remains available for older evidence and layered
profile resources. The exported `PrimerLightV7` property is recorded in the
public API baseline.

Added renderer tests cover relative typography, absolute overrides, table-cell
metrics, default-profile behavior, image alignment, RTL, text scale, and
selection after cancellation. One review follow-up is a stronger independent
alignment assertion for images on later wrapped lines; the current multiline
test checks snapshot consistency but not that full geometric oracle.

The 145 already tracked SVG result files changed in the worktree are included
as diagnostic results. They are not relabeled as a freshly qualified Edge
corpus. Generated build caches and the larger local evidence directories remain
on disk and are ignored by Git.

## Validation results

### Current checkpoint checks

- Same-byte corpus tests: 40/40 passed, zero skipped.
- PowerShell syntax checks: both modified Sandbox capture/replay scripts passed.
- Release/x64 renderer focus, including the new profile, typography, image
  placement, and public API baselines: 120/120 passed, zero skipped. The normal
  test build rebuilt all referenced renderer packages and verified source-bound
  worker identities for all three RIDs.
- WinUI readiness and automation source contracts: 85/85 passed, zero skipped.
- Full Release/x64 renderer regression suite: 2,945/2,945 passed, zero skipped,
  including the compiled public API baselines, in 2 minutes 29 seconds.

These checks do not establish a fresh V7 app publish, Sandbox visual pass, or
browser-relative performance result.

Reproduce the focused checks from the repository root:

```powershell
node --test eng/readme-audit/same-byte-corpus.test.mjs
dotnet test MarkdownRenderer/MarkdownRenderer.GitHub.Tests/MarkdownRenderer.GitHub.Tests.csproj -c Release --no-restore -p:Platform=x64 --filter "FullyQualifiedName~GitHubReadmeVisualStyleProfileTests|FullyQualifiedName~RelativeScriptFontSizeTests|FullyQualifiedName~InlineImageCssMiddleAlignmentTests|FullyQualifiedName~PrimerTableCellTypographyTests|FullyQualifiedName~PublicApiBaselineTests"
dotnet test JitHub.WinUI.Tests/JitHub.WinUI.Tests.csproj -c Release --no-restore -p:Platform=x64 --filter "FullyQualifiedName~NativeFirstViewportImagesReadyContractTests|FullyQualifiedName~AutomationHarnessSourceContractTests"
```

The full renderer check runs the first `dotnet test` command without its filter;
this checkpoint used `--no-build` only after the successful fresh package/test
build above. Local TRX receipts are `renderer-focus.trx`, `renderer-full.trx`, and
`host-contracts.trx` in the checkpoint evidence directory.

### Last completed app validation

The frozen `4e961abe` NativeAOT app and matching runner completed ten pinned
README cases in Sandbox. All ten reported no native rendering failure and clean
shutdown. Nine reported zero unavailable images; rank 7 reported one. Text-token
coverage was 1.0 for all ten. Mean matched-viewport SSIM ranged from 0.0810 to
0.7213, so the visual gate failed. SSIM is sensitive to wrapping and displacement;
these values are not percentages of correctly rendered content.

Rank 7's captured browser image had failed decoding and zero bounds, with no
captured asset route. Native screenshots show no visible error placeholder for
that decorative occurrence. However, the diagnostic does not yet prove the
exact source occurrence and final bounds, so the unavailable event remains a
failure. Excluding all occurrences by URL hash would be unsafe and is not an
accepted fix.

The frozen implementation also passed 2,910 renderer tests, 151 release/security
checks, and a clean x64 NativeAOT app/runner publish with payload verification.
An actual worker interop retention run recorded 5,685,248 bytes of aggregate
private growth and no handle growth across 100 cycles. These results apply to
their frozen binaries and do not certify the V7 changes.

The ten-case evidence is under
`MarkdownRenderer/artifacts/goal-release-20261011/offline-fix2-4e961abe-ranks001-010-20261012/`,
including `offline-same-byte-sandbox-summary.json`. The detailed plan preserves
its run identity, source hash, and all failed receipts.

### Top 500 audit history

The audit has been run. An earlier live audit passed 500/500 at
`1831ed123b544a297745f259d4efadd506e8ec3a` in
[Actions run 36361277068](https://github.com/JitHubApp/JitHubV2/actions/runs/36361277068).
That result is valid for that run's checks and inputs.

The later strict same-byte audit
[36501560943](https://github.com/JitHubApp/JitHubV2/actions/runs/36501560943)
failed: nineteen shards failed and one succeeded. That job-level result does
not prove that every individual case completed. The latest ten-case batch is a
targeted diagnostic rerun. The required current-candidate full 500-case quality
and performance qualification remains open.

### Performance and deployment limits

The full physical before/after benchmark has not qualified. Its reference leg
failed measurement stationarity and other requirements before all four legs
completed. A separate full candidate diagnostic also failed; it is not a
replacement counterbalanced comparison.

That diagnostic recorded scroll-frame p95 of 8.8389 ms against 8.3333 ms,
measured renderer scroll-work p95 of 0.1996 ms, and a 10 MiB publication maximum
of 5.0821 ms against 2 ms. It does not establish the scheduler/compositor/GPU
cause of the frame misses. Renderer language or GC cannot be assumed to be the
primary cause from these observations.

The display driver exposes 60 Hz and 240 Hz at the tested 5120 by 1440 mode;
the approved temporary 120 Hz attempt was rejected by the driver. No monitor
setting was changed. Benchmark environment qualification needs resolution.

Package budgets also remain open. This checkpoint's freshly built Core/WinUI
managed DLLs total 1,865,216 bytes against the existing 1,258,291-byte cap
(178,176 Core bytes and 1,687,040 WinUI bytes). The frozen x64 worker
alone is 3,751,936 bytes before adding its provider, exceeding the selected
deployment allowance of 3.5 MiB. The all-RID compressed package gate has not
been closed. Size experiments that caused substantial performance regressions
were rejected rather than accepted as progress.

## Remaining work in priority order

1. Close representative complete-page fidelity failures: mixed inline/block
   flow, table typography and containment, relative code sizing/padding,
   inline-image placement, and complete superscript/subscript run positioning.
   OpenType glyph substitution alone does not position an ordinary phrase
   correctly, and an atomic replacement would regress wrapping and selection.
2. Profile and reduce document-scale preparation, placement generation, fallback
   reflow, semantic projection, allocation, and upload copying. Target the costs
   of the failing complete documents rather than treating microbenchmark gains
   as page-load improvements.
3. Publish a matching app/runner and use the already approved fresh Sandbox batch
   to measure the V7 changes. Complete same-byte Edge performance qualification
   and the full counterbalanced release benchmark without removing failed
   samples or weakening thresholds.
4. Complete the current-candidate 500-case same-byte audit with full traversal,
   content coverage, unavailable-image accounting, and visual/performance gates.
5. Close package-size and architecture/runtime gates. x86 restricted worker
   startup still fails with `0xC0000022`; ARM64 physical execution, required
   install/signing/Store combinations, and the full device/theme/accessibility
   matrix remain incomplete. Large WebP working-memory coverage and animated
   first-frame composition also remain open.

## Architecture assessment and Rust direction

A responsive native renderer with bounded memory and correct supported content
is a reasonable goal. The complete combination of every workload within ten
percent of Edge, extremely close cross-engine pixel agreement, small packages,
and the full hardware matrix is an aggressive research target whose feasibility
has not been established. No requirement is changed by this assessment.

The duration reflects both engine complexity and execution problems. Parsing is
only one stage; layout, image lifetimes, selection, accessibility, device state,
and incremental work interact. Too much effort went into local optimizations
and evidence infrastructure without demonstrating corresponding improvements in
the failing complete documents. Representative visual/performance comparisons
should have driven prioritization earlier.

C# is not a demonstrated dominant bottleneck. The tested application is already
NativeAOT, Win2D delegates text layout to native DirectWrite, and SVG rendering
already runs in Rust. There is no measurement showing that replacing Markdig
alone would close the remaining gaps. See
[NativeAOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/),
[Win2D text layout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm),
and [resvg](https://github.com/linebender/resvg).

Rust is attractive for explicit resource ownership, compact storage, and
predictable release without garbage collection in the core. Those properties
support a redesigned engine; translating repeated scans, copies, or incorrect
layout rules into Rust would preserve the main defects. Foreign-function,
native graphics, and GPU lifetimes still require explicit design. See
[Rust ownership](https://doc.rust-lang.org/book/ch04-01-what-is-ownership.html).

The proposed direction is a Rust document/layout/resource core behind a thin
WinUI adapter. Painting, hit testing, selection, and accessibility should share
one geometry result. A versioned coarse-grained interface should exchange
bounded snapshots/batches and explicit leases rather than call across the
language boundary per character. Existing isolated processing should remain
contained. Windows text/graphics backends should initially stay the same so a
prototype measures the effect of the core redesign.

Before broad migration, compare a complete Rust vertical slice with the current
NativeAOT implementation on a long table-heavy README, mixed HTML/text, and an
image-heavy document. Require repeatable improvement in total latency, memory,
copying, and UI stalls with equivalent source mappings, selection, and output.
The existing implementation and corpus remain the reference. This is an
architectural recommendation; the user has not yet requested a Rust rewrite,
and none is included in this checkpoint.

## Local evidence and recovery

Fresh checkpoint test receipts are retained under
`MarkdownRenderer/artifacts/local-checkpoint-20261012/`. Historical evidence
remains in its recorded locations. These local generated artifacts are not
automatically shipped in packages or added to Git; tracked comparison fixtures
remain versioned.

Named `obj-*`, `bin-*`, and `artifacts` directories are ignored as generated
outputs. Two empty accidental root files, `LayoutPassCancellation'` and `{`,
were moved recoverably into
`MarkdownRenderer/artifacts/local-checkpoint-20261012/scratch-files/`. Nothing
was deleted. No benchmark gate, isolation policy, monitor mode, or remote branch
was changed for this checkpoint.
