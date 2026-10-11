# Opt-in progressive performance pipeline for MarkdownRenderer

Status: in progress. Work is being performed in the active local run, not deferred
to the next heartbeat. The current priority is to remove document-scale memory
growth and input-blocking native reflow before spending more time on evidence
bookkeeping. The 1.0 performance and release goals are not yet met.

## Current priority and evidence — October 11, 2026

Work continues directly in this task; the recurring MarkdownRenderer automation
has been removed and must not be recreated without an explicit user request.
The full objective and all original release limits remain unchanged.

The user approved the previously proposed September 28 published PR checkpoint
as the frozen performance reference:
`1c00c83ddfd2e8b65e05cb57e359a3f092b37271` (17:07 PDT, schema 12), for a future
clean candidate containing the current local optimizations. This is the exact
proposal recorded below, not the separate PR base `e6b43d9f...` (14:50 PDT),
which lacks the measurement contract. The initial approval entry incorrectly
substituted that PR base; no benchmark or repository setting used it. The
approved reference is a strict ancestor of local candidate
`67874e80b0403b4f1972394385805406f01c20bf`, committed without pushing. The user also explicitly approved
Windows Sandbox for the remaining JitHub/Edge UI and rendering tests. The user
subsequently approved a local commit of reviewed MarkdownRenderer changes,
excluding unrelated changes and **without pushing**, and the physical-desktop
120 Hz responsiveness benchmark when ready. README and SVG rendering tests
remain Sandbox-scoped. Neither approval relaxes clean-source, same-byte,
signing, or device qualification requirements.

The reviewed checkpoint and full physical measurement have now been attempted;
the clean-checkout AOT build blocker is fixed in local commit
`63135f0e7aab30a2dfb91292e903a31e4d510895`, without pushing. The immediate
priorities are the real offline README replay's aggregate-readiness failure,
the Sandbox's nonfunctional WebP codec, and the measured publication/frame-pacing
failures below. The app-startup failure has been resolved. The
unfinished request-local SVG coverage cache was preserved separately and is
excluded from this checkpoint; the tested disjoint-column worker remains the
candidate. Its prototype is not sufficient evidence to delay the full-system
measurement or to claim a production speedup. Source-bound local results must
not be presented as the joined GitHub Actions release qualification.

The candidate's isolated clean checkout builds the Release x64 performance
harness with zero warnings or errors. The full, unchanged R1/C1/C2/R2 physical
attempt stopped after **R1 failed**, as required; no quick population or retry
was used. R1 completed in 8m12s, with valid cache/schedule/pinned-thread evidence
but failed first-viewport stationarity, some latency/publication limits, scroll
frame pacing, and cancellation repeatability. Cancellation itself completed
200/200 requests with zero stale commits. Evidence is retained under
`MarkdownRenderer/artifacts/goal-release-20261011/physical-counterbalanced-67874e80/`.

A separate **full-population candidate diagnostic**, not a replacement R1 or
counterbalanced comparison, completed in 7m22s and also **failed**. It passes
cancellation (200/200, zero stale commits), all retained-memory bounds, the
100-cycle lifetime plateau, and both 100 KiB first-viewport conditions. Remaining
failures include larger-document stationarity, a 10 MiB cache-hit publication
maximum of 5.0821 ms against the unchanged 2 ms limit, and scroll frame pacing
(p95 8.8389 ms against 8.3333 ms). Renderer scroll work is 0.1996 ms p95 with zero
Gen2 collections; observed 222.64 Hz also misses the configured 240 Hz display's
unchanged 95% refresh qualification. The 1 MiB retained-managed result is
18,267,176 bytes versus R1's 30,052,968; uncached/cache-hit first-page p95s are
170.4837/98.5453 ms versus 259.4744/175.7427 ms. These are individual diagnostic
observations, **not a qualified speedup claim**; the 10 MiB fixture is slower in
this pair and all original gates remain open. Current-build evidence:
`MarkdownRenderer/artifacts/goal-release-20261011/physical-candidate-full-67874e80/`.

The real four-README offline replay failed before app launch because the AOT
binary predated the final source checkpoint. Its failure also exposed two
wrapper bugs now covered by headless regressions: Windows PowerShell 5.1 needs
the process snapshot refreshed before reading a fast child's exit code, and
aggregation must accept an initially empty failure collection. Neither fix
turns the failed replay into rendering evidence. A clean-checkpoint AOT rebuild
is required before the next replay. That build caught a reproducibility defect:
the source fingerprint included an ignored standalone `native/vendor/tiny-skia/Cargo.lock`
created by library tests, absent from a clean checkout and unused by the worker
build. The narrow fix excludes nested vendor lockfiles while continuing to bind
the root worker lockfile, every vendor manifest, and every native source file;
all three official worker rebuilds reproduce the existing binaries exactly,
and refreshed source-bound identity verification passes with fingerprint
`d8c8674167801ad150cd302a2ae1846612e839763e4aa39988815e6f623a8a48`.
The identity regression suite passes 28 fixtures; the offline wrapper's focused
regressions also pass. The subsequent locked-restore, clean-checkout Release/x64
NativeAOT publish and native-artifact verification **pass** at `63135f0e`, without
the ignored test lockfile. Publish logs contain no warning/error matches. The
app SHA-256 is
`76B2088C0FF427074F72184AA448AEBE2E0C0929C628604546F26619B90E4E61`, and the deployed
worker remains `3367203B...F8171E`. Evidence:
`goal-release-20261011/jithub-aot-clean-63135f0e-{restore,publish,verify}.log`.

The first real replay of that clean AOT build attempted **ranks 1–4** and
**failed all four before rendering**: each app process remained responsive with
a top-level window, but no app-ready signal arrived within the unchanged
30-second deadline. Native image/fidelity and browser comparisons were not
reached; this is not four successful README renders or top-500 qualification.
The failed cases and startup-process observations are preserved under
`goal-release-20261011/offline-same-byte-ranks001-004-clean63135f0e/`.
A separate outer progress-reader error also exposed a file-sharing conflict
when reading the runner's actively written log. Its fix must preserve partial
lines, exit-code handling, and both watchdog deadlines; it cannot convert the
app-startup failures into passes. A later guest inventory finds none of the
four app PIDs or unique-run descendants remaining; no stale PID was killed.

Guest inspection subsequently identifies the startup blocker: the Sandbox has
only older inbox Windows App Runtime packages, **not the x64 1.8 framework**
required by this framework-dependent app. The guest executable's hash matches
the clean AOT artifact above. The top-level window appears before the first
`Program.Main` phase marker, consistent with the bootstrapper's configured
missing-runtime prompt. The offline replay wrapper must install/verify the
existing hash-pinned runtime in the **guest**, once before replay, and retain
the current-user package inventory as setup evidence. This does not require an
app-binary change or authorize host-runtime changes. The rendering retest after
that setup correction is recorded below.

The standalone guest prerequisite check now **passes**: the hash-pinned official
installer installs runtime `8000.921.1539.0`, and the resulting current-user
inventory contains the healthy x64 framework, Main, Singleton, and DDLM package
families. Both setup and evidence delivery exit 0. The before inventory lacks
all four families. Evidence:
`goal-release-20261011/sandbox-runtime-setup-c330635556534612ba144fdf9683a9af/evidence/windows-app-runtime.json`.
The offline wrapper also needs its generated shard script transferred as a
file: the expanded UTF-16/Base64 command already exceeds the Windows process
command-line limit when launch arguments are included. Process-scoped script
execution is explicit; no persistent guest or host policy is changed.

The next runtime-fixed replay stopped **before any app case** because nested
source-root paths were recorded with mixed slash separators. The runtime helper
was present, but its identity lookup could not match the snapshot entry. The
writer now canonicalizes the complete relative path, without bypassing hash
checks. A regression creates an actual nested source snapshot and checks its
helper path, content hash, and staged file. The complete offline-wrapper focused
suite passes with this regression, Windows PowerShell 5.1 runtime inventory
fixtures, bounded concurrent log reading, and file-based script transport.
Failed setup evidence remains at
`goal-release-20261011/offline-same-byte-ranks001-004-runtimefixed63135f0e/`;
zero completed cases there must not count as rendering evidence. The final bounded
four-case replay has completed at
`goal-release-20261011/offline-same-byte-ranks001-004-canonical63135f0e/`.

That replay now launches the clean AOT app and reaches app-ready/render-complete
for **all four cases**, confirming the runtime/setup correction. It nevertheless
**fails all four** at the unchanged 20-second native readiness deadline: rank 1
at the first viewport, and ranks 2–4 at viewport 1. The app's recorded first-image
readiness signals report `HasVisibleLoadingImages=false`, but the UIA aggregate
readiness wait does not settle. Its old failure message does not retain the
actual aggregate status, so image-plan staleness or deferred-scene readiness are
still hypotheses, not established root causes. No Edge comparisons are reached.
The three captured native PNGs were inspected; rank 4 visibly shows an
unavailable freeCodeCamp social banner. The transport also exposes a separate
Windows PowerShell 5.1 redirected-child exit-code loss after polling; the runner
must retain its process handle and preserve failed case results independently.

A headless, same-byte WebP probe narrows the banner failure to the guest codec
environment. Its 148,596-byte source has SHA-256
`499B8931ABFB4C1A902BCE3F686C5408D71553EBA5B53D6D6D347C8D94EAB7CC`.
Both automatic and explicit Windows WebP decoder creation fail in Sandbox with
`0x88982F8B` (component initialization), despite WebP appearing in the decoder
inventory. The exact probe and bytes decode successfully on the host as
1500×500, with identical transformed pixel hashes between automatic/explicit
selection. The guest has no WebP extension package; the host has
`Microsoft.WebpImageExtension` 1.2.31.0. This is not evidence of a corrupt download,
nor proof that every production decode/upload path passes. A guest with a working
official WebP codec, or a separately designed portable decode solution, is still
needed to complete this case. No codec DLL was copied from the host, no rendering
gate was relaxed, and the failed replay remains failed. Probe source and logs:
`goal-release-20261011/webp-decode-probe/`.

The WinUI UI-testing skill's two fix-and-rerun limit has been reached for the
README replay and fresh SVG oracle in this active turn. Further UI launches are
paused; source review and headless regressions continue. These partial four-case
results are not the top-500 audit and cannot close the release gate.

The final offline wrapper headless suite passes all seven reported groups,
including Windows PowerShell 5.1 redirected-child handle/exit-code ownership,
bounded concurrent log reads, failed runtime setup, command-line limits, and
path quoting. It now returns a nonzero guest exit as well as failed evidence when
runtime setup fails, and disposes its retained child-process handle. The README
timeout diagnostic now retains the bounded actual host status, viewport and
scroll location, and visible/loading peer counts from the existing timeout-only
UIA walk; neither the polling predicate nor its deadlines changed. It explicitly
labels per-image error counts as unavailable through the current UIA contract,
instead of treating a missing count as zero. The Automation Release build passes
with zero warnings/errors, and the focused diagnostic/source-contract tests pass
**5/5 with no skips**. The first focused run found a test locator that assumed
the wrong helper order; that assertion was corrected without changing the
readiness behavior. Both runs are retained under
`goal-release-20261011/automation-visible-image-diagnostics{,-rerun}-20261011/`.
The final wrapper test log is `goal-release-20261011/offline-wrapper-final-tests.log`.
These are test-infrastructure fixes, not successful renderer or top-500 results.

The full physical trace additionally contains 12,000 composition intervals,
2,100 above 8.333 ms and none above 16.7 ms. Existing callback timing does not
include lazy-layout publication. That path scans the full focus/layout candidate
plans; it is a real document-scale path worth measuring, but it is not yet proven
to cause the measured frame misses. The 10 MiB publication maximum is isolated to
one of 600 samples; the other 599 are below 1 ms. No production scheduling change
or display-setting change is justified solely by that isolated sample.

An isolated semantic-text allocation prototype was measured through the actual
`MarkdownSemanticDocument.Build` path, not only its accumulator. On the same
prepared 1 MiB / 128-paragraph cold-projection fixture, five-build diagnostics
allocate **4,262,476.8 bytes/build** before and **2,153,084.8 bytes/build** with the
prototype. Those runs check output length and within-run fingerprint stability;
the stronger shared expected-text/fingerprint assertions were added afterwards
and have not completed their paired run. The roughly 49.5% allocation
reduction is promising but is **not integrated**: an adversarial 32 KiB fragmented
input still allocates 166,896 versus 147,000 bytes (+13.5%), failing its guard.
The 3.31/2.71 ms diagnostic timings are too small a sample for performance
qualification. The prototype remains in the separate `semantic-text-alloc`
checkout; neither its code nor its results are part of the release candidate.

The restored scanner and official worker identity were verified again before
the checkpoint: the scanner still hashes to `B988164F...019BB` and all three
workers bind to source fingerprint `cb51458b...c588`. The cache experiment is
retained only in the ignored `sponsor-glyph-reuse-analysis/` artifact directory.

The Sandbox README wrappers now distinguish SHA-256 content hashes from Git
blob SHA-1 identities, validate actual pinned bytes, safely normalize Windows
manifest separators, accept the initially empty snapshot manifest, and avoid
stale native exit codes after a PowerShell verifier. The public-capture guest
launcher drains stdout/stderr separately so expected per-case stderr cannot
abort the batch under Windows PowerShell 5.1. Focused tests and a complete
offline-wrapper orchestration test pass; the latter uses real AOT verification
and source snapshots, but stubbed guest transport and an intentional failing
shard, which remains failed/nonqualifying. This is harness correctness evidence,
not a completed README rendering or top-500 gate.

- Fresh SVG oracle capture is currently blocked before the first image: the
  guest now has Edge **155.0.4283.45**, while the saved comparison set used
  **154.0.4258.48**. The unchanged persistent WARP driver loses its page renderer
  during startup (`Runtime.enable`, `Render process gone`). Two bounded diagnostic
  variations—restoring Edge's default process sandbox and avoiding a new window
  in the isolated browser context—also fail before capture. Both speculative
  source changes were reverted. Evidence:
  `svg-fresh-oracle-capture24{,-default-sandbox,-context-tab}.log` and the matching
  capture directories, including browser and font hashes. No new fidelity result
  or renderer regression is inferred from these setup failures; the **195**
  historical comparison failures remain open. The UI-testing skill's two-repair
  limit was reached for this oracle path; README tests and headless work continue.

- The disjoint-pixel-column SVG optimization is integrated into the actual
  raster entry point, with whole closed contours kept together, unchanged
  coverage reduction order, and shared cumulative work/output/peak-memory
  limits. The first production build caught a missing test macro import and
  an unconnected prototype entry point; both are fixed, not waived. Current
  scanner SHA-256 is
  `B988164F53A0012CD0B5C6496D19F7A517A0917696BCF8D92ED75458522019BB`.
  The full native workspace passes **543** executed tests with four existing
  ignores; strict worker Clippy passes. The reviewed source/license inventory
  verifies all **76** locked dependency records. Evidence:
  `native-x-cluster-full-tests-rerun.log`, `native-x-cluster-strict-clippy.log`,
  and `native-x-cluster-supply-chain-verification.log`.
  All three official workers were rebuilt and their source identities refreshed
  and verified. The ordinary unfiltered provider build/test then passes
  **644/644, zero skipped** (`provider-x-cluster-full.log` and
  `test-results/provider-x-cluster-full/provider-x-cluster-full.trx`). This
  includes the previously failing 576-image sponsor fixture and unchanged
  CSS allocation limit. The sponsor test's total duration is 2.869 seconds
  (test setup + open + render, not an isolated render measurement); neither
  the three-second request deadline nor allocation assertion was relaxed.
  The tested x64 worker is
  `3367203BC008EFF188625139370FFA3105CF4694D0835A08032F2144FBF8171E`.
  A fresh ordinary NativeAOT publish passes the 100-cycle render/evict smoke:
  **4,890,624 bytes** aggregate retained-private growth and **zero handle
  growth**, plus isolation, eight shutdown races, immutable-source recovery,
  and no-orphan checks (`resvg-aot-x-cluster-fresh-{publish,run1}.log`). The
  fresh AOT host SHA-256 is
  `79BC9D20E52F7383C185C151DF777451FF54AF198668691583030A6B40B632C1`.
  Saved-image replay retains **493/688 passes, 195 failures, zero runtime
  errors**, with **zero changed comparison rows** versus the dense-tree
  checkpoint (`svg-x-cluster-historical-replay/`). This is historical-image
  regression evidence, not a fresh Edge oracle or proof of byte identity.
  Neither this bounded lifecycle check nor the replay qualifies full-app
  leak-freedom, browser-relative performance, or the release gates.

- Current default Release/x64 Core + WinUI packing succeeds, with both public
  XML documents retained, but the size gates still fail: **753,670 compressed
  bytes** versus **537,600**, and **1,778,688 managed bytes** versus **1,258,291**.
  The fresh packages are in `goal-release-20261011/packages-current-default/`;
  `managed-footprint-analysis/report.log` identifies 884,080 bytes of metadata
  and 706,341 method-body bytes in the 1,600,512-byte WinUI assembly. This is a
  local footprint observation, not a reproducible signed release qualification.
  The source-bound worker identity fixtures pass **25/25**, shared SVG/CSS
  preparation tests **55/55**, and managed non-orchestration tests **1,305/1,305**
  with zero skips (the two external benchmark-orchestration suites were not
  rerun in this check).
- Fresh x86 NativeAOT provider-smoke publication succeeds, but execution in the
  user-authorized x64 Windows Sandbox fails before rendering with worker native
  exit **0xC0000022**, matching the host's unresolved failure. Evidence:
  `resvg-aot-x-cluster-fresh-x86-{publish,sandbox}.log`. This disproves a
  host-only-configuration explanation; neither x86 support nor cross-architecture
  runtime qualification is complete. No restricting SID, mitigation, or ACL was
  weakened for this test.
- A fresh full JitHub Release/x64 NativeAOT publish and native-artifact verifier
  pass (`jithub-aot-x-cluster-fresh-{publish,verify}.log`). The app executable
  SHA-256 is
  `F02640DDDB186093D609D2A6B95DE07A666F578D1F43E95EF7E4E61DE4A0F40F`, and its
  SVG worker matches the `3367203B...` checkpoint above. The separately built
  ReleaseSecurity suite passes **131/131, zero skipped**
  (`jithub-release-security-fresh.log` and matching TRX). The separate locked
  restore, dependency/feed policy, and transitive vulnerability audit also pass
  (`jithub-dependency-security-fresh.log`). Sandbox runtime checks are not implied
  by publication or those security checks.
  The fresh, unfiltered JitHub managed suite also passes **3,581/3,581, zero
  skipped** (`jithub-full-fresh.log` and
  `test-results/jithub-full-fresh/jithub-full-fresh.trx`).
  The fresh self-contained automation runner is in `automation-runner-fresh/`.
  Current source-license inventory verification passes Mermaid **196**, TextMate
  **57**, math source/fonts **112**, and Resvg **76** records, with no unknown or
  incompatible licenses; the NativeAOT dependency ledger verifies **58** entries.
  Evidence: `license-source-closure-audit/`. The rechecked 14-package SPDX set
  in that folder is explicitly stale; it does not qualify current shipping
  artifacts, package sizes, or signatures.
- Both the approved `1c00c83...` reference in its clean, separate managed
  worktree and the candidate performance harness build in Release/x64 without
  warnings or errors. Evidence: `approved-reference-1c00c83/` and
  `candidate-harness-fresh-build.log`. The candidate was subsequently committed
  and the full physical runs executed with the failures recorded above. No
  qualified release comparison is implied; the last GitHub runner inventory
  reports zero configured self-hosted runners. The frozen reference must not
  be silently changed to bypass these requirements.
- Selected Resvg deployment accounting now includes **both the selected-RID
  worker and provider runtime DLL**, retaining the original 3.5 MiB ceiling and
  5 MiB all-RID compressed-package ceiling. Exact-cap, one-byte-over, missing,
  duplicate, zero-byte, and real-ZIP/decoy tests pass. The full compliance-tool
  self-test also passes, including **76** locked Rust source/checksum/license
  records. A strict-mode provenance bug is fixed: Git-pinned vendored sources
  omit an archive hash, while registry-pinned sources still require an exact
  independently pinned archive hash. No pin or license policy was relaxed.
  Evidence: `package-compliance-selected-deployment.log`. This does not mean
  the actual shipping package meets either size ceiling or is signed.
  The current unsigned x64 worker (3,332,096 bytes) plus provider runtime DLL
  (348,160 bytes) totals **3,680,256 bytes**, exceeding the unchanged
  **3,670,016-byte** selected-deployment ceiling by **10,240 bytes**, before
  signing overhead. The corresponding x86/ARM64 totals are 3,495,424/3,189,248
  bytes. These local file-length observations do not replace a fresh signed
  all-RID package check.
- Bounded dense-row accumulation and direct order-tree searches/deletion reduce
  the 576-entry diagnostic from the affine checkpoint's **4.140/4.166 seconds**
  to **3.332/3.124 seconds**. This is a substantial additional improvement, but
  **still above the unchanged three-second deadline** and not a qualified Edge
  performance comparison. Empty pre/post-path scan bands are skipped; a tiny
  rectangle at y=100,000 stays within a 32-visit test budget. Negative scanlines
  now initialize the row-flush watermark correctly. Source checkpoint:
  `D1C843EAE7C79DC415D76B4E2A9BB443B54954984C2F7B34205B3028C87B0B1A`.
  Full native workspace: **531 executed tests pass**, four existing ignores
  (`native-dense-tree-empty-band-full-tests.log`). An exact disjoint-column
  clustering prototype is being investigated separately; it is not production
  evidence and must preserve shared cumulative work and memory admission.
- The dense-row/tree worker, SHA-256
  `6ED78C2DFE9F847ECD11A95CFC91090802A8658E94ADB6B4329529B3CEE5C133`,
  retains **493/688** historical saved-image passes, **zero runtime errors and
  zero pass-to-fail changes** versus the circle/affine checkpoint. There are
  21 changed comparison rows, so the replay is not byte-identical. Its unchanged
  x64 AOT 100-cycle smoke passes with **4,894,720 bytes** aggregate retained-private
  growth and **zero handles**, plus the existing isolation, shutdown-race,
  immutable-source recovery and no-orphan checks. Evidence:
  `svg-dense-tree-historical-replay/`, `resvg-aot-dense-tree-run1.log`, and
  `sponsor-render-profile/run-dense-tree.log`. The unchanged full provider suite
  against this exact worker is **641/643**, zero skipped: the 576-entry sponsor
  render still times out at three seconds, and the irrelevant-CSS allocation
  test measures 8,200 bytes against its unchanged less-than-8,192-byte limit.
  Evidence: `provider-full-6ed78c2d-20261011/`. Neither failure has been waived
  or retried away; the CSS allocation cause is being investigated separately.
- A source-bound three-RID worker identity check now rejects stale deployment
  payloads during ordinary project builds and packing. Its **25/25** mutation
  cases cover native/build-input drift, both protocol versions, nonselected-RID
  tampering, mixed compiler identities, the test project's worker-directory
  override, fixed-input parent junctions, and LF/CRLF checkout stability without
  changing raw binary bytes.
  It requires each RID's fresh official build and reproducibility
  comparison. A three-RID refresh and ordinary provider build completed with
  zero warnings/errors and copied the expected x64 worker. Subsequent native
  formatting invalidated that identity, and the next ordinary build correctly
  failed before compilation. **The current source still requires a fresh
  three-RID build/identity refresh**; the earlier successful build does not
  qualify the newly edited source.
  Current native provenance verification passes **76** locked dependency
  records (`native-dense-tree-supply-chain-verification.log`). Unsigned local
  artifacts do not satisfy the signed shipping gate.
- The sponsor-grid timeout is localized to text rasterization, not image
  decoding. A diagnostic decomposition of the 576-entry shape measures images
  alone at **55–78 ms**, clipped rectangles at about **11 ms**, and the two
  short labels per entry at about **6 seconds**. Replacing repeated polygon
  clipping with exact affine-clamp integration reduces the full diagnostic
  from **6.246/6.476 seconds** to **4.140/4.166 seconds**. The **three-second
  hard request deadline still fails** and is unchanged. This decomposition
  uses equivalent geometry, not the exact managed fixture bytes or a matched
  Edge performance run (`sponsor-render-profile/run-{before,affine}.log`).
  Bounded row accumulation is the next performance target.
- The affine-area worker, SHA-256
  `FD995A1D84CD9C6B74515591C63DB84421496496045E911567B085EE91B9F9E5`,
  retains **493/688** saved-image passes with **zero runtime errors and zero
  pass-to-fail changes**. There are 29 changed comparison rows; they must not
  be described as byte-identical. Across each complete provider/Markdown
  replay set, 635 pixels change, with maximum alpha delta **1** and maximum
  channel delta **5** after unpremultiplication
  (`svg-affine-area-historical-replay/`). Its
  unchanged x64 AOT 100-cycle smoke passes with **4,976,640 bytes** aggregate
  retained-private growth and **two handles**, including isolation, shutdown
  races and immutable-source recovery (`resvg-aot-affine-area-run1.log`).
  **195 fidelity failures remain**, and this is not fresh Edge, qualified
  latency, complete leak-freedom or full release evidence.
- The managed cancellation failure was caused by a valid single circle at
  the top clipping boundary: `<circle cx='8' cy='0' r='8'/>` in a 512-square
  SVG returns scanner `NumericRange` at both 1× and 4×; moving its y coordinate
  to 0.001 succeeded. Near-equal event ordinates were grouped, but edge
  survival incorrectly used the first ordinate rather than the group's upper
  bound. The corrected worker, SHA-256
  `8E6800B3ED14702DC2112FBCE9CA16CC7A774AA9CF6C4E23D3879AD791354D39`,
  passes **20/20** separate runs of the unchanged 20,000-circle/one-millisecond
  cancellation test, and **642/643** full provider tests (zero skipped).
  **Only the sponsor-grid timeout remains in that suite.** Its saved 688-case
  comparison rows are all unchanged from the affine checkpoint, and its x64
  AOT 100-cycle smoke passes with **4,648,960 bytes** retained-private growth
  and **two handles**. Evidence: `provider-circle-affine-20261011-1010/`,
  `svg-circle-affine-historical-replay/`, `resvg-aot-circle-affine-run1.log`.
  Full native workspace: **525 executed tests pass**, four existing ignores
  (`native-circle-affine-full-workspace-tests.log`). This includes pre-allocation
  admission of the outer contour table. A subsequent source-only regression
  also closes the same start/end edge-survival hole in the crossing-group
  insertion branch; it must be included in the next worker checkpoint.
- Deployment integration remains open: normal builds and tests copy the
  checked-in RID-specific worker rather than rebuilding Rust. Those runtime
  binaries predate the current host protocol; successful diagnostic overlays
  are not proof that ordinary JitHub builds receive these fixes. Refresh and
  reproducibly verify all three runtime payloads from the next tested native
  checkpoint, then run provider/AOT checks using the normal build copy path.
  Keep signed release evidence separate; no unsigned diagnostic is a signed
  shipping artifact.
- Scanner growth now admits the simultaneous old-plus-replacement allocation
  peak for flattened geometry, scratch vectors, heaps, coverage events and
  output spans. Exact-limit and one-byte-short tests preserve existing buffers
  on rejection; output span storage is charged against both intermediate and
  total budgets. The locked/offline native workspace passes **518 executed
  tests**, with four existing manual/diagnostic/doc tests explicitly ignored
  (`native-growth-peak-final-full-workspace-tests.log`). The resulting worker,
  SHA-256 `FE2C6FD3EECC59A33E43DF8379A248EC2174EDEDD035A48DCA2D5EDA74C61863`,
  retains **493/688** saved-image passes and **zero runtime errors**. Every
  comparison row is unchanged from the crossing/mask replay, not just the
  total count (`svg-growth-peak-historical-replay/`). Its unchanged x64 AOT
  100-cycle smoke passes with **4,550,656 bytes** aggregate retained-private
  growth and **zero handle growth**, including isolation, shutdown races,
  immutable-source recovery and no-orphan checks (`resvg-aot-growth-peak-run1.log`).
  These do not establish complete leak freedom or release performance.
  Independent review identified an additional uncharged quadratic crossing-seed
  scan; its bounded-work repair is in progress.
- The same exact worker's full isolated managed provider suite is **641/643**,
  zero skipped. The active-cancellation test receives `ResourceLimitExceeded`
  instead of `Canceled`, and the 576-image/text/clip sponsor fixture times out
  during rendering. Both are open failures under investigation, not waived.
  Evidence: `provider-full-managed-20261011-0815/results/provider-managed-full-FE2C6FD3.trx`.
- Native provenance, SBOM and license records were regenerated for the reviewed
  renderer changes. Verification passes **76 locked dependency records** and
  **84 deduplicated license texts**, with the original BSD notices and explicit
  MIT notices for new local tiny-skia code retained. Source-pin tamper tests
  and **27** SPDX-selection cases pass. Logs:
  `native-growth-peak-supply-chain-{generation,verification}.log`,
  `native-source-provenance-mutation-tests-current.log`, and
  `native-spdx-selection-tests-current.log`. Later source changes require
  new pins and verification; this is not a signed-package or all-dependency
  release-license qualification.

- The analytic crossing fix removes the erroneous solid stroke wedges: scheduled
  crossing pairs seed complete coincident-edge groups, ended edges are not
  reinserted, and deterministic in-place sorting avoids hidden sort buffers.
  The new simultaneous six-edge crossing regression is independently checked
  against midpoint winding integration. Saved-image full-pipeline replay now
  passes **493/688**, with **zero runtime errors and zero pass-to-fail changes**
  versus both the 492- and 488-pass checkpoints below. The previously regressed
  `stroke-linejoin.svg` at 300% is restored. **195 fidelity failures remain**;
  this is not fresh Edge, timing, or release qualification. Worker SHA-256:
  `E0ED07C63FA5AEDC2A9EAF932816DAB8B8984309ADB55699645A2112ABA9DFD8`.
  Evidence: `goal-release-20261011/svg-crossing-mask-historical-replay/`.
  This same worker passes the unchanged x64 AOT 100-cycle smoke with
  **4,382,720 bytes** aggregate retained-private growth and **zero handle
  growth**, isolation, shutdown races, restart recovery, and no orphans
  (`resvg-aot-crossing-mask-run1.log`). This checkpoint predates the separately
  verified scanner reallocation peak admission above.
- Mask-region geometry now uses the same bounded analytic path/coverage
  admission as color rendering, without cloning paths or allocating an RGBA
  intermediary. The shared span replay validates every span before touching
  mask/color pixels. Focused tests cover transformed curves, winding rules,
  existing alpha, long fractional edges, exact budget rejection and malformed
  later spans. Full-parser alpha/luminance masks share and retire their request
  credit. The full locked/offline workspace passes worker **69**, resvg **50**,
  tiny-skia **131 + 164**, tiny-skia-path **29**, usvg **9 + 27 + 31**, plus one
  runnable doctest (`native-crossing-mask-full-workspace-tests.log`); the later
  crossing-group follow-up passes **132** tiny-skia library tests. Four existing
  manual/diagnostic/doc tests remain explicitly ignored, not counted as passes.
- The unchanged x86 AOT host can run the x64 restricted worker on this x64
  machine: three 100-cycle diagnostic runs pass with **2,711,552 / 3,452,928 /
  3,047,424 bytes** aggregate growth and **0 / 4 / 0 handles**. This narrows the
  failure to the native x86 worker loading path, not the tested cross-bitness
  protocol. It is a manually staged diagnostic, not a deployment fix or native
  32-bit Windows qualification. No security controls or supported configurations
  were removed. Evidence: `x86-host-x64-worker-crossbitness-20261011/` under
  `goal-release-20261011/`. A supported native 32-bit Windows runner is still
  needed; x86-on-x64 startup remains unresolved.

- Bounded non-AA filling and stroke drawing now admit transformed paths, dash
  configuration, contour scratch, dash expansion, stroke builders, scan edges
  and shader/pipeline storage against the request's remaining budget. Hairline
  drawing preserves the existing pixels at fractional clips, large tile
  boundaries, gradients and masks. **768** deterministic solid/dashed outline
  comparisons match the legacy geometry exactly. A full-parser regression also
  caught and fixed the all-off-dash case falling back to a solid source path.
  The locked/offline native workspace passes worker **69**, resvg **49**,
  tiny-skia **124 + 164**, tiny-skia-path **27**, usvg **9 + 27 + 31**, and its
  runnable doctest (`native-bounded-stroke-full-workspace-tests-rerun.log`).
  The layer-isolation test keeps its original 100/400-byte limits and uses an
  embedded raster so scan-edge admission is not confused with nested-layer
  admission. Earlier failed logs are retained.
  **Historical, not releasable:** saved-image replay was **492/688**, zero runtime errors,
  but includes **one pass-to-fail regression** (`stroke-linejoin.svg` at 300%).
  Some expanded stroke joins showed erroneous solid wedges. The crossing fix
  above removes this regression without a waiver or legacy fallback.
  Evidence: `goal-release-20261011/svg-bounded-stroke-historical-replay/`, worker
  `58D770A9AFBF95C559DB1776387B63DDDD6CD522E9ABDA559C0BC75F38A54657`.
  The same worker passes the existing x64 AOT 100-cycle smoke with **4,321,280
  bytes** aggregate retained-private growth, **+2 handles**, isolation, restart
  recovery and no-orphan checks (`resvg-aot-bounded-stroke-run1.log`). This is
  not complete architecture, leak-freedom, fidelity or performance qualification.
- The x86 invalid-argument-only A/B isolates the restricting-SID boundary:
  the production RC-only token exits **0xC0000022** before worker argument
  parsing, while the otherwise identical diagnostic token without restricting
  SIDs reaches the parser and exits **70**. Removing restrictions is not a
  shipping solution. Appending All Restricted Application Packages instead
  failed at `CreateRestrictedToken` with error **87**, before child launch;
  the SID/ABI validity is being investigated. No system ACL or production
  security policy was changed. Production source was restored byte-for-byte.
  Evidence: `x86-restricted-sid-ab-diagnostic-20261011/` and
  `x86-restricted-sid-arap-ab-diagnostic-20261011/` under `goal-release-20261011/`.
- The in-place analytic coverage reduction now renders the entire historical
  688-case SVG corpus without initialization or resource errors (**0**, down
  from **53**). **488/688** satisfy the unchanged comparison limits, versus
  487 previously, with **no pass-to-fail regressions**. Text is available again
  but its visual mismatch is still a release failure; **200** cases remain
  outside fidelity limits. Evidence:
  `goal-release-20261011/svg-inplace-compaction-culling-historical-replay/`;
  diagnostic worker SHA-256
  `E5148E126B54CBEA40333B3C41C2611BE596334213A6400E5B79AFA8AAAD582B`.
  This is saved-image evidence only, not fresh Edge/performance qualification.
  The same worker passes the unchanged x64 NativeAOT 100-cycle smoke with
  **4,780,032 / 4,784,128 / 4,415,488 bytes** aggregate retained-private growth
  and **+2 / +2 / 0 handles** across three runs, plus
  isolation, startup/shutdown races, restart recovery and no-orphan checks
  (`resvg-aot-inplace-compaction-culling-run1.log` through `run3.log`). The existing AOT host hash
  remains `A624C2FE29395EDB9D9E35B565A22E344434124CF34D544BA871EC1E90D69507`.
- Offsurface path culling uses transformed fill/stroke bounds against each
  actual destination, including filter inputs. Filters no longer silently
  crop their input to a fixed multiple of the root viewport: complete authored
  surfaces must fit the existing request budget or fail before allocation.
  Regression tests cover 1,000 distant paths, wide strokes, transformed paths,
  offscreen sources moved into view by a filter, nested opacity, huge filter
  rejection, and large-coordinate group-origin precision. The new corpus
  fixture `08-filters/off-viewport-offset.svg` still requires a fresh oracle.
  Evidence: `native-culling-surface-origin-focused-tests.log` (45 pass, 1 then-
  open pinned-font failure, 2 ignored) and `native-surface-origin-regression.log`
  under `goal-release-20261011/`. The pinned-font failure was subsequently
  resolved by the coverage reduction above; no historical failed log is erased.
- A child-only invalid-argument x86 diagnostic cleared only the creation-time
  `ProhibitDynamicCode` mitigation. Both baseline and diagnostic still exited
  **0xC0000022** immediately after mapping the worker and SysWOW64 `ntdll.dll`.
  That single mitigation is not the identified cause. No content was admitted,
  no production mitigation was changed, and all diagnostic processes exited.
  Production `WindowsWorkerProcess.cs` was restored exactly; evidence is in
  `goal-release-20261011/x86-debug-event-ab-diagnostic-20261011/`.

- The current source also publishes the strict ARM64 NativeAOT smoke successfully
  with locked restore, full trimming and warnings-as-errors. The produced PE is
  ARM64 (`0xAA64`), SHA-256
  `2C92B87FCECA2AE590BF5FF2A1CBEA8C8AA7EA1C5885474B075EA580EEF9C078`.
  Evidence: `goal-release-20261011/resvg-aot-arm64-native-source-publish.log`
  and `resvg-aot-arm64-native-source-20261011/`. This is compilation evidence
  only: no ARM64 runtime or device qualification has been performed here.
- Fixing partially covered span forwarding restores continuous long edges;
  the real SVG-to-canvas regression verifies all 399 boundary pixels against
  independently calculated half/quarter coverage. Saved-image comparison rises
  to **487/688**, with **36 fail-to-pass** and **4 pass-to-fail** changes versus
  the 455-case reference. It still has **53 errors** (48 text, 5 settings-icon
  scales) and is **not releasable**. The newly added deterministic pinned-font
  test catches ordinary Noto Sans text failing the unchanged memory limit;
  the installed-font diagnostic identifies `ActiveWorkLimit` for Segoe UI
  `browser`. These failures remain open, not waived or counted as passes.
  Evidence: `native-analytic-row-reduction-resvg-tests.log`,
  `analytic-row-reduction-installed-font-diagnostic.log`, and
  `svg-analytic-row-reduction-historical-replay/` under `goal-release-20261011/`.
  Diagnostic worker SHA-256:
  `698CA59AA92DAF95EEC5698F00C34B2D4967B478B7C031782F1F47B789A8901A`.
- Shared intermediate-memory admission now also covers all filter primitive
  images and scratch buffers; filter-result aliases retain their pixel credit,
  and capacity-checked vectors cannot grow past their reservations. The real
  parser/group/filter/canvas test exercises **19 primitive variants**, rejecting
  unadmitted work while preserving valid output. The full locked/offline native
  workspace passes: worker **69**, resvg **38**, tiny-skia **106 + 164**, usvg
  **9 + 27 + 31**, plus its runnable doctest. Existing manual benchmarks and the
  new explicit installed-font diagnostic are not counted as automatic passes.
  Two upstream writer tests initially failed because their exact test fonts were
  absent; restoring the pinned upstream Noto Serif/Amiri fixtures with OFL
  notices and independent hashes makes the unchanged writer suite pass.
  Evidence: `goal-release-20261011/native-filter-resource-validation-full-pinned-fixtures.log`.
- The first bounded analytic-path integration is **not releasable**. Its saved-
  image replay is **465/688**, versus 455/688, but includes **22 pass-to-fail
  regressions** and **48 text initialization errors**; aggregate improvement does
  not excuse either. Ordinary combined Segoe UI digits exhaust the scanner's
  event ceiling even though every individual digit passes. Pixel inspection
  also identifies incomplete forwarding of long, partially covered spans.
  These root causes are being fixed without raising limits or switching to a
  fallback renderer. Worker SHA-256:
  `08B866499A671A8161032414ED1CD69622A634FC650AA8E25DBAEC23A18EE505`.
  Evidence: `svg-analytic-resource-full-historical-replay/` and
  `analytic-installed-font-{path,glyph,digit}-diagnostic.log` under
  `goal-release-20261011/`. This remains historical-image diagnostic evidence,
  not fresh Edge or performance qualification; shipping workers are unchanged.
- The corrected exact-area worker passes the unchanged x64 AOT smoke with
  **4,644,864 bytes** aggregate private growth and **+2 handles**, including the
  182-image, isolation, restart and no-orphan checks. Its verified saved-image
  replay remains **455/688**, with no changed pass/fail classifications versus
  the preceding normalized-coverage worker. Exact-area x64 worker SHA-256:
  `0E4970EDA44C9FBB190B6C62630471A7B66D80D1737DEBE720AE964D0E192C8F`.
  Evidence: `goal-release-20261011/resvg-aot-native-source-exact-area-run1.log`
  and `svg-exact-area-verified-full-historical-replay/`. The similarly named
  earlier folder without `verified` is explicitly invalid for this checkpoint:
  its copy step failed and it ran the previous worker. No fresh Edge or timing
  qualification is claimed.
- The current x86 NativeAOT smoke **publishes successfully**, but all three
  runtime attempts fail at restricted-worker startup with **0xC0000022
  (STATUS_ACCESS_DENIED)**, before the 100-cycle measurement. This is an open
  architecture gate, not a memory pass. Host SHA-256:
  `4CB8E5506C62F6B274779BE3055D5C4E6E49853332C7B0E3B19A1C89FF503B64`;
  worker `A207D116D8578808310BE2804AEC71DECCBDF3114ABA780103D5034CF78ADCFC`.
  Evidence: `goal-release-20261011/resvg-aot-native-source-x86-20261011/`.
- Request-local intermediate memory is now shared across nested group, clip,
  mask, pattern and SVG-image surfaces, with fallible allocation, exact buffer
  retirement and sticky typed errors instead of successful partial output.
  The first integration passes **28 resvg unit tests** and **68 worker tests**
  (each retains its existing ignored manual benchmark). Filter scratch and the
  analytic-path adapter remain in progress; this is not yet a full-renderer
  allocation-bound claim. Evidence: `native-resource-budget-{lib,worker}-tests.log`.

- Native-backed immutable source snapshots now reduce the unchanged x64
  NativeAOT 100-cycle aggregate private-memory growth to **4,734,976 /
  4,538,368 / 4,558,848 bytes** in three passing runs (previous normalized-worker
  checkpoint: **7,884,800–8,056,832 bytes**). Handle growth is **0 / 0 / 2**;
  managed allocations fall from approximately **7.43 MB to 4.32–4.34 MB**.
  No forced collection, additional warm-up, or changed limit was used. All RTL,
  182-image pixel, worker-isolation, eight startup/shutdown-race, immutable-source
  recovery after idle-worker termination, and no-orphan checks pass. The full
  provider suite passes **643/643**, zero skipped, including final-reader
  admission retention and allocation-failure custody. The initial focused run's
  **143 pass / 8 fail** marker-search boundary mismatch is retained; restoring
  its existing past-end contract yields **151/151** focused passes.
  Evidence: `goal-release-20261011/native-source-lifetime-{focused-rerun,full}/`
  and `resvg-aot-native-source-{publish,run1,run2,run3}.log`;
  AOT host SHA-256
  `A624C2FE29395EDB9D9E35B565A22E344434124CF34D544BA871EC1E90D69507`,
  worker `02BD963D7269643AFCE52ECE828CA870B3102BE9023849A40CA828AEA14F6849`.
  This provides material headroom below the unchanged 8 MiB limit, not proof of
  all-architecture leak freedom or full browser-relative release qualification.
- The shared 0..256 coverage-to-8-bit-opacity conversion now rounds the
  normalized area rather than treating 256-scale coverage as a byte. The
  `very-thin.svg` 300% regression is eliminated (alpha IoU **1.0**, previously
  **0.994791667**); the full saved-image diagnostic improves **454/688 to
  455/688**, with no pass-to-fail change or alpha-IoU reduction greater than
  **0.00001**. The focused replay remains **23/32**, and all eight KaTeX-shaped
  182-image scales still pass. Evidence:
  `goal-release-20261011/svg-normalized-coverage-{full-,}historical-replay/`;
  isolated worker SHA-256
  `02BD963D7269643AFCE52ECE828CA870B3102BE9023849A40CA828AEA14F6849`.
  These are unqualified historical-image diagnostics, not fresh Edge or
  performance evidence. Independent geometric-area tests pass; five existing
  tiny-skia fractional-rectangle golden expectations still need reconciliation
  against independently computed coverage, not renderer-generated replacement
  images. No shipping worker has been promoted from this checkpoint.
- That normalized-coverage worker also passes **three/three** unchanged x64
  NativeAOT 100-cycle checks, including RTL text, all 182 embedded images,
  restricted-worker isolation, eight startup/shutdown races, source recovery
  after exact idle-worker termination, and no orphan process. Aggregate private
  growth is **8,007,680 / 8,056,832 / 7,884,800 bytes**, with handle growth
  **0 / 2 / 2**. The smoke host remains
  `BA5EA5DB6E6568B4E3F5EE6D154A4BD87968EA3F25A1766AD1E62C79E377DF3C`;
  these runs do **not** yet include the in-progress native-backed source
  optimization. Evidence:
  `goal-release-20261011/resvg-aot-v9-normalized-worker-run1..run3.log`.
- The x64 NativeAOT smoke now also kills only its exact owned idle worker,
  verifies the original kernel object has exited, then renders an already-open
  182-image document in a distinct replacement worker after clearing the
  caller's input array. All image pixels, replacement isolation and awaited
  no-orphan shutdown pass. The new check runs **after** the unchanged 100-cycle
  memory measurement and eight startup/shutdown races; it adds no warm-up or
  relaxed limit. The prior managed-array provider checkpoint passes this check
  with **8,028,160 bytes** aggregate private growth, **+2** handles and
  **7,440,992** allocated managed bytes. Evidence:
  `goal-release-20261011/resvg-aot-v9-restart-ownership-{publish,run1}.log`;
  host SHA-256 `BA5EA5DB6E6568B4E3F5EE6D154A4BD87968EA3F25A1766AD1E62C79E377DF3C`,
  worker `1FBE1602A3D9F542A61AF66E530F5D05A39FB7D1D1DED6BCCE849360D7237620`.
  This is idle-worker recovery evidence, not active-content crash replay,
  all-architecture leak freedom, or published JitHub UI qualification.
- The current x64 JitHub NativeAOT publish completes successfully with the
  default dependency/lock/feed/vulnerability gate and **131/131** release-
  security tests. The actual output also passes `Verify-NativeAotArtifact.ps1`
  for x64 (native executable/required payloads and no CLR/JIT runtime).
  Evidence: `goal-release-20261011/jithub-aot-current-operation-reuse/`,
  `jithub-aot-current-operation-reuse.log` and
  `jithub-aot-current-operation-reuse-verification.log`. This proves compilation
  and artifact structure only; matching protocol-v9 worker packaging, live
  published-artifact Sandbox validation and other architectures remain open.
- The bounded fractional-span forwarding fix restores every pixel of long
  stroked spans. The 182-unique-image KaTeX-shaped fixture now meets the unchanged
  historical-image thresholds at **all eight scales**, through both the provider
  and Markdown/Win2D composition. The focused replay improves **20/32 to 23/32**,
  and the wider replay **451/688 to 454/688**. Both remain explicitly
  unqualified saved-image diagnostics, not fresh Edge or timing evidence.
  A new `very-thin.svg` failure at 300% (alpha IoU worsens by **0.002597367**)
  blocks promotion of this worker; aggregate counts do not hide that regression.
  Native tests pass **70 unit + 160 integration**, and **68 worker** tests
  (one existing manual benchmark ignored). Worker SHA-256:
  `004D01AED5813C3CB26F631D69E5028652D758BCD5D19A7441DED0029F9E9970`.
  Evidence: `goal-release-20261011/svg-long-span-fixed-historical-replay/` and
  `svg-long-span-fixed-full-historical-replay/`; native test logs are under the
  repository-root `artifacts/goal-release-20261011/`. Shipping workers have not
  been overwritten by this diagnostic checkpoint.
- The current JitHub application unit/contract suite passes **3,581/3,581**,
  zero skipped, after repairing two stale public-capture source checks to
  verify the canonical probe-name constant and its actual dispatch/parse use.
  Browser-only isolation and rejection of resume/evidence-reuse options remain
  asserted. The initial **3,579 passed / 2 failed** report is retained.
  Math and Mermaid suites also pass **76/76** and **115/115**, zero skipped.
  Evidence: `goal-release-20261011/test-results/jithub-app-current-fixed/`,
  `math-current/` and `mermaid-current/`. These managed results do not substitute
  for live Sandbox UI, same-byte Edge or top-500 release qualification.
- The full same-RID renderer suite now passes **2,690/2,690**, zero skipped.
  Selection clipping preserves the original extent on an unclipped axis; exact
  viewport tests pass **30/30** without added tolerances. The intended
  `PhysicalViewport` API entry was moved to its actual exported sort position,
  not added or removed to hide a mismatch; API checks pass **12/12**.
  Evidence: `goal-release-20261011/test-results/github-full-corrected-baseline/`,
  `selection-viewport-parity-final/` and `public-api-baseline-corrected/`.
- The current provider suite passes **626/626**, zero skipped, after correcting
  the injected mapping-factory seam: a caller supplies either its test factory
  or the selected worker's exact-primary-token security context, never both.
  Production mappings retain their per-worker protected user+RC DACL and low
  integrity label. The three internal open/attach/render operations now use
  runtime-pooled, single-consumption async state machines, as worker exchanges
  already do; source/raster ownership and public APIs are unchanged.
  Evidence: `goal-release-20261011/test-results/resvg-operation-reuse-full/`.
  The matching x64 NativeAOT checkpoint passes **eight/eight** unchanged
  100-cycle runs, with aggregate private-byte growth **8,040,448 / 8,130,560 /
  7,962,624 / 8,048,640 / 7,962,624 / 8,003,584 / 7,958,528 / 7,905,280**,
  handles **0 or 2**, and approximately **7.44 MB** managed allocations. No
  forced GC, additional warm-up or raised limit was used. Host SHA-256:
  `81617FAD7C330318DA3123F71EFFAC24ACC0A7C214EB069A939BB51890EF1481`;
  worker SHA-256:
  `1FBE1602A3D9F542A61AF66E530F5D05A39FB7D1D1DED6BCCE849360D7237620`.
  Evidence: `goal-release-20261011/resvg-aot-v9-operation-reuse-{publish,run1..run8}.log`.
  This is checkpoint evidence, not all-architecture leak freedom: the preceding
  context-final checkpoint passed three runs then failed at **8,679,424 bytes**
  when worker growth increased to **684,032 bytes**. That failure is retained in
  `resvg-aot-v9-context-final-run4.log`; further headroom remains important.
- The earlier exchange-only x64 NativeAOT checkpoint passed the unchanged
  100-cycle rich-text plus
  182-unique-image memory/raster/isolation/shutdown checks in **four consecutive
  runs**: aggregate private-byte growth **8,105,984 / 8,282,112 / 8,294,400 /
  8,105,984**, handles **0 / 2 / 2 / 2**. Managed allocations are approximately
  **7.59 MB**, with no forced GC, extra warm-up or changed limit. The host now
  reuses one exact-primary-token security descriptor per worker generation and
  uses the runtime's pooled async state machine for single-consumption worker
  exchanges; no source or raster ownership is pooled. Host SHA-256:
  `4E806B237EB49CB0148229A888F5FFA0E2B100EFF8F53211B176E3B8B00FAA95`;
  worker SHA-256:
  `1FBE1602A3D9F542A61AF66E530F5D05A39FB7D1D1DED6BCCE849360D7237620`.
  Evidence: `goal-release-20261011/resvg-aot-v9-exchange-reuse-{publish,run1,run2,run3,run4}.log`.
  These are historical x64 checkpoint measurements, not all-architecture leak freedom or
  browser-relative qualification. Focused security/transport/UTF-8 tests pass
  **55/55**; the full provider run is **621 passed / 5 failed**, exposing injected
  mapping-factory/context wiring incompatibilities subsequently fixed and
  covered by the current 626-case suite above. The preceding descriptor-only run was
  **two passes / two failures** and is retained, not discarded.
- Fractional root clipping now passes **69 tiny-skia unit + 157 integration**
  cases and **68 worker** cases (one existing manual benchmark ignored).
  Offline comparison against saved Edge images improves **14/32 to 20/32**
  within the original pixel thresholds, with provider and full Markdown/Win2D
  composition agreeing pixel-for-pixel. This historical-image diagnostic is
  explicitly unqualified: it does not replace fresh same-byte Edge capture,
  timing, or the full corpus. Remaining failures include fractional KaTeX
  stroke coverage, tiny-circle alpha and one fractional corner case. Evidence:
  `goal-release-20261011/svg-root-clip-historical-replay/` and native root-clip
  test logs. Shipping workers have not been replaced by diagnostic artifacts.
- The broader saved-image replay is **451/688** within the unchanged pixel
  thresholds, **237** outside (`goal-release-20261011/svg-root-clip-full-historical-replay/`).
  This remains an explicitly unqualified historical diagnostic. A first stroke
  coverage attempt regressed the focused replay from **20/32 to 14/32**; a
  stroke-only revision restored **20/32**, but KaTeX fractional-scale alpha
  worsened (125% **0.931919** versus **0.982900** before the change). Direct pixel
  inspection localizes that regression to long horizontal stroke coverage,
  not embedded-image bytes or Win2D composition. The revision is being repaired;
  passing native unit tests alone does not qualify it. Evidence:
  `svg-stroke-aa-historical-replay/`, `svg-stroke-final-historical-replay/` and
  `svg-stroke-final-full-historical-replay/`. No diagnostic worker has replaced
  a shipping runtime, and no visual threshold was relaxed.
- Current provider verification is **619/619 passed, zero skipped** after
  removing the redundant XML input-byte buffer for large, undeclared UTF-8
  snapshots. Strict decoding writes into the XML parser's character buffer;
  XML declarations and UTF-16/32 signatures keep the existing encoding-detection
  and normalization path. New tests cover surrogate/chunk boundaries, invalid
  UTF-8, legacy encodings and forbidden content. Dense preflight allocations
  are **5,708,000 bytes / 100 inspections**, including the private source copies.
  Evidence: `goal-release-20261011/test-results/resvg-direct-utf8-{focused,full}/`.
  Matching x64 NativeAOT publish succeeds; the unchanged 100-cycle run still
  **fails** at **8,912,896 bytes** aggregate private-memory growth (host
  **+8,577,024**, worker **+335,872**), **+2** handles and **8,252,744** managed
  allocation bytes. Raster/isolation/disposal/eight startup-race checks pass.
  Evidence: `goal-release-20261011/resvg-aot-v9-direct-utf8-{publish,run1}.log`.
  The remaining difference from the 8 MiB ceiling is **524,288 bytes**; this is
  not a completed memory milestone or proof of leak freedom.
- Protocol request/response buffers are now reused within each worker's existing
  serialized transaction and cleared on terminal paths. Two bounded preflight
  workspaces also reuse only their cleared initial 1,026-character scratch
  arrays, never grown data-URI/path arrays. These changes passed **592/592**
  provider tests and a fresh x64 NativeAOT publish. The unchanged dense
  100-cycle gate still **failed** at **9,191,424 bytes** aggregate private-memory
  growth (host **+9,035,776**, worker **+155,648**), handles **+2** and managed
  allocations **8,669,304**. Raster/isolation/disposal/startup-race checks passed.
  Evidence: `goal-release-20261011/resvg-aot-v9-attribute-scratch-run1.log`;
  preceding protocol-only checkpoint was **9,605,120 bytes**, also a failure.
  No forced collection, additional warm-up or limit change was used.
- The full current renderer suite completed **2,662 passed / 27 failed / zero
  skipped**. Failures split into twelve exact selection-geometry discrepancies,
  fourteen reflection-helper parameter mismatches and one public API mismatch.
  API compatibility and the underlying geometry path are being repaired;
  neither floating-point tolerances nor the public baseline are being relaxed
  to make this result green. Evidence:
  `goal-release-20261011/test-results/github-full-current-placement/`.
- Current host preflight now inspects attribute spans, reuses two bounded,
  cleared digest workspaces, and checks cancellation between attribute-read
  chunks of at most 4,096 characters. Security, Unicode, depth-sensitive nested validation,
  stale-dispose and oversize-workspace regressions pass as part of **591/591**
  provider tests, zero skips (`goal-release-20261011/test-results/resvg-provider-workspace/`).
  Dense 182-image preflight allocations are **6,380,008 bytes / 100 inspections**,
  including private source copies. A fresh x64 NativeAOT publish succeeds, but
  its unchanged 100-cycle process-memory gate still **fails**: aggregate growth
  **10,645,504 bytes**, host **+10,104,832**, worker **+540,672**, handles **+2**;
  managed allocations **9,746,824**. Raster/isolation/shutdown checks pass.
  Evidence: `goal-release-20261011/resvg-aot-v9-workspace-run1.log`;
  host SHA-256 `79A1F0B722E6F21229BF91D5C18615BCC60B2A027879D637E107008EF5204B8A`,
  cache-checkpoint worker `09FC4E25D702F98D891AD8B6ACD019D57A4C3FBBF7E697B5C702A5C0055D7A27`.
  This improves on the span-only checkpoint (**12,021,760 bytes** growth), but
  does not close the 8 MiB gate. Further protocol/source allocation work is
  in progress; no forced collection, extra warm-up or raised limit is used.
- Absolute-origin placement and its exact prepared-root publication fence now
  pass the broader same-RID **247/247** layout, image/vector, selection/UIA,
  reentrant geometry and tile-ownership cases, zero skips. The fence retains
  pristine clone validation at zero, then validates the recorded final absolute
  origin; it no longer incorrectly requires a committed candidate to remain at
  zero. Evidence: `goal-release-20261011/test-results/placement-origin-fence-fixed/`.
  Full renderer-suite verification is running; this focused result is not a
  full-suite or browser-performance qualification.
- The expanded 182-image NativeAOT stress run now has actual process evidence,
  and **fails** the unchanged 8 MiB aggregate growth limit. With the current
  protocol-v9 cache-fix worker, 100 render/evict cycles retain **14,196,736 bytes**
  more private memory: host **+14,192,640**, worker **+4,096**. Handle growth is
  zero; all raster checks, exact live-worker isolation, disposal and eight
  startup/shutdown races pass. Managed diagnostics record **15,754,704 bytes**
  allocated, one gen-0 collection, **320,968 bytes** of last-GC live heap and
  **16,142,336 bytes** committed. This points the next work at transient host
  allocation/heap growth; it does not establish leak freedom or waive process
  memory. Evidence: `goal-release-20261011/resvg-aot-v9-cache-checkpoint-run.log`;
  diagnostic worker SHA-256 `09FC4E25D702F98D891AD8B6ACD019D57A4C3FBBF7E697B5C702A5C0055D7A27`.
  The worker's focused suite is **65 passed / 1 failed / 1 ignored**; the known
  fractional-root clipping failure remains. Initial host SHA-value-key,
  small stack-decode and geometry-allocation changes pass **123/123** focused
  tests but did **not** close memory growth. Further attribute-span inspection
  work is in progress; no forced GC, extra warm-up, or limit change is used.
- Changed-root candidates now measure under their prospective placement scope
  and then arrange at the final absolute origin, with matching published base
  coordinates. This preserves eager floating-point operation order in table
  rows without reflowing unchanged roots. The real same-RID Release graph and
  table/reentrant-layout suite pass **50/50**, zero skips, covering the 24
  remaining table parity failures from the prior focused run. Evidence:
  `goal-release-20261011/test-results/placement-absolute-origin/placement-absolute-origin-20261011.trx`.
  Broader selection/UIA and full renderer-suite verification remains required.
- Native decoded-resource retention now accounts for raster-cache index storage,
  not only pixel bytes. The real 182-unique-PNG regression retains and reuses all
  182 resources (the former reservation retained only two). Font-catalog refresh
  also releases each retired text tree's raster reservation; a 100-generation
  regression passes. Parsed-tree eviction now sorts victim ages once and retires
  trees/document aliases in one batch instead of repeated whole-cache scans;
  its 4,096-entry regression passes. Explicit trimming releases backing hash
  tables as well as their contents. The current worker suite is **64 passed /
  1 failed / 1 existing ignored manual benchmark**; the remaining failure is
  fractional root-viewport overflow, not cache accounting. Vendored resvg tests
  are **17 passed / 0 failed / 1 ignored**, and usvg tests **9/9**. Evidence:
  `MarkdownRenderer/artifacts/native-codec-20261011/{native-raster-cache-workspace-final,
  resvg-library-final,usvg-library-final,font-refresh-after}.log`. These are
  source-level cache/lifetime results, not current-worker process-memory or
  browser-relative qualification; matching rebuilt protocol-v9 workers remain
  required.
- A separate retained-allocation regression exposed another native cache
  undercount: the smallest parsed SVG was charged **126 bytes**, while its
  root/index headers alone occupied at least **944 bytes**. Parsed-resource
  accounting now includes conservative tree/Arc/bucket/locale overhead. The
  regression fails before and passes after; the combined parsed-cache and
  4,096-entry eviction checks pass **3/3** (`native-codec-20261011/
  tiny-parsed-cache-metadata-{fail-before,after}.log` and
  `parsed-cache-metadata-and-eviction-after.log`). Retention pressure remains
  eviction, not a new content-admission rejection, and no ceiling was raised.
- The NativeAOT smoke workload now renders and checks **182 unique embedded
  PNGs in an 890×158 SVG on every one of its 100 render/evict cycles**, in
  addition to the existing RTL/repeated-image workload. Its allocation-free
  pixel checks reject the loss of any individual image; validation unit tests
  pass **8/8**, zero skips (`goal-release-20261011/svg-dense-aot-validation.trx`).
  Its current-source x64 NativeAOT publish also succeeds with warnings treated
  as errors (`resvg-aot-v9-dense-publish.log`, artifact `resvg-aot-v9-dense/`).
  The unchanged 8 MiB/four-handle limits are not yet claimed for this expanded
  workload: a fresh published-artifact run with matching workers is required.
- Release signing now has a separate all-RID staging path with exact caller-
  selected certificate identity, mandatory timestamp verification, unchanged
  worker size ceilings, and no source-worker mutation. Strict pack/release checks
  compare the signed PE bytes with the actual unsigned build, allowing only
  documented Authenticode checksum/certificate-table changes; a self-authored
  JSON hash manifest alone cannot qualify different executable content.
  `Test-ResvgWorkerSigning.ps1` passes its negative and preimage-integrity tests,
  independently rerun by the main agent. No production signing was performed:
  an approved signing certificate/service is still required.
- A fresh x64 Release NativeAOT publish succeeds with the default release
  gates enabled: dependency/lock/feed/vulnerability checks and **131/131**
  ReleaseSecurity tests pass, zero skips. The nested test build previously
  included generated C# from a nested `obj-aot-contract` scratch directory;
  recursive generated-output exclusions now prevent duplicate assembly
  attributes without skipping the gate. Evidence: `jithub-aot-app-only-20261011-044220.log`
  under `MarkdownRenderer/artifacts/goal-release-20261011/`; MSBuild resolved
  the publish directory relative to the app project, so the actual artifact is
  `JitHub.WinUI/MarkdownRenderer/artifacts/goal-release-20261011/jithub-aot-app-only-20261011-044220/`.
  This is current managed-code compilation evidence, not runtime qualification:
  the packaged SVG workers still need replacement with matching protocol-v9
  binaries, followed by published-artifact Sandbox validation.
- Origin-preserving placement mapping fixes **17** previously failing layout
  cases without changing tolerances. The focused suite is **180/205 passed,
  25 failed, zero skipped** (`test-results/layout-origin-preserving-parity-20261011.trx`).
  It now maps offsets relative to the original root into the new root rather
  than adding a rounded translation; the exact-origin regression pins the
  floating-point operation order. Remaining table selection metrics and one
  inline-embed test's missing production placement scope are under investigation.
- Tiled SVG composition no longer paints loading chrome or a previous bitmap
  behind transparent replacement pixels. Four fractional last-tile regressions
  first failed and now pass; the combined fractional suite is **9/9**, zero
  skips (`fractional-edge-tile-coverage{,-fixed}.log` and matching TRX files).
  The coverage sweep visits resident tiles, not the virtual image grid, and
  preserves older content only in missing regions. The expanded image-provider
  and native paint-lifetime suite passes **73/73**, zero skips, including
  partial-load, previous-bitmap, sparse multi-row and clip-failure checks
  (`svg-tile-transparency-and-paint-lifetime.log`, matching TRX). The clip now
  retains cleanup ownership if antialiasing-state restoration fails. This is composition correctness
  evidence, not browser-fidelity or frame-budget qualification.
- Fractional physical viewports now travel separately from integer raster
  allocations through the provider request, protocol v9, ImageBox GPU keys,
  tiles, placement clones and previous-good fallback state. The focused SVG
  integration suite passes **109/109**, zero skips. A new real Win2D regression
  first failed at 125%, 150%, 175% and 225% because a fractional source-rectangle
  crop multiplied already-antialiased provider pixels a second time. Whole and
  tiled painting now keep the complete allocation at the requested physical
  scale. Evidence: `fractional-edge-coverage-{before,after}.log` and matching
  TRX files in `goal-release-20261011/`. This does not fix the native worker's
  separate root-overflow/rectangle-corner antialiasing mismatch.
- The v9 diagnostic worker reduces the KaTeX 125% mean channel error from
  **1.532 to 0.155** and improves SSIM from **0.967710 to 0.998874**; its exact
  alpha IoU remains **0.982748**, below the unchanged **0.995** gate. The new
  odd-sized viewport fixtures also exposed screenshot and Win2D target-size
  rounding in the test harness. Edge now captures an integral CSS extent and
  retains every pixel of the prescribed ceil-sized physical allocation; it
  neither rescales the SVG nor crops to overlapping ink. Capture-bound tests
  pass **10/10**, including exact-buffer reuse (`browser-capture-bounds-v3.log`). The 32-case diagnostic
  remains **14 passed / 18 failed**, zero skips; six of those failures identify
  the analogous Win2D target allocation issue, whose correction awaits the
  next composed run. Evidence: `svg-physical-viewport-v9-integral-capture-*`.
- The full current managed core suite passes **1,821/1,821**, zero skips
  (`managed-core-v9.trx`, 55m34s). The separate layout parity suite still has
  **25/205** failures after the origin-preserving correction above; this core
  result does not qualify Win2D geometry.
- Fresh public pinned top-500 README/asset capture was interrupted before any
  rank results were collected. A guest-log read unexpectedly made WinApp start
  a new, unattached Sandbox epoch; the host orchestrator was stopped and no
  reconnect or host-desktop fallback was attempted. The 320 staged input files
  remain in `artifacts/goal-release-20261011/public-same-byte-top500-20261011-105408-6ebe429468e24bfc97756f1db3f1417e/`.
  This is not corpus completion, native replay, fidelity, or performance proof.
  Further browser/app validation needs an authorized usable Sandbox session.
- The strict, verified-WARP, eight-scale SVG rerun completed **450/688 passed,
  238 failed, zero skipped**. First failing assertions are 229 exact-alpha IoU,
  six SSIM and three mean-channel-delta failures. KaTeX's 182-image fixture
  passes whole-number raster-size scales but fails 125%, 175% and 225%: the
  renderer stretched fractional physical viewports to rounded allocation sizes.
  Viewport/allocation separation is now being implemented through the public
  request, protocol, ImageBox, GPU identities, tiles and previous-good fallbacks.
  Evidence: `goal-release-20261011/svg-strict-fallback-matrix-results` (TRX file)
  and `svg-strict-fallback-matrix-pixels/`. This is not a release pass.
- A pinned resvg pattern defect is corrected: integer tile allocation no longer
  changes the authored repetition period. Both added regressions fail before
  the fix and pass afterward; all four vendored resvg library tests pass. The
  canonical static worker's Sandbox Edge rerun is **6/16 passed, 10 failed**.
  Stripes mean error improves from 45.012 to **0.660** at 125%, 32.417 to
  **0.450** at 175%, and 25.493 to **0.433** at 225%. The 225% case now passes;
  125%/175% SSIM and all eight dots alpha-coverage cases remain below the
  unchanged gates. Evidence: `resvg-pattern-{before,after,library-tests}.log`,
  `svg-pattern-period-static.log`, and its TRX/pixel directories. Worker SHA-256:
  `1674E52F497572C0434EAB168081834991F3145F07831CA82CD69AD027D62C8E`.
- Exact root-local image/vector caption painting regressions pass **69/69**,
  zero skipped, after using the caller's original paint transform. The full
  GitHub suite completes **2,605 passed / 66 failed / zero skipped**; residual
  geometry-rounding, reentrant-arrange and source-contract failures are still
  under investigation. No tolerance or release threshold was relaxed.
- Review found the earlier pixel oracle weakened the named fidelity metrics:
  alpha overlap matched neighboring pixels, while SSIM ignored non-overlapping
  coverage and snapped nearby candidate colors to the reference. The comparer
  now measures exact per-pixel alpha coverage (`sum(min)/sum(max)`) and local
  8x8 premultiplied-luminance SSIM over the entire output, including partial edge
  windows, with no alignment or color replacement. The unchanged thresholds
  now apply to these actual metrics. Comparer regressions pass **13/13**,
  zero skips (`svg-strict-metrics-tests.log`, matching TRX). Earlier corpus
  passes below used the old normalized metrics and **do not qualify the original
  release fidelity gate**. The full current-worker eight-scale rerun is required.
- The persistent Edge driver exposed an oracle configuration defect: the old
  `--use-angle=warp` argument is not a recognized current ANGLE backend. Edge
  logs show GPU initialization failures. A separate private browser context also
  prevents Edge's startup-page lifecycle from retiring the attached fixture page.
  The driver and legacy launcher now request `d3d11-warp`; the persistent driver
  additionally verifies `SystemInfo.getInfo` reports `angle=d3d11-warp` and saves
  backend/version evidence. The tiny viewport and 182-image KaTeX-shaped fixtures
  pass **16/16**, zero skips, over all eight required scales, through provider,
  actual Markdown composition and zero-additional-request GPU-cache-hit paths
  (`svg-verified-warp-smoke.log`, matching TRX and pixel directory). The complete
  corpus and performance gates remain required. Earlier
  screenshots remain evidence of their observed pixels, but are **not qualified
  WARP-backend evidence**. Diagnostic evidence: `goal-release-20261011/
  svg-persistent-{smoke,active-page,detach-diagnostic,crash-diagnostic-02}.log`.
- The corrected provider passes the complete **85/85** same-byte Edge
  SVG corpus at 100% scale, with zero skips. RTL anchor, inherited/nested bidi,
  negative letter spacing and the 182-unique-PNG KaTeX-shaped fixture all pass;
  the latter has exact pixel equality and independently checks every PNG.
  The tests now additionally exercise actual Markdown parsing, image resolution,
  ImageBox, Win2D composition and a second document's GPU-cache hit without
  another provider open/render. The expanded composed suite finishes **84/86**,
  zero skips: the new Unicode/emoji fixture fails alpha IoU (**0.807773**), and
  the tiny-image comparison includes paragraph leading on only the Markdown
  side. The latter test must compare complete image bounds on both paths;
  the Unicode text-advance mismatch remains a real provider failure. Evidence:
  `goal-release-20261011/svg-rtl-results` (TRX), `svg-rtl-pixels/`,
  `svg-composition-v2.trx`, `svg-composition-v2-pixels/`, and
  `svg-composition-smoke-sandbox.log`. These results do not establish the
  full DPI, theme, latency, CPU, memory or top-500 release gates.
- A fresh no-build all-RID Resvg package is **4,824,234 bytes**, below the
  unchanged 5 MiB limit by **418,646 bytes**. Its three packaged worker hashes
  match the rebuilt shipping workers, and all **76** locked native dependencies
  pass source/checksum/license validation. Evidence: `goal-release-20261011/
  resvg-package-v8-fresh-01/`. The workers remain unsigned development artifacts;
  this does not qualify production signing or the separately failing Core+WinUI
  size gate, and it reuses existing managed Release outputs rather than claiming
  a fresh full graph build.
- The published x64 NativeAOT SVG smoke now passes **all raster, retained-resource,
  isolation and shutdown checks**. Actual Sandbox Edge geometry exposed the
  upstream text-layout defects: RTL anchoring did not follow the computed bidi
  direction, and finite negative letter-spacing advances discarded glyphs.
  The pinned, inventory-verified usvg patch preserves those glyphs and applies
  direction-aware anchoring; CSS/inherited direction and nested bidi regressions
  are covered. The unchanged 100-cycle RTL/embedded-PNG workload retains
  **0 handles / 2,314,240 aggregate private bytes**, verifies the worker exits
  before disposal returns, and passes eight startup/shutdown races. Full
  provider tests pass **545/545, zero skips**; Rust tests pass **56**, with one
  existing ignored manual benchmark, and the vendored text tests pass **7/7**.
  Evidence: `goal-release-20261011/resvg-v8-rtl-{publish,full,sandbox}.log`,
  matching TRX, and `rtl-anchor-edge-matrix.log`. All three RID workers rebuild
  and the supply-chain check verifies **76 packages**. The x64 worker SHA-256 is
  `EA3C904B97B854C121D985A03933590D40D2AFCECFD874F9880580A1C77D8972`.
  This closes the formerly failing RTL AOT smoke, not the full Edge-fidelity,
  whole-document memory, performance, or architecture release matrix.
- Pool shutdown now also owns **detached retirees and in-flight worker startup**.
  Removing a worker from a scheduling slot no longer removes it from the
  asynchronous shutdown barrier. A bounded lifetime registry retires late
  startups, drains old font/process owners, and closes the job only after all
  startup reservations finish; ordinary shutdown cancellation does not count as
  a worker startup failure. The real-process regression holds a detached
  worker's font-cleanup task and proves pool disposal cannot return early.
  Full provider tests pass **544/544, zero skips**, and strict x64 NativeAOT
  publish succeeds. The published Sandbox 100-cycle test retains **0 handles /
  2,461,696 aggregate private bytes** and verifies worker exit, while still
  failing the unchanged RTL raster requirement (exit 21). Evidence:
  `goal-release-20261011/resvg-v8-pool-lifetime-{build,focused,full,publish,sandbox}.log`
  and matching TRX. No public API, safety ceiling, or release gate changed.
  A subsequent actual NativeAOT check also exercises **eight immediate
  startup/shutdown races** and verifies no SVG worker children remain after
  each disposal. Normal shutdown during warmup is reported as cancellation,
  not worker failure. The full provider suite passes **545/545**, and this
  published run retains **0 handles / 2,220,032 private bytes** over the unchanged
  100-cycle workload. RTL remains the only failed smoke check (exit 21).
  Evidence: `resvg-v8-startup-lifetime-{build,publish,full,sandbox}.log`.
- An image resolver completing successfully inside a document cancellation
  callback could win `WaitAsync` before cancellation propagated to its linked
  token. The deadline wrapper now rechecks the original document token and
  deadline token before publishing the result, so abandoned successful results
  use the existing exactly-once cleanup path. All **16** actual deadline tests
  pass in the isolated linked-source regression project (`deadline-regression.log`).
  The broader run started before this fix finished **1,819/1,820**, zero skips,
  with only that known cancellation failure (`managed-core-v8-tests.log` and
  `test-results/managed-core-v8.trx`). A current-source full rerun is in progress
  (`managed-core-v9-tests.log`); neither result is presented as a full-suite pass.
- PR #99 still points to `1c00c83ddfd2e8b65e05cb57e359a3f092b37271` and its
  historical preview CI failed on `ForegroundReadinessWaitsForAQualifiedSample`
  before packaging or benchmark stages ran. The current compiled-source
  visibility regression suite passes **25/25** (`core-ci-visibility-regression.log`
  and TRX); this local result does not change the remote CI conclusion or prove
  the release benchmark.
- The real NativeAOT Sandbox 100-cycle render/evict test now reaches and passes
  its **retained-resource and shutdown** checks. It exposed two production bugs:
  per-mapping `WindowsIdentity` owners leaked two host token handles per cycle,
  and async worker disposal awaited font preparation but not process exit.
  Identity ownership is now deterministic; async disposal awaits the native
  process signal and callback cleanup, and fails on a shutdown deadline. The
  initial run grew **200 handles / 12,210,176 aggregate private bytes**. After
  both fixes and replacing oversized async XML I/O buffers with stream-sized
  synchronous reads of already-admitted memory on the existing background
  preflight path, the identical 100 cycles grew **0 handles / 2,248,704 private
  bytes**, below the unchanged 4-handle / 8 MiB ceilings. The exact worker is
  verified exited before `DisposeAsync` returns; no forced GC or settling delay
  was introduced. Full provider tests pass **537/537, zero skips**. The smoke
  still returns failure for RTL fidelity; independent failed pixel checks are
  recorded allocation-free and no longer prevent resource measurement.
  Evidence: `goal-release-20261011/resvg-v8-{resources,lifetime,preflight}-sandbox.log`,
  `resvg-v8-preflight-{publish,full-final}.log` and matching TRX. This is one
  x64 workload's lifetime result, not proof of all renderer memory/release goals.
- Protocol **v8** now supplies installed fonts through host-verified, read-only
  file mappings to the restricted worker, without granting filesystem access or
  parsing font bytes in the host. A fixed, bounded table is acknowledged only
  after every mapping succeeds; glyph-producing warmup must succeed before text
  readiness. Refresh admission retains the published catalog plus at most one
  replacement, with nonblocking busy retries and explicit publication probes.
  Host and worker charge the same 64 KiB-rounded mapping extent; private-memory
  job limits and content deadlines remain unchanged. Rust tests pass **56/56**
  plus **one existing ignored manual benchmark**, and the full provider suite
  passes **525/525, zero skips**, including all three previously failing CJK/
  font-generation cases. The locked native inventory again verifies **76**
  packages, checksums and compatible license selections. All three worker RIDs
  rebuild; x64 SHA-256 is
  `5EC24F8CC0A9FEEA4B1833BB0CC945EF741A594DE5E45974BDFA81E9ED83998E`.
  Evidence: `goal-release-20261011/resvg-v8-{rust-final-tests,full-final,
  supply-chain}.log`, `resvg-v8-bounded-win-*.log`, and matching TRX results.
- The protocol-v8 NativeAOT publish succeeds with linker warnings treated as
  errors; that setting is now persistent in the smoke project. The Sandbox
  rich-render gate still **fails**, now because RTL ink is clipped at x122–127
  instead of anchoring leftward. The exact SVG rendered by Edge 154 in the same
  Sandbox has ink at x77–119, y36–51; the screenshot was inspected. This is a
  real renderer fidelity defect, not a fixture waiver. The 100-cycle memory
  assertion still has not been reached. Evidence: `resvg-v8-aot-{publish,
  sandbox}.log`, `rtl-anchor-edge.log` and `rtl-anchor-oracle/{rtl.svg,edge.png}`.
- Immutable placement/anchor regressions pass **64/64**, including a 100,000-band
  lookup fixture. A snapshot-owned sparse realization-plan candidate index now
  removes the control's walk over ordinary text blocks while preserving semantic
  order, measured-generation admission, code-block atomicity and lease fences;
  its focused suite passes **25/25**, with a zero-warning/error Release x64 build.
  The subsequent full default GitHub suite has exposed additional placement and
  fallback failures and is under investigation. Focused passes do not qualify
  this layout change or establish a document-scale timing win.
- The isolated SVG worker now passes a real **NativeAOT Sandbox** render. The
  startup failure was two separate defects: the restricted primary token needed
  a user+restricted-code default DACL, and named-section access still failed with
  Win32 error 5 in the separate low-integrity process. Protocol **v7** now transfers
  an anonymous section through a non-inheritable, read/write-only duplicated
  handle. The host owns that remote handle until the response is validated; on
  cancellation, transport failure or malformed reply it retires the worker
  before reclaiming the handle. The worker owns only its mapped view. No token,
  integrity, inherited-handle or in-process-fallback restriction was relaxed.
  `goal-release-20261011/resvg-handle-aot-sandbox.log` records exact worker-path,
  RC-only restricting SID, low integrity, disabled dangerous privileges, no
  thread impersonation, job membership, queryable mitigations and default-DACL
  checks plus verified green raster output. Heap-termination-on-corruption is
  requested but not claimed verified through the documented query API.
  Rust validation passes **47 tests**, with **one existing manual benchmark
  ignored**, in `resvg-handle-rust-tests.log`. Broader provider tests, repeated
  lifetime/fault checks, fresh UI rendering and all architecture/release gates
  remain required; this is not browser-parity or leak-free release evidence.
- The expanded provider suite now runs real protocol-v7 mapping/transport fault
  cases (wrong IDs/flags, partial responses, pipe loss, caller cancellation,
  deadlines and concurrent disposal), all passing. It also exposed a real
  capacity defect: every tiny source reserved the full 8 MiB document ceiling,
  rejecting twelve visible SVGs before parsing. Initial admission now reserves
  the exact clone length, while normalization still separately reserves its
  bounded overlap before allocation. The twelve-document regression and an
  exact-reservation regression pass; aggregate ceilings are unchanged.
  `resvg-exact-source-tests.log` records **512/515 passing, zero skips**. The
  three failures all concern missing text pixels and remain unwaived.
- The richer NativeAOT Sandbox smoke correctly **fails** on absent RTL text
  after verifying isolation and both embedded raster regions. Its 100-cycle
  resource gate therefore has not run and is not qualified. Evidence:
  `resvg-aot-lifecycle-{publish,sandbox}.log`. The restricted worker's Windows
  font access is under investigation; a successful empty font catalog must not
  be mistaken for working text support. Production token restrictions and the
  original performance/memory requirements remain intact.
- A normal locked restore and fresh default Release/x64 graph build now pass.
  The full default GitHub suite passes **2,657/2,657**, zero skips, clearing the
  three previously retained mixed diagnostic/default Resvg dependency failures.
  Evidence: `MarkdownRenderer/artifacts/goal-release-20261011/` contains
  `github-default-{restore,build,tests}.log` and `test-results/github-default.trx`.
  This result precedes the internal carrier-size cleanup below.
- The fresh app NativeAOT publish passes for **x64**, including native compilation,
  artifact verification, locked dependency/feed/vulnerability checks and
  **129/129** release-security tests. Three CsWinRT1028 errors were fixed by
  making the image source/candidate/cancellation owners and nested parent partial;
  no AOT warning was suppressed. The explicit publish is
  `goal-release-20261011/jithub-aot-win-x64`; restore/publish logs are
  `jithub-aot-{restore,publish}-third.log`. This is not the full architecture matrix.
- The Sandbox guest now starts with input and capture ready. The release smoke
  has an explicit AOT mode that verifies the supplied publish directory and
  compares the launched guest executable hash. It launched a real native process
  with no managed runtime modules, then correctly failed an identity check that
  still used the pre-publish hash: WinApp's required `--aot` publish changed the
  executable. The published host and guest hashes both equal
  `745f3286c4742b3c844c48a087283b642c646db3ba73869bd042ae34b5169084`.
  The script now re-verifies the post-publish artifact before comparing guest
  identity and checks for host changes during the smoke. **13/13** offline smoke
  cases pass, including stale-hash, wrong-PID and managed-runtime rejection.
  A fresh successful UI/screenshot run is still required; neither this failed
  receipt nor the previous unpublished JIT apphost attempt is a UI pass.
- Twenty-three non-public readonly record carriers have been converted to
  readonly structs, preserving constructor/default/`with` behavior and the
  dimension equality used by the performance cache. Focused regressions were
  added. Fresh locked restores, serial Release/x64 rebuilds and packing succeed
  for all **14** shipping packages. The separate strict license inventory reports
  no unknown or incompatible dependency finding, with SBOM/notices and native
  asset checks passing. Exact evidence is `goal-release-20261011/full14-candidate-
  073009/compliance-073451`. Size still fails: Core+WinUI are **730,805 compressed
  bytes / 537,600 cap** and **1,750,016 managed bytes / 1,258,291 cap**. The cleanup
  saves **28,160 managed bytes**, leaving **491,725 bytes** excess. Post-change
  default tests remain to be rerun; skipping size in the independent license
  audit is not an overall compliance pass.
- An artifact-only pinned libwebp feasibility probe decodes the oversized
  2048×8193 lossless WebP to 256×1025 with **8,894,328 bytes** of tracked decoder
  allocation highwater. This is not a shipping isolated decoder. The earlier
  integer-height Edge comparison (SSIM **0.984679**) used different display
  geometry: the real 256-DIP-width image has height **1024.125 DIPs**, while its
  raster has 1025 rows. Corrected fractional-layout investigation remains below
  the **0.995** geometry threshold and is still artifact-only; it does not qualify
  final Win2D composition. No new decoder is enabled and no fidelity or speed
  gate is waived.
- The existing full-500 CI artifacts were retrieved and rechecked: **378 pass /
  122 fail**, including **114** first-viewport readiness timeouts. The remaining
  failures include valid images reported unavailable and incomplete traversal.
  These are historical published-head failures, not a fresh local audit. The
  high-impact immutable placement-generation replacement is being implemented
  across paint, hit-testing, selection, UIA and hosted geometry; a viewport-only
  offset overlay is not accepted as a solution to blocking suffix reflow.

Full counterbalanced performance, same-byte Edge qualification, the complete
500-README audit, package size, native memory and the complete architecture/
device/theme/signing matrix remain open. PR #99 still points to `1c00c83`; its
historical CI results are not evidence for this uncommitted source revision.

## Previous priority and evidence — October 5, 2026

Latest full diagnostic-suite-verified local implementation: **AV-b + AT/AU + AS + AR + AO/AP-c + AN-b +
AJ-b + AI + repaired AH + AG/AF**, with the ineffective AQ fragment experiment
removed. At this checkpoint default validation retained three separately
identified optional Resvg dependency mismatches (cleared by the fresh October 11
default graph above); release gates remained unmet.
The subsequent diagnostic-only AX-a run passes **2,685/2,685**, no skips,
including all 12 physical public API rows, against unchanged frozen AV-b products.
AX changes no production code; it adds the two real builtin suffix profile cases
described below, not a completed optimization or release qualification.

AV removes the remaining unleased UI-thread focus-list reads during initial
snapshot adoption and task-availability refresh. Its internal capture requires
nonblocking snapshot admission, preserves the fixed-array fast path and exact
dynamic overflow order, and returns only value identities. One scalar-only
generation/document/revision/request ticket retains a Busy retry on the existing
weak recovery clock. New documents clear old focus before publication callbacks;
same-document Busy retains the complete old list. Capture and publication are
separate: native/retirement callbacks finish, snapshot/revision/epoch/ticket fences
pass, then the currently focused identity is mapped into the staged list. No
pre-callback restore can undo newer navigation. A throwing overflow query leaves
the old plans intact, releases the lease, clears only its matching ticket and
preserves its original exception stack through existing failure handling instead
of entering a repeated 100 ms poison-query loop. No public API, task-editing
feature, dependency, audit policy or resource ceiling changed.

Normal final AV-b diagnostic/default Release builds pass in **142.61 / 148.61
seconds**, zero errors and three existing unsuppressed NU1900 warnings each.
This does not claim fresh vulnerability-feed access. Focus passes **227/227**,
no skips. Full diagnostics pass **2,683/2,683**, no skips, all 12 physical public
API baseline rows, in 2 minutes 6 seconds. Default passes **2,654/2,657**, no
skips, in 2 minutes 24 seconds; only the same three borrowed diagnostic Resvg/
normal-default dependency mismatches remain, not waived. Four new facts exercise
real snapshot admission, independent dynamic eligibility/order, Busy relayout,
recovery and retirement. The additional Control contract is explicitly source-
only, not live dispatcher/UIA proof; the existing realization contract now
requires capture under lease and staged publication after the final fences.
Final source/binary hashes, exact receipts and unchanged release-gate failures
are in `focusable-plan-av-verification-summary.json`, `source-focusable-plan-av-b`
and `renderer-runtime-focusable-plan-av-b-{diagnostics,default}` under the mixed-
vector artifact root. Earlier AV-a custody is retained. Default Core/WinUI DLLs
total **1,778,176 bytes**, still above the unchanged **1,258,291-byte** cap; no
fresh compression or first-viewport/Edge-relative AV speedup is claimed.

Read-only attribution of the frozen AV-b default renderer confirms **883,444
metadata bytes**, **632,343 IL bytes**, 849 type definitions and 6,903 method
bodies. The required WinRT generic-helper and vtable namespaces together account
for **22,747 IL bytes**, including the global lookup (not counted twice).
Metadata growth is distributed across methods, parameters, attributes, fields,
names and signatures; generated source-text length is not packaged size. The
largest direct IL contributors are the control, image, inline and snapshot code.
Two independent read-only reviews found no isolated ABI/generator/helper removal
that safely closes the **519,885-byte** aggregate excess. Exact frozen hashes,
heap/table counts and attribution limits are in
`package-size-attribution-av-b.json` under the same artifact root. No optimizer,
public package boundary, dependency, warning policy or size-accounting rule was
changed. This is evidence for a substantial implementation-slimming redesign,
not a completed size milestone or permission to relax the cap.

AT makes selection-adorner refresh one nonblocking snapshot read: Busy retains
the prior surface and coalesces one frame-paced retry. Visible selection geometry
uses the existing viewport index and bounded child traversal instead of scanning
every root; foreground painting, endpoint rectangles and selected-point hit tests
consume the same admitted generation. Endpoint crossing keeps the physically
captured handle independent of its logical endpoint role. Recognized logical
gesture waiters do not capture, block layout publication or force synchronous
measurement. They retain viewport-relative point/contact values, wait for both
measured geometry and its UI/anchor publication, and replay in FIFO order. Raw
unowned touch release/cancel cannot deadlock that publication. Real captures and
drag sessions still fence snapshot transfer. Document replacement cancels old
source-bound waiters after the authoritative commit/retirement; it never rebinds
them to a new graph. Native captures and rebuild readmission run after lease
release. Lifecycle, terminal geometry recovery and unavailable retry routes clean
up the pending work without discarding logical selection.

The expanded native code-selection oracle exposes two real defects: RTL
horizontal placement applies overflow twice, and displaced code chunks reject
hits that are inside their parent code viewport. Both are fixed. DirectWrite
geometry, selection/foreground WARP pixels and caret rows now agree at zero,
middle and maximum overflow for LTR/RTL at 100%, 150% and 200% simulated scaling.
Ordinary inline hit-testing still rejects points outside its own horizontal box;
only the clipped code parent can authorize the overflow-native query. These are
not physical touch, mixed-monitor or theme-matrix results.

AU removes selection range from the neutral native selection-text-layout cache
key. Range changes reuse the same full styled clone and apply the current exact
clips/decorations at paint time. Native text/style/width replacement and color
changes still invalidate it; cancellation, failed creation, prepared transfer
and durable retirement preserve the existing owner. Normal final diagnostic/
default builds pass in **55.94 / 37.31 seconds**, zero errors, three existing
unsuppressed NU1900 connectivity warnings each. Pure queue focus passes **6/6**,
touch/input focus **45/45**, expanded renderer focus **117/117**, no skips. Full
diagnostics pass **2,678/2,678**, all 12 physical public API rows, in 1 minute
56 seconds. Default is **2,649/2,652**, no skips, in 2 minutes 4 seconds: only
the retained optional Resvg diagnostic/default type mismatch remains in three
tests, not waived. Four obsolete gesture source-wiring contracts were repaired
to require logical-waiter versus physical-owner fencing and post-lease
readmission; all failed receipts remain retained.

Eight quiet counterordered launches per diagnostic verify the work reduction.
The same-runtime **100,000-root / 12-refresh / eight-visible-rectangle** traversal
queries **1,200,000 -> 96 leaves** and allocates median **297,601,632 -> 9,216
bytes**. Synthetic whole-loop medians are 185.9495 / 0.024 ms, not a native-page,
frame-budget or browser-relative result. A separate hash-pinned old/new/new/old
then new/old/old/new native WARP paint diagnostic uses byte-identical test/runtime
files except the renderer/performance DLLs and PDBs. Across 120 range changes,
selection-layout replacements and creator-device accesses are **120 -> 0**;
managed allocation medians are **178,776 -> 112,712 bytes**, **66,064 less**.
CPU command-submission loop medians are 11.0455 / 4.526 ms; GPU completion is not
measured. All 16 launches pass exactly once, with strict scalar receipts and no
owned competing build/test/trace or agent work in either measurement window.
No first-viewport speedup is measured or claimed for AT/AU. Scripts/receipts and
hashes are recorded in `selection-performance-at-au-verification-summary.json`
under `MarkdownRenderer/artifacts/mixed-vector-root-20261004/`, with immutable
`source-selection-performance-at-au-h` and final `renderer-runtime-selection-
performance-at-au-f-{diagnostics,default}` custody. Default Core/WinUI DLLs now
total **1,775,104 bytes**, still above the unchanged **1,258,291-byte** cap; no
fresh package-compression result is claimed.

AS caches the code block's visual-line template against exact chunk/native-metric
identities and canonical text offsets. Vertical placement recomputes tops from
fresh pixel-snapped origins using the original sequential-double recurrence,
reusing row/edge storage; unchanged origins (including horizontal scrolling)
skip row reconstruction entirely. Width/native remeasurement invalidates the
template. Missing/invalid metrics clear the complete index and its borrowed
metric references; recovery and private clone adoption build fresh indexes.
Gutter digit calculation no longer allocates an invariant decimal string.
No public API, native ownership, cache/resource caps, harness or GC policy changed.
Normal diagnostic/default production builds pass in **66.36 / 56.85 seconds**,
zero errors, three existing unsuppressed vulnerability-feed connectivity warnings
each. Existing focused regressions pass **237/237**. Independent native-line
oracles cover all eight simulated scales, LTR/RTL, far/fractional origins, wrapping,
highlighting, cancellation, invalid metrics and clone adoption. Initial AS-a/b
focus passes 27/28: the fixture had independent disclosure-state identity and
CRLF-versus-canonical-LF chunk mismatches. AS-c shares the immutable context state
and partitions the actual `CodeText`; per-chunk and whole-shell preflight asserts
preserve the strict production rules. Final focus passes **28/28**, no skips.
Full diagnostics pass **2,621/2,621**, no skips, all 12 physical public API rows,
in 2 minutes 40 seconds. Default passes **2,592/2,595**, no skips, in 2 minutes
33 seconds; only the same three optional Resvg diagnostic/default dependency
mismatches remain, not waived. Failed fixture receipts are retained.

Eight quiet AR/AS/AS/AR then AS/AR/AR/AS launches run the exact same hash-pinned
test assembly against the frozen old/new production assemblies. For 120 placements
of a real WARP-native **2,056-visual-line / eight-chunk** block at 150% scaling,
median allocation is **28,827,200 -> 0 bytes**; the separate 120 same-origin
horizontal changes are **25,625,256 -> 0 bytes**. All eight tests pass once, no
skips, with unchanged native text layouts and independently checked exact geometry.
Rounded whole-loop elapsed medians are 39 / 1.5 ms for placement and 24.5 / 0 ms
for horizontal changes. The latter is below the coarse reported millisecond
resolution, not a zero-time claim. Vertical placement still validates/visits rows;
this does not remove the document-wide suffix Arrange loop. A separate eight-run
unchanged quick first-viewport harness observes primary 1 MiB cache-hit allocation
medians **16,058,060 / 16,052,320 bytes**, only **5,740 bytes less**. Viewport
medians 40.99 / 38.77 ms and snapshot medians 13.98 / 13.63 ms are diagnostic,
not a qualified general page-load speedup. All eight reports exit zero, schema 12,
`passed=true`, `isReleaseEvidence=false`, with unchanged harness/runtime policy,
warmups and measured-GC behavior. No owned competing build/test/trace ran in
either measurement window. Scripts and receipts are `Measure-CodeLinePlacementAS.ps1`,
`code-line-cache-as-counterordered-placement-c.log`,
`Measure-CodeLineFirstViewportAS.ps1` and
`code-line-cache-as-counterordered-firstviewport-a.log` under the mixed-vector
artifact root. Frozen AS-c tests/source and AS normal products remain separately
retained. Current normal default Core/WinUI DLLs total **1,754,624 bytes**, above
the unchanged 1,258,291-byte managed cap; no fresh AS NuGet compression result is
claimed. PE attribution finds 619,571 IL bytes and an 874,096-byte metadata directory
in the 1,576,448-byte renderer DLL, so removing tiny helpers is not a sufficient
package-size strategy.

The broader suffix redesign remains an open placement milestone; its next
implementation must first demonstrate a material shared-gate cost on a
representative builtin/composite workload. The proposed narrow AW slice was
rejected by two independent impact reviews before integration: changed-root
graphs are capped at **256 total nodes**, their native Measure already runs
privately, and moving their remaining scalar Arrange would add document-wide
capture/validation/storage alongside the existing fused suffix pass. It would
not remove unchanged composite suffix work. The uncompiled, untested scaffold
and draft tests are preserved byte-for-byte outside compilation in
`rejected-prepared-suffix-aw/` under the mixed-vector artifact root, with exact
hashes and correctness constraints in its README. No existing production source
was changed by AW, no runtime/test result is attributed to it, and it is not
counted as progress toward a gate. Existing synthetic leaf-suffix timings are
not representative native/composite lock-duration evidence.

AX-a now measures the actual unchanged builtin suffix using an ownership-safe
paragraph renderer for two zero-height marker roots, with the same marker/source
topology in the independently executed legacy oracle. Eight quiet fresh-process
launches pass **16/16** rows, no skips. The near-1 MiB README-shaped derivative
has **2,320 roots / 10,672 boxes**, 288 tables and 288 code blocks; the adversarial
nested-list/table/code fixture has **487 roots / 19,986 boxes**, 2,049 table rows
and 5,039 visual lines. Candidate height changes from **53.28125 to 277.28125
DIPs**, must take the committed private-root path without recovery, and enters
the native WIC decoder. Exact descendant bounds, child/row/chunk/visual-line
indexes, measured viewport slots, full focus order and one representative
selection/hit match the independent legacy digest in every launch. Snapshots
retire sequentially, cache keys are isolated, and hashing/assertions/cleanup
remain outside the timed interval.

The marker-bracketed Arrange subloop, with layout-lock ownership checked at both
ends, has median **2.2145 ms** (range **1.178–4.626**) for the README-shaped graph
and **2.273 ms** (range **1.771–2.981**) for stress. This is only a **lower bound**
on the locked commit: it excludes other preparation/validation/final publication
work, and is not an upper bound, callback p95, frame guarantee, image-memory or
Edge comparison. Legacy warms the candidate; the mixed fixture restores its
last incomplete generated section with prose, and markers alter topology, so it
is not the exact release corpus. No forced GC or production instrumentation was
added. Full diagnostics pass **2,685/2,685**, no skips, all 12 physical API rows,
in **2 minutes 20 seconds**. Test-only compilation, frozen product hashes, source
custody, scalar samples and oracle limits are in
`suffix-gate-ax-verification-summary.json`, `source-suffix-gate-ax-a` and
`renderer-runtime-suffix-gate-profile-ax-a-diagnostics` under the mixed-vector
artifact root. These samples do not justify adding the rejected extra
document-wide pass or close the broader placement gate; the directly measured
large native-codec peak remains the higher-impact memory failure.

A useful broader protocol must atomically pair
authoritative root geometry, viewport index and metadata, preserve the exact float
continuation, and cover live-measure/read/fallback paths; an index-only or estimated-
Bounds-only overlay would split generations. The independent review finds no
high-impact leaf-only shortcut: base leaf Arrange is already trivial, whereas
Stack, ListItem, Table, CodeBlock and VectorScene origins/indexes depend on their
own exact float/order equations. A useful implementation needs typed value-only
placement inputs and prepared descendant bounds plus child/row/chunk indexes,
computed off-lock, then an allowlisted, noncancelable scalar/index commit after
snapshot/revision/width/mask/root/native-owner/cancellation fences. Unknown or
custom graphs retain the existing fallback. The independent legacy/fresh Arrange
oracle must cover descendant geometry, selection/hits/focus/UIA/hosted plans,
fractional/far origins, bidi/DPI and stale/canceled publication before accepting
it. That protocol is not implemented or counted as a completed milestone.
Native decoder scratch memory,
package-size limits, the approved-reference full release benchmark, same-byte Edge
corpus, complete top-500 audit and physical architecture/device/DPI/AOT gates
remain open. These local AS/AT/AU/AV results do not satisfy or weaken any release gate.

AR removes the separate List/backing array from the common one-run inline and
one-child stack cases, using one shared internal `SingletonMutableList<T>`.
Captured read-only views remain live, promotion retains ordinary List growth,
and concrete/interface empty enumeration, versioning, null items, detach,
reindexing and ownership transfer preserve independently checked .NET 10
contracts. Box changes are limited to their backing collection fields; public
signatures, layout and native ownership logic, resource limits, cache policy
and GC/harness settings are unchanged. Normal diagnostic/default production
builds pass (202.92 / 173.64 seconds, zero errors, three existing vulnerability-
feed connectivity warnings each). The first test compile catches an
unconstrained generic identity-test helper; adding the same reference-type
constraint as production preserves the identity assertion and fixes it.
Focused validation passes **383/383**, no skips. Full diagnostics pass
**2,593/2,593**, no skips, all 12 physical API baseline rows passing, in
2 minutes 1 second. Default passes **2,564/2,567**, no skips, in 1 minute
57 seconds: only the same three optional Resvg diagnostic/default dependency
mismatches remain; they are not waived. The structural census retains all
15,845 unique boxes and the exact inline/stack histograms.

Eight quiet frozen AP/AR/AR/AP then AR/AP/AP/AR launches observe primary 1 MiB
cache-hit allocation medians **16,662,864 / 16,058,032 bytes**, **604,832 bytes
(3.63%) less**. All eight exit zero, schema 12, `passed=true`,
`isReleaseEvidence=false`, using unchanged harness/runtime-policy hashes,
warmups, measured-GC behavior and one primary sample. No owned competing
build/test/trace runs in that window. Viewport medians are 41.88 / 39.79 ms;
snapshot medians 21.16 / 11.85 ms. These quick timings do not qualify a release
or browser-relative claim. Exact custody/results are retained in
`Measure-SingletonBoxesAR.ps1` and
`singleton-boxes-ar-counterordered-quick-a.log`. Default packages contain
**741,783 compressed / 1,753,088 managed bytes**, zero native entries: both
unchanged lean caps still fail. Frozen product/test/source receipts are under
`renderer-runtime-ar-singleton-boxes-a-{diagnostics,default}` and
`source-ar-singleton-boxes-c/`. The required semantic-output allocation and
shared-gate suffix reflow remain larger architectural work, not solved by AR.

A separate direct cumulative AG/AR/AR/AG then AR/AG/AG/AR comparison uses the
same frozen harness and byte-identical generated 1 MiB workload; it does not
multiply earlier checkpoint percentages. Primary cache-hit allocation medians
are **19,806,116 / 16,057,900 bytes**, **3,748,216 bytes (18.92%) less** across
the combined accepted local changes since AG. All eight launches exit zero,
schema 12, `passed=true`, `isReleaseEvidence=false`; no owned build/test/trace
runs concurrently. Viewport medians are 61.99 / 34.94 ms and snapshot medians
32.97 / 10.89 ms, diagnostic only, not a qualified latency or browser-relative
result. Exact hash-pinned product/harness custody and reports remain in
`Measure-CumulativeGraphAR.ps1` and
`cumulative-graph-ar-counterordered-quick-a.log`. The approved-reference full
counterbalanced release benchmark, same-byte Edge corpus, complete top-500
audit, native decoder memory, suffix reflow, package limits and device/AOT
matrix remain open; these local observations do not waive any gate.

The directly measured large native-codec peak remains the highest-impact
memory failure. Prioritize bounded codec scratch memory; require representative
shared-gate measurements before selecting a broader suffix-placement redesign,
and address the failed package-size gate without moving required bytes outside
the accounting boundary. All three remain implementation work;
another unproven fragment/pooling micro-optimization is not the next milestone.
The AX read-only source audit confirms the exact proposed libwebp commit
`e7a9045111cfb6349affc37e64d6664f5f77dc93` is absent from the repository and
configured `E:/packages/cargo` registry/Git source caches. The available
`image-webp 0.2.4` is an independent MIT/Apache-licensed Rust implementation,
not that decoder; its already measured full-buffer VP8L path is not a substitute.
`native-codec-source-availability-ax.json` records the checked scope and precise
next prerequisite: the pinned source/license/build inputs on a network-enabled
normal runner, followed by actual scratch-memory, isolated ordinary-raster
worker and fidelity qualification. Source acquisition alone would not close
the memory gate; no new runtime/dependency or weakening fallback was introduced.
The mandatory native/release proof also needs an authorized Windows runner
with a normal primary token, functioning network access, Edge/Windows Sandbox
and the NativeAOT toolchain, plus a clean writable descendant checkout and an
approved reference. This restricted session cannot supply those conditions:
restricted-token creation returns Win32 87; sandbox automation is unauthorized;
Edge fail-fasts; the configured proxy blocks dependency/artifact access; a
fresh optional provider's mandatory vulnerability audit fails; NativeAOT task
hosting fails; and Git metadata is read-only. No security, audit, sample,
reference or gate-policy workaround has been substituted. PR #99's existing
head cannot validate these unpublished local sources.

The earlier combined AO/AP checkpoint replaces four construction-only stack
enumerations with indexed traversal and retains singleton semantic children
directly before list promotion. AP-a builds pass, but its expanded focus is
162/170: eight independently-oracled exhausted non-generic `Current` cases
expose a pre-existing collection-view mismatch, reproduced unchanged against
AN-b (6/14 pass). AP-b separates exhaustion from later child-count changes;
normal diagnostic/default rebuilds pass (68.76 / 68.60 seconds), and focus is
168/170. The two remaining cases distinguish .NET's captured empty-list
interface enumerator from its versioned value enumerator. Their independent
oracles and failed receipts are retained. AP-c preserves the versioned concrete
value enumerator and the BCL empty-list interface fast path separately, without
boxing the view in the adapters. Its normal diagnostic/default rebuilds pass
(52.76 / 52.84 seconds), and expanded focus passes **181/181**, no skips,
including a new real linked-empty-alt image focus-order fixture. Full diagnostic
validation passes **2,561/2,561**, no skips, in 2 minutes 32 seconds; all 12
physical public API baseline rows pass. Default passes **2,532/2,535**, no
skips, in 2 minutes 34 seconds: only the same three optional Resvg dependency
mismatches remain. Eight quiet frozen AN/AP/AP/AN then AP/AN/AN/AP launches
observe primary 1 MiB cache-hit allocation medians **17,647,720 / 16,662,464
bytes**, **985,256 bytes (5.58%) less** for the combined indexed-stack plus
singleton-child checkpoint. All eight exit zero, schema 12, `passed=true`,
`isReleaseEvidence=false`, with unchanged harness/runtime-policy hashes,
warmups, measured-GC behavior and one primary sample. No owned competing
builds/tests/traces ran in that window. Viewport medians are 54.70 / 49.61 ms;
snapshot medians 27.05 / 23.63 ms. Those quick timings are not a qualified
release/browser comparison, and no individual AO-only gain is claimed. Exact
reports/stages and the hash-pinned `Measure-SingletonChildrenAP.ps1` are retained
with `singleton-children-ap-counterordered-quick-a.log`; no approved release
baseline was selected or substituted.
AP-c default packages total
**740,293 compressed / 1,750,528 managed bytes**, zero native entries; both
unchanged lean caps still fail.

The README console diagnostic now preserves an allow-listed native failure
category plus numeric HRESULT, never exception text, URLs, paths or README
content. Its normal automation build passes (12.45 seconds, zero warnings/
errors). App source-linked tests initially exposed four missing SVG metadata
type references; linking the real `SvgIntrinsics` and `SvgSourceMetadata`
helpers repairs that build (38.06 seconds, zero warnings/errors). Focus passes
44/44, no skips. The first frozen runtime was placed below MarkdownRenderer's
own `Directory.Build.props`, causing one repository-location fixture failure;
the same unchanged assembly passes from the correct app-test artifact location.
All failed receipts remain. The first full app suite is **3,569/3,570**, no
skips: a stale linked-image source assertion still searches `LayoutSnapshot`
for a predicate now owned by `LayoutSnapshotIndexBuilder`. The updated
architecture-aware contract preserves all Hyperlink/Invoke/control checks and
asserts semantic observation precedes decorative-image filtering. Its fresh
normal build passes (38.98 seconds, zero warnings/errors); the fresh full app
suite passes **3,570/3,570**, no skips. This diagnostic improvement does not
establish that the 19 failing top-500 shards are fixed.
PR #99 still tests the September 28 head, not these uncommitted local changes.

The rejected AQ experiment targets the profiled semantic text accumulator's
duplicate mutable UTF-16 buffer. Its frozen implementation borrows immutable source
slices, retains its first fragment inline, coalesces adjacent slices/repeated
characters, and materializes one final exact-length string. Exact expected-text
identity reuse remains unchanged. Arbitrary spans are copied immediately;
large spans use the existing buffer path rather than retaining caller memory.
Final copying and spill are chunk-cancelable. Cumulative descriptor-allocation
cost, including discarded growth arrays and trim/reappend churn, triggers a
normal StringBuilder fallback; the 65,536-descriptor cap also spills, never
rejects valid text. Independent StringBuilder oracles cover every UTF-16 offset
on modest inputs, mutable-span snapshots, expected identity/mismatch, trimming,
cancellation, density and hard-cap boundaries. Normal diagnostic/default
production builds pass (64.26 / 90.93 seconds, zero errors, the same three
mandatory-audit connectivity warnings). Initial focus passes 111/111, and the
first full diagnostic suite passes **2,573/2,573**, no skips, all 12 public API
baseline rows passing, in 2 minutes 10 seconds. A thirteenth fact protects the
intentional cumulative allocation policy across dropped/reallocated fragment
lists using platform-specific estimates; updated focus passes **112/112**, no
skips. Final diagnostic validation passes **2,574/2,574**, no skips, in
2 minutes 49 seconds; default is **2,545/2,548**, no skips, in 2 minutes
21 seconds, retaining only the same three optional Resvg assembly mismatches.
Eight quiet matched AP/AQ/AQ/AP then AQ/AP/AP/AQ launches all exit zero,
schema 12, `passed=true`, `isReleaseEvidence=false`, with the unchanged frozen
harness/runtime hashes, warmups, measured-GC behavior and one primary 1 MiB
cache-hit sample. Their allocation medians are **16,662,464 / 16,665,952
bytes**, **3,488 bytes worse**, not an improvement. Viewport medians are
52.21 / 51.26 ms and snapshot medians 25.40 / 28.05 ms; no qualified timing
conclusion follows. AQ is correctness-verified but is **not accepted as a
performance milestone**; its ineffective implementation has since been removed,
with the original source and measurements preserved. No owned competing build/test/trace
ran in the measurement window. Exact custody and results remain in
`Measure-SemanticFragmentsAQ.ps1` and
`semantic-fragments-aq-counterordered-quick-a.log`. A fresh attribution-only
EventPipe capture deliberately retains runtime-contract failure; it is not
performance evidence. Frozen AQ default packages total **743,166 compressed / 1,755,648
managed bytes**, zero native entries; both unchanged lean caps still fail.
The frozen AQ structural census and reflection-only builder replay pass
**27/27**, no skips (`mixed-census-aq-focus-c.trx`). The same generated 1 MiB
bytes, GFM profile, width and lazy band are used, but the neutral test theme
and WARP device make this structural evidence, not first-presentation timing
or fidelity. Of 7,493 inline containers, **7,204 have one run**; of 6,912
stacks, **4,896 have one child**. There are 2,309 roots and 15,845 unique boxes;
both histogram totals and traversal cancellation are checked. A fresh semantic
builder first materializes at root ordinal **2**, an inline container: before
that root there are four fragments, capacity four, 63 UTF-16 characters and
120 estimated descriptor-allocation bytes; after it text length is 355.
This narrows the ineffective AQ path to the early mixed paragraph, not the
final text copy itself. The next measured implementation targets singleton
run/child storage, with stable live views and independently-oracled enumeration,
transfer and disposal semantics. The factory source link required exactly two
`global::System.Math.Min` qualifications; reversing those changes reconstructs
the recorded pre-edit SHA-256, and the frozen performance harness is unchanged.
Static review identifies early descriptor density and cumulative geometric
backing-array growth, not trimming (terminal trimming occurs only after the
whole builder traversal). The unproductive AQ fragment implementation is now
removed from working source, restoring the previously verified buffer/expected-
projection path plus the small source-slice append adapter. Original product,
tests and spill probe are recoverable under
`source-aq-removal-owned-fragments/`; the failed matched measurements and frozen
AQ products remain untouched. Eight implementation-independent source-slice
facts replace fragment-storage/allocation assertions; the completed structural
census remains. The combined removal and singleton storage implementation have
now passed the fresh AR rebuild, full correctness and matched diagnostic checks
reported above; none of these checks qualify the remaining release gates.

The remaining suffix-reflow gate is explicitly not solved by these allocation
changes. Current geometry-changing root-COW publication still arranges the
document suffix under the shared layout gate. A safe successor must compute
the existing sequential float placements off-lock and publish one immutable
placement generation, routing paint, point/selection transforms, UIA, hosted
control plans and cached nested/table/code/vector geometry through it. A
viewport-index-only offset overlay would leave inconsistent geometry. Existing
private-root safety caps, unsupported-shape fallbacks and native transfer/
rollback fences remain unchanged; O(N) off-lock candidate work is not eliminated
CPU work. No partial overlay or raised cap has been implemented or accepted.

A separate
GIF harness audit found no demonstrated leaked owner in the existing no-GC
receipts, but found that the ImageBox cycle sampled after work drain rather
than terminal retirement. It now awaits each box's actual `RetirementTask`
with the existing 20-second bound before the unchanged cache/admission/ledger
checks. That test-source fix is compiled and included in both full runs. A
separate quiet recheck completes all 100 native starts/pixel leases and all
terminal-retirement/cache/admission assertions, with the same eleven warmups,
interleaved fixtures, shared context, output limits and natural-GC behavior.
Private memory is **55,877,632 baseline / 68,136,960 peak and final**, a
**12,259,328-byte** increase, still above the 8 MiB limit. Handles are
643 baseline / 651 peak / 639 final. Managed heap grows 4,688,792 bytes with
zero measured gen0/gen1/gen2 collections and unchanged GC committed bytes;
cycle/observer allocations are 3,264,280 / 1,376,864 bytes. These are
process-wide diagnostic observations, not proof of a rooted native leak or
release qualification. The precise retirement barrier does not close the
memory gate; no collection, delay, fixture reduction or metric exclusion was
introduced (`gif-terminal-retirement-aq-b-quiet-a.trx`).

AI packs exact transformed-text source ranges into one immutable `uint[]`
instead of two `int[]` arrays under the unchanged 32 KiB / 16 KiB ceilings.
AH uses a frozen bounded generated-ID page index with an exact custom-ID spill
map; the original 11-argument snapshot constructor is retained unchanged.
Independent legacy oracles cover LTR/RTL dense/spill parity, duplicate IDs,
all UTF-16 offsets, maximum endpoints, atomic transformations and fallbacks.

Normal diagnostic/default builds pass (70.79 / 113.62 seconds, zero errors,
three existing audit-connectivity warnings each). The fresh full diagnostic
suite is **2,509/2,509**, no skips, all 12 public API baseline rows passing.
Default is **2,480/2,483**, no skips: the same three retained Resvg
diagnostic/default dependency mismatches. Focus is **62/62**, no skips.
The initial AH focus's unchanged constructor-compatibility failure is retained
and fixed, not waived; the initial AI test compile's `MarkdownRenderer.Math`
namespace collision is retained and fixed with qualified `System.Math` calls.
The full source-linked **AF** suite completes **1,805/1,805**, no skips, in
57 minutes 6 seconds. That is the frozen AF assembly, not a full AI pure-suite
result. AI's fresh source-linked build passes with zero warnings/errors in
5.67 seconds; its frozen assembly's focus passes **85/85**, no skips. Its fresh
full unfiltered validation passes **1,817/1,817**, no skips, in 57 minutes
55 seconds (`pure-managed-ai-full-a.trx`).

AI default Core/WinUI packages total **739,810 compressed / 1,752,576 managed
bytes**, zero native entries. Both unchanged lean caps still fail. Frozen
products/sources, TRX, packages and matched AG/AI script are under
`MarkdownRenderer/artifacts/mixed-vector-root-20261004/`. All owned competing
builds/tests finished for the matched AE/AG and AG/AI measurements below.
Eight frozen AG/AI launches, AG/AI/AI/AG then AI/AG/AG/AI, observe primary
1 MiB cache-hit allocation medians **19,802,520 / 18,545,728 bytes**:
**1,256,792 bytes (6.35%) less** for the repaired index plus packed projection
checkpoint. Viewport medians are 94.11 / 64.22 ms; snapshot medians
32.58 / 25.39 ms. These quick timings do not qualify release/browser speedup.
All eight exit zero, schema 12, `passed=true`, `isReleaseEvidence=false`, with
unchanged harness/runtime-policy hashes, warmups, measured-GC behavior and
one primary sample. No owned competing builds/tests/traces ran in that window.
No approved release baseline was selected or substituted. The exact reports,
stage files and script are retained with
`packed-projection-ai-counterordered-quick-a.log`.
The next candidate addresses redundant per-run
container-alias list storage, with original Add-time capture, independently
copied clone arrays and measured-placement fences retained. Independent AJ
review confirms that the unchanged 8,192-key-entry cap counts every actual
cloned key array: inherited aliases are now copied once per container; explicit
overrides and each run's aliases/modifiers remain separately counted. The
reference-only transfer array stays bounded by the unchanged 256-run cap.
AJ's first generated-source capture build failed because its diagnostic import
overrode the MSIX SDK's reserved before-targets hook. That failed log is retained;
using the unused after-targets hook retains the SDK import, analyzers, audit
policy and compilation. The corrected normal diagnostic build passes in
61.34 seconds, zero errors, three existing audit-connectivity warnings. Default
build passes in 66.39 seconds with the same three warnings and zero errors.
The first frozen AJ focus fails **4/76** (72 pass, no skips), and AJ is not
accepted as correctness-verified: image detachment's alias rebuild incorrectly
applies the newly-appended-run guard to the already-populated retained run list.
The three new clone fixtures also fail existing admission; their setup is being
checked without relaxing those gates. The separate initial test compilation's
nullable warning is fixed with an explicit non-null assertion; failed compile
and focus logs/TRX and original/corrected fixture sources remain intact.
AJ-a default packages total **740,347 compressed / 1,753,088 managed bytes**,
zero native entries; both unchanged lean caps still fail. Fresh October 5 WinRT
generator output is captured per project/config. AJ-b repairs the dedicated
detach rebuild path without changing normal append guards; clone fixtures now
share the required disclosure-state identity and have valid sequential source
spans. Its normal diagnostic/default builds pass in 87.80 / 79.36 seconds,
zero errors and three existing audit-connectivity warnings each. Frozen test
compilation passes both modes. Expanded focus passes **80/80**, no skips,
including inherited-only aliases, first/last overrides, all-images-to-empty,
and unchanged rejection of invalid normal append ordinals. The fresh full
diagnostic suite passes **2,518/2,518**, no skips, with all 12 public API baseline
rows passing. The full default suite is **2,489/2,492**, no skips, with only the
same three retained Resvg diagnostic/default dependency mismatches; no allocation improvement
for AJ is claimed until the quiet matched measurement completes. AJ-b packages
total **740,338 compressed / 1,753,600 managed bytes**, zero native entries;
both unchanged lean caps still fail.
Current default assembly metadata measurement shows 618,507 method IL bytes,
873,264 metadata bytes and 844 types; all WinRT generic helper/global lookup
method bodies total only 22,774 IL bytes. The recursive semantic-node collection
closure is a real private root, but removing it alone cannot close the package
gap. A non-enumerable node plus an allocation-free child value view is being
evaluated for both projection closure and UIA enumerator boxing, not claimed
as a completed package fix. The next combined candidate also moves six
vector-only fields out of ordinary text boxes into a lazy, per-box holder and
avoids intermediate literal strings when flattening exact source-backed text.
Existing fallbacks, eager semantics, native placement fences and resource caps
remain. Independent current-source reviews found no concrete production defect;
the combined **AN** candidate's normal diagnostic/default builds pass in
43.01 / 61.96 seconds, zero errors and three retained audit-connectivity warnings
each. Its new fixtures' missing abbreviation import, ambiguous AST-document
type and non-generic enumerator disposal were corrected before compilation;
the cold-text vector-state fixture now has a valid source span. Frozen product
and source custody is retained. The first exact-product test compilation failed
missing vector-fixture namespaces and an xUnit filtering assertion; corrected
compilation passes both modes. The initial focus is **132/136**, no skips: four
new assertions incorrectly demand distinct immutable empty-array singletons.
They now assert distinct box-owned holders and distinct populated geometry,
retaining every corruption/rejection case; corrected focus is **136/136**.
The full diagnostic suite is **2,528/2,529**, no skips: an existing unmodified
logarithmic-search contract cannot pass its counting read-only list to the new
concrete helper signature. AN-a is not accepted as fully correctness-verified.
The **AN-b** repair retains the original private read-only-list entry point over
one shared generic constrained search core; the production value-view calls
stay unboxed, and the original counting/boundary contract is unchanged.
AN-a default validation is **2,499/2,503**, no skips: the same helper-signature
regression plus the three retained optional Resvg dependency mismatches.
AN-b's normal diagnostic/default rebuilds pass in 110.66 / 59.77 seconds,
zero errors and three retained audit-connectivity warnings each. Fresh test
assemblies compile against both frozen products. Expanded focus, now including
the unchanged logarithmic-search regression, passes **143/143**, no skips.
Full diagnostic validation passes **2,529/2,529**, no skips, in 2 minutes
10 seconds; all 12 physical public-API baseline rows pass. Default passes
**2,500/2,503**, no skips, in 2 minutes 24 seconds: only the same three retained
Resvg dependency mismatches remain, with no helper-signature regression.
AN-b default Core/WinUI packages total **740,074
compressed / 1,750,016 managed bytes**, zero native entries; both unchanged
lean caps still fail. The exact AN-b runtime and matched allocation script are
frozen. Eight matched AI/AJ launches (AI/AJ/AJ/AI then AJ/AI/AI/AJ) observe
allocation medians **18,545,072 / 17,859,480 bytes**, **685,592 bytes (3.70%)
less**. Viewport medians are 58.40 / 64.33 ms and snapshot medians
20.66 / 24.48 ms; timing does not establish a speedup. Eight matched AJ/AN-b
launches (AJ/AN/AN/AJ then AN/AJ/AJ/AN) observe medians **17,861,448 /
17,648,712 bytes**, **212,736 bytes (1.19%) less**; viewport medians
66.19 / 61.39 ms and snapshot medians 31.71 / 30.63 ms are non-gating diagnostics.
All 16 launches exit zero, schema 12, passed=true, isReleaseEvidence=false,
with one primary 1 MiB cache-hit sample, unchanged harness/runtime hashes,
warmups and measured-GC policy. No owned competing builds/tests/traces ran in
either matched window. Exact reports, sidecars and scripts are retained with
`sparse-aliases-aj-counterordered-quick-a.log` and
`compact-interaction-an-counterordered-quick-a.log`. No release reference was
chosen or substituted. The next AO candidate replaces the four
semantic-builder StackBox child enumerations with ordered indexed traversal;
its nested-list/quote fixture has independent expected text/preorder/source,
list metadata and focus order. Independent review found no concrete defect;
normal compilation and exact-product validation remain pending.

A fresh frozen AN-b attribution-only EventPipe trace is **58,094,998 bytes**,
SHA-256 `94687A81950CE2520C52336A08A884BD4D22ADD3344B4CCF9438D9EB7520EE36`.
The instrumented harness intentionally rejects its runtime override (exit one,
schema 12, passed=false, isReleaseEvidence=false); it is not performance proof.
The reader has zero interval/allocation event loss and zero unresolved stacks
for the primary measured cache-hit window; later canceled/ambiguous intervals
remain explicitly rejected. That window's sampled child-list / child-array
weights are **313,056 / 455,984 bytes**, with concrete Add/BuildGroup owner
stacks. These are neither exact allocation totals nor a child-arity census.
The original reader/report is retained; an expanded reader now prints all type
totals and bounded child-storage owner stacks rather than dropping them below
the top-16 cutoff. Its normal build passes with zero warnings/errors in 3.35s.
This supports testing the next AP candidate: one owner field stores zero,
one direct child, or a promoted ordered list, preserving eager semantics and
all version/enumerator/identity contracts. AO and AP will be built/measured as
one next candidate; no individual AO benefit is promised.

Fresh read-only PR #99 inspection still shows head
`1c00c83ddfd2e8b65e05cb57e359a3f092b37271`, not these uncommitted local fixes.
Full decoded logs for top-500 run 36501560943 shards 76–100 / 451–475 show
15/25 / 9/25 cases failing after UIA image readiness at the existing
approximately two-second app-readiness wait, reporting empty parity and
`native unavailable=-1`. Other cases pass through the same path. No exception,
HRESULT or crash detail in those logs establishes a more specific cause.
Per-case result JSON in the ZIP artifact is not readable through the currently
supported connector surface. The readiness deadline and gates are unchanged;
there is no claimed full-500 rerun or speculative rendering fix.
No stale September output is used to
infer current package roots.

Previous correctness/allocation-verified local implementation: **AG + AF**, following
AE/AD/AC/AA/Z/Y/X/V/W. AF transfers immutable semantic-span page ownership instead
of allocating a second full span array. AG compacts common semantic-node typed
owners, text metadata and role-specific numbers; rare mixed combinations retain
every original value in compatibility holders. The complete eager accessibility
tree, node identity, semantic indexes, children/parent behavior, exact owner and
string identities, defaults, unknown enum values and selection mappings remain.

Normal diagnostic/default builds pass (103.33 / 81.84 seconds, zero errors,
three existing audit-connectivity warnings each). The fresh full diagnostic
suite is **2,485/2,485**, no skips, all 12 public API baseline rows passing.
Default is **2,456/2,459**, no skips: the same three retained Resvg
diagnostic/default dependency mismatches. The selected graph/index/projection
focus is **52/52**, no skips. The first AG production build failed two constant
byte-mask casts; the corrected build retains equivalent bit-clearing behavior,
and both logs are preserved. AF's source-linked focus is **42/42** with zero
warnings/errors at build; its fresh full unfiltered suite has now completed
**1,805/1,805**, no skips, as reported above.

AG default Core/WinUI packages total **736,159 compressed / 1,746,432 managed
bytes**, zero native entries. Both unchanged lean caps still fail. AG's frozen
diagnostic/default products and sources, full/focused TRX files and matched
AE/AG measurement script are under
`MarkdownRenderer/artifacts/mixed-vector-root-20261004/`.

Eight frozen AE/AG launches, AE/AG/AG/AE then AG/AE/AE/AG, observe primary
1 MiB cache-hit allocation medians **21,546,764 / 19,802,336 bytes**:
**1,744,428 bytes (8.10%) less** for compact graph plus frozen-span ownership.
Viewport medians are 85.27 / 88.44 ms; snapshot medians 29.84 / 31.24 ms.
Timing remains variable and does not establish a speedup or release/browser
performance. All eight exit zero, schema 12, `passed=true`,
`isReleaseEvidence=false`; identical harness/runtime-policy hashes, warmups,
measured-GC behavior and exactly one primary sample are retained. There were
no owned competing builds/tests/traces in this measurement window. No approved
release baseline was selected or substituted. Evidence is preserved in
`compact-graph-ag-counterordered-quick-a.log` and its eight reports/stage files.

Previous correctness/allocation-verified local implementation: **AE + AD**.
Semantic spans are immutable 40-byte values with one common box-owner slot;
ordered position indexes hold span indexes instead of object references. Rare
synthetic multi-owner spans retain every typed owner. Completed-image transfer
uses immutable source/span fences for exact literal slices, preserving laziness
through capture, measurement, validation and swap; other runs retain the exact
string fallback and all lifecycle/style/source/lease fences.

Normal diagnostic/default builds pass (54.00 / 48.76 seconds, zero errors,
three existing audit-connectivity warnings each). The fresh full diagnostic
suite is **2,474/2,474**, no skips, all 12 public API baseline rows passing.
Default is **2,445/2,448**, no skips: the same three retained Resvg
diagnostic/default dependency mismatches. Focus is **53/53**. Earlier failed
test compilation and context/order fixtures are retained; corrections preserve
the ownership fences, exact ranges and independently rebound semantic owners.
The source-linked project builds with zero warnings/errors; its fresh full
unfiltered suite remains pending, not substituted by that build or focus.

Eight frozen AC/AE launches, AC/AE/AE/AC then AE/AC/AC/AE, observe primary
1 MiB cache-hit allocation medians **21,854,536 / 21,548,332 bytes**:
**306,204 bytes (1.40%) less**. Viewport medians are 84.64 / 85.11 ms;
snapshot medians 39.72 / 35.89 ms. Timing remains noisy and does not establish
release/browser performance. Identical harness/runtime-policy hashes, warmups
and measured GC behavior are retained; all eight are schema 12,
`isReleaseEvidence=false`. AE default Core/WinUI packages total **733,227
compressed / 1,740,288 managed bytes**, zero native entries: both unchanged
lean caps still fail. Evidence is under
`MarkdownRenderer/artifacts/mixed-vector-root-20261004/`.

The active next priority is remaining full-document graph/storage allocation.
A fresh post-AE EventPipe trace (58,602,434 bytes, SHA-256
`9697AD111DFEEF400EB18C044FC2F38A907DF611A3C54BDDD403ECA0EE5F855B`)
has no reported event loss. Its measured cache-hit 1 MiB interval has zero
unresolved allocation stacks, with sampled weights of 3,735,048 bytes for
semantic nodes and 369,104 bytes for the final span flatten copy. These are
sampling weights, not exact allocation savings. Runtime tracing overrides
correctly make its schema-12 report fail the runtime contract; it is attribution
evidence only, not a benchmark pass or cryptographic same-byte comparison.

The now-correctness-verified **AF/AG** implementations address those two paths: ownership-transferred immutable
span pages eliminate the flatten copy, and compact common node metadata retains
the eager complete accessibility tree, stable node identity, typed owners,
null/empty/string identities, numeric defaults, semantic indexes and selection
mappings. Unusual mixed metadata has lossless compatibility holders. AF's
source-linked storage focus is **42/42**, no skips; its fresh full unfiltered
suite completes **1,805/1,805**, no skips. Independent code review found no concrete AF/AG compatibility
defect; normal product/API results are above. Matched allocation measurement
is now recorded above; neither candidate is release-qualified by this quick
diagnostic. The **AH** candidate addresses the traced `Dictionary<int,int>`
resize in per-block top-level ordinal lookup, using a bounded generated-ID
domain with lazily allocated pages and an exact custom-ID spill dictionary.
Holes, arbitrary/duplicate IDs, first-wins values and snapshot reconstruction
must remain exact. The first AH normal diagnostic/default builds passed
(92.73 / 52.13 seconds, zero errors, three audit-connectivity warnings each),
but its focus was **49/50**, no skips: the unchanged reflection compatibility
test detected replacement of the original 11-argument snapshot constructor.
That checkpoint is not accepted. The original constructor is restored, with
a separate internal bound-aware overload and explicit builder forwarding;
the compatibility assertion is unchanged. The initial test compile also
retains its xUnit2013 failure and corrected `Assert.Single` assertion.

The next **AI** checkpoint includes that compatibility repair plus exact
packed source projections: one `uint[]` replaces paired start/end `int[]`
arrays under the unchanged 32 KiB source / 16 KiB rendered-text ceilings.
Every half-open UTF-16 source interval remains exact, including atomic
entities/escapes/emoji and source-only edge syntax. Explicit bounds checks
prevent a future limit increase from truncating offsets. A new independent
two-array oracle compares every offset, maximum endpoints, null/fallback
cases and concurrent immutable readers. AI is being built/verified; neither
AH nor AI has a measured allocation benefit or qualified release result yet.
No next scheduled run is needed to pursue this work.

The SVG startup blocker is narrowed by a dependency-free token probe, built
with zero warnings/errors. This runner reports a primary token that is already
restricted, five restricting SIDs, no Restricted Code SID, no AppContainer,
and the unchanged production `CreateRestrictedToken` call returns Win32 87.
[Microsoft documents](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-createrestrictedtoken)
that restricting SID sets are intersected for already-restricted inputs. An
empty intersection is therefore the best-supported context explanation, not
a documented guarantee that Windows must return 87. The same unchanged call
must be qualified on an authorized normal primary-token runner to isolate the
cause. The probe never launches a worker, changes the source token, tries
alternate restrictions or prints identity data. No isolation bypass/fallback
was added. Its source, build and exact run log are preserved in the same
artifact root (`token-restriction-probe-ag/`, `token-restriction-probe-ag-*.log`).

Previous verified implementation: **AC**. Exact-source
literal slices avoid eager per-run `Substring` copies while preserving transformed
text, equal-but-distinct parser/source backing, imprecise-span fallbacks, native
buffers, Unicode/RTL text, safe HTML and every selectable subrange's source map.
The fresh full diagnostic suite is **2,469/2,469**, no skips, including all 12
exact public API baseline rows. Default is **2,440/2,443**, no skips, with the same
three retained Resvg diagnostic/default dependency mismatches. New independent
PNG/JPEG/WebP metadata fixtures and every-byte Windows color-management parity
checks pass; the actual ImageBox/WARP cycle matrix now includes those color
variants. These are Windows-oracle correctness checks, not Edge/HDR/device
qualification. The source-linked project builds with zero warnings/errors and
29/29 focused storage/source-contract tests pass. Earlier failed test-only
fixtures and their corrections remain in the artifact record.

Eight frozen AA/AC launches, AA/AC/AC/AA then AC/AA/AA/AC, with identical harness
and runtime-configuration hashes, observe primary 1 MiB cache-hit allocation
medians of **22,594,364 / 21,851,568 bytes**: **742,796 bytes (3.29%) less**.
Viewport medians are 68.35 / 57.74 ms and snapshot medians 19.90 / 16.90 ms;
individual timings remain highly variable, so no qualified speedup is claimed.
All eight are schema 12, `isReleaseEvidence=false`; measured GC behavior, warmups
and runtime policy are unchanged. AC default packages total **732,610 compressed
/ 1,739,264 managed bytes**, zero native entries: both unchanged lean caps still
fail. Exact frozen products, sources, test results and eight reports are under
`MarkdownRenderer/artifacts/mixed-vector-root-20261004/`.

The now-verified AD/AE candidates addressed two traced/correctly reproduced paths:
**AD** preserves literal laziness through completed-image placement transfer,
using immutable source identity and captured span/length rather than forcing
source and clone strings during the final fence; **AE** compacts semantic span
storage and indexes while retaining canonical text, typed owners, span order
and first-wins mapping. Their fresh evidence is above, separate from AC's
results; AC did not qualify those later candidates.

AA's paged
source-map storage and private hosted-focus storage preserve exact frozen source
views, affix rewrites, custom equal-key ordering, concurrent snapshot reads and
native focus traversal. Count-based bounds reject unused focus-array slots.
Normal diagnostic/default SDK builds pass with the existing audit warnings;
the fresh diagnostic suite is **2,439/2,439**, no skips, and the fresh default
suite is **2,410/2,413**, with the same three retained Resvg mismatches. The
32-case actual-product focus and 32-case source-linked focus both pass; the
source-linked project builds with zero warnings/errors. Live focus/UIA/device
and full managed release qualification are not established by these checks.

Eight frozen-harness Y/AA launches, Y/AA/AA/Y then AA/Y/Y/AA, observe stable
primary 1 MiB cache-hit allocation medians of **23,497,256 / 22,596,924 bytes**:
**900,332 bytes (3.83%)** less. Timing is inconclusive: viewport medians are
63.94/70.10 ms and snapshot medians 24.57/37.66 ms; reversed reference runs also
slow sharply, and every measured trial includes a natural Gen2 collection.
All reports are schema 12, `isReleaseEvidence=false`, with the same frozen
harness/runtime configuration. No additional collection or policy override was
introduced. This is allocation evidence, not a qualified speedup. An **AB**
candidate tested 4,096-entry bounded pages plus boundary-only page allocation.
The eight matched AA/AB launches (AA/AB/AB/AA, then AB/AA/AA/AB) passed their
quick checks, not release qualification. Primary allocation medians were
22,598,112 / 22,678,152 bytes: **80,040 bytes more** for the larger pages.
Viewport medians were 86.69 / 91.33 ms and snapshot medians 38.79 / 42.77 ms;
reference timings drift substantially, so no speedup is established. The
larger-page hypothesis is rejected: shipping storage returns to the 1,024-entry
bound, retaining only safe boundary-only page allocation. AB's exact frozen
products/logs remain available; they passed **2,445/2,445** diagnostic and
**2,416/2,419** default tests (the same three retained Resvg mismatches), with
38/38 focused boundary/concurrency tests. These counts do not qualify the next
source revision. AB packages total 731,158 compressed / 1,737,216 managed bytes,
still above both unchanged lean caps. The next **AC** candidate addresses the
traced literal `Substring` copies through exact-source slices, preserving
transformed-text fallbacks, source selection, semantic text and native buffers.

Fresh AA default Core/WinUI packages total **731,121 compressed / 1,737,216
managed bytes**, zero native entries. Both unchanged **537,600 / 1,258,291**
lean limits still fail. Actual PE metadata confirms 19 fewer generated WinRT
helper types, retaining adapters needed elsewhere; stale generated source is
not used as size proof. Optional-provider rebuilding, native scratch/retained
growth, Edge/top-500, approved-reference counterbalance and AOT/device gates
remain open. Current PR #99 still publishes `1c00c83`; old-head rank-5 readiness
and rank-214 image-path failures already have local fixes and do not establish
that the current implementation passes a fresh top-500 audit.

Z raises only
native-image admission weights, accounting for conservative codec scratch and
GIF frame-zero canvas/descriptor overlap. Existing FIFO/cancellation, capacity,
output limits and GPU-upload lifetime stay unchanged. Oversized admitted images
run alone; they are not rejected. Normal diagnostic/default SDK builds pass with
three existing audit-connectivity warnings each. The fresh diagnostic suite is
**2,415/2,415**, no skips; the fresh default suite is **2,386/2,389**, no skips,
with the same three retained Resvg diagnostic/default dependency/API mismatches.
These scheduling weights do not bound a single WIC codec's internal peak.

**Highest-impact remaining native-memory work:** the installed WIC WebP decoder
does not expose `IWICBitmapSourceTransform`, and banded `CopyPixels` did not
reduce its measured scratch. The pinned `image-webp 0.2.4` dependency retains a
full-source lossless pixel buffer; disguising ordinary images as SVG input would
not solve this. A concrete upstream prototype candidate now exists:
[libwebp commit e7a9045](https://chromium.googlesource.com/webm/libwebp/+/e7a9045111cfb6349affc37e64d6664f5f77dc93),
committed September 23, 2026, adds sliding lossless history rows rather than
full-height storage. This is a source-level opportunity, **not** an implemented
backend, measured process-memory saving or fidelity pass. Pin the exact source,
carry `COPYING` and `PATENTS`, and prototype an explicit isolated ordinary-raster
worker role with shared-memory output. Prove row-window activation, actual peak,
alpha/premultiplication, resampling, ICC/EXIF behavior and every-byte/Edge fidelity
before promotion; do not silently fall back in-process after worker failure.
The optional SVG worker's document/font/cache protocol must not be repurposed as
an implicit raster API. Obtaining/building pinned native sources and executing
the unchanged restricted worker require the networked normal-token Windows
runner already needed for the release gates; no sandbox, audit, image ceiling,
package cap or performance gate is relaxed to manufacture progress.

The proposed construction-set capacity hint was rejected: discarded/reused
custom blocks can inflate the set independently of the graph traversal. Its
experiment and rejection rationale are retained; no shortcut ships. Paged
source-map storage and private native-focus storage are now under fresh build,
correctness, allocation and package-size validation as described above.

Y's verified allocation changes remain: semantic leaf
nodes and unmeasured inline containers now allocate child/style-slot storage only
when needed. Ordered source-map affix lookup uses binary bounds rather than an
eager per-block dictionary; out-of-order custom entries retain a lazily built
defensive index. Live child views, enumeration invalidation, exact source ranges,
style identity and private-clone transfer fences remain covered. Normal diagnostic
and default SDK builds pass with the existing audit-connectivity warnings. The
fresh diagnostic suite passes **2,411/2,411**, no skips, including all 12 exact API
assembly baseline rows (`foundation-diagnostics-y-lazy-storage-a.trx`). Fresh
default tests compile against the exact frozen Y products, not the older owner
reference directories: **2,382/2,385**, no skips, with only the same three retained
Resvg dependency/API mismatches (`foundation-default-y-lazy-storage-a.trx`). The
source-linked managed source-map focus also passes 9/9. These are not passing
optional-provider, full-managed, AOT or device release gates.

Eight frozen-harness X/Y diagnostic launches, ordered X/Y/Y/X then Y/X/X/Y,
observe primary cache-hit presentation allocation of **24,805,984–24,806,816**
versus **23,495,880–23,499,016** bytes for the 1 MiB corpus. Median reduction is
**1,307,988 bytes (5.27%)**. First-viewport medians are 44.64/43.37 ms and snapshot
medians 10.58/10.55 ms; natural GC/host noise and the quick population preclude a
qualified latency improvement. Every report remains schema 12,
`isReleaseEvidence=false`; no runtime policy override or release gate changed.

Six fresh-process direct owned-WIC probes (full/64-row/16-row, then reverse)
confirm the row-band experiment does **not** materially reduce the oversized
WebP peak: approximately 138 MB incremental private bytes for every mode, with
identical source/output hashes. The separate 26-case focus and full diagnostic
suite establish exact pixels across WebP, JPEG orientations, profiled alpha PNG
and GIF, not native scratch containment. Band copying stays diagnostic-only.
Z's conservative native-graph scheduling estimates are separately verified
above; paged source-map allocation is the next active validation.
Fresh default Y Core/WinUI packages total **734,000/537,600 compressed bytes**
and **1,753,088/1,258,291 managed bytes**, with no native payload entries; both
unchanged lean limits still fail. The optional provider/default API mismatch,
package size, native scratch/retained growth, browser/top-500, approved-reference
counterbalance and fresh AOT/device gates remain open.

X's last separate
layout-index traversal is now collected during semantic construction, retaining
exact preorder, identity, ambiguous-image ownership, overflow aliases, text/range
fingerprints and keyboard eligibility. Standalone semantic builds do not mutate
snapshot indexes, bounds or focus plans. Normal diagnostic/default SDK builds
pass with three existing audit-connectivity warnings each. The frozen diagnostic
full suite passes **2,382/2,382**, no skips, including the installed-codec
capability/path counters and all 12 exact API assembly rows
(`foundation-diagnostics-x-semantic-c.trx`). The preceding 2,381-case receipt is
preserved. The first
actual default-products X full run is **2,366/2,370**: the same three retained
Resvg dependency/API mismatches plus a test-observer race between concurrent
`Process.Refresh`/private-byte reads. Only that observer pair is serialized;
sampling cadence, memory limits and workload remain unchanged. The repaired
memory/reflow focus passes 7/7 and the full diagnostic rerun above passes; the
fresh default rerun is **2,368/2,371**, no skips: the observer failure is absent
and only the same three Resvg dependency/API mismatches remain
(`foundation-default-x-semantic-b.trx`). This is not a passing default release
gate. The blocked fresh optional-provider
rebuild is not waived. U's unfiltered source-linked managed suite finished
**1,762/1,762**, including its long external-process fixtures; it does not qualify
later X or WinUI native/device/AOT release gates.

U's bounded GIF frame-zero transforms remain on the owned native WIC graph
alongside static PNG/BMP/JPEG/WebP. Logical-screen layout, descriptor pixels,
fractional placement, the over-60-frame preview policy, worker-side retirement
and admission held through GPU upload/composition remain intact. X is separately
verified; W's preserved custody manifest does not cover it.

Eight same-frozen-harness W/X diagnostic launches, ordered W/X/X/W then X/W/W/X,
observe **25,379,480–25,386,296** versus **24,803,320–24,806,456** primary cache-hit
allocated bytes for the 1 MiB corpus: approximately 0.58 MB less per presentation.
Timing is mixed: primary snapshot medians are 24.89/18.96 ms, but primary cache-hit
first-viewport medians are **93.41/103.48 ms**. These quick runs include differing
natural GC behavior on a noisy host and are not a qualified speedup. The latency,
CPU, memory and browser-relative release gates remain open.

The next highest-impact targets are still document-scale image reflow and codec
scratch memory. A real completed-image regression with 2k/10k/100k roots confirms
that every trailing root is still arranged once under the commit gate; the cheap
100k-root fixture takes 7.72 ms, not a bound for rich content. Paint/input read
admission correctly rejects a busy commit, rather than proving direct queries are
nonblocking. Removing that suffix requires coherent effective coordinates across
paint, hit testing, selection and UIA, not skipping child arrangement blindly.
The admitted ImageBox WebP fixture (2048×8193 into 256×1025) still shows a large
transient process-private-byte peak despite bounded output. The fresh isolated
diagnostic observes +191,725,568 serial / +171,950,080 overlapping-request bytes;
host/runtime/device allocations are included, and 2 ms sampling is not native
heap attribution. Explicit counters prove the owned ImageBox WIC branch in the
latest focus, which observes +191,619,072 serial / +172,265,472 overlapping bytes.
A separate 138,874,880-byte direct-WinRT probe is **not** the ImageBox branch.
The installed WebP frame returns E_NOINTERFACE (0x80004002) for
IWICBitmapSourceTransform; direct decoder-side scaling is unavailable here.
The three-case capability/path focus passes (`native-webp-path-capability-x-focus-a.trx`).
Final-converter row-band copying is a diagnostics-only experiment, not a
production optimization until exact pixels and peak/CPU costs are measured.

The owned X EventPipe trace (`allocation-stacks-x-a.nettrace`, SHA-256
31B8793BFFF8A261D8852BF83E4C31E5A5A237140E0D2546F9B166E3BE310D21)
now provides allocation-stack attribution. Eight serialized 1 MiB first-viewport
windows match the sidecar by PID/generation/source-length and chronological
occurrence, with no lost events or unresolved stacks in those windows. Overlaps,
unclosed/malformed windows and payload-duration mismatches are rejected; later
cancellation windows are retained as invalid diagnostics. This is not same-byte
cryptographic or release evidence. Cache-hit sampled weights identify semantic
nodes/child lists, inline boxes/style-slot arrays and source-map collection
growth as actionable costs. The traced harness correctly rejects its diagnostic
runtime overrides; its timings do not qualify the frozen performance contract.
Y's lazy child storage, lazy run-style slots and ordered-map affix lookup now
have the fresh build/test/profile evidence above; sampled allocation weights
are not substituted for exact whole-presentation allocation measurements.

The new GIF product-cycle test passes exact pixels, source-cache reuse and all
ownership ledger drains, but **fails the memory objective**: three fresh default
testhosts observe 12,984,320 / 12,349,440 / 12,148,736 private-byte growth after
100 cycles, above 8 MiB. An observer-allocation probe records 3.86 MB cycle-work
allocation, 0.44 MB observer allocation, unchanged GC committed bytes and zero
collections; private bytes still rise by 11,710,464 and handles by five. An
explicit tiered-JIT-off diagnostic changes private growth substantially, but also
ends five handles higher. Neither that control nor ledger drains is substituted
for the shipping/AOT/Edge memory gate. Separate controls now pass their functional
assertions: direct decoding grows private bytes by 7,499,776 (+2 handles), while
upload-only grows by 3,141,632 (-80 handles, one natural collection per generation).
A production-like shared-context cohort still grows by 11,628,544 (+7 handles),
so recreating the test layout context was not the main cause. These different
process/JIT/GC cohorts are not additive native-leak attribution. No forced GC or
extra warmup masks the original failed objective.

Private clipboard/run-alias arrays remove 16 unnecessary nested string WinRT
helper types (83 to 67, none added); required UIA helpers remain. The default X
renderer DLL is 1,536 bytes larger than W. Fresh default-X packages still fail
both unchanged lean limits: **731,849/537,600 compressed bytes** and
**1,745,408/1,258,291 managed bytes**, no native payload entries. This is not a
package-gap closure. The measured layout target is whole-snapshot
construction. V collects semantic graph indexes during Builder construction,
preserving exact lookup/node identities.
The first experiment increased 1 MiB presentation allocations by about 215 KB;
that online block-index growth was removed, restoring one exact-capacity integer
map fill. Matched frozen-harness quick runs observe median snapshot times
**24.9713 ms (12 U samples), 21.5968 ms (12 V), 18.7238 ms (8 W)** for the
1 MiB `readme-mixed-v1` corpus. Cache-hit presentation allocation falls from
26.23 MB to 25.37–25.38 MB. These unequal quick sets on a noisy host are explicitly
non-gating diagnostics, not a qualified counterbalanced/browser performance claim.
The initial V repeated-cell oracle omitted existing terminal-newline range
clamping; the corrected exact oracle now passes. W's new GIF diagnostic controls
also needed bounded read-only shared-retirement quiescence before recording
baselines, because older retry tails were legitimately draining during the full
suite. Their exact ledger assertions and the original failed memory gate remain
unchanged; failed receipts are preserved.
Exact W custody and retained failures are in `verified-checkpoint-w.json`; U/T/S
remain unchanged. ICO/AVIF, non-identity PNG/WebP EXIF and legacy eager paths,
all-codec peaks, browser/top-500, fresh AOT and device-matrix gates remain open.

### Active local follow-up: mixed math/image completion and snapshot allocation

The October 4 active run now extends bounded root-local image placement to
paragraph/heading roots containing stable, materialized inline vector/math runs.
Private clones borrow only immutable scene values and scalar metrics: they have
no native drawing, cannot prepare or paint, and never dispose the published
source's drawing. The final transfer retains original runs, scene/drawing owners,
source maps, accessibility spans, bitmap leases and subscriptions. Candidate
geometry is validated and remapped to source-run identities before the final
gate; stale metadata, invalid regions, generations and ownership reject the batch
before any swap. The same-band lazy coalescer now fences the active layout
revision so an older in-flight refill cannot absorb the stationary retry after
image completion. Unsupported nested shapes, incomplete vectors, whole-tree
mixed/vector COW and the O(N) early-height suffix remain open.

Fresh checkpoint-G Release/x64 diagnostic and checkpoint-B diagnostics-disabled
builds pass with zero errors and the unchanged NU1900 audit-connectivity warnings.
The latest 68-case native mixed-vector/snapshot/admission focus passes without
skips (`mixed-vector-snapshot-focus-e.trx`), including 16 exact pixel/geometry/UIA rows
at simulated 100%, 150%, 200% and 400%, LTR/RTL, normal D3D/WARP, plus progressive
stationary geometry/marker refresh, cancellation, late stale-vector fencing,
aggregate image rejection and malformed candidate geometry. The first full
renderer run is 2,009/2,010: its only failure was a stale terminal-disposal source
assertion. After correcting that contract, the second full run is also
2,009/2,010: Release collection of an expected-live weak waiter invalidated a
test fixture's count. The test now explicitly keeps those expected-live owners
alive without changing production weak ownership or the collection regression.
Both failed receipts remain preserved. The final fresh unfiltered diagnostic
renderer rerun passes **2,013/2,013**, no skips
(`renderer-all-mixed-vector-snapshot-c.trx`), including all 12 exact public API
baseline rows. The prior matched diagnostics-disabled focus passes 59/59; the
latest diagnostics-disabled unfiltered attempt is **2,001/2,004**, no skips
(`renderer-default-all-mixed-vector-snapshot-b.trx`). Its three failures are all
Resvg reflection/API checks: the retained diagnostic provider dependency refers
to `MarkdownSvgWorkerPolicySnapshot`, absent from the default renderer. Rebuilding
that provider against the actual default outputs is blocked by the unchanged
NU1900-as-error audit connectivity failure, even through the limited test-dependency
compiler target (`resvg-default-test-dependency-compile-a.log`). No stale/failed
provider output was staged and no audit warning was disabled or demoted. This
mixed test runtime is not a fresh full shipping/AOT/provider graph or a passing
default release gate. TRX counters and unique execution IDs were checked using a
DTD-disabled XML reader; failures are preserved rather than removed from reports.

Profiled 1 MiB `readme-mixed-v1` snapshot construction was the dominant pipeline
stage, rather than Win2D text-layout construction. Semantic range hashing now
batches the exact prior SHA-256 byte stream in a fixed 4 KiB stack buffer; an
independent legacy-stream regression passes. Single-unit grapheme boundaries
are represented implicitly, with actual StringInfo validation at Unicode edges;
CRLF, surrogate pairs and combining clusters retain authoritative segmentation.
The index now stores at most 4,096 exceptional multi-unit graphemes as compact
start/end/cumulative-extra records, with binary-search ordinal/offset conversion.
One emoji in a long otherwise-single-unit document no longer forces a full
boundary array. Dense inputs retain the authoritative StringInfo dense fallback;
its temporary sparse collectors are bounded, not allocation-free. The fresh
Unicode/end-navigation/sparse/dense allocation focus passes **30/30**
(`sparse-grapheme-focus-d.trx`). The fresh source-linked managed scope passes
**1,255/1,255**, no skips, excluding only the 501 unchanged external-gate fixture
cases (`core-all-except-external-fixtures-d.trx`).
These are not a newly executed unfiltered managed release gate.

Semantic position maps preallocate only actually mappable spans, discounting
consecutive atomic-boundary duplicates without changing first-wins insertion
order. Standalone-image documents no longer reserve unused position entries;
ambiguous inline block IDs retain their range fallback. Block ordinals and inline
block maps reserve known counts rather than growing through repeated dictionary
copies. Three native regressions verify the image-only, atomic-inline and
ambiguous-ID cases; source/run/UIA identities and projection remain intact.

The separate opt-in diagnostic sidecar is restricted to quick runs, binds exact
runtime hashes and does not change schema-12 release qualification. The
checkpoint-F profile D shows cache-hit process allocation of 27,252,224 and
27,252,088 bytes versus profile B's 29,200,584 and 29,200,448. Checkpoint-G profile
E reduces that further to **26,227,496 / 26,227,360 bytes**: **2,973,088 fewer bytes
(2.84 MiB) per cache-hit presentation** than B, including an additional 1,024,728
bytes saved by the index changes relative to D. The intervals are one warmup
sample and one measured sample, not two qualified recorded trials. Cache-hit
snapshot samples in E are 21.22/21.53 ms versus B's 34.21/30.28 ms; first-viewport
samples are 61.96/51.20 ms versus B's 63.43/61.49 ms. These timings are noisy quick
diagnostics, not a qualified latency speedup or browser comparison. Publication
samples in E are 0.69/0.64 ms. The allocation interval includes all process
threads, not just semantic construction. Profiles A–E and all failed
compiler/test attempts are retained under
`MarkdownRenderer/artifacts/mixed-vector-root-20261004`. A pure 21-pair constructor
microdiagnostic additionally covers ASCII, CJK, combining text, emoji and a late
emoji; it is explicitly non-gating and not WinUI/Edge evidence. The fresh sparse
implementation (`grapheme-construction-diagnostic-b.json`) allocates 208 bytes
for the 1,000,000-unit late-emoji constructor versus the dense oracle's 4,000,024
bytes, with paired median times 1.23/21.11 ms. Dense combining/emoji inputs add
98,496 bytes of bounded collector/object overhead versus the legacy retained
array (2,098,520 / 2,000,024); their paired medians are 10.96/11.00 and 12.20/11.37
ms. Those micro-results do not qualify an overall renderer/Edge performance gate.

Fresh diagnostics-disabled Core/WinUI packages bind the built DLLs exactly, but
fail both unchanged size ceilings. The latest checkpoint-B pair is
**691,349 / 537,600 compressed bytes** and **1,638,400 / 1,258,291 managed bytes**
(excess **153,749 / 380,109 bytes**), preserving the preceding 690,881 / 1,636,864
attempt too.
No native payload is present in this lean pair. The stock size gate fails on its
first compressed-size assertion; independent archive inspection measures the
managed failure too. Archive inspection finds no PDBs, duplicate DLLs, or unused
native artifacts in this lean pair: deleting every non-DLL entry would still
fail the compressed gate. Closing this requires actual managed-code reduction,
not moving required code to an always-needed third package to evade accounting.
Optional packages and the complete package/license graph have not been
requalified. Earlier footprint passes/headroom are historical,
not evidence for this expanded local implementation.

The priority inventory and preceding checkpoints below remain useful context;
the qualified counterbalanced benchmark, approved ancestor reference, same-byte
Edge/top-500 corpus, aggregate native admission, full device/theme/DPI/AOT matrix
and package-size/license gates remain mandatory and open. PR #99 is still at
the September 28 published head; these local changes are not published.

### Additional same-run milestone: fused viewport index refresh

After checkpoint G, the same active run removed another general document-scale
scan from geometry publication. `RefreshViewportIndexNoLock` now uses a stack-only
sequential writer after all authoritative arrangement succeeds. It copies bounds,
normalizes them exactly as before, checks adjacent/ordinal order and accumulates
prefix maxima in one pass. Normal full refresh no longer copies then separately
validates and accumulates the entire index; normal suffix refresh no longer
copies then rescans its suffix. A prior custom visual order refreshes from zero;
new ordering violations still invoke the existing full defensive sort/commit.
The index is not modified during Arrange, so an arrangement exception retains
the prior committed index and metadata. This does **not** remove the necessary
O(N) changed-height suffix arrangement or migrate authoritative bounds.

Fresh checkpoint-H diagnostics-enabled and checkpoint-C diagnostics-disabled
Release/x64 builds succeed, with the unchanged audit-connectivity warnings.
The latest unfiltered diagnostic renderer/GitHub suite passes **2,016/2,016**, no
skips (`renderer-all-fused-viewport-d.trx`); the native fused-index/mixed-vector
focus passes **44/44** in both modes (`fused-viewport-native-focus-f.trx` and
`default-fused-viewport-native-focus-g.trx`). The 4,096-root fixture compares all
indexed bounds/ordinals and boundary queries with a complete rebuild across
measured and estimated lazy content. Custom-order fallback and failure-before-index
publication are covered. The fresh pure index/Unicode focus is **41/41** and the
managed scope is **1,261/1,261**, still explicitly excluding the same 501
external-gate fixture cases (`core-all-except-external-fixtures-e.trx`). The last
unfiltered default attempt remains checkpoint B's 2,001/2,004, not a latest-H
default release pass; rebuilding its diagnostic resvg dependency remains blocked
by NU1900-as-error. Failed receipts were not discarded or recategorized as skips.

The latest default lean pair binds checkpoint C's built DLLs but still fails:
**691,809 / 537,600 compressed bytes**, **1,639,936 / 1,258,291 managed bytes**
(excess **154,209 / 381,645 bytes**), zero native entries. All preceding package
attempts are preserved. Profile E and constructor diagnostic B belong to
checkpoint G, not the later H binaries; no H latency/allocation speedup or release
qualification is inferred from them. The latest owned DLL/PDB checkpoint is
copied under `checkpoint-h-owned-binaries` before further staging changes;
older runtime staging paths are mutable, with their execution-time hashes
recorded rather than asserted to remain immutable deployments.

### Additional same-run milestone: failed-live-layout recovery

Checkpoint I closes the failed-live-Measure/Arrange recovery gap without treating
persistent unsafe geometry as transient Busy. After a live mutation attempt
fails or is canceled, the snapshot permanently denies read/paint/input leases
and live geometry queries until replacement. Restoring outer bounds, masks and
the previously committed viewport index does not certify nested native state.
Pre-canceled work and failures before live placement adoption do not latch it.
Only one current control can claim the replacement request. Device-loss state
is scalar; the latch does not retain the exception or its document graph.

The control clears deferred input, captures, selection/focus overlays, flyouts,
stale geometry work and bitmap scans, but preserves logical selection and the
last-good canvas tiles. Completion paths check recovery before cancellation or
staleness fences can suppress replacement. Graph refill and readiness exit on
recovery/retirement rather than spinning on Busy. Device recovery now owns one
identity-fenced dispatcher timer with capped backoff, canceled on unload,
disposal and successful paint; a rejected/throwing recovery path cannot enqueue
duplicate full replacements. Cleanup attempts independent owned resources even
when one native/XAML cleanup fails. This is managed/source-contract coverage,
not live Sandbox/UIA/device-matrix qualification.

Fresh Release/x64 diagnostic build K and diagnostics-disabled build D pass with
zero errors and the unchanged three NU1900 audit-connectivity warnings. Test
compiles T/H use those actual outputs, not a renamed diagnostic renderer. The
final unfiltered diagnostic suite passes **2,046/2,046**, zero skips, including
all 12 exact public API baseline rows (`renderer-all-geometry-recovery-h.trx`).
The matched default recovery/layout/image focus passes **85/85**; its latest
unfiltered suite is **2,034/2,037**, zero skips
(`renderer-default-all-geometry-recovery-d.trx`). Its remaining three failures
are the retained diagnostic Resvg dependency referencing the diagnostic-only
`MarkdownSvgWorkerPolicySnapshot`; a fresh default provider graph remains
blocked by NU1900-as-error. No audit check or public contract was weakened.

All failed attempts remain: the first diagnostic full run's eight stale
post-failure live-query assertions were replaced with stronger retained-mask,
index, bounds and fail-closed checks. The next run rendered its oversized SVG
tile successfully but timed out during exact global admission cleanup. The
fixture now starts at a confirmed zero-byte ledger, awaits actual image
retirement and reports owner/cache/ledger state under the same 20-second cleanup
deadline. A new deterministic regression proves that baseline capture waits for
a previous owner to release its reservation. The original timeout did not
record ledger snapshots and is not proof of a production leak. The first fresh
default full run also exposed a scene-retry test race: queued cancellation
cleanup is asynchronous. The test now waits for the existing retry callback
only if rescheduling is rejected, retaining its no-offscreen-publication and
eventual-visible-publication assertions. No test is excluded or marked skipped.

The current-K quick profile F passes with no reported failures, schema 12 and
`IsReleaseEvidence=false`. Its 1 MiB cache-hit samples are 64.38/95.14 ms first
viewport, 22.28/15.78 ms snapshot construction, 26,339,280/26,236,712 bytes process
allocation and 0.82/0.53 ms publication. These are one measured sample per
variant, not counterbalanced release trials or evidence of an Edge-relative
speedup. Fresh default-D packages bind the produced Core/WinUI DLLs but fail
both unchanged lean-pair ceilings: **692,977 / 537,600 compressed bytes** and
**1,643,008 / 1,258,291 managed bytes**, no native entries. Full optional package,
license and AOT graphs remain unqualified. Owned DLL/PDBs and tests are copied
under `checkpoint-i-owned-binaries`; exact hashes, all passing/failed receipts
and limitations are recorded in `verified-checkpoint-i.json`.

### Additional same-run milestone: bounded native vector admission

Checkpoint J integrates process-wide weighted vector native admission, durable
credit retirement, readable attached/inline/detached fallback, actual-visible-
band priority and weak generation-based stationary retries. A scene reserves
its whole weight before native factories run. Credits are refunded only after
all owned resources close; quarantined failed disposal remains charged. Band
transitions commit readable incoming content before retiring outgoing credits,
then generation notifications retry stationary visible work. Notifications do
not retain documents or controls, and rejected dispatch uses bounded backoff.
The fixed 64 MiB-equivalent x64/ARM64 and 32 MiB x86 ceilings are **complexity
credits, not measured native bytes or an RSS/GPU/driver-memory cap**. Capacity
fallback does not falsely report native fidelity readiness. Bounded captions
reuse two shared LTR/RTL text formats; Direct2D DrawText may still do transient
native work and needs live performance qualification.

Fresh diagnostic Release/x64 build Q and diagnostics-disabled build I pass,
zero errors and the same three audit-connectivity warnings. Normal SDK test
compiles Z/M bind those owned outputs. The final unfiltered diagnostic suite
passes **2,085/2,085**, zero skips and all 12 exact public API rows. Diagnostic
and default native-capacity focuses both pass **40/40**. The default full suite
is **2,073/2,076**, zero skips: the same three retained diagnostic Resvg
dependency failures remain, not exclusions. All earlier failures and binaries
are retained, including the disposed-Paint regression and a weak-observer test
fixture whose owners needed explicit GC liveness. Assertions remain intact.

Fresh default-I packages still fail the unchanged lean-pair gates:
**701,784 / 537,600 compressed bytes** and **1,663,488 / 1,258,291 managed
bytes**. Owned DLL/PDBs, scoped source hashes, all passing/failed receipts and
limitations are recorded in `verified-checkpoint-j.json` and
`checkpoint-j-owned-binaries`. This is local unpublished managed evidence,
not release, native-memory, live UI, complete optional-provider or AOT evidence.

### Additional same-run milestone: image metadata and reserved raster geometry

Checkpoint K replaces the retained title/geometry SVG scans with one bounded
borrowed-byte XML reader. It preserves first direct-root title/description,
inert external SVG declarations, malformed-input rejection, Unicode-safe
truncation and the existing root-geometry semantics. Metadata is not security
admission: the provider and worker still perform authoritative validation.

Opted-in image prefetch now collects orientation-correct raster dimensions on
the existing admitted background CPU/source path. Only scalar dimensions are
stored on the exact existing source-cache entry; publication fences its asset
and entry identity. Measurement never resolves a source, exposes cached bytes,
promotes cache recency or persists an advisory hint into image clone/checkpoint
fences. A first transformed bitmap avoids completion reflow only when its
authoritative dimensions prove the exact already-committed geometry. Cache
misses, replacement mismatches, failures, clipped bounds and active/rolled-back
inline checkpoints retain the conservative reflow path. Both existing
`PrefetchDocumentImages` and `UseDisplaySizedRasterDecode` options are required;
no public API or default behavior was added.

Fresh Release/x64 diagnostic build S and diagnostics-disabled build J pass,
zero errors and the unchanged three audit-connectivity warnings. Normal SDK
test compiles AC/O bind those actual owned products. The new image focus passes
**67/67 in both configurations**, including actual WIC orientation, scalar
cache identity/lifecycle, authored sizing, captions, table-width constraints,
pristine clone fences and simulated 100/125/150/175/200/225/300/400% scales. The
unfiltered diagnostic suite passes **2,152/2,152**, zero skips and all 12 exact
public API baseline rows. The default full suite is **2,140/2,143**, zero skips:
the same three retained diagnostic Resvg dependency failures remain. That
runtime is not a fresh full default provider graph or a passing release gate.

On the project-generated 890x158 SVG with 182 unique embedded PNG payloads,
the retained metadata helpers allocate 3,816,096 bytes over 12 parses; the new
reader allocates 254,776-267,136 bytes across the recorded focus/full runs,
about 93% less. **Latency is not improved by this diagnostic:** paired times
are 2.06/4.93 ms in the diagnostic focus, 21.63/36.16 ms in its full run,
1.21/5.26 ms in the default focus and 1.09/5.82 ms in its full run (old/new).
These uncounterbalanced timings are informational, not Edge or release
evidence. The initial 66/67 receipt and binaries remain: its generated fixture
contained malformed XML, and only that fixture was corrected, not assertions.

Fresh default-J Core/WinUI packages fail both unchanged lean-pair ceilings:
**708,715 / 537,600 compressed bytes** and **1,675,776 / 1,258,291 managed
bytes**. Scoped source hashes, owned DLL/PDB custody, passing/failed receipts
and limitations are recorded in `verified-checkpoint-k.json` and
`checkpoint-k-owned-binaries`. This is local unpublished managed evidence,
not live DPI/device, release, complete optional-provider, license or AOT
qualification.

### Additional same-run milestone: identity-bound SVG preparation metadata

Checkpoint L collects metadata during the provider's existing top-level full
security/resource-validation walk; nested SVG validation cannot replace the
outer metadata. A common bounded collector preserves the standalone reader's
namespace, first-direct-child, Unicode and root-geometry semantics. Metadata is
carried internally only after successful validation and bound to the exact
prepared byte-array identity. Cloned bytes, public `Admit`, rejected preparation
and providers without metadata retain the full bounded fallback scan. Final
cancellation/deadline checks leave rejected metadata empty; the borrowed stream
also checks cancellation during large XML reads. No public API, discovery,
reflection, source-copy or security bypass was introduced.

Fresh base Release/x64 diagnostic build U and diagnostics-disabled build L pass
with zero errors and the unchanged three audit-connectivity warnings. Normal
SDK test compiles AE/Q bind those products. The final focus passes **73/73 in
both modes**. The unfiltered diagnostic suite passes **2,158/2,158**, zero skips
and all 12 exact public API rows. The default full suite is **2,146/2,149**, zero
skips, retaining the same three diagnostic-Resvg dependency failures. Separate
complete runtime folders were frozen and hash-checked before these runs and
checked again afterward; initial and final owned binaries remain distinct.
An initial launch raced runtime preparation and found no test source; it
executed no tests and produced no TRX. Those logs remain, not a claimed pass.

**Provider integration is still unqualified.** Its fresh normal build fails the
unchanged NU1900-as-error audit-connectivity check. New provider tests and the
fused provider walk were source-reviewed, not executed against a newly built
provider. The retained old provider is not evidence for new provider code.
Fresh default-L packages fail both unchanged lean-pair ceilings:
**709,591 / 537,600 compressed bytes** and **1,677,824 / 1,258,291 managed
bytes**. Checkpoint L's scoped pre-follow-up source hashes, owned binaries,
runtime custody and passing/failed receipts are in `verified-checkpoint-l.json`.

The L isolated metadata diagnostic still allocates only **244,800 versus
3,816,096 bytes** over 12 parses (~94% less), but its **20.28 versus 0.99 ms**
standalone latency is a regression, not a speedup. The provider-fused path
avoids a second XML walk by design, but no end-to-end timing benefit is claimed
until it can be tested with the actual fresh provider. The O(N) authoritative
suffix, approved clean counterbalanced reference, same-byte Edge/500-page
audit, package-size and complete release matrix remain open.

### Additional same-run milestone: avoid redundant metadata attribute walks

Checkpoint M counts every SVG element's attributes without visiting every
embedded-image payload attribute a second time. Only root geometry attributes
are individually inspected by the metadata collector. `XmlReader.Read` still
validates all XML, including malformed descendant syntax, entities, duplicate
attributes and namespaces; all source, depth, structural, text and cancellation
limits remain. Five malformed-attribute regressions and just-below/above the
cumulative 800,000-attribute ceiling prove that the count is not bypassed.

Fresh Release/x64 diagnostic build V and diagnostics-disabled build M pass with
zero errors and the unchanged three audit-connectivity warnings. Normal SDK
test compiles AF/R bind those owned products. Separate frozen runtime folders
pass **80/80 focused tests in both modes**, **2,165/2,165 unfiltered diagnostic
tests**, all 12 exact public API rows and zero skips. The default full suite is
**2,153/2,156**, retaining the same three diagnostic-Resvg dependency failures.
The isolated twelve-parse SVG metadata diagnostic is **5.31 ms** (preceding L:
20.28 ms) at the same **244,800 bytes** versus **3,816,096 bytes** for the legacy
helpers. Legacy takes 1.20 ms in this sample and does less validation; these
uncounterbalanced timings are not release or browser-relative evidence.

Fresh actual WinApp 0.7 project-mode quick profile G passes, schema 12,
`IsReleaseEvidence=false`. Its one recorded 1 MiB cache-hit presentation takes
43.19 ms first viewport, 17.05 ms snapshot construction, 26,231,224 process
allocation bytes and 0.56 ms publication. This does not establish a release
speedup, UI callback distribution, Edge parity or the required device matrix.
Fresh default-M packages still fail both unchanged ceilings:
**709,422 / 537,600 compressed bytes**, **1,677,824 / 1,258,291 managed bytes**.
Archive and actual packaged-DLL inspection rule out a packaging-only fix:
even dropping every non-DLL entry would leave DLLs above the compressed cap,
with no reduction to the managed cap. No documentation stripping, mandatory
assembly shift or gate relaxation was applied. Scoped source/binary custody,
receipts and limitations are in `verified-checkpoint-m.json`.

### Additional same-run milestone: table/list vector and image reflow

Checkpoint N extends the bounded placement-only transaction to already
materialized vector/math leaves in direct table roots and existing
list/stack/quotation containers. Image-free vector cells/leaves participate in
the same geometry transfer. Native measure and transfer capture occur outside
the published source gate, with source/native/owner/generation checks before
atomic adoption. Existing scene, drawing, run, bitmap, cache-lease, source-map
and semantic-document owners are preserved. Generic publishable COW, nested
tables inside containers, incomplete native resources and over-budget trees
remain ineligible; all previous structural/text/style/image ceilings remain.

The tests found and fixed an actual completeness bug: GFM table construction
silently discarded math/extension inline fragments. A per-context scoped
child-inline route now uses the owning LayoutBuilder's canonical fragment
construction, and is cleared before publication or on cancellation/exception.
It neither probes providers nor retains the builder on the published context.

Normal Release/x64 builds X/O pass for Core, WinUI, Performance and GFM;
normal SDK compiles AK/W bind those exact owned products. Separate frozen
runtimes pass **170/170 focused tests in both configurations**, including 80
new rows, eight simulated DPI scales, LTR/RTL, hardware/WARP, overflow tables,
exact fresh-layout geometry/pixels/selection, resource ownership and failure
fences. The diagnostic full suite is **2,245/2,245**, all 12 exact API rows,
zero skips. The default suite is **2,233/2,236**, with the unchanged three
retained diagnostic-Resvg dependency mismatches, not suppressed or excluded.
Both fresh HTML builds fail NU1900-as-error, so the linked HTML child-builder
integration and other retained optional products are **not freshly qualified**.

Initial N failures and final repaired binaries/receipts are preserved in
`verified-checkpoint-n.json`. Final normal default-J packages still fail the
unchanged limits: **710,578 / 537,600 compressed bytes** and
**1,680,384 / 1,258,291 managed bytes**. No release/browser-relative speedup,
physical device matrix or top-500 full-page pass is claimed. The next active
high-impact investigation is the accepted near-40MP raster decode envelope;
the capped final bitmap alone does not bound WIC's source scratch memory.

### Additional same-run milestone: charge actual bounded raster outputs

Checkpoint O removes an unnecessary process-admission bottleneck. Transformed
raster misses previously charged output as `min(sourcePixels, MaximumPixels)`
even for small display-sized previews. The output weight now uses the larger
of the exact raw/swapped-axis sizing plans, conservatively covering EXIF
quarter-turns, authored dimensions, rounding and pixel caps. GIF charges its
logical canvas without an impossible EXIF rotation. Full source-surface bytes,
compressed bytes, output coexistence multipliers, admission capacities and
large-source exclusive handling remain unchanged. This is a scheduling
estimate, **not a codec-memory guarantee**. Unit evidence proves two 8MP/800px
preview weights can fit the existing x64 admission where their former
full-output weights unnecessarily serialized them; it is not a latency gate.

A generated **39,992,976-pixel PNG** passes the unchanged static-preview policy
and renders through actual ImageBox/WIC as **256x256**. Serial and two-request
demand tests verify exclusive native decoding, queueing and exact logical
lease drain. Initial sequential focused runs observe 1.58-1.59 MB serial and
8.95-9.47 MB two-request private-byte growth at 2 ms sampling. These values
include test-host effects, can miss brief allocations and do not qualify
other codecs or establish a hard native/RSS bound. Source generation precedes
the baseline; this fixture does not fetch remote content.

The first default full O run also exposed a table fixture race: the ownership
assertion ran before asynchronously retired construction clones released
their bitmap leases. The fixture now awaits the registered retirement ledger
before asserting the **same exact reference count**. No production disposal
was made synchronous and no assertion was relaxed; the failed receipt remains.

Normal diagnostic/default builds Y/P and test compiles AN/Z pass. Immutable
final runtimes pass **260/260 combined focused tests in both configurations**,
**2,261/2,261 unfiltered diagnostic tests**, all 12 exact API rows and zero skips.
The default full suite is **2,249/2,252**, only the same three retained Resvg
binary mismatches. Fresh default-K packages still fail unchanged ceilings:
**710,622 / 537,600 compressed bytes**, **1,680,896 / 1,258,291 managed bytes**.
`verified-checkpoint-o.json` records exact source/binary/receipt/package custody
and supersedes the table fixture's pre-retirement observation in checkpoint N.

### Additional same-run milestone: terminal WIC resource custody

Checkpoint P now owns the source-stream adapters, WinRT decode operations,
SoftwareBitmap and process decode credit as one preallocated retirement unit.
Both operations are awaited directly and closed only after terminal status.
Status-only failures retain retryable custody; a throwing native Close/Dispose
permanently quarantines remaining resources and credit without repeating native
access. Late single-slot quarantine now moves the retirement ledger's pending
count into its quarantined count without reporting an unknown resource as freed.
Legacy decoding, Fant filtering, EXIF, ColorManageToSRgb and policy limits remain.

Normal diagnostic/default Core/WinUI/Performance builds AA/R pass. Frozen P
runtimes pass **72/72 focused tests in both modes** and **2,273/2,273 unfiltered
diagnostic tests**, including all 12 exact API rows and zero skips. The initial
default full result is **2,260/2,264**: three retained Resvg dependency mismatches
plus a queued-generation fixture synchronization race. That failed receipt is
preserved; deterministic synchronization and subsequent prototype work require
their own verification and are not covered by the frozen P results.

Actual ImageBox/WIC testing exposes the next substantive memory gap: the pinned
16.8MP lossless WebP renders at 256x1025 but two **serially decoded** requests
retain approximately **273 MB sampled process growth** after logical cleanup.
Terminal-operation Close preserves exact pixels but does not release the
non-closable decoder's GC-timed surfaces. A DoNotColorManage hypothesis failed
exact pixels and did not solve memory, so it was rejected. Production never
forces GC. These 2 ms samples are diagnostic, not hard native bounds or qualified
performance comparisons. A source-generated, uniquely owned native WIC graph
is being tested before any production adoption; its first exact-pixel comparison
fails and no backend substitution is shipped.

Fresh default-L packages still fail unchanged limits: **712,428 / 537,600
compressed bytes**, **1,685,504 / 1,258,291 managed bytes**. IL/metadata/archive
attribution finds no accidental payload. A separate CsWinRT OptIn experiment
both omits required vtables and remains oversized; no production setting changed.
`verified-checkpoint-p.json` records exact ownership, frozen binaries, source and
receipt hashes, the rejected experiments and open qualification limits.

### Additional same-run milestone: native WebP lifetime proof and fused suffix index

The test-only Q prototype now produces **byte-identical** output to the existing
WinRT preview for the pinned 16.8MP ICCP-free, orientation-1 VP8L fixture. Its
unique native WIC interfaces and borrowed source pin retire deterministically;
the final pipeline uses explicit sRGB 32bpp PBGRA before Fant scaling. Direct
scaling, premultiply-only and a 64bpp CMM experiment failed exact pixels and are
preserved as rejected receipts. No shipping backend or fidelity gate changed.
The fresh memory-only eight-cycle run reports 58,793,984 baseline private bytes,
195,870,720 sampled peak, and 58,294,272 after logical retirement, without forced
GC. Handles increase 550 to 556; this is not the 100-cycle/four-handle release
gate, a hard codec memory bound, or qualified browser parity. Arbitrary ICC,
EXIF, other codecs, fault paths and architecture qualification remain open.

R now fuses ordinary document-order viewport-index publication into the required
root-local suffix arrangement. Custom/mismatched ordering keeps its defensive
full refresh. This removes one duplicate suffix-wide bounds/prefix traversal,
not the O(N) authoritative arrangement or its lock duration. Full-rebuild index
and band/endpoint/gap query parity is checked in existing native single/multi/lazy
root matrices. The queued-generation test now uses a deterministic producer
barrier, retains stale-result/reschedule/diagnostic assertions, and adds 100
repeated generation cycles.

The normal R diagnostic Core/WinUI/Performance build passes, and its immutable
unfiltered runtime passes **2,276/2,276**, zero skips, including all 12 exact API
rows. Optional products remain retained; this is not a fresh default/provider
release gate. Review subsequently found same-thread custom Arrange reentry can
still reserve a read lease during partial index mutation. A read-lease guard,
partial-write failure regression and 100-cycle native retirement qualification
are the next active local work; these are not claimed by R's passing receipt.
`verified-checkpoint-q-r.json` preserves this exact cohort and its limitations.

### Additional same-run milestone: partial-index read fencing and 100-cycle native proof

S closes the same-thread reentry gap found in R review. A tainted mutation's
owning thread cannot acquire another paint/interaction read lease; ordinary
healthy partial-lazy reads resume after commit. The new actual root-local image
batch regression first fails against R because its suffix Arrange callback
obtains a paint lease after earlier index entries were written. It then passes
against S, checks the fail-closed recovery latch after a partial-write failure,
keeps committed size/revision unpublished, and drains source/construction native
retirement. Nested reads on committed geometry are not broadly disabled.

Normal S diagnostic/default builds pass with the three unchanged NU1900
connectivity warnings. Frozen unfiltered results are **2,278/2,278 diagnostic**
(12 exact API rows, no skips) and **2,266/2,269 default** (no skips). Default's
only failures are the retained diagnostic-Resvg dependency/API type mismatch;
the prior queued-generation synchronization race is fixed. No tests were removed
or failures reclassified. The first expanded test compile's xUnit1030 error and
an MSIX pre-import collision are preserved; the Fact keeps xUnit's context and
the SDK test-reference hook now uses the after-import without bypassing MSIX.
An earlier zero-match stale-binary attempt is explicitly invalid evidence.

The separate owned native WIC candidate now runs 8 warmups then 100 synchronous
decode/retire cycles on one worker with one reusable output buffer. Three fresh
quiet testhosts each assert **900/900 owned interface releases**, zero outstanding
queried references, every source pin released and no quarantine. Process bytes
fall from 57.6/57.8/57.7 MB to 52.8/52.3/53.3 MB, and handles fall from
550/549/549 to 539/538/538. No forced GC occurs. These are post-retirement
observations, not transient peak bounds, product render/evict release gates,
general ICC/EXIF/codec coverage, or browser comparisons. The shipping WinRT
BitmapDecoder still lacks deterministic public disposal; the candidate is not
silently promoted. Its exact full-pixel oracle remains mandatory.

Fresh default-M packages still fail stock unchanged size limits:
**712,509 / 537,600 compressed bytes**, **1,686,528 / 1,258,291 managed bytes**,
with zero native payload entries. `verified-checkpoint-s.json` records exact
source, actual diagnostic/default runtime, passing/failed receipt and package
hashes, along with open external and implementation qualification needs.

Highest remaining priorities are qualified counterbalanced and same-byte Edge
evidence/top-500 full-page auditing, the package-size gap, exact authoritative
suffix placement architecture, and full codec/native-memory qualification.
This execution context still cannot provide the required restricted-worker
token, Sandbox/Edge process path or fresh optional-package audit connectivity;
those need a normal-token Windows runner with working NuGet/GitHub access.
The clean candidate also needs writable Git metadata and the human-approved
compatible benchmark reference. Physical architecture/device/DPI/theme/AOT
release gates remain open; no quick result is substituted for them.

### Additional same-run milestone T: shipping owned raster decoding and product eviction cycles

The exact native WIC candidate is now integrated into `ImageBox` for bounded,
single-frame PNG/BMP/JPEG/WebP transforms. Header preflight and immutable source/
orientation/output planning precede native construction. The worker validates
the actual container GUID, dimensions and frame count. It uses source-generated
COM and interop, a borrowing pinned source stream, authored-profile color
management (no synthetic sRGB-to-sRGB quantization), PBGRA/Fant scaling and all
eight JPEG EXIF mappings. It creates/uses/releases its unique native interfaces
and balances COM initialization synchronously on the same worker. The returned
CPU pixel lease contains no COM interface and holds process credit through GPU
upload. Safe failures erase partial output and refund once; uncertain native
release roots the entire graph, pin and credit in permanent non-retry custody.
The native cache partition prevents old raw-axis WinRT variants from being
substituted. GIF/ICO/AVIF, non-identity PNG/WebP EXIF and legacy eager paths remain
on their prior path. No native runtime failure falls back to WinRT under this
allowlist, and no public API, package dependency, limit or gate was changed.

Normal Release/x64 diagnostic/default Core/WinUI/Performance builds pass with
zero errors and the three unchanged NU1900 audit-connectivity warnings. The
production-focused corpus passes **70/70** in each build, no skips. The frozen
diagnostic unfiltered renderer runtime passes **2,338/2,338**, no skips; this
includes the unchanged exact API baselines. Failed first build/compile/runtime
attempts remain separate immutable evidence. The sole complete pre-promotion
failure was an expected-live weak-waiter fixture; the sole first production-full
failure was a collision fixture selecting old cache stripes. Both fixtures now
exercise the actual production contract without changing its weak ownership,
capacity, cache gate, or ordering assertions.

The full actual default-products runtime is **2,326/2,329**, no skips. Its only
failures are the three retained diagnostic-Resvg API/type-load mismatches; a
fresh provider build with audit enforcement intact still needs working NuGet
access. Fresh default-N packages contain no native binaries, but exceed the
unchanged compressed/managed lean limits at **731,369/537,600** and
**1,753,600/1,258,291** bytes. `verified-checkpoint-t.json` records the exact
source/runtime/receipt/package hashes, failed attempts and remaining gates.

The actual product ImageBox/WARP matrix contains 21 variants: PNG alpha,
indexed/transparency, grayscale/alpha, 16-bit grayscale/RGBA and authored ICC,
BMP/JPEG landscape/portrait, JPEG EXIF 1–8, and pinned oversized WebP. Each case
warms once before 100 deterministic interleaved load/upload/evict cycles. All
bitmap BGRA bytes match Windows; every cycle verifies native-branch completion,
held output charge, source-cache reuse and subsequent process/output/live-bitmap/
retained-cache accounting drain. Three fresh serial cycle-only testhosts pass
**300/300 product cycles**, no skips or forced GC. Observed post-retirement
private bytes grow by **6,516,736 / 6,930,432 / 6,615,040**, and handles fall by
**2 / 2 / 2**. These local observations fall within the unchanged 8 MiB/4-handle
retained-growth thresholds for this corpus, but are not transient native/driver
peak bounds, Edge-relative CPU/latency/memory qualification, a physical device/
architecture matrix, full-page rendering or top-500 release evidence.

Readonly review confirms the authoritative changed-height suffix still performs
O(suffix length) placement under the shared paint/input lock, although it runs on
a worker. The normal fused path no longer allocates another O(N) continuation
array or performs a second index pass; a range-offset-only index would be
incorrect because concrete box/semantic bounds and sequential float placement
remain authoritative. A coordinate-model migration must preserve those readers,
identities and exact placement before replacing that pass.

### Additional same-run milestone U: owned GIF frame-zero pipeline and retained memory failure

`RasterGifFirstFrameDecodePlan` captures the admitted logical screen, first image
descriptor, frame count, total resource charge and exact physical output axes.
The native worker repeats the complete GIF framing/trailer admission and validates
the actual WIC container/count/descriptor dimensions before decoding frame zero.
Palette/LZW/interlace behavior remains WIC-owned, not a hand-written decoder.
The same source-generated unique COM graph is retired synchronously on the worker
before returning its CPU lease. Native failures cannot fall back to WinRT. A
separate cache partition avoids legacy first-frame axis aliases. Temporary frame
and final canvas GPU bytes are admitted before factory construction; fractional
composition shares the established transparent-canvas/Linear draw helper. The
conservative process estimate charges 12x canvas output for full and partial GIFs.

The 11-variant ImageBox/WARP cycle matrix covers global/local palettes, zero and
nonzero transparent indexes, offset/interlaced descriptors, animation disposal
0–3, a 61-frame static preview and authored ICCRGBG1. Windows produces identical
GIF pixels with/without that ICC extension on this runtime; this is not a claim
of GIF ICC color management. Exact native frame and final composition parity also
cover 0.75–4x scale and one-pixel caps. Every cycle preserves 2,825 source-cache
bytes with no resolver refetch, verifies the native route and exact BGRA output,
then drains CPU/process/GPU/live-bitmap/retained-cache ledgers. The first cycle
receipt fails because the fixture required exact CanvasBitmap for an intended
CanvasRenderTarget; the repaired assertion explicitly requires the correct exact
type according to descriptor composition. The failed receipt remains intact.

Three fresh serial cycle hosts pass functional assertions but exceed the unchanged
8 MiB private-growth target by 12,984,320 / 12,349,440 / 12,148,736 bytes; handle
growth is -7 / +2 / 0. The additional observer probe remains above the private
limit and grows five handles. Its measured 3,861,872 bytes of cycle work plus
437,864 observer bytes, zero collections and unchanged 29,188,096 GC committed
bytes do not locate the native/runtime high-water. A separate stock testhost with
`DOTNET_TieredCompilation=0` ends 12,308,480 bytes below baseline but five handles
higher. That changed configuration is diagnostic only, not a shipping pass or
gate relaxation. Decoder-only and WARP-upload-only controls are separate fresh
testhosts; no forced GC is introduced. Per-graph wrapper-manager review found
only modest unused cache bookkeeping on installed .NET 10.0.12, not the 16-bucket
cache present in newer runtime sources; that newer-source estimate is rejected.

Normal SDK U diagnostic/default graphs pass. The diagnostic full runtime passes
2,364/2,364 and all exact API rows; the default full runtime remains 2,352/2,355
with only the retained provider/API mismatches. Limited normal pure-test compile
now includes `SvgSourceMetadata.cs`; the missing-include failure is retained, and
the unfiltered managed suite was still running when U was sealed. It subsequently
passed 1,762/1,762; W records the completed receipt without rewriting U. Fresh stock
packs fail at 729,979 compressed and 1,741,824 managed bytes against unchanged
537,600 / 1,258,291 limits. Private payload arrays remove 16 generated nested
string helper types, zero replacements, and keep required UIA collection helpers;
the final DLL reduction is 11.5 KiB, not a package-gap closure. All U runtime,
receipt, package and scoped source hashes are in `verified-checkpoint-u.json`.

1. **Bound live image output across controls and caches.** Implemented an opt-in,
   process-wide unique-bitmap admission ledger: 64 MiB on x64/ARM64, 32 MiB on
   x86, including uncached entries, cloned leases, pending publication, retained
   resize outputs, SVG tiles, and GIF frame/canvas overlap. Separate speculative,
   initial-visible and replacement tiers reserve visible headroom. Denied work
   drains bytes, CPU slots and native candidates before registering a bounded
   source-free waiter. Retry reauthorizes and resolves the source again. This is
   participating pixel ownership, not a driver/RSS or complete vector-memory cap.
   `ReleaseOffscreenImageResources` governs participation; JitHub already opts in.
2. **Remove native image-completion reflow from the source snapshot gate.**
   A bounded root-local path now takes priority over whole-tree COW for completed
   standalone raster images, paragraph/heading and table-cell inline images,
   ordinary list/blockquote/container roots, and full non-tiled SVGs. It measures
   up to 32 changed roots and 32 aggregate image pairs outside
   the source snapshot's read/paint/input gate, independently of document length
   or pending/unsupported siblings. Every image keeps its source object, bitmap
   lease, lifetime, subscriptions and semantic identity; inline roots also keep
   every original text/link/image run and source-map identity. Only fresh native
   placement layouts and measured run dimensions are adopted. The inline envelope
   stays at 256 runs, aggregate 64 Ki layout text and 8,192 copied style-key entries;
   the transfer fence borrows candidate-owned immutable keys rather than copying
   the payload twice. Tables charge the same aggregate 256-node/run, 64 Ki text,
   8,192-key and 32-image ceilings, including image-free and genuinely empty
   cells; row/column counts are bounded before sparse-row enumeration. The GFM
   parser's zero-length built-in text run is accepted only for a clean table
   cell, with the run ceiling applied before scanning it. Table column/row
   geometry is privately measured and adopted without replacing cells, runs,
   source-map/UIA identities or bitmap leases. Source and prepared image widths
   are fenced separately when responsive columns rebalance. Distinct completed
   cell IDs, including mixed root/child dispatches, consolidate into their unique
   top-level roots; duplicate raw IDs remain rejected. Cross-cell/image native
   layout ownership uses one pre-sized reference set, allocated before the final
   gate, rather than a quadratic all-cell comparison. The ordinary container path
   charges the same aggregate ceilings before cloning or taking image leases,
   including image-free paragraph/heading/list-marker leaves. Fixed-width
   container leaves and responsive variable-width table cells have separate
   transfer modes. List/stack shells retain their original children, markers,
   runs, image objects and semantic/UIA identities; their detached placement,
   configuration, child order, exact source bounds, raw/effective margins and
   fixed widths are fenced before any swap. Hidden raw-margin changes cannot
   bypass the fence through the effective-margin getter. Child-edge and native
   layout aliases are checked with pre-sized reference sets rather than
   quadratic scans. Nested tables, code, hosted task controls and persistent
   safe-HTML flow containers remain conservative fallback shapes. Successful
   stack/list arrangement now clears dirty state; list-item placement uses
   absolute child coordinates so repeated arrangement cannot accumulate margins.
   All source/provider/lease/pointer/width/
   revision fences and cross-pair layout-ownership checks pass before any placement
   is swapped. A stale final pair leaves earlier placements untouched. One shared
   fresh context owns cleanup; cancellation cannot interrupt live placement plus
   metadata publication after adoption. Late cancellation still publishes committed
   geometry, while stale scroll anchors are suppressed. Successful output-admission
   epochs are inert history, not live waiters; the transfer preserves that history.
   Inline children preserve their existing dirty-state contract; the clean measured
   parent and exact per-image width/lease fences guard adoption. Native visual
   clipping narrower than the run's desired width is accepted and tested before
   paint through the image-plan geometry path. Lazy roots preserve the source's
   residency mode instead of reading a reset shared construction hint.
   Root labels share the existing aggregate 64 Ki layout-text ceiling. Opted-in
   dirty storms dispatch bounded 32-index payloads and retain the remainder for
   subsequent work (72 indices drain as 32/32/8); failed setup consumes no indices.
   Delayed retry callbacks carry scalar metadata rather than retaining dirty arrays.
   Dirty roots now use a live-only ordered set, so capturing B roots costs
   O(log N + B) rather than scanning a drained HashSet's historical capacity;
   earlier document roots are dispatched first. A 100,000-index storm/drain/requeue
   regression preserves all live indices. The root-local commit now computes
   final height during its required sequential suffix arrangement, eliminating
   the duplicate suffix prepass without changing float placement semantics. Its
   metadata object is allocated before adoption, completed once before publication,
   then frozen; published readers never see candidate size. A document-ordered
   viewport index refresh touches only the changed suffix and preserves prefix
   maxima. Custom ordering retains full defensive refresh/sort. The suffix still
   does O(N) work under the gate for an early height change; bounded private
   payloads are not proof of a 60 Hz interaction budget. Over-budget containers,
   nested tables, code, hosted/task/HTML flow, tiled, incomplete or otherwise unsupported
   changed roots still use the protected paths below.
   The higher-frequency lazy viewport path now preserves the untouched prefix
   instead of re-arranging the entire document on every newly visible band.
   Exact float continuations recorded after `AdvanceBlock` seed suffix placement;
   a double `Bounds.Bottom` reconstruction is not used. Lazy snapshots pay four
   bytes per root, released at retirement; eager snapshots do not allocate this
   buffer. Revision, width, finite-continuation and document-order fences reject
   stale/custom-order acceleration. Root-local images, legacy changed-owner
   relayout, full measured relayout and bounded private candidates maintain these
   scalar checkpoints in their existing arrange loops, with no second scan or
   allocation at adoption. If a suffix start differs from the exact checkpoint,
   acceleration is invalidated rather than inventing a new prefix. When new
   measurement preserves exact outer bounds, margins and both shared-flow edges,
   reflow/index refresh is skipped altogether; mask, metrics, revision, UIA and
   residency publication still occur. Shared height observations are hints, not
   authority to move an unrelated committed estimated prefix. All boxes retain
   physically arranged, authoritative bounds for paint, hit/selection, anchors,
   focus and UIA. Fixed-point realization still finds roots entering the band
   after a height change. Attempted-root rollback starts before native measure,
   includes its predecessor for flow-edge recovery, and runs with cancellation
   suppressed. If recovery itself fails, the snapshot rejects new read/paint
   leases and retires asynchronously; both failures are retained. This geometry
   fail-closed behavior does not solve persistent native-disposal ownership.
   Legacy changed-owner reflow also computes final height in its one required
   sequential arrange pass, replacing the duplicate suffix prepass and full
   prefix index refresh. Early changes still require O(N) suffix work; this is not
   an effective-bounds migration or a browser-relative performance qualification.
   Recursive private placement now covers bounded eager and lazy documents with
   paragraphs/headings, rich text, standalone and inline images, tables, lists,
   quotes, code blocks and completed full non-tiled SVGs. Mutable wrappers,
   cells, chunks, text layouts and code-line lists are privately owned; native
   measurement, arrangement and index/semantic construction occur outside the
   source gate. Lazy candidates preserve the measured mask and estimated heights
   without realizing distant content. Never-started image descriptors may be
   copied without bytes, tasks or provider calls; active/pending work remains
   ineligible. Whole-tree preflight rejects unsupported state before acquiring
   leases. The existing ceilings remain 64 roots, 256 nodes/runs, 64 images,
   depth 32, 64 Ki layout text and 128 Ki semantic text; code lines and syntax
   spans charge those aggregate budgets, and style-key copies are capped at
   8,192 entries. General over-budget, mixed/vector, tiled-SVG and active-image
   whole-tree COW remain open; root-local preparation does not raise those ceilings
   and can operate with such untouched siblings in the same large document.
   SVGs share only accepted immutable metadata and independent bitmap leases;
   provider generation callbacks occur outside source/image locks. Code clones
   preserve syntax colors, horizontal offset, selection/focus identities and
   native Copy-button ownership through exact source-to-placement mappings.
   The old tree retires before host/XAML callbacks; every button update checks
   its current owner and publication tuple. New viewport membership is reconciled
   immediately. Gesture guards preserve pending touch and captured selection,
   table and handle interactions across completion and cancellation.
3. **Make native vector ownership safe before adding aggregate admission.**
   Partial geometry, stroke, text-layout and temporary text-format construction
   now attaches native owners before cleanup. Successful slots clear; failed
   slots stay owned for a later release/dispose retry. Cleanup attempts every slot
   and preserves the primary preparation/paint exception with bounded secondary
   detail. Eager inline runs register with the construction scope before preparing;
   graph refill creates a deferred drawing owner before native preparation. Paint
   closes clips in LIFO order while native close results remain known, restores
   the caller's exact transform even when the
   first transform setter fails, and disables repaint if failed clip slots remain.
   Detached fallback replacement disposes the old layout before adopting the new
   one; failed old layouts remain nonpaintable and owned, with the candidate still
   caller-owned. Real nested clip/group pixels repeat exactly at simulated
   100%, 150%, 200% and 400%, on normal D3D and WARP (eight parity rows).
   Durable native-only retirement now covers drawing construction/refill/terminal
   cleanup, detached vector fallback layouts, and paragraph/table primary,
   selection and temporary intrinsic layouts, plus image captions/placeholders,
   temporary inline-failure measurements and shared cached text formats.
   Records and their intrusive links
   are reserved before native factories. Successful slots clear; terminal failures
   move only native slots into process-owned custody, without retaining the scene,
   document context, runs, resource-creator wrapper or exception graph. Failed
   constructor prefixes retire even when no drawing is returned. Each inline
   placement swap moves its primary/selection layout and matching retirement
   record together; pre-sized identity sets fence both before adoption. Selection
   rebuilds stage a complete candidate before replacing the cached layout, and
   rollback always retires a candidate even if restoring geometry or collecting
   secondary failures throws. The existing two-argument inline constructor CLR
   signature remains intact for already-built friend assemblies.
   Image placement swaps now move both layout/record pairs together, fencing
   exact source and prepared generations before any swap. Cross-image and
   image/cell aliases of either layouts or records reject the entire batch.
   Inline checkpoints preserve original pairs; rollback restores their geometry
   and ownership before publishing detached candidates. Disposal revokes late
   checkpoint tokens but retains original pairs until background terminal cleanup.
   That pass uses four fixed local pairs, attempts every unique native close
   outside the image lock, publishes failures before diagnostic aggregation, and
   retains the existing RetirementTask failure contract. No terminal arrays or
   recovery records are allocated. Options/getter failures retain the candidate
   through its owner reserved before the native factory.
   Image labels and code header/gutter formats now borrow immutable cache leases
   with their original wrapping, direction, locale, vertical alignment and normal
   DirectWrite line spacing. The original six-argument cache Acquire signature
   remains available. Cache misses reserve custody before native initialization
   and stage leases before publication; failed hit-lease creation balances only
   its acquired reference. Eviction preserves active leases, and failed final
   releases retain only native formats, not entries, styles or lease graphs.
   Single-slot layout/format records store their native slot directly, removing
   five small arrays per reservation. Executed x64 allocation assertions bound
   this path at 192 bytes per reservation and require at least 96 bytes less
   allocation than the grouped path. These are allocation checks, not a latency,
   aggregate-memory or browser-relative performance result.
   A single precreated background clock services at most eight records and 128
   visited slots per pass, rotates failures fairly and backs off to five seconds.
   Failed session-bound clip closes remain quarantined, never retried off-session;
   ordinary geometry/layout/format/builder/style slots retry until cleanup succeeds.
   This bounds retry service, not the backlog, native bytes, RSS or driver memory.
   Grouped publication still compacts the original slot arrays in O(N); timer-arm
   failure retains custody but presently needs a later reservation to rearm.
   Outer paint layers now share a source-free, drawing-session-keyed custody
   guard across code, tables, images, block vectors, selected text and link focus.
   Reservations precede native layer/rounded-geometry factories. A failed inner
   pop stops all ancestor closes, poisons the session, and quarantines uncertain
   layers plus dependent geometry without background retries. In-flight factory
   results attach to their pre-reserved slots before reentrant poison is acted
   on. A vector clip factory throw also poisons the stack: depth advances only
   after a wrapper returns, and an uncertain partial push never causes known
   ancestor layers to be popped. The isolated real-native fault test creates a
   second layer and throws before returning it, then proves all known ancestors
   stay untouched; test-owned wrappers are closed manually while the session
   lives. Production cannot assume access to an unreturned native wrapper.
   After either uncertain push or pop, vector geometry owners also remain
   nonretryable, including when every known clip slot is null. Conservatively
   retaining the compact geometry group avoids releasing a clipping dependency
   while an unreturned native layer may still use it. Terminal handoff retains
   only native slots; the scene and test callbacks are cleared.
   Geometry retries only after its dependent layer has successfully closed.
   Primary paint and cleanup failures remain visible together; bounded,
   allocation-free graphics-error classification inspects aggregate siblings
   before descending into a large primary exception graph, then fairly partitions
   the remaining visit budget among nested siblings. A shared primary exception
   DAG cannot consume every visit before a wrapped cleanup device-loss/shutdown
   failure is inspected. The 1,024-visit and 32-level bounds remain unchanged;
   no flattening, heap queue or identity set is introduced. Successful private
   nodes/records reuse by maximum nesting depth within one drawing session;
   published failure records never recycle. Scalar generation tokens reject
   stale close and attach calls. This is not a global cross-frame pool.
   Vector drawing scratch storage now uses actual path/text/special-stroke
   counts and peak group/clip nesting, instead of five command-sized arrays.
   Independent resource cursors consume hidden/transparent commands too. The
   counted scalar shape survives reversible eviction; no command-index map or
   repeated warm count pass is added. The 8,256-command sparse-path fixture
   retains 512 bytes of scratch-array payload versus the prior 462,336 bytes
   on x64, excluding array headers, native allocations and immutable graphs.
   Normal detached image replacements may queue cleanup too; service quanta
   and compact scratch storage do not bound retained native bytes.
   Full aggregate native admission remains open. It requires a typed deferred
   preparation outcome, readable block and inline fallback, visible-over-overscan
   priority and coalesced stationary recovery that preserve the previous active
   band on denial, not a ledger-only change to synchronous void preparation.
   A governor-only patch was rejected: denial currently has no safe stationary
   recovery/first-layout fallback, and old-plus-new viewport transitions could
   deadlock capacity. Native weight estimates are not RSS or driver-memory proof.
   A future typed accounting lease must stay charged until every native slot in
   its retirement record drains; putting it into the ordinary retryable slots
   would release capacity while nonretryable layers remain quarantined. Visible
   headroom and a bounded fallback-backed transition must avoid reserving an
   entire old and new viewport simultaneously. Both block and inline fallbacks,
   capacity-epoch retry and separate actual-visible/overscan priorities remain
   prerequisites, not completed admission behavior.
4. **Avoid unnecessary work and recover admitted work fairly.** Raster completion
   with proven fixed authored width/height is paint-only, with UIA status updated
   and no layout rebuild. A shared recovery clock scans a bounded current-image
   band and retries output admission even if the finger/viewport is stationary;
   a weak, coalesced managed fallback handles native timer creation failure.
   Separate capacity and waiter-slot versions prevent lost wakeups. Eight of
   256 waiter slots remain reserved for initially visible work. These are opt-in
   progressive behaviors already enabled by JitHub, not new public feature flags.
5. **Verify the implementation without substituting diagnostics for release gates.**
   The latest compact-vector/paint-custody checkpoint passes 1,984/1,984
   unfiltered renderer cases, no skips, including all 12 public API baselines
   (`renderer-all-paint-compact-custody-e.trx`). The preceding focused native/API
   checkpoint passes 165/165 (`paint-compact-native-custody-e.trx`). There are 54
   new renderer cases beyond the earlier image/format checkpoint: compact shape
   and scratch-storage checks, 32 exact old-painter pixel comparisons at simulated
   100%, 150%, 200% and 400%, LTR/RTL, normal/High Contrast palettes, D3D/WARP,
   and real-native layer/push/pop/geometry custody faults. Hidden and transparent
   resource commands consume the same compact cursors; release/reprepare retains
   the cached shape. Partial pushes, failed pops, outer session poison, stale
   layer/geometry tokens, dependent geometry, factory reentrancy and source-free
   quarantine are covered. Test-owned native wrappers close manually while their
   session lives; production never retries an uncertain layer pop.
   Fresh diagnostics-disabled Release library builds pass, with zero errors and
   the same three NU1900 audit-connectivity warnings. Tests explicitly compiled
   against those three outputs pass 119/119 selected native cases, no skips
   (`paint-compact-default-release-c.trx`); other package references remain the
   existing inputs. This is not qualification of the full default shipping graph,
   its SVG worker, NativeAOT or package graph. Fresh diagnostic app source-linked
   verification passes 3,571/3,571, no skips
   (`artifacts/performance-priority-20261003/app-priority-paint-compact-custody-c.trx`).
   The default app variant separately passes 3,567/3,567 (`...-b.trx`): its four
   omitted cases are explicitly diagnostic-only activation/worker audit tests,
   not removed tests. Strict DTD-disabled TRX reads confirm unique execution IDs,
   every recorded outcome and the 12 baseline rows.
   Final diagnostic compiler/staged DLL SHA-256 values are Renderer
   `FD6F9F1AB45085BF21D5258B0D5152B47B31E8CD91B4D9D6B8FE7327856B3DE3`,
   Performance `C0930D06ADDA613D2261EE617CCC01A1FE5BC3E0DDDCED62800DE5AB79D78AEA`,
   tests `57AE7E71905AD7A61E725D4C6AEA2B8C3986F3EB5359E8E823627F8CA15BF220`.
   Core remains `C1B5E6CBA1DAABDE9C64BCF5090E4CE13A888F79CE5B1259BB7916BCEFDE8176`.
   Final diagnostics-disabled hashes are Renderer
   `FBE85C0A8B0197D7425FDBF11195BFB047CFF9BA14684E0239E9C70D2A0688A3`,
   Performance `4FF504F903D6AEDA9CB9259DE8EF56D101B10009FAC1F187DD950031905FD732`,
   Core `17DC9BEF49510002C6B0FB0E3F41A236D56BCE15232318E22CFB538F867E1A48`,
   tests `CBBE64ADD4B240169F387C27CF46AE2ADB5693D4A7EA00DD88A3E5B65171C289`.
   Failed earlier receipts remain preserved. A paint fault fixture originally
   let its intentionally quarantined native layer reach session teardown; it now
   closes only the known test wrapper first. Two Mermaid typography fixtures
   indexed text layouts by command ordinal; they now map complete DrawText
   commands to dense text-resource ordinals, retaining exact typography/ink
   assertions. Compiler failures exposed an unqualified System.Math reference
   and a missing CanvasGeometry import. After the latter, D accidentally staged
   the previous successful test DLL with the new production DLL: its three
   failures and mixed scope are retained, never treated as valid final evidence.
   Two stale clip expectations were corrected to require retained geometry too.
   The unchanged third failure was a 32-operation allocation comparison. The
   steady-state test keeps its 64 B/operation ceiling, explicitly warms both
   Win2D paths, measures five alternating-order paired 512-operation batches,
   and checks exact node/retirement-record identity on every managed iteration.
   The separate fresh-session 1,024 B/operation ceiling is unchanged. Both tests
   pass in the unfiltered/default runs and three fresh-process repeat receipts
   (`paint-compact-allocation-repeat-b/c/d.trx`, 2/2 each); repeat A discovered
   only the fresh-session case because its warm-test filter used the old name.
   No persistent cost is waived; these are managed allocation checks, not a
   browser-relative latency, native-memory or cross-frame pooling claim.
   The first default test-output isolation attempt redirected unbuilt dependency
   metadata to nonexistent paths. The corrected target isolates only the test
   project; failed compiler output is not staged. The previous full managed
   attempt exposed a stale SessionlessImageRelayout source contract; the fixed
   contract verifies bounded pending-index capture and removal after scheduling,
   preserving the remainder rather than expecting a whole-set Clear. Its focused
   graphics/handoff run passes 24/24 (`core-paint-compact-targeted-a.trx`). The
   fresh isolated unfiltered managed rerun passes 1,747/1,747, no skips
   (`core-all-paint-compact-custody-b.trx`), including 501 external-gate fixture
   cases. Its test DLL is
   `EF787EADD79F2D9EBD039A1F4A601AF04B337035D308B678D41CFB93C6E744EC`.
   The preserved earlier A receipt passes 1,745/1,746 and fails only the stale
   handoff source contract; it is not a passing receipt.
   Final review subsequently found that shallow sibling inspection alone could
   miss a nested cleanup graphics failure after a large shared primary DAG.
   The fair-budget fix adds one zero-allocation regression covering nested
   device-loss/shutdown failures and negatives. Its current frozen source-linked
   managed scope passes 1,247/1,247, no skips
   (`core-paint-fair-graphics-c.trx`), excluding only the 501 external-gate fixture
   cases; those fixture methods and verifier inputs were unchanged during this
   classifier-only follow-up. Its test DLL is
   `554C7CC70038549329654FB577D02983560652625BB48D85CF874015F928CEA5`.
   This is separate-scope evidence, not a newly executed unfiltered 1,748-case
   receipt. The final renderer/default receipts above include the fair classifier
   implementation; Core.dll is unchanged because the classifier lives in Renderer
   and is source-linked by the pure managed test project. The preceding full
   renderer D and default B receipts remain valid pre-fair-fix checkpoints against
   Renderer `B1175448655A832E364ED3ECA3A7583C695D6B7F1854F47B53BDB692E79F2DA2`
   and default Renderer
   `3010E02E03CC24C3A152A11AC5F962A0E90C1A6E7383E8C81ED946D98AB8BFF8`.
   This checkpoint does not establish aggregate native admission or remove
   the remaining O(N) early-height suffix reflow. The next implementation must
   complete typed deferral, both block/inline fallback, visible-first transition
   headroom, weak stationary retry and retirement-coupled charges together.
   Absolute-bounds migration instead touches paint, inverse hit transforms,
   selection, anchors, UIA, image residency and hosted-control placement; simply
   skipping the suffix would publish inconsistent geometry. Release/Edge/top-500,
   AOT, worker, architecture/device/theme and package/license gates remain open.
   The earlier image/format-custody checkpoint follows for provenance.
   Fresh Release/x64 diagnostic and diagnostics-disabled library builds pass with
   zero errors and three unchanged NU1900 audit-connectivity warnings each.
   Earlier focused custody/format/allocation verification passes 140/140, no skips
   (`image-format-custody-b.trx`), including 40 new cases beyond the first native-
   custody checkpoint. Actual native fault tests cover image Options failures,
   checkpoint commit/rollback/disposal, late checkpoint tokens, caption/layout
   paired swaps, stale source/prepared generations, record aliases, shared-format
   initializer/entry/lease/publication failures, cache-hit rollback, eviction,
   concurrent final release and source-free failed-native retry. Sixteen exact
   code header/gutter pixel comparisons cover simulated 100%, 150%, 200% and
   400%, LTR/RTL and normal D3D/WARP, against the original direct-format path.
   Existing root/image/table/lazy/native-pixel/API regressions pass 215/215, no skips
   (`image-format-root-parity-a.trx`). Unfiltered renderer verification passes
   1,930/1,930, no skips, including all 12 public API baselines
   (`renderer-all-image-format-custody-b.trx`). Fresh app source-linked verification
   passes 3,571/3,571, no skips
   (`artifacts/performance-priority-20261003/app-priority-image-format-custody-b.trx`).
   The same 40 newly added native/cached-format cases also pass against the
   diagnostics-disabled Core/WinUI/Performance outputs, no skips
   (`image-format-default-release-c.trx`). Their matching test assembly is
   explicitly compiled against those three outputs; other managed package inputs
   remain the existing test references. This does not qualify the full default
   shipping graph. The first default-runtime attempt incorrectly reused the
   diagnostic test DLL and failed 2/40 on the intentionally different internal
   bitmap-cache signature. Recompilation fixes both cases. Supplemental default
   API attempt B remains 51/52 because the still-diagnostic SVG provider requires
   an omitted diagnostic type. Its isolated default rebuild fails on NU1900
   warning-as-error; that dependency-audit gate was not disabled or demoted.
   Default API/worker qualification therefore remains open. All failed receipts
   and the two default-test compiler outputs are preserved separately; the normal
   diagnostic compiler outputs are rebuilt after this variant check.
   The isolated default DLL SHA-256 values are Renderer
   `3860234D7A4E4953A7D38AEDA8985C8CEAB972AAD0E9A08FF5CEEAC55E4836D1`,
   Performance `96FA715FB21E436D0A6410A80020E19A2F812EF14256E60A80B2F5FCA3698919`,
   Core `17DC9BEF49510002C6B0FB0E3F41A236D56BCE15232318E22CFB538F867E1A48`,
   matching tests `9A0E16A2F8464467FD0FE8A3C77308E5CDC7AFA2D9BFFCB00CDA485F235ACA33`.
   Compiler/staged DLL SHA-256 values are Renderer
   `209330E821C4BAE391114B2FF19CB0153E0ABA8A260A1574C127405DD48C40D2`,
   Performance `1A1527E774843799433D03FE2173B1083EEA9C770D0000AF2D3BA8B35AA38C32`,
   tests `A16482CE2EBD33B1DA8895E46BF092E8C13372D98B6DD0948878048254FEED71`.
   Core is unchanged at the hash recorded below. Strict DTD-disabled TRX reads
   confirm every recorded execution ID is unique and all 12 baseline rows pass.
   The failed A focused receipt remains 120/140: the new code fixture omitted
   real text chunks and its direct-format oracle omitted the normal header
   separator. The fixture now adds an actual measured chunk and renders the
   original separator; no pixel region, tolerance or assertion was reduced.
   The failed A app receipt remains 3,570/3,571: its old source assertion expected
   direct format construction. The updated contract verifies the image's exact
   theme-style/wrapping/direction/locale/alignment forwarding and the cache's
   font-family/font-size mapping, preserving the typography requirement.
   Initial test compilation rejected four xUnit analyzer violations; those use
   the required filtered Assert.Single overload now. No failed compiler output
   was staged. These runs do not qualify the worker, Edge, full top-500 audit,
   physical device/DPI/theme matrix, memory ceilings, security or NativeAOT gates.
   The earlier native-custody checkpoint
   passes 73/73 focused cases with no skips (`native-durable-custody-f.trx`), including
   19 new tests. Actual Win2D failure injection covers constructor prefixes,
   geometry/builders/temporary formats/styles, detached fallback candidates,
   cancelled paragraph reflow, superseded primary/selection layouts, intrinsic
   rollback, complete selection staging and placement-layout/record swaps. Weak-
   reference tests keep the registry alive while proving source graphs collect,
   failed native slots remain retained through another failed retry, and successful
   retry releases the last slot. Queue quantum, fairness, duplicate publication,
   concurrent drain exclusion, post-publication mutation and quarantine are covered.
   Existing root/image/table/lazy/native-pixel/API regressions pass 215/215, no skips
   (`native-custody-root-parity-b.trx`). Fresh unfiltered renderer verification
   passes 1,890/1,890, no skips, including all 12 public API baselines
   (`renderer-all-native-custody-a.trx`). Fresh app source-linked verification passes
   3,571/3,571, no skips
   (`artifacts/performance-priority-20261003/app-priority-native-custody-b.trx`).
   That checkpoint's compiler/staged DLL SHA-256 values are Renderer
   `CD486532A1DCFC5B47039FD9134F97DB07CE5FFA35AA6C35A734F2EC1EF0E53C`,
   Performance `98CF52914EBD021D35272A2316BF74AA2D43335BD49D40A5CD5DE4F842BD5116`,
   tests `AF02FCE6B5A8EB7FC37A4ACD1D76C7DBC818241B29E5C3A3684E6D20FEDC3685`.
   Core is unchanged at `C1B5E6CBA1DAABDE9C64BCF5090E4CE13A888F79CE5B1259BB7916BCEFDE8176`.
   Earlier failed A–E custody receipts and A root-parity receipt remain preserved.
   They exposed test-frame temporary GC roots, mutated covariant ownership-array
   fixtures, distinct context disclosure snapshots, capture-after-measure ordering,
   an outdated terminal source assertion, and the real removed-constructor ABI
   regression. The factory now creates scenes inside a no-inline lifecycle frame;
   final GC keeps the registry live. Context identity and capture-before-measure
   match production, and the original constructor signature was restored. No GC,
   ownership, pixel, API, resource or cancellation assertions were weakened, and
   no failed compiler output was staged. These are diagnostic correctness tests,
   not a native-memory cap or browser-relative performance qualification.
   The earlier lazy/suffix
   checkpoint passes 100/100 with no skips (`priority-lazy-suffix-a.trx`), including
   32 new cases: a 100,000-root realization leaving all 99,999 measured prefix
   roots untouched, exact-estimate no-op, shared-hint changes, large-Y/fractional
   float and shared-flow arithmetic, fixed-point membership, pre-mask failures,
   ambient cancellation, fail-closed recovery failure and cross-path checkpoint
   maintenance. Sixteen actual DirectWrite/Win2D rows compare both seeded no-op
   and unseeded changed-suffix layouts with an explicit full-prefix oracle at
   simulated 100%, 150%, 200% and 400%, LTR/RTL, normal D3D and WARP. Exact root/
   descendant bounds, measured masks, pixels, UIA text/selection rectangles,
   hit/focus, anchors and viewport probes match. These are Light-theme simulated
   scale regressions, not physical DPI/theme/device or Edge qualification.
   Fresh unfiltered renderer verification passes 1,871/1,871, no skips, including
   all 12 public API baselines (`renderer-all-lazy-suffix-a.trx`). Fresh app
   source-linked verification passes 3,571/3,571, no skips
   (`artifacts/performance-priority-20261003/app-priority-lazy-suffix-a.trx`).
   That earlier checkpoint's compiler/staged DLL SHA-256 values are Renderer
   `BABC6676E98C073F7895420299DA1490C91555B44C971E3D1F5265B5D07AAB45`,
   Performance `74952973181A7A314D996F7315521E57556F1EE4FF91CA07AB75F2C2F89D2E6B`,
   tests `1E6F4F3B06C4F97331D1E64323E67E3DD3791C923552B82C4E802F31E12FFA31`.
   Core remains unchanged at the hash recorded below; its earlier unfiltered
   receipt remains applicable. Two test compilation attempts rejected new
   test-only local-name shadowing and an invalid namespace import; neither failed
   compiler output was staged or used for verification. The final successful
   compilation uses explicit `System.Math` in the sibling Math namespace.
   The earlier root-batch checkpoint
   passes 93/93 (`rootmeasure-batch-focused-c.trx`), including real parsed
   GFM/local PNG/WIC/Win2D completion, atomic batch failure/ownership fences,
   >64-root lazy documents with measured pending siblings and distant unmeasured
   images, gesture/revision/cancellation races and dirty-storm capture. Single and
   batch root pixels/geometry match the established reflow exactly at simulated
   100%, 150%, 200% and 400%, in LTR/RTL, on normal D3D and WARP devices (32 parity
   rows). The latest combined table/inline/root/vector/lifetime/index run passes
   372/372, no skips (`priority-container-root-combined-b.trx`), including native partial-
   allocation cleanup, construction-scope retirement and exact nested clip/
   transform pixels. The 26 new inline-root cases include 16 exact mixed inline/
   standalone pixel-parity rows at the four simulated scales, both directions
   and both devices, plus clipped source geometry and image-plan indexing before
   paint, a stale late inline run leaving the earlier image untouched, provider
   generation reads outside source/image gates, lazy pending siblings, cancellation,
   pointer/revision races, aggregate image-limit rejection and unsupported nested
   code in a list. Ordinary lists are now supported; the older rejection fixture
   explicitly verifies its real code child and preserves every no-mutation check.
   The earlier table/index checkpoint passes 54/54, no skips
   (`priority-table-root-transfer-d.trx`). Its 34 table cases include 20 exact
   fallback pixel/geometry rows: the four scales, LTR/RTL and both devices at
   640 DIP, plus four constrained 320-DIP rows. Wider tables genuinely rebalance
   columns; constrained tables retain responsive image sizing at their content
   minima. Tests retain nonzero horizontal scrolling and the exact fallback clamp,
   image-plan bounds before paint, UIA/selection/source identities, pending/distant
   lazy siblings, late cancellation, pointer/revision/offset races, mixed five-root
   batches, distinct child-ID consolidation, cross-cell/image native alias rejection,
   and pre-lease 33-image/256-node/257-empty-run limits. A last-slot update in a
   100,000-entry viewport index reuses its backing array with zero managed allocation;
   overlap, nonfinite bounds, custom-order fallback and repeated commits are covered.
   Metadata tests enforce one-time prepublication completion, no setter, frozen
   concurrent reads, and rejected completion/republication after publication.
   The new container/table/index checkpoint passes 90/90, no skips
   (`priority-container-root-transfer-g.trx`), including 33 new list/blockquote
   cases and two additional full non-tiled SVG table cases. Six container
   adversarial rows independently exercise single-root/cross-root image caps,
   aggregate/single-leaf run caps, node count and depth, with source placements
   genuinely measured and image work complete. Rejection occurs before private
   measurement or candidate-context exposure and retains exact identities,
   bounds, metadata, resolver counts, bitmap leases and reference counts. The
   16 container parity rows include parsed loose/tight nested lists, blockquotes,
   nonzero asymmetric list-item margins, image-free leaves, links, PNG and SVG at
   the four scales/directions/devices. Repeated list arrangement is idempotent;
   hidden raw margins, child order, descendant image geometry, configuration,
   pointer/cancellation races and lazy pending/distant siblings are covered.
   Three lower-level inline transfer rows preserve dirty children, cover changed
   prepared widths, and reject wrong mode/width/checkpoint state. SVG metadata tests use an offline fake provider,
   not the blocked resvg/Edge release oracle. The earlier unfiltered renderer verification
   passes 1,839/1,839, no skips, including all 12 API baselines
   (`renderer-all-container-root-transfer-a.trx`). Prior
   code-block parity at those simulated scales/directions remains covered. The
   unchanged Core DLL matches the earlier 1,743/1,743 unfiltered receipt, no skips
   (`core-all-priority.trx`); that hour-long suite was not rerun for these renderer-
   only changes. All native/Core receipts are under
   `MarkdownRenderer/artifacts/live-bitmap-admission-20261003`; staged compiler
   DLL hashes match the tested runtime. The earlier real 72-image/72 MiB storm
   remains green within the unchanged image ledger cap (`admission-focused-c.trx`).
   The earlier app source-linked verification passes 3,571/3,571, no skips
   (`artifacts/performance-priority-20261003/app-priority-container-root-a.trx`).
   That earlier checkpoint's compiler/staged SHA-256 values are Renderer
   `ECC503351E7B36917853D1582C244389F1AD169DF5C29674AB4D2DD33A87D008`,
   Performance `FDDC2800070DC75327D136D31FD3222D31FA615EA8D5F5BCFF7944DC92254327`,
   tests `5A4D0C50D167D6D88C5EAA81692F0F727F27579F58EBDE91F104A01DD09D7B13`;
   Core remains `C1B5E6CBA1DAABDE9C64BCF5090E4CE13A888F79CE5B1259BB7916BCEFDE8176`.
   The three failed table attempts remain preserved. They isolated the real
   zero-length GFM-cell eligibility rejection and corrected fixture assumptions:
   formatting keys versus modifiers, block IDs versus root ordinals, and expecting
   column/scroll-range shrink where the fixed minimum-content widths cannot shrink.
   The final tests cover both constrained and flexible widths rather than skipping
   those cases; deadlines, resource ceilings and fallback parity are unchanged.
   The container/table SVG attempts and first combined container run remain
   preserved. Their fixture corrections distinguish post-rebalance selection
   geometry from old geometry, actual persistent HTML flow from a span that
   legitimately normalizes to text, and suppressed raw-margin changes from
   visible left/right changes. Exact lease-count checks join the fresh context's
   existing asynchronous retirement before comparison; the same deadline and
   reference-count assertions apply. Inline images retain their valid dirty-state
   contract, so budget fixtures prove measured placement through the real inline
   checkpoint/dimension predicate rather than force-clearing dirty state. The
   single-leaf fixture uses normal measured reflow after load. Repeated URLs now
   subscribe before triggering a synchronous cache hit. Neither cache pressure,
   resource ceilings, expected identities nor timeouts was relaxed. The stale
   ordinary-list rejection fixture now contains a confirmed unsupported code
   child, while the new ordinary-list positive parity matrix remains mandatory.
   All earlier failed attempts remain preserved, including the real admission-
   history transfer failure, delayed dirty-array retention guard, warm-cache fixture
   isolation failures, and tiled lifetime fixture's missing host repaint after
   legitimate pressure recovery. The tiled fixture now models the actual host's
   paint-only recovery hint with coalesced asynchronous pulses, the same ten-second
   total deadline and unchanged residency/disposal assertions; it does not clear
   cache pressure, enlarge budgets, or skip the test. Warm-cache identity fixtures
   use the existing nonparallel process-cache collection. Two later vector-suite
   failures remain preserved: the incoming-inline cancellation fixture now uses
   its existing device gate after the first prepared scene rather than polling a
   short-lived prefix window, with the same ten-second deadline and assertions;
   the original-source collection fixture now waits for both render-await
   registration and return of the provider Open consumer continuation. This proves
   source-producing inline completion frames have unwound before forced GC, not
   merely that Render has started. The strict collection assertion tracks every
   resolver-created buffer; no timeout, budget or memory assertion was relaxed.
   Startup-only and clearable-source-owner experiments did not resolve it and
   were removed. The final control run uses the unchanged production resolver
   path. Failure-only bounded field-path diagnostics print no byte payloads.
   The first inline combined receipt (`priority-inline-root-transfer-a.trx`)
   remains preserved with 19 failures: 16 stale test-only private-predicate
   argument lists, premature nonnull preparation-context exposure on preflight
   rejection, an exact-object metadata cast and a fixture subscribing after a
   synchronous cache-hit event. The source context is now exposed only after all
   shape/budget checks, and fixtures use the new explicit block mode, dictionary
   interface and subscribe-before-load order. Existing assertions, deadlines and
   resource ceilings are unchanged; none of those cases is skipped.
   Ordinary
   main-app build is still blocked by NU1900 treated as an error, not bypassed.
   Native button reentry still needs live WinUI validation; simulated DPI is not
   physical mixed-monitor/device/High Contrast qualification. Unchanged historical
   v72 native/provider dependencies support this local runtime, not a fresh AOT
   or signed worker release. Synthetic gate tests are not release benchmarks.

The qualified counterbalanced release benchmark, same-byte Edge corpus,
top-500 whole-page audit, AOT/device/theme matrix, full aggregate native-memory
ownership and package-size gates remain mandatory and open. Local diagnostics
are not a substitute. The v72 footprint numbers below are historical for the
pre-admission generation and do not measure these new changes. The GitHub
connector now permits read-only PR/CI inspection despite the blocked CLI proxy:
PR #99 is open at `1c00c83ddfd2e8b65e05cb57e359a3f092b37271`; the renderer
preview and top-500 consolidation checks still fail, and the product benchmark
was canceled. These local changes have not been published to that head; no new
CI/publication success is claimed. The remaining highest-impact runtime work is
general mixed/vector and over-budget or incomplete container/table image-driven reflow,
removing the remaining O(N) suffix gate, plus complete aggregate native/vector ownership, followed by
the unchanged release qualification and package-size gates. Qualification needs
a Windows execution context where
Edge/SVG child processes, Sandbox UI automation, NuGet/network access and publication
are available, plus an approved strict-ancestor reference SHA for the full
counterbalanced benchmark. Current failures include Sandbox gRPC Unauthorized,
restricted SVG startup Win32 87, Edge fail-fast/Node child-process EPERM and
read-only Git metadata. Repeating the same blocked diagnostic is not progress;
none of these restrictions authorizes weakening gates or bypassing the sandbox.

The user-approved performance "before" source checkpoint is the
September 28 published PR head `1c00c83ddfd2e8b65e05cb57e359a3f092b37271`, for a
future clean candidate containing the current local optimizations. It becomes a
strict ancestor only after that candidate is committed as its descendant; it
cannot be compared to itself. This measures the current local batch, not all
optimizations since the project's original baseline. Absolute and same-byte Edge
gates still apply independently. No repository reference variable has been set.
Schema 12 alone is insufficient: the local harness and verifier have changed,
so any reference report must pass the current strict verifier before it is
qualified. The earlier `1a403a296358a18ed754e74bf8d3c23df58341d2` is schema 10;
the separately prepared schema-12 `7ac710c3645f0c751cd8de66a2e6ccab5ac369c0`
is not an ancestor of the published candidate. Neither is a valid substitute.
Known runner/tool restrictions remain recorded below.

## Historical checkpoints

The latest **sealed v72-A cancellation follow-up** closes a mid-pass token
propagation gap found during review: an absent explicit token no longer masks
ambient cancellation in initial or direct lazy-band realization. Eager/lazy
construction and measure checkpoints use the selected effective token. Three
new Facts cancel during the first measure, require the next block to remain
unmeasured, and verify token restoration plus retirement/drain. Fresh coupled
verification passes **1,242 Core / 1,515 renderer / 477 no-worker provider**
cases and all **12** API baselines. The ordinary Release/x64 app suite again
passes **3,567/3,567** executions, with 40 named inputs stable. The current
receipts are `MarkdownRenderer/artifacts/svg-coupled-v72-a-seal-20261003/seal.json`
and `artifacts/cancellation-v72-a-20261003/diagnostic-receipt.json`. These are
managed diagnostics, not release qualification. The seal explicitly binds
the three changed cancellation files and 714 unchanged selected non-artifact
inputs from the prior checkpoint. See the v72 checkpoint below.
The fresh **v72-C default-Release footprint** now measures the same sealed
Core/WinUI source generation with diagnostics disabled, XML documentation
retained and normal NuGet audit enabled. It fails both unchanged ceilings:
**588,498 / 537,600 compressed bytes** and **1,400,832 / 1,258,291 managed
bytes** (excess **50,898 / 142,541 bytes**); native payload is zero. All seven
owned build/query phases exit zero and drain; 227 source/configuration inputs,
215 seal-bound project inputs and 484 compiler outputs remain stable. This is
a current negative package-footprint result, not a test-build projection or
release qualification. Receipt:
`MarkdownRenderer/artifacts/packages-reflow-v72-c-20261003/footprint-evidence.json`.
The failed A/B helper attempts are preserved. See the footprint checkpoint below.
The preceding **sealed v71-C renderer follow-up** extends private raster relayout
across up to **64 eager Body/Heading roots / 256 runs / 64 completed rasters**.
Ordinary text and links have fresh mutable placement/native-layout ownership;
only explicit immutable bitmap leases are shared. Admission also bounds layout
text to **64 Ki UTF-16 units** and prior semantic text to **128 Ki units**.
Shape, metadata and lease copying occur under the source gate; native
measurement, arrangement, indexes and semantic construction occur outside it.
Streaming semantic comparison reuses the exact immutable text projection
without duplicating matching text storage; fallback copying is chunked and
cancellable. Eager/lazy builds preserve ambient/context cancellation when no
explicit pass token is supplied. This is not general composite/lazy/vector COW,
an aggregate resource cap, or a measured DirectWrite/UI latency guarantee.
Fresh coupled verification passes **1,242 Core / 1,512 renderer / 477 no-worker
provider** cases, including **30** additional multi-root, text-boundary,
semantic and cancellation Facts and all **12** API baselines. The independent
C2 seal binds **724** selected sources, **484** outputs and **376** staged-copy
identities. The ordinary Release/x64 app suite passes **3,567/3,567** executions,
with **40** named inputs stable. These are managed diagnostics, not
release/browser/physical-DPI proof. Its historical receipts are
`MarkdownRenderer/artifacts/svg-coupled-v71-c2-seal-20261003/seal.json` and
`artifacts/multi-root-v71-c-20261003/diagnostic-receipt.json`. Failed v71-A
compilation, v71-B fixture and the first C seal expectation remain preserved.
See the v71 checkpoint below for exact scope and remaining local/external needs.
The preceding **sealed v70-C renderer follow-up** extends private raster relayout
to multiple completed rasters and text runs within one eager Body paragraph.
Descriptor capture is bounded to **64 images / 256 runs**; native measurement,
fresh geometry/indexes and semantic construction remain outside the source
snapshot gate. Publication validates every ordered image and exact source
lease, not just counts. Each clone has independent retirement ownership; no
source native text layout or mutable placement is shared. This is still not
multi-root/composite/lazy/vector COW, an aggregate residency cap, or a bound on
text length and native DirectWrite cost. Fresh coupled verification passes
**1,242 Core / 1,482 renderer / 477 no-worker provider** cases, including **24**
new multi-raster cases and all **12** API baselines. The independent seal binds
**720** selected sources, **484** outputs and **376** staged-copy identities;
all seven parent and four provider phases exit zero and drain. The separate
ordinary Release/x64 app suite passes **3,567/3,567**, with **31** named inputs
stable. These are managed diagnostics, not release/browser/physical-DPI proof.
Its historical receipts are
`MarkdownRenderer/artifacts/svg-coupled-v70-c-seal-20261003/seal.json` and
`artifacts/multi-raster-v70-b-20261003/diagnostic-receipt.json`. Failed v70-A
compilation and v70-B harness-input attempts remain preserved. See the v70
checkpoint below for exact scope and qualification prerequisites.
The preceding **v69 app follow-up** makes first-viewport readiness publication
failures observable without changing readiness acceptance or its progress-drain
barrier. Outcomes use a separate bounded worker/file, fixed statuses and the
actual admitted source identity; optional diagnostic preservation cannot fail
an otherwise qualified audit. Required snapshot copying stays strict and now
cleans per-attempt temporary files on IO and non-IO publication failures.
The fresh ordinary Release/x64 managed app suite passes **3,567/3,567** cases,
including **15** new behavior cases, with zero failures/skips. Initial attempt E
has **19** named inputs unchanged before/after; the fresh post-SVG-change F run
also passes **3,567/3,567**, with **21** named inputs unchanged before/after.
The ordinary automation harness build reports
zero warnings/errors. This is source-linked managed verification, not a native
app, NativeAOT, worker, browser-performance or top-500 qualification. Its
post-SVG app receipt is `artifacts/readiness-publication-v69-f-20261003/diagnostic-receipt.json`;
the failed A/B/C/D attempts remain preserved. See the v69 checkpoint below.
The preceding **sealed v69-A renderer follow-up** prunes SVG resize/theme fallback
tiles outside a one-old-grid-tile viewport band, and retires in-band fallback
only after complete canonical current-grid coverage. Exact rational projection
preserves partial final tiles and DPI-crossing boundaries; invalid geometry
retains the previous good tile. Retirement ownership is allocated before map
removal or queued-work claim, with cleanup outside the tile lock in `finally`.
All **13** new Facts pass, including actual no-native queue pruning and warmed
**zero-allocation** helper/queue checks. Fresh coupled verification passes
**1,242 Core / 1,458 renderer / 477 no-worker provider** cases and all **12**
API baselines, with **717** selected source identities stable. Three retained
NU1900 warnings prevent security-qualified build claims; the provider remains
an explicitly audit-disabled offline diagnostic. Its seal is
`MarkdownRenderer/artifacts/svg-coupled-v69-a-seal-20261003/seal.json`.
This closes a fallback-residency gap, not aggregate admission, native memory,
browser parity or top-500 qualification. No new 1,743-case unfiltered Core
execution/reuse attestation is claimed for this changed ImageBox generation.
The prior **sealed v68-G follow-up** reuses an immutable semantic text string,
grapheme-boundary index and text/source fingerprint for the narrow raster
candidate, only after exact SourceMap identity and ordinal UTF-16 comparison.
It still builds fresh semantic nodes, spans and geometry bindings, recomputes
the semantic-range fingerprint, and preserves the existing constructor/API
shapes. This removes duplicate retained text/index storage, not O(document)
semantic traversal or the missing aggregate resource cap. Seven new renderer
Facts cover identity/fallback, chunked Unicode, cancellation, concurrent reads
and fresh image/heading accessibility bindings. Earlier verification failures
are preserved:
v68-D preserves two test compilation errors (missing namespace and non-LINQ
StringBuilder chunk enumeration); v68-E preserves 1,444 passes and one wrong
new fixture hierarchy assertion, corrected to require its fresh level-3 heading
wrapper and paragraph grandparent. Neither is sealed. v68-F then stops on the
unchanged provider `WarmMarkerReadsDoNotAllocate(segmented: true)` Fact:
**476/477** pass, with **8,040** measured bytes against the unchanged **zero**
ceiling. Its owned phases exit/drain completely and all **716** selected source
identities remain stable. Its allocation cause remains unexplained; the fresh
G run below is a separate full verification, not a correction of F's receipt.
It is not a browser-relative performance qualification.
The GC-counter investigation includes quiet, concurrent-GC, empty and positive
controls. An upstream [macOS ARM64 runtime report](https://github.com/dotnet/runtime/issues/134724)
describes similar positive counter deltas around no-inline empty work, but does
not establish bookkeeping versus runtime allocation. It is a hypothesis aid,
not proof of the cause on this Windows x64 machine or grounds for raising the
zero-allocation limit. All diagnostic trials and failed checkpoints are retained.
A fresh exact-source Release probe now measures both contiguous and segmented
marker reads in six owned processes, including genuinely concurrent GC stress.
All retained warm marker blocks report zero bytes and 100,000 matches; the
spin-only and positive-allocation controls remain recorded separately, not
normalized to zero. Three focused VSTest repetitions against the actual F-built
assembly also pass both theory cases (six executions), with held input handles,
complete process/output drains and unchanged runtime/source identities. These
probes do not explain or replace F's full-suite failure, change its zero-byte
ceiling, or qualify F. The receipts are
`MarkdownRenderer/artifacts/svg-source-buffer-allocation-v68-investigation-20261003/results-20261003-b.json`
and `f-vstest-repeats-20261003-a/receipt.json` in that directory.
The separate default-Release footprint probe preserves its G/H preflight
failures: dictionary rows were not reliably sorted by their `path` key. The
observed file bytes/hashes are unchanged; the probe must normalize file records
to real PSObject properties before comparing them. Neither attempt built a
package or proves a size gate. Public XML documentation, mandatory NuGet audit
configuration and the existing size ceilings remain unchanged. The fresh I
footprint observation actually packs both default-Release libraries with
diagnostics disabled and unchanged inputs: **583,325** compressed bytes against
**537,600**, and **1,387,008** managed bytes against **1,258,291**. Both size
gates fail; native payload is zero and both public XML documents are retained.
All seven owned child phases exit zero with complete drains. Its nonzero outer
outcome is the size gate, not source drift. The independent footprint receipt is
`MarkdownRenderer/artifacts/packages-reflow-v68-lean-i-20261003/partial-size-evidence.json`
(839,285 bytes, SHA-256
`0226614707fe8723d14f9043138b116516b90bc267bfe431ff81f95a59570247`).
It is not a security/AOT/native-worker or full-package release qualification.

The fresh **v68-G coupled managed checkpoint** passes **1,242/1,242** selected
Core, **1,445/1,445** renderer (all **12** public API baselines) and **477/477**
no-worker provider cases, with no failures or skips. All seven parent and four
provider phases exit zero and drain completely; **716** selected source,
**484** output and **376** staged-copy identities are rechecked by the seal.
The current seal is
`MarkdownRenderer/artifacts/svg-coupled-v68-g4-seal-20261003/seal.json`
(1,557,999 bytes, SHA-256
`8a02fcb401b3a0623669cde90109b3dc29f987a229a62c5e1ae42eb1b6a7e87c`).
The first G seal-helper attempt failed before creating a seal: it mistook the
normalized restore-receipt folder list for raw assets JSON. The separate G2
helper binds the actual assets bytes and exact two-folder set, then resolves
all 253 SDK files from the explicitly recorded package root. It does not choose
a fallback folder or change the test checkpoint. G2's subsequent reuse check
fails because its helper was edited after sealing; that historical seal and
failed check are retained. A G3 preflight exposes unordered dictionary rows,
then G4 normalizes rows before sorting and validates reordered identities plus
hash/path/duplicate/missing-row rejection against the actual seven new Facts.
G4's seal execution exits zero. Its first Core-reuse attester fails closed on
a nested row-array shape. The separate G4B attempt preserves its failure on a
replay-schema assumption: the replay binds the full-Core bridge, not the raw
compiler receipt. Both failed receipts remain unchanged. The fresh G4C helper
binds that exact replay-to-bridge-to-compiler/seal chain, retains and compares
the three external NuGet configuration inputs, and verifies the actual C-to-G
source delta without discarding external rows. Its full read-only validation
and parent-owned attestation both pass. The current receipt is
`MarkdownRenderer/artifacts/core-runtime-reuse-v68-c-to-v68-g4c-20261003/attestation.json`
(120,204 bytes, SHA-256
`3265a4ea4b1ba2f56613f05048182b952c0cbc3181ddc51038bbfab049ea5c02`).
It attests exact reuse of the guarded C run's **1,743** actual results for the
unchanged Core project plus **224** Compile inputs and all **92** byte-identical
Core runtime files, including the six dynamic source/fixture inputs and seven
source-reading tests. It launches **zero** product/test/build children; this is
not a fresh unfiltered G execution, nor coverage of the changed renderer or app
sources. All **817** original held-input identities and exit/drain/release proofs
remain bound, including the preserved legacy parser failure and strict F3
two-alias supplement. These helper faults do not alter shipping/test source or
the actual passing G test executions. Neither this managed seal/reuse proof
nor the synthetic release-validator fixtures prove the required live Edge,
top-500, device/DPI or counterbalanced release gates.
Offline Node client-contract diagnostics now record **69 passes / one failure**
across two separately owned, source/Node-hash-bound receipts. The remaining
emoji-generator integration failure is an actual `spawnSync` **EPERM** with
exit 1 and complete output drains, not a skip or a passing release gate.
A positively enumerated 19-title filter excludes the live Edge test; an
artifact-only sentinel verifies that exclusion before the real fixture run.
The earlier negative-name filter did not reliably exclude Edge under this
Node test mode: its 40-minute attempt timed out and is retained, not interpreted
as a production renderer hang or a completed browser run. The safe-fixture
receipt is
`MarkdownRenderer/artifacts/same-byte-node-client-contracts-v68-20261003/20261003T184847305Z-5b08c555dfff4f5388914084f7e5d1c2-positive-allowlist-client-contracts/allowlist-receipt.json`;
the separate remaining-case receipt is
`20261003T185436759Z-8b699539e9f24844838d913793589524-emoji-generator-single-case/integration-case-receipt.json`
under that same diagnostics directory (28,022 bytes, SHA-256
`a40f205d36e83fe2ee2ffad11a15ffd5c0b17492fa409d8876a6f04a62edb613`).
Synthetic filter sentinels are not counted in the 70 client cases. No Edge,
same-byte capture, live README or performance qualification is inferred.
The preceding sealed **v68-C coupled managed checkpoint** passes **1,242/1,242**
selected Core, **1,438/1,438** renderer (all **12** API baselines), and
**477/477** no-worker provider cases, with no skips. It adds a deliberately
narrow private image-placement candidate: one eager Body paragraph containing
one ordinary text run and one completed non-SVG raster image. The candidate
owns a fresh layout context, native text layout, geometry, indexes and semantic
projection, plus an explicit cloned bitmap lease. Measurement occurs outside
the old snapshot gate. Logical UIA generation and text/range fingerprints remain
unchanged; publication/discard and actual-work retirement are one-shot.
This is not complete COW: lazy layout, multiple roots, composites, vectors,
embeds, incomplete images and active work retain the existing serialized route.
Integration review found three publication/ownership defects:
the public image resolver is compared to its effective document-scope wrapper,
which rejects the normal scoped JitHub path. The private context still copies
the control-valued `PerformanceDocumentOwner`, and geometry-only publication
reset active pointer/touch state. The current follow-up replaces the resolver
comparison with exact scope/session/policy matching, separates opaque image
admission identity from scene ownership, and rebinds deferred pointer work to
the known replacement without resetting the gesture. These changes now pass
the sealed managed checkpoint; managed leaf/candidate and source-wiring tests do not prove
actual live control routing, touch behavior or absolute weak-control lifetime.
The same checkpoint fixes unkeyed downscaled raster images losing their original
intrinsic dimensions: the worker creates the completed lease with authoritative
intrinsic metadata before UI publication. A unique-entry numeric pixel-weight
ledger keeps evicted-but-leased bitmaps charged until final successful disposal;
it is observability, not a hard aggregate native/GPU or RSS cap. Cache mutation
preparation now precedes membership changes for Set, SetBudget and RemoveWhere;
failed unpublished stores return bitmap ownership to their caller. These are
27 additional renderer regression Facts, not live device/memory qualification.
All seven parent and four provider owned phases exit zero and drain completely,
with **715** selected source and **484** output identities stable. The seal is
`MarkdownRenderer/artifacts/svg-coupled-v68-c-seal-20261003/seal.json`
(1,549,047 bytes, SHA-256
`cd5f5f849e44fbd242f58559f08c3b11394eb38b7783e2b7d293802890ce0488`).
Preserved v68-A fails compilation on the native BitmapSize type; v68-B passes
all newly added cases but exposes an existing reflection test that selects the
wrong constructor after the new uncached-lease overload. C fixes both bindings
without removing cases or relaxing thresholds. Its full-Core bridge is
`MarkdownRenderer/artifacts/full-core-v68-c-bridge-20261003.json`
(290,145 bytes, SHA-256
`cbd3747519a73cc250a83cf0c68689265d667cd408a299e5efa13aafaf8e8c22`).
The fresh unfiltered **1,743-case** C replay completed locally at 17:19 UTC in
`MarkdownRenderer/artifacts/v68-c-unfiltered-core-v68-458103f08d4947399587b0bc7238953f`.
The outer guard holds **817** exact inputs: all **715** selected sources, all
**92** Core runtime files, and the bound receipts/harness/tools. It retains
read-sharing handles through owned-child exit, stdout/stderr drains and final
held-file verification. The guard does not lock every one of the 484 compiled
outputs or prevent transient creation of unlisted files; its supplementary
Compile-inventory checks apply to the Core test-project root. The independent
evidence directory is
`MarkdownRenderer/artifacts/v68-c-core-v68-guard-evidence-458103f08d4947399587b0bc7238953f`.
All **1,743** actual tests pass with no skips or failures. The independent guard
records owned-child exit, both output drains, log flush and all held-input,
source-inventory and Core-runtime verification as complete; it releases only
after these checks. The original reader's nonzero outcome is preserved: the
same two pinned supplementary-character display aliases require the strict F3
definition/result/execution/discovery attestation. The fresh F3 receipt is
`MarkdownRenderer/artifacts/full-core-v68-c-trx-bindings-f3-a-20261003/receipt.json`
(357,936 bytes, SHA-256
`9e9c7c82b6757260952b99caee429e941c852949ae023ee395940cc31fe64c1e`).
Its 501 synthetic release-gate cases are not live README audits. The source
freeze is now released; unlike the earlier D replay, no intermediate selected
source mutation is known or admitted during this guarded interval. Two guard
preflight faults are preserved: an empty accumulator parameter rejected valid
initialization, then a nested-array runtime return miscounted 92 files as one.
Both stopped before a Core child launched. Focused regressions fix the binding
and row shape without admitting empty manifests or relaxing identities.

The preceding sealed **v67-D** checkpoint passes **1,242/1,242** selected Core,
**1,411/1,411** renderer and **477/477** no-worker provider cases. Its seal is
`MarkdownRenderer/artifacts/svg-coupled-v67-d-seal-20261003/seal.json`
(1,535,786 bytes, SHA-256
`dcb3efbda4d5b4d083bd4b8452897047c2a2514c4d43ac2eced6687cc7d44b83`).
The actual unfiltered **1,743-case** Core run completed locally against
that sealed runtime in
`MarkdownRenderer/artifacts/full-core-v67-d-unfiltered-a-20261003`.
Its immutable schema bridge is
`MarkdownRenderer/artifacts/full-core-v67-d-bridge-20261003.json`
(288,669 bytes, SHA-256
`612e7e59db61601ac6a0193b47871f5f93c07b6c19a88d3b373820d827f5b241`).
All **1,743** actual results, including **501 synthetic release-gate validator
fixtures**, pass strict F3 definition/result/execution/discovery binding. The
owned launcher exited zero at 15:37:46 UTC. The original replay reader's failure
is preserved: two pinned supplementary-character display-name aliases require
the existing exact F3 attestation, not blanket label normalization. The fixtures
are not live README audits. The supplemental receipt is
`MarkdownRenderer/artifacts/full-core-v67-d-trx-bindings-f3-a-20261003/receipt.json`
(355,672 bytes, SHA-256
`694cd749cbfe8bce5dcbb01e29d796ebb8093c0f4bf065809ab84b9af4335132`).
An artifact-preparation
patch accidentally appended raw diff text to five shipping files at
15:02:19–20 UTC while the run was active. The parent preserved those exact
drifted files, removed only the accidental hunks, and rehashed all **709** sealed
source identities at 15:10:26 UTC: all match again. No compiled/runtime outputs
were changed. This known intermediate violation means the run cannot be claimed
as a clean source-frozen checkpoint, even if its actual Core results pass.
The exception is recorded at
`MarkdownRenderer/artifacts/cow-prep-source-drift-v67-d-20261003/README.md`;
it accompanies the exact TRX attestation via
`MarkdownRenderer/artifacts/full-core-v67-d-source-freeze-exception-20261003.json`
(1,493 bytes, SHA-256
`a86d26784c835e030f7effb7e3d0f1a433deb6752ff3b888facc0db872f0e37d`).
Shipping changes resumed only after actual replay exit/drain and F3 verification.
The next full run will use held read-sharing source/runtime handles, not just
before/after equality. Preserved v67-A/B/C attempts respectively
fail test compilation, provider scaffold admission, and two new test fixtures;
D fixes the missing namespace, complete scaffold and fixture binding/lifetime
issues without removing cases or relaxing thresholds.
This remains borrowed-feature/source-linked managed diagnostic evidence, not
normal SDK/security/AOT, native worker, UI/DPI, aggregate-memory, same-byte Edge,
full counterbalanced Release or top-500 qualification. The local GitHub CLI
still fails at the configured `127.0.0.1:9` proxy, but a fresh read-only GitHub
connector retrieval now confirms PR #99 is open at the unchanged published head
`1c00c83ddfd2e8b65e05cb57e359a3f092b37271`. Its six returned PR-triggered runs
include failed preview validation and top-500 workflows, canceled product
benchmark, and successful Full Native AOT for that published head only. The
returned top-500 job page has 19 failed audit shards, one successful shard and
a failed consolidation; it is not a qualified 500/500 result. The read is
preserved at `MarkdownRenderer/artifacts/pr99-connector-read-v68-20261003/evidence.json`.
Fresh Sandbox snapshot still fails at its `localhost:80` broker with access
denied. No live UI state or CI qualification of local changes is inferred;
local changes remain unpushed.

The preceding sealed **v66-G coupled managed checkpoint** passes **1,242/1,242**
selected Core, **1,391/1,391** renderer (all **12** API baselines), and
**477/477** no-worker provider cases. It integrates bounded UTF-8 hashing for
SVG security partitions and inert HTML-media identities while preserving exact
UTF-8 replacement fallback and digest bytes. Eight new compatibility/allocation
Facts pass; the large-key case retains the unchanged 8,192-byte assertion.
All seven parent and four provider owned phases exit zero and drain completely,
with selected source/output/reference/tool identities stable. The seal is
`MarkdownRenderer/artifacts/svg-coupled-v66-g-seal-20261003/seal.json`
(1,525,988 bytes, SHA-256
`dc688cb0942ebcdf6f2bfbf16ca311c87977e5e32397c3c8576ea2ab16199c5d`).
The parent is 1,641,140 bytes, SHA-256
`853c4335924327e0b83014a86c1bf20069920db0da6d750fa268f84d3a14cb44`.
This remains borrowed-feature/source-linked managed diagnostic evidence, not
normal security/AOT, native workers, aggregate memory, UI/DPI, Edge or top-500
qualification. The ordinary renderer build reports NU1900 because the mandatory
vulnerability feed is unreachable; the no-worker provider graph explicitly
disables audit only for its diagnostic. Neither is a security-release pass.
After the G build, a fresh read-only `gh pr view 99` still fails before retrieving
GitHub state: the configured proxy at `127.0.0.1:9` refuses the connection. No
new PR-head/check result is inferred, and these local changes are not pushed.
The preceding sealed **v66-F coupled managed checkpoint** passes **1,242/1,242**
core, **1,383/1,383** renderer (all **12** API baselines), and **477/477**
no-worker provider cases. SVG preparation/Open and Render now have distinct
async state machines. After actual Open completes, resolver, built-in file and
data-URI callers clear their source-bearing aliases and synchronously detach
their original source reservation before entering Render. The source-free
one-shot handoff separately owns the candidate document/use, linked image/session
cancellation, image retirement lease and legacy keyed/provider admissions.
Pending disposal retires an unconsumed Open; caller disposal cannot revoke
consumed ownership before actual uncooperative Render/upload cleanup finishes.
Tile publication transfers its document/use/slot before enqueue and preserves
idempotent rejection cleanup. No new public API or background provider discovery
was added. The barrier regressions cover session and legacy resolver paths,
committed-file/data-URI inputs, original-buffer collection with distinct prepared
bytes, canceled Open never invoking Render, delayed Render cancellation and
unconsumed-handoff retirement. These fake-provider tests do not qualify real
provider/parser/native/GPU or total-process retention.
All seven parent and four provider phases exit zero and drain completely; selected
source/tool/output/reference identities are stable. The seal is
`MarkdownRenderer/artifacts/svg-coupled-v66-f-seal-20261003/seal.json`
(1,522,295 bytes, SHA-256
`8ad75abb76e6dcd4d33ec6229fe071e3c20e272748fee8b74769f123bbdce9b7`).
It remains borrowed-feature/source-linked managed diagnostic evidence, not
normal security/AOT, native worker, UI/DPI, aggregate memory, Edge or top-500
qualification. Preserved v66-A reproduces the four original-source lifetime
failures before the split. B fails a static-member qualification during build;
C passes all eight added handoff cases but fails seven existing reflection
factory calls missing the new scalar timing argument and one callback-field
selector that also matched the new Render state machine. D passes after those
test bindings are updated: publication callbacks still cannot capture documents,
whereas active Render must own its document. No cases or performance, allocation,
memory or release thresholds were removed or relaxed. Preserved v66-E reproduces
three cache-isolation failures: a resolver-owned key can satisfy a built-in local
fallback, a keyed resolver writes a bare built-in source alias, and unkeyed boxes
share a device bitmap. F separates the explicit built-in alias from the resolver
security partition and gives empty/whitespace resolver keys a stable private
per-box partition. Resolver content hits still reuse explicitly partitioned
entries without creating a bare source alias; bitmap memoization updates only
the content key when no built-in alias was supplied. The four new regression
cases pass without removing earlier case identities or changing budgets. This
does not define mutable-file freshness or infer source authority from render
completion order. The required off-UI Render dispatch also records its queue
wait separately, using a scalar timestamp and the existing diagnostic stage.
The full unfiltered F Core run completed locally on October 3, 11:31–12:46 UTC,
against the sealed runtime: **1,743/1,743** cases pass with no skips, including
**501 synthetic release-gate validator fixtures**. Its output directory is
`MarkdownRenderer/artifacts/full-core-v66-f-unfiltered-a-20261003`. The actual
owned dotnet launcher exits zero with complete drains, no timeout or stderr.
The original replay reader's failure receipt is preserved: it rejects the two
exact emoji display-name differences below rather than a product-test failure.
The immutable schema bridge
`MarkdownRenderer/artifacts/full-core-v66-f-bridge-20261003.json` binds the F seal,
owned compiler phases and exact source/runtime identities to the older replay
runner (287,029 bytes, SHA-256
`b047c3c71fb6a6e5da9151b49c87b73382337e7928af5502cc90c7686c0610bb`).
It does not execute another compiler or qualify full SDK/security/AOT closure.
The existing replay reader compares discovery/result display names literally.
An independently bound review of the unchanged xUnit adapter and the earlier
immutable 1,719-case TRX identifies exactly two known emoji display-name
differences: discovery contains the globe grapheme, while the TRX represents
that same grapheme as the literal surrogate escape pair. Those older IDs are
not reused. A fresh supplemental verifier leaves the original replay reader and
its failure receipt untouched. It binds every current-F result to a unique
test definition and entry, including execution ID, assembly path, adapter,
class/method, outcome and exact discovered identity; only those two specific
display aliases are admitted. It also reuses the original all-pass/counter/
unfinished/501-fixture checks and rehashes all bound source/runtime inputs.
Its owned self-test process passes two valid synthetic fixtures and rejects
24 malformed variants, with unchanged inputs, exit zero and complete drains:
`MarkdownRenderer/artifacts/trx-binding-self-tests-v66-f-b-20261003/receipt.json`
(5,900 bytes, SHA-256
`fb34c863ad598039f503b621cf65709344631bbdc0aeb776500a5815570d3016`).
These are evidence-verifier tests, not product tests. The final F3 driver also
tests both real V60 receipt shapes, rejecting ten malformed process/timestamp/
assembly/TRX variants and preserving absent failure-path fields as absent in
the original. Its owned self-test K exits zero and drains, with stable inputs:
`MarkdownRenderer/artifacts/fullcore-v66-f3-schema-selftests-k-20261003/receipt.json`
(11,663 bytes, SHA-256
`d9d02e555ed912ef459c39d40857a24e57ff13904a22986d452232f4c53349be`).
The actual current-F supplemental attestation now passes: exact 1,743 unique
definition/result/entry bindings, all-pass counters, the exact two allowed
aliases, actual selected-assembly/compiler linkage, unfiltered argv, ordered
captured launcher identity/times, source/runtime and log identity rechecks.
`MarkdownRenderer/artifacts/full-core-v66-f-trx-bindings-f3-a-20261003/receipt.json`
is 353,815 bytes, SHA-256
`a27e7f83f044e291d35efb1b0d1b350e8d07372b2bfcf5ffa776ec22d5fff850`.
It does not rerun product tests, excuse missing/failed rows, assert a VSTest
descendant PID, or turn diagnostic evidence into a release pass. The original
578,648-byte failure receipt remains SHA-256
`0f2b59c03251ec56d5db7883f9cf84e2049f746e3e4b957ac9dd1eaad28f16d5`.
The synthetic 501-case set is not the live top-500 README audit; same-byte Edge,
the full counterbalanced Release benchmark and external device gates remain open.
A fresh, read-only F-to-G attestation now permits explicit reuse of that full
Core result: the current project and all **224** reconstructed Core compile
inputs match the F before/after records and the G compiler/seal; all **92**
runtime files match byte-for-byte across F before/after, G compiler/seal and
current disk. The selected test DLL is 1,418,752 bytes, SHA-256
`7078050fdedb899caba7025a17a338fe223ecc4a7f272389b4a2749347fb032a`.
The attestation binds the unchanged F replay, raw TRX and F3 exact 1,743
definition/result/entry mapping, retaining only the two explicit display aliases.
Its **11** primary, **13** F3 and **704** G source/tool identity rows, and the
92-file F runtime, remain identical before/after; the G Core runtime is separately
rehash-checked. The full 484-row G output manifests are compared with the seal;
this attestation does not independently rehash the other 392 output files.
`MarkdownRenderer/artifacts/core-runtime-reuse-v66-f-to-g-j-20261003/attestation.json`
is 462,733 bytes, SHA-256
`67a82671db0881a501c2a51a11221ecb924adb2f1cceb6b62ef2fdf60a8e6f33`.
This is **zero new product test executions**, not a rerun or new full-SDK,
NativeAOT/native/UI/browser qualification. The new hashing utility is excluded
from this Core compile partition and is tested by G's separate renderer suite.
Earlier failed verifier attempts are preserved and do not qualify any evidence.
A source-reviewed correction to the prepared first-COW-slice worklist is recorded
in `MarkdownRenderer/artifacts/layout-cow-semantic-generation-correction-v66f-20261003.md`.
The semantic graph is not generation-independent: its nodes/spans contain actual
box/run references. A cloned layout must rebuild that projection, verify identical
text/range fingerprints, and preserve the logical automation generation only for
an unchanged document. Logical text ranges resolve the current projection;
snapshot-fenced virtual child peers must invalidate instead of reading retired
geometry. The candidate must remain strongly owned until commit or rejection.
This is an implementation constraint and artifact-only patch preparation, not a
shipping COW implementation or evidence that the open COW gate has passed.
The existing complete-rebuild route already creates an independent context/tree
and semantic projection, swaps snapshots, and asynchronously retires the old
graph. It is not an incremental image-reflow substitute: new ImageBox instances
cannot adopt already-loaded bitmap/SVG document ownership across snapshots, so
rebuilding on every image completion could duplicate decode/render work and
overlap resources. The next real COW slice still requires generation-owned
placement and explicit shared image-resource leases, private indexes/semantics,
and a complete atomic publication boundary. In-place suffix Arrange exceptions
can currently leave mixed geometry before exceptional rebuild recovery; this
correctness/performance gap remains open, not deferred as a runner-only issue.
The source-frozen review also identifies source-sized UTF-8 temporaries in SVG
security-partition hashing and inert HTML-media identity hashing. An artifact-only
bounded streaming candidate preserves the original `Encoding.UTF8` replacement-
fallback semantics in **1,313** exact digest comparisons (Unicode, malformed
surrogates, chunk boundaries, deterministic randomized text and digest prefixes).
For a warmed **8 MiB** ASCII key, it allocates **448** managed bytes versus
**8,388,840** for the original full-array algorithm, below the new diagnostic's
unchanged **8,192-byte** bound. Its owned process exits zero and drains, with all
five input identities stable:
`MarkdownRenderer/artifacts/utf8-sha256-candidate-v66-f-b-20261003/receipt.json`
(5,249 bytes, SHA-256
`2cfc739bae0218868b353e1b2ad79ce74f8bb1c8e1c32c9114149d425450479f`).
That first prototype is not shipping-integrated and is not a latency, aggregate
memory, NativeAOT, full pipeline or browser-performance pass. A follow-up
rotating-order microdiagnostic finds ordinary-key overhead in its streaming
path, so that version is not adopted. The simpler follow-up retains the exact
original one-shot expression through 4,096 UTF-16 characters (at most 12 KiB of
UTF-8 temporary input) and streams only larger inputs. It passes **468** exact
digest-prefix comparisons across three alternatives, **12** invalid-length
checks, and the same warmed **448-byte** allocation at 8 MiB. The final hybrid's
8 MiB median is 0.706× the original in this managed microdiagnostic; short-key
medians differ by tens of nanoseconds, including one 43-character URI at 1.116×.
These are not release/browser timing gates and do not prove end-to-end parity.
The owned process exits zero and drains, with stable inputs and no stderr:
`MarkdownRenderer/artifacts/utf8-sha256-f2-v66f-e-20261003/receipt.json`
(SHA-256 `98d1ef9da5647595e719cf460a4f13af9081ac038d7e264aad2a435734a92fa7`).
After the full-F process and attestation completed, this bounded hybrid was
integrated into SVG security-partition hashing and inert HTML-media identity
hashing, with eight new compatibility/allocation Facts. The fresh coupled G
checkpoint above now passes; F remains historical evidence for the preceding
candidate, not a newly executed full-Core test run on G. The initial A
attempt fails compilation because the standalone artifact lacked an explicit
nullable annotation context; its owned exit/failure receipt remains preserved.
A fresh optimized Release call-shape diagnostic links the exact shipping helper
source, not the G assembly or the application runtime. It passes nine exact
digest comparisons with rotating-order delegate/direct-call measurements.
Ordinary security-partition and media-identity keys retain the original 280-byte
and 192-byte allocations; their median timing ratios are 1.012/1.019 and
1.001/1.000 respectively. The 8 MiB case allocates 448 versus 8,388,840 bytes,
within the unchanged 8,192-byte assertion, with median ratios 0.762/0.724.
This is a focused microdiagnostic, not the required counterbalanced release
benchmark. Tiered compilation and ReadyToRun are disabled only for the owned
diagnostic process and restored afterward; no JIT disassembly/inlining evidence
is claimed. Its captured process exits zero, drains completely, has no timeout
or stderr, and preserves all input/tool identities:
`MarkdownRenderer/artifacts/utf8-sha256-shipping-callshape-run-20261003-v2/receipt.json`
(28,582 bytes, SHA-256
`71ee7096c77f85e05f6ff4e4daeb5bf14b931a38571188e0d64c5c8a55c6e7b3`).
All release, NativeAOT, same-byte Edge and top-500 qualification flags remain
false. The earlier harness-only nullable compile failure and its exact source
are preserved separately; it was not a renderer compilation failure.
The prior **v65-E coupled managed checkpoint** passes **1,242/1,242** core,
**1,371/1,371** renderer (all **12** public API baselines), and **477/477**
no-worker provider cases. It adds reference-fenced, single-lock replacement
of matching SVG content/URL bitmap aliases without resurrecting evicted entries
or overwriting newer source/theme owners. Immutable LRU payloads are preserved;
pair weights, overflow, budget denial, changed/missing aliases, same-key aliases,
outside-lock cleanup and controlled competing completions have regressions.
All seven parent and four provider phases have captured zero exits and complete
drains, with selected sources/tools/output/reference bytes stable. The seal is
`MarkdownRenderer/artifacts/svg-coupled-v65-e-seal-20261003/seal.json`
(1,516,866 bytes, SHA-256
`a9f002ac111125dc50bf4e25bdf1fb84d0fbfcc2862f8acc4dfc89334fa94071`).
This remains borrowed-feature/source-linked, managed diagnostic evidence only,
not normal audit/AOT, native worker, UI/DPI, aggregate memory, Edge or top-500
qualification. At this prior checkpoint, pre-render source-cache writers and
the outer caller-source Open-to-Render split were unfinished; v66-F above
implements and verifies that managed ownership slice.
Preserved v65-C fails readonly storage and an immediate parking-disposal count;
D restores immutable storage and waits for actual asynchronous parking cleanup,
but times out the two-session codec-admission fixture. That fixture used random
nonempty keys that can collide in the earlier 64-stripe decode gate. D did not
record its generated stripe IDs, so its exact timeout cause is not proven.
E isolates process admission with empty keys and adds a deterministic colliding-
key regression proving serialization occurs before CPU/process admission.
No timing, byte, concurrency, allocation or release threshold was relaxed.
The fresh **v64-H coupled managed checkpoint** passes **1,226/1,226** selected
core, **1,347/1,347** renderer (including all **12** public API baselines), and
**469/469** no-worker provider cases. It verifies actual-capacity threshold
wakeups, accepted-registration barriers, cumulative bounded pressure parking,
exact denial/generation retry fences, unstarted activation failure recovery,
sessionless background image-reflow handoff, and detached/fenced post-input
actions. All seven parent and four provider process records have captured zero
exits and completed drains, with selected source/reference identities stable.
The seal is `MarkdownRenderer/artifacts/svg-coupled-v64-h-seal-20261003/seal.json`
(1,499,292 bytes, SHA-256
`f2ece77721e76b61b409e8b732b4f4a94aa19e60183e7a6aa556545a5e398302`).
Borrowed feature runtime and source-linked provider remain explicit. This is
not normal audit/AOT, native worker, aggregate-memory, browser or UI evidence.
The preserved v64-E/F runs each fail one unchanged zero-allocation assertion
(4,016 and 2,944 bytes in different methods on the same assembly). Their bound
static review is `svg-demand-visibility-allocation-static-review-v65-20261003.md`;
the allocator remains unidentified. Allocation-counter test classes now run in
explicit nonparallel collections, with warm-up, loop counts, zero-byte limits
and case selection unchanged. H is a clean rerun, not proof of the E/F cause.
G's missing provider-project scaffold failed before provider launch and remains
preserved. New v65 host-preparation byte-admission work changes the candidate
after H; the fresh **v65-B coupled managed checkpoint** now passes
**1,226/1,226** core, **1,365/1,365** renderer (all **12** API baselines), and
**477/477** managed no-worker provider cases. It includes the actual ImageBox
preparation/Open boundary, typed malformed admission, cancellation cleanup,
uncooperative-render retirement, full-peak threshold accounting and simulated
32/64 MiB admission profiles. The seven parent and four provider owned phases
exit zero with complete drains and selected source/output/reference identities
stable. Its current-source seal is
`MarkdownRenderer/artifacts/svg-coupled-v65-b-seal-20261003/seal.json`
(1,509,503 bytes, SHA-256
`5d08c683e3d404b6d395d17cd7af5c31f5ded7ab21f9392ab6581224fb8f7b3a`).
It still borrows feature runtime and source-links the provider; it does not
qualify native workers, normal audit/AOT, actual x86/ARM64 devices, total memory,
UI responsiveness or browser-relative Release behavior.
The preserved v65-A attempt passes 1,226 core cases and compiles the renderer
and source-linked provider, but fails provider discovery before test execution:
its strict addition-count check still says 49 while the exact enumerated set
contains 57. Fresh v65-B corrects that scaffold check to the same 57 identities
(477 total), without changing cases, thresholds or production sources. Its
combined verification and current-source seal are recorded above.
At the prior v65 checkpoint, the preparation lease covers only the opt-in host's temporary output byte
buffers on the provider's existing process admission domain. Successful opens
drop the nested core's prepared-source aliases before rendering; outer resolver
and built-in download buffers still retain their separately accounted source
lease through rendering. Source-cache, parser/XML, native/GPU and total-process
memory are outside this lease; this is not aggregate-ownership qualification.
The latest ordinary no-restore Release lean-pair diagnostic **v63-H fails the
unchanged size gates**: **569,247/537,600** compressed bytes and
**1,349,120/1,258,291** managed DLL bytes, with no native payload. Both pack
processes and before/after evaluated-property probes exited zero; selected
source, assets, cache and restore-copy inputs stayed stable. Its immutable
receipt is `MarkdownRenderer/artifacts/packages-reflow-v63-lean-h-20261003/partial-size-evidence.json`.
It is 270,545 bytes, SHA-256
`6fb3fe54aa7a4dd8f3ea466fe147aed95852c2e088f0c6cc1e8604e9d68d7602`.
It retains normal audit configuration but is not a security/AOT/full-package
release qualification. The twelve archive-versus-restore-graph SHA-512
differences are now reconciled by the SDK's independently recomputed canonical
package-content hash, not by assuming signed archives were repacked. Each
archive matches its sidecar; each SDK content hash matches the restore graph
and `.nupkg.metadata`. These are distinct hash domains for signed packages,
as documented by [NuGet's metadata design](https://github.com/NuGet/Home/wiki/Nupkg-Metadata-File).
The source/cache/tool-bound read-only receipt is
`MarkdownRenderer/artifacts/lean-content-hash-reconciliation-v64-20261003/receipt.json`
(33,715 bytes, SHA-256
`c80e50b50e72196e673fc3aa2b4dd8f6a1082b35d987ea48eb5fc73cda7ab726`).
It does not qualify certificate trust, vulnerability audit or Release packaging;
H's original diagnostic and its incorrect cross-domain comparison are preserved.
Earlier A–G runner failures and their artifacts are preserved; no size ceiling
or source/dependency identity check was loosened to turn this into a pass.
The read-only v63-I PE/IL audit of those exact H packages finds only 871 bytes
of duplicate method bodies at least 16 bytes long, far below the 90,829-byte
managed-size gap. Public XML documentation removal would not affect that gap;
required generated WinRT/AOT interop is not an eligible shortcut. The owned
metadata-only process exited zero with unchanged bound inputs
(`MarkdownRenderer/artifacts/package-il-audit-v63-i-20261003/execution-receipt.json`,
SHA-256 `0a5f97242519854c1f227608060cbfacc20758a2b7c25e5e28200056a84cbb84`).
This is a consolidation diagnostic, not a size or Release pass. Its historical
archive/restore-graph warning predates the canonical-hash reconciliation above.
Provider-only **v64-D** passes **469/469** exact managed no-worker cases against
immutable v63-E main/Core references, with four owned exits zero and all 76
selected source identities stable. It admits private normalization-array
overlap before the writer's first output allocation, shrinks to final retained
capacity, and returns typed transient host denial (or direct resource-limit
failure) only after cleanup, without hashing/quarantining on that path. Its
receipt is `MarkdownRenderer/artifacts/svg-provider-v64-d-20261003/managed-provider-audit.json`
(338,822 bytes, SHA-256
`8135c6a7d39f26c65432a5b743f60c588d94f288cc8e6a472dde6a9a3573a020`).
At the provider-only D checkpoint, shared-host threshold wakeup integration was
still pending: version-only wakeups
can observe a failed attempt's own release without sufficient free scratch
capacity. The later H checkpoint above verifies the managed threshold slice.
D is not a fresh coupled host candidate or a native-worker/security/
AOT/aggregate-memory/Edge qualification.
The October 3 **v61** coupled SVG source-admission, pressure-parking and demand-
promotion implementation passes its sealed managed checkpoint. Its engine-neutral internal seams
and pure unit contracts are written; the fresh C diagnostic passes **24/24**
primitive cases, including rejected/throwing promotion-queue scheduling and
registration-slot cleanup. Its immutable receipt is
`MarkdownRenderer/artifacts/svg-demand-primitives-v61-c-20261003/receipt.json`
(SHA-256 `a245ae4e3ff229ae84fb5b47fcbd47f209075ddbad1e82e9a765024587a3bd90`),
with an owned dotnet exit zero, all eight selected source identities stable,
and exact TRX result/definition identities. This is not host/provider integration.
The immutable C runtime then passes ten fresh-process replays, **240/240**
results with the same exact 24-case set and all bound source/runtime identities
stable (`svg-demand-primitives-repeat-v61-b-20261003/receipt.json`, SHA-256
`e58db47cc908b5f1dcc92ac8384d5325dafb889f65fbfa5da50ed27e23e93674`).
The first repetition runner's path-resolution preflight failed before launching
tests; its A artifacts are preserved and supply no qualified result.
Fresh M passes **1,215/1,215** fast core, **1,258/1,258** renderer/API and
**436/436** managed no-worker provider cases, including all twelve API baselines.
The coupled seal rechecks selected source/configuration, staged reference/output
identities, and all seven parent/four provider owned compiler/test process records:
`MarkdownRenderer/artifacts/svg-coupled-v61-m-seal-20261003/seal.json`, SHA-256
`ef1befe6c98aa08d3be3b40731356b936c4d094b8a6d630dae039f1b7a6f310f`.
Borrowed feature runtime and the source-linked provider diagnostic graph remain
explicit. This is not complete SDK closure, normal security/AOT packaging, native
worker execution, aggregate-memory or browser-performance qualification. Later
v62 changes require fresh evidence; M does not qualify a changing candidate.
The coupled slice now also addresses reviewed host defects: pressure parking's
self-rejecting current-plan predicate, never-painted overscan liveness,
allocation-before-mutation ordering, source-free deferred wakeup ownership,
transactional tile-cache replacement and failure fallback cleanup. Current
viewport visibility is wired through the fenced scroll-realization pass
because Paint culls offscreen images that remain inside the lazy band. A per-plan
nonblocking observation/parking claim prevents visible re-entry from racing
the final parking detach. These changes and their new managed tests pass M;
source contracts and read-only review do not qualify dispatcher, device, worker
or performance behavior.
The fresh coupled attempts A–L are preserved, including launcher/schema/encoding
preflight failures rather than overwritten runs. The latest core phase passes
**1,215/1,215** with exact baseline-plus-addition identities; main/Performance and
renderer tests compile after correcting tile-key types, dispatcher delegation,
an inaccessible nested method, and xUnit collection assertions. Disposed host
waiter handles now retain only their opaque identity, not the pending entry.
Provider H's second-lease timeout was traced to its test still owning the sole
worker lease from the preceding scenario; explicit disposal fixes the fixture
without changing the deadline. Fresh I–L provider runs pass **436/436**. Renderer
I–K expose and correct obsolete constructor/source-contract fixtures and a weak-
owner setup that permitted collection before asserting full pending capacity.
Fresh L passes **1,257/1,258** renderer cases, including every new v61 case, but
the unchanged `WarmContextualStyleLookup_DoesNotAllocate` reports **5,872 bytes**
against its **zero-byte** requirement. That exact source and production binary
passed the same test in I/J/K; no deterministic allocator has been identified.
Ten focused immutable-L-runtime replays pass with exact case/definition identity,
captured owned exits zero, and all 376 bound runtime identities stable. Their
receipt is `theme-snapshot-allocation-v61-l-20261003/focused-replay-receipt.json`,
SHA-256 `6aebb9e19ec72aa177e38a2268d454cf789913d73a41f657ec7e099fd4bc5c8f`.
They were sequential but overlapped M, not whole-machine isolated. M also passes
the unchanged zero-byte test. The original failure remains preserved and its
allocator unidentified; neither threshold nor warm-up count is relaxed and no
global performance pass follows. Provider discovery uses strict UTF-8 decoding and
two explicitly pinned TRX/discovery supplementary-character formatting aliases;
renderer discovery preserves unique test IDs despite two pairs of pinned,
truncated API-test labels. These fixes do not relax outcome or case-identity gates.
The same run's read-only trace also confirms a separate source-activation lane
gap: raster/SVG ImageBox demand promotes when it becomes visible, but the optional
activation scheduler keeps the initial visibility boolean, leaving queued work
in the overscan lane and evictable. Cancellation/rescheduling does not close it
while the image remains in the lazy band. The next slice is recorded in
`MarkdownRenderer/artifacts/source-activation-demand-promotion-design-v62-20261003.md`:
an internal engine-neutral demand contract, original-arrival FIFO promotion,
pre-eviction/dequeue reconciliation, and bounded registration/cancellation tests.
The **v62-F** slice now passes **1,218/1,218** fast core, **1,279/1,279**
renderer/API and **436/436** managed no-worker provider cases, with all twelve
API baselines preserved. Its sealed selected source/output/reference identities
and seven parent/four provider owned exits are recorded in
`MarkdownRenderer/artifacts/svg-coupled-v62-f-seal-20261003/seal.json`
(1,453,982 bytes, SHA-256
`fa199a76b7454f902d10c5e500e6a4fe04ad3957e6b0c1368aac1e3e32122d62`).
Demand is borrowed through the internal session seam; promotion moves the same
bounded queue node in original-arrival order, and exact ticket/generation/token
checks prevent stale rejected activation callbacks from clearing a resumed load.
This closes the managed source-activation priority gap, not live latency or
browser-performance qualification. Ten preliminary fresh-process scheduler
replays pass **250/250** cases; they are diagnostic repetitions without the
coupled runner's per-process identity/cleanup receipt. A/B's compiler defects,
C/D's asynchronous test-fixture bookkeeping failures and E's stale expected
test-name binding are preserved. The final fixture waits for callback claim and
observes delivery independently of registration disposal; no production deadline,
test assertion, capacity or zero-allocation requirement was relaxed.
The full immutable-F **G** replay passes **1,719/1,719**, including exactly 501
synthetic external gate-validator fixtures. Its retained outer wrapper and
inner dotnet launcher exited zero, with no timeout/capture error; all 221
reconstructed compile inputs, 92 runtime files, three live source contracts and
the copied sibling gate script stayed stable. The exact TRX/discovery/definition
bijection retains the two pinned Unicode display aliases. Receipt:
`MarkdownRenderer/artifacts/full-core-v62-f-unfiltered-g-20261003/full-core-replay-receipt.json`
(891,086 bytes, SHA-256
`4352c07ced0895ef67635729f1a746e00d4075ec1b22898498808201b47d0d31`).
The inner owned run took 5,161,030 ms; the preceding focused 15/15 orchestration
probe is not substituted for it. Earlier failed/aborted launchers are preserved.
This qualifies the immutable F managed diagnostic only, not subsequent source,
testhost identity, live README rendering, UI/worker behavior or the qualified
counterbalanced Release/Edge/top-500 gates.
The **v63-E** slice passes **1,218/1,218** fast core, **1,318/1,318**
renderer/API and **447/447** managed no-worker provider cases, with all twelve
API baselines and seven parent/four provider owned exits zero. Its selected
source/output/reference identities are sealed in
`MarkdownRenderer/artifacts/svg-coupled-v63-e-seal-20261003/seal.json`
(1,479,177 bytes, SHA-256
`7c5348ee2e3ea2f11ab7d8f88551d0a3307add330e4947918d11f671d9b68fe4`).
Normalized SVGs now transfer bounded segments directly into their retained owner
and shared-memory copy without a final source-sized flatten. Exact tail capacity,
split-boundary sanitization/hash, source/request lifetime, and pre-allocation
retired-source rejection are covered. A mapped-view constructor failure now
releases its native view/handle. This managed no-worker partition does not execute
or qualify those native mapping/security paths. Cancellation remains allowed
during reflow measurement; after placement begins, nested arrange and committed
metadata/index publication finish without token interruption. Eight new tests
cover single/multiple owners, nested stacks, tables/width and unknown-owner
fallbacks, pre-cancellation and measured-placement recovery. This is a cancellation
boundary, **not COW or rollback of arbitrary nested Measure/Arrange faults**.
Style-name lookup now shares one mapping; 31 new known/unknown-role cases preserve
localization fallback behavior, and an unused private record's generated value
semantics were removed. Neither change proves the lean package cap passed.
Attempts A–D retain their compile/case-binding/test failures. D's one incorrect
new literal index was corrected to compare the entire retained source span; no
gate or lifetime assertion was removed. Provider-private normalization scratch
admission is the next bounded slice, designed in
`MarkdownRenderer/artifacts/svg-normalization-scratch-admission-v63-20261002.md`.
Original-buffer/normalization scratch overlap, aggregate native/GPU/RSS accounting,
true COW layout publication, oversized-codec workspace and all live
release/device/Edge gates remain open.
The subsequent **v64-C provider-only foundation** passes **463/463** exact
managed no-worker cases against immutable v63-E main/Core references. Six new
growth/threshold-wait cases cover atomic lease/process accounting, priority
headroom, disposal races, insufficient self-release, lost-wakeup and cancellation
behavior. A review caught and corrected the lease-side growth weight before
acceptance. Eight marker-boundary oracle cases plus two 50,000-iteration warm
zero-allocation checks pass for contiguous and segmented storage. Marker scans
now retain one captured storage owner and use span search instead of repeated
per-byte owner reads. The source-bound receipt is
`MarkdownRenderer/artifacts/svg-provider-v64-c-20261003/managed-provider-audit.json`
(335,758 bytes, SHA-256
`9fd8617809245682a4a4ab820ffc88944b44aa16ca67ae0b00a7f2eb28dfe575`),
with all four owned exits zero and stable selected sources/references. A's stale
explicit-addition count failed discovery and is preserved; B passed 461 before
the two warm allocation cases were added. These primitives are not yet wired
before normalization or through the host threshold interface at that checkpoint,
so C does **not** close source-scratch admission or qualify a full v64 candidate.
The subsequent D provider slice, H size diagnostic and G full replay above are
separate evidence; none qualifies the changing coupled host candidate.
A fresh read-only WinApp readiness query confirms released CLI **0.7.0**, SDK
**10.0.401** and enabled Developer Mode, but `winapp target snapshot sandbox --json`
still exits **1** before guest inspection: the Sandbox broker's gRPC request to
**localhost:80** is denied (`wsbVerb=list`, **0x80131500**). Receipt:
`MarkdownRenderer/artifacts/winapp-readiness-v64-20261003.json`. No guest/app was
started, no input injected, and no host fallback, feature change or restart was
attempted. This does not establish that Windows Sandbox is absent. The WinUI
UI-testing skill requires stopping that sandbox validation path here; an
authorized Windows 11 runner with a working broker is still needed for the live
release matrix. Local managed/build/ownership work continues independently.
The October 2 **v60-f** follow-up passes its sealed managed candidate checkpoint. It closes the
source-sized cache-policy bypass, captures immutable EXIF-aware raster sizing,
enforces pixel caps for one-pixel edges, matches authored sizing to cell
containment, and shares process-wide SVG/raster preparation admission. Additional
nonblocking readiness/scroll/focus and detached context-menu ownership changes
pass **1,191/1,191** fast core and **1,227/1,227** renderer/API tests. It also
corrects partial/offset GIF logical-canvas composition, fences hosted-focus
publication, and prevents temporary SVG worker/runtime resource limits from
poisoning the permanent content-failure cache. The earlier E checkpoint has
**3,580** targeted passing results in twenty fresh processes. Both E warm tile queue probes
allocate zero bytes over fifty thousand calls. Default lean package size still
fails: **548,718/537,600** compressed and **1,301,504/1,258,291** managed bytes.
The prior frozen E unfiltered core replay passes **1,692/1,692**, including 501
synthetic gate-validator cases, with captured owned-launcher exit zero and
unchanged E source/runtime identities. A separate managed, no-worker provider diagnostic passes **420/420**
against E references; all **46** Rust cases pass in a separate locked/offline
Release unit run. Neither substitutes for isolated-worker execution. Earlier
runner-preflight failures are preserved. This is not full COW, aggregate memory closure, Release/Edge or
top-500 README qualification.
The earlier sealed October 2 **v59G** candidate passes **1,121/1,121** fast core and
**1,128/1,128** renderer/API tests, including all twelve API baselines, with all
four compiler/test exits zero and **494** source/**486** output identities
verified again at the seal. Its matching 50,000-call pending-tile warm probe
allocates **zero bytes** in both queue methods without new work or provider
calls. These are managed correctness/allocation diagnostics, not a normal
security/AOT build, UI/frame timing, or Edge qualification. The freshly packed
default Core+WinUI pair fails both unchanged size caps: **1,274,368/1,258,291**
managed bytes (**16,077 over**) and **539,137/537,600** compressed bytes
(**1,537 over**). Aggregate memory, COW, codec-workspace and live release gates
remain open. The optimized-gate replay passes **501/501** synthetic fixtures
against immutable v58I, with actual launcher/child exits zero and a strict
supplemental binding. It cannot qualify later G source changes or the README
audit; the original launch-record gap remains explicit.
The completed October 2 **v58H** local candidate passes **1,616/1,616** unfiltered core
(including **501** synthetic external-evidence fixtures), **1,115/1,115** fast core,
**1,072/1,072** renderer/API, **420/420** selected managed SVG, and a separate
**1,072/1,072** current-provider integration run. Strict geometry/lifetime/ImageBox
repetitions pass **1,540** results; fresh SVG policy/concurrency repetitions pass
**2,460** results with exact identities and stable source/runtime bytes.
These are managed correctness diagnostics, not a full Release/Edge/500-README
qualification. The default Core+WinUI managed pair still fails its unchanged
cap by **12,493 bytes** at H's historical checkpoint (superseded by G's measured
size failure above). H's actual owned launcher exit is zero; a separately
hashed Toolhelp/strict-TRX binding preserves the original receipt's CIM capture
and `Completed`-summary parser limitations. Its 491 source inputs and 486 output
identities were current at the full seal. Subsequent tile/gate optimizations need
fresh candidate evidence. The archived v57C unfiltered pure-core replay completed
**1,616/1,616**, including all **501** synthetic external-evidence fixtures;
its 92 runtime files and reconstructed 211 C# inputs plus project matched the
archived core provenance when captured; later H source edits mean it is not
current-source evidence. The watcher did not capture a numeric process exit code,
so the strict completed-process checkpoint remains unqualified. This is not a
README-rendering audit.
Opt-in SVG host-preparation CPU admission and whole-operation session cancellation
now pass direct integration regressions. They do not close aggregate byte ownership.
The October 1 v41j off-band image-residency, bounded data-URI, scene-independent
projection and single-block graph detach/refill checkpoint, with the host
memory-pressure follow-up, passed 5,718 selected managed tests and strict
checks for all fourteen packages at that point in time. The subsequent v42
source-ownership checkpoint passed 1,090 fast managed tests and 3,486
headless application tests. The v43 follow-up passed 1,090 fast managed
tests and 3,507 headless application tests. The preceding v44 checkpoint passes
3,511 headless application tests and 121 isolated source-linked SVG diagnostics.
Its native Rust suite passes 35 tests (one pre-existing manual benchmark remains
ignored), including actual direct/nested/filter image pixels; Clippy and builds
for x86/x64/ARM64 pass. The refreshed unsigned development workers do not qualify
production signing or isolated execution. Three fresh end-to-end SVG probes fail
before worker startup with restricted-token error 87. A broader 997-test staged
renderer/API suite passes with the current SVG assembly and reused v42 test/
unchanged feature assemblies, as diagnostic-only evidence including all twelve
API baselines. The preceding v43 partial packaging measured 536,214 bytes for
Core+WinUI, 1,386 bytes below the
unchanged 537,600-byte compressed cap; the optional Performance pack is 110,432
bytes under its unchanged 131,072-byte cap. The all-fourteen-package gate has
not passed: packaging stops at the HTML project's mandatory dependency-security
error. Host SVG source pins and queued raw-buffer publication captures are
removed; retired provider documents now clear their source snapshots. V44 removes
the host MIME-repair DOM pass and source-sized worker lowercase copies in metadata
inspection, but live provider/sanitizer copies and broader aggregate-memory
qualification remain open. The
preceding v41 app compiled C# and XAML before its security gate
failed; the v43 normal app and current v44 SVG provider builds stop
before compilation with NU1900 because the vulnerability feed is unreachable.
Production GitHubService integration is therefore not compiler-qualified by
the passing headless seam tests, and the Release build is not qualified.
The isolated SVG correctness compiler uses existing explicitly hashed runtime
references, not the shipping Windows TFM/AOT analyzer graph. This is local
managed evidence, not independent reproducibility or a live performance verdict. The
preceding v45 checkpoint passes 160 isolated source-linked SVG ownership/cleanup
tests and 997 staged renderer tests, including twelve API baselines. Retained
completed open/render/inspection tasks now release source aliases after actual
work drains. Diagnostic-only numeric snapshot ledgers report scoped live/peak
private-source lengths, not total memory or admission; aggregate source bounds
remain open. The preceding v46 checkpoint passes 240 isolated source-linked SVG
tests and 1,002 staged renderer tests, including all twelve public-API baselines.
Static-source preparation now has iterative deep/wide traversal, cancellation
checks and bounded segmented output; canceled inline-image measurement restores
the prior native geometry/layout. These are managed diagnostic results, not
browser-relative release qualification. An independent conditional compile
excludes ledger types with diagnostics disabled. Lossless, CRC/SHA-verified
NuGet recompression restores lean size headroom: the fresh Core+WinUI pair is
530,133/537,600 compressed bytes and 1,255,424/1,258,291 managed bytes. The five
fresh available packages pass AnyCPU IL inspection; the full fourteen-package
gate still fails, rather than substituting stale packages, because the normal
pack stops at HTML's mandatory NU1900. The preceding October 2 v47 checkpoint
passes 310 isolated source-linked SVG diagnostics and 1,008 staged renderer
tests, including all twelve public-API baselines. Source-byte admission now
bounds fitting-request bypass so small arrivals cannot starve an older large
request; worker affinity reduces avoidable cross-process reattachment without
indefinite FIFO bypass; live secondary dispatch respects Energy Saver and
unknown power state. Native text-document aliases reject stale font generations
before output mapping. This is tree invalidation, not a Windows font-catalog
reload; generation-aware asynchronous catalog refresh remains open. The native
suite passes 37 tests, Clippy and all three architecture builds pass, and the
required live-worker font test fails before startup with restricted-token error
87. Fresh five-package lean sizing is 530,200/537,600 compressed bytes and
1,255,936/1,258,291 managed bytes. The full fourteen-package/security gate remains
failed; no missing package or required native fixture is waived. The normal
provider/full-test build also remains blocked by the mandatory vulnerability feed.
The preceding v48 implementation adds generation-aware asynchronous Windows font
catalog refresh, short font probes that release content-worker slots, bounded
parked admission, nested-resource font/theme metadata, and retained/queued
document quarantine enforcement. Final exact-source regressions and evidence
are recorded in the v48 checkpoint below. The normal provider/app builds still
stop at mandatory NU1900; required isolated-worker execution still fails before
startup with restricted-token error 87. These local correctness changes are not
font-latency/memory qualification, package qualification or browser parity. The
preceding v49 checkpoint retires ordinary SVG provider documents after actual
render/upload work drains, removes strong retired-graph aliases from image-reflow
UI handoffs, and closes per-operation publication ownership on unload/dispose.
The final exact-source fast core and staged renderer suites pass 1,102 and 1,012
cases respectively; the separate unfiltered core suite is still queued behind
the preserved earlier external-process run, not claimed as passed. Twelve
handoff and twenty image-lifetime cases each pass twenty fresh processes.
The five fresh packages meet the unchanged lean compressed and managed caps,
with 6,153 and 307 bytes of headroom respectively; the full fourteen-package
gate still fails at mandatory NU1900. The detailed v49 checkpoint below keeps
the incomplete COW, aggregate-memory, live-worker and browser gates explicit.
The preceding v50 follow-up also removes strong control/image identities from
queued image-completion callbacks. It passes 1,102 fast core and 1,013 staged
renderer cases, including twelve API baselines; twenty-one retention cases pass
twenty fresh processes. The new exact-source unfiltered core suite is running,
not claimed passed at that checkpoint. Fresh lean packaging remains within the unchanged caps,
with 6,097 compressed bytes and 307 managed bytes of headroom. Exact rank-5 CI
evidence now identifies a published-head readiness callback rearm defect already
fixed locally, but the fix remains unpublished and without a qualifying live
rerun. The preceding v52 checkpoint completes that exact-source unfiltered managed
core run: **1,603/1,603**, including all **501** external-gate fixtures, with
captured compiler source hashes unchanged. It also closes deterministic SVG
renderer shutdown over retained document handles and actual in-flight opens,
and prevents shutdown cancellation from quarantining valid SVG source. Final
selected source-linked SVG tests pass **400/400**; staged renderer/API checks
pass **1,013/1,013**, including twelve API baselines. Twenty-eight shutdown,
registry and source-lifetime cases pass in each of twenty fresh processes.
These results qualify managed correctness only, not a shipping Windows
TFM/AOT/security build or browser-relative performance. The required worker
test still fails at restricted-token error 87, and the normal provider build
still fails at mandatory NU1900. Aggregate-memory/COW/WIC work and the full
counterbalanced Release, same-byte Edge and **500/500** README gates remain
open. No new provider/all-fourteen-package qualification is claimed. The preceding
v53 checkpoint passes **3,556/3,556** headless app tests and **51** repository-image
audit contracts in each of twenty fresh processes. Opt-in hash-only service/fallback
outcomes now expose the missing-image investigation without labeling cache hits as
HTTP successes, crossing audit epochs, or allowing success samples to hide failures.
Intrusive traces are explicitly excluded from release timing qualification. The normal
Release automation harness and diagnostics-disabled headless compile pass; a fresh
locked app restore still fails mandatory NU1900 with dependency locks unchanged.
Rank-214 code-server screenshots remain a confirmed published-head failure, not a
fixed or same-byte-qualified case. The preceding v54 follow-up adds loaded image-reflow
handoff recovery, including rejected/withheld publication and image work preserved
when a replacement build fails. It also removes the extra UI dispatch on ordinary
image completions and reuses immutable completion event args. The final fresh managed
results and package measurement are recorded in the v54 checkpoint below; they do
not qualify a live dispatcher, SVG worker, shipping app/AOT or browser performance.
The preceding v55 follow-up closes the **two-package** lean size regression without
relaxing limits: **531,837/537,600** compressed bytes and **1,257,984/1,258,291**
managed bytes. It removes an unnecessary per-SVG callback-carrier allocation,
preserves Debug-only logging call contracts and centralizes identical image
publication/cleanup logic. SVG normalization now releases producer/consumer
input aliases before flattening output; this is not an aggregate/RSS cap.
Final exact-source diagnostics pass **1,114** fast core, **1,023** current-SVG
renderer/API and **404** selected SVG cases; four normalization-ownership cases
pass in twenty fresh processes. The normal app/provider still fail mandatory
NU1900, and the fresh required-worker test fails restricted-token creation with
error 87. No fourteen-package/shipping/AOT/browser qualification follows. The required live
runner, aggregate source/COW/WIC work and all browser-relative release gates remain
open. The preceding v56 follow-up avoids repeated validation/decode allocations for
large, identical raster data URIs within one SVG inspection, and fences code
highlighting reads with the existing layout lease. Highlight tasks now carry
copied text and weak snapshot identity, not retired BlockBox/document graphs.
It passes **1,114** fast core, **1,026** renderer/API and **414** selected SVG
cases; current-SVG integration independently repeats the **1,026** renderer cases.
Lean Core+WinUI sizing is **531,941/537,600** compressed and
**1,257,984/1,258,291** managed bytes. A counterbalanced **host-preflight-only**
diagnostic finds repeat-heavy fixtures faster with fewer allocations, but
unique/small fixtures about 5–7% slower. This is not the required full Release
or Edge-relative benchmark. A preserved v56 full-suite zero-allocation failure
remains unexplained; passing later runs do not establish its cause. Normal
app/provider security builds, required isolated-worker execution, Sandbox,
aggregate-memory/COW/WIC and the full **500/500** browser audit remain open.
The detailed v56 checkpoint below retains every scope/provenance limitation.
The historical v57 implementation addresses mutable UIA/native-plan geometry reads,
busy/reentrant UI publication, and highlighter lifetime registration before
reentrant provider code. Exact-source v57 compilation, regressions and package
measurement were pending at that checkpoint; preceding v56 results did not qualify
those edits. The later sealed checkpoints above supersede that pending status.
Lazy completion dispatcher recovery and resource-independent text-position
projection are also being implemented. The
historical v41j lean compressed package had only ten bytes of headroom; this
is not the current v43 partial package result. The follow-up also
replaces the quadratic hidden-ancestor attachment walk with a tested linear pass. Earlier
zero-allocation measurement failures remain unexplained despite subsequent
passing full runs; their zero-byte gates are unchanged. A separate 501-case external-gate validator
partition also passes, not as a renderer-speed benchmark. Detailed evidence is
recorded below. Independent rebuilds
fail due to confirmed WinRT source-generation nondeterminism. The published
PR's top-500 audit is still failed; no current-candidate browser-parity or
qualified counterbalanced release result exists.

- Locally implemented an explicit `--capture-same-byte-corpus` foundation for
  the top-README audit. It captures the pinned public README through a streamed
  16 MiB cap and verifies Git blob SHA-1 plus content SHA-256; decoded Edge
  image bodies are checked against actual byte limits even if transfer-length
  telemetry under-reports. The corpus stores content-addressed assets and
  SHA-256 URL lookup keys only, and a loopback replay server fails closed on
  missing or changed data. JitHub gets a separate audit-only resolver that is
  enabled only for a freshly captured case, validates repository/commit/README
  identity and asset hashes, and returns a handled miss without network
  fallback. Raw Edge comparison data exists only in a temporary file outside
  the artifact tree and is deleted after comparison; persisted source/link
  fields are hashed. The focused Node capture/replay tests, all seven C#
  resolver tests, and the automation and WinUI x64 Debug builds pass locally.
  The app build reports existing WUI analyzer warnings unrelated to this
  change. This is replay infrastructure, not same-byte top-500
  performance/fidelity evidence; the qualified release benchmark and full
  top-500 audit remain open.
- September 27 local checkpoint: bounded the Edge image-DOM and response-alias
  indexes, and made canonical GitHub Camo URLs replay against the identical
  captured original image bytes. All 26 focused audit Node tests pass. A
  pinned rank-1 Debug x64 end-to-end replay used the same README and four
  captured image assets in Edge and JitHub, with no unavailable image; it is a
  one-case diagnostic, not Release top-500 evidence. The opt-in 500-case
  same-byte CI path and its positive complete-500 merge fixture now exist
  locally, but have not run as a release audit. An earlier local Release
  external-gate fixture completed 485/485 tests before the latest scene
  changes; no qualified foreground counterbalanced benchmark has passed.
- September 27 local checkpoint: the isolated SVG worker now records precise
  late-stage progress, elapsed time, and worker-input/executable hashes for
  the intermittent exact-byte rank-153 timeout. This is diagnostic provenance,
  not a timeout fix. Scene deferral is under implementation, but its current
  Core+WinUI pack exceeds the unchanged managed-size gate by 3,789 bytes;
  it cannot be accepted until the gate passes. The current live top-500 run
  is still in progress. Oversized ordinary-raster tiling remains open: the
  existing WIC transform does not guarantee tile-bounded decode memory, and
  source bytes need a budgeted lease before retaining tiles. Copy-on-write
  dirty-block layout publication also remains open because mutable boxes and
  UIA geometry still reference the active layout graph.
- September 27 qualification correction: the first manual 500-case
  `same_byte_release` dispatch is diagnostic only. It replays captured image
  bytes for native JitHub, but its Edge denominator still includes live
  GitHub/CDN navigation and it did not prove that the native Markdown input
  was the captured README. An offline Edge article/image replay with
  client-scoped timing, exact native README-source evidence, and per-case
  known-outlier checks are being implemented. Do not use this first dispatch
  as the same-byte release verdict even if its workflow turns green.
- September 28 local qualification checkpoint: schema-2 corpus capture now
  binds both the Edge-captured README and the native rendered source to the
  pinned Git blob and SHA-256, validates every replayed asset, and reports
  zero fallback network requests. Pinned Release one-case diagnostics passed
  rank 1 (`codecrafters-io/build-your-own-x`) and rank 66 (`vercel/next.js`)
  with no unavailable images. A separate rank-59 (`airbnb/javascript`) replay
  exposed a Win2D `E_INVALIDARG` while painting a code diff marker; routing
  markers through the existing guarded gutter-font path made the pinned
  Release reproduction pass with 100% text and 99.90% structure coverage.
  None of these three cases is a current-head 500-case pass.
- The first offline Edge replay used GitHub's already-rendered article HTML
  and a 1000-pixel browser content width, while native JitHub parsed raw
  Markdown in a roughly 623-pixel content pane. Its rank-1 native/Edge ratios
  (4.44 first, 4.44 full) are diagnostic, **not a qualified source-render
  comparison**. The release denominator must instead parse the identical
  captured Markdown in Edge with a pinned client GFM parser, replay exactly
  the captured asset bytes, and match the measured native content viewport,
  theme, image-ready boundary, and traversal. Keep the GitHub-HTML replay only
  as a separate diagnostic. No 110% same-byte performance claim is made.
- September 28 local source-bound checkpoint: the audit now replays the exact
  captured README through a pinned MIT GFM parser in Edge at the measured
  native content viewport and DPR, with only hash-matched captured images.
  The old GitHub-HTML replay remains diagnostic. All 37 focused Node tests and
  the positive/negative complete-500 merge fixtures pass. A current-corpus
  Release rank-1 end-to-end case passed rendering checks (100% text, 99.93%
  structure, zero unavailable images), including an SVG with an inert external
  doctype. It did **not** pass the unchanged browser-relative performance
  gate: native/Edge first-image-ready was 572.8/169.6 ms (3.38x), and full
  traversal was 934.5/267.9 ms (3.49x). This is one noisy local diagnostic,
  not a qualified counterbalanced or 500-case result. The current optional
  scene/role-demand candidate also remains over the immutable lean managed
  package cap; it must be reduced and remeasured before shipping.
- September 28 oracle correction: the first source-bound replay accidentally
  inherited the 14-pixel page font for its Markdown article. Primer's
  `.markdown-body` uses a 16-pixel font and 1.5 line height, with additional
  list-item spacing. The replay now follows those documented rules and tests
  assert them. Replaying the same pinned rank-1 corpus at 660 × 611 DIPs
  changes the Edge page extent from 10,757 to 13,704 CSS pixels; the prior
  native extent was 12,824 DIPs (native/Edge 0.936). This corrects a biased
  fidelity denominator, not the native slowdown: the new Edge one-case
  image-ready and traversal times were 198.5 and 331.1 ms, respectively.
  They have not yet been paired with a rebuilt candidate or a qualified run.
  The audit now also requires complete contiguous source-replay page tiles
  and keeps the 0.90–1.10 full-page extent gate, rather than accepting only
  first-viewport text and structure checks.
- A subsequent rebuilt x64 Release rank-1 diagnostic used the corrected
  source-bound oracle and passed text (100%), structure (99.93%), complete
  image resolution, and page extent (12,824/13,704 = 0.936). Native
  first-image-ready/full-traversal were 375.2/545.5 ms versus Edge
  87.6/201.9 ms on that run. The style-role-demand mask was unexpectedly
  unapplied, and theme resolution still took 77.8 ms with 718 app-resource
  lookups and 747 scoped-resource lookups. Exact-corpus AST fallback diagnosis
  is in progress. These are one-case, non-counterbalanced observations; they
  neither meet the 110% performance gate nor replace the top-500 release
  audit or qualified R1,C1,C2,R2 benchmark.
- September 28 follow-up: the complete built-in role-demand mask was not
  applied because the three built-in GitHub/HTML renderer types were checked
  against the GFM assembly name rather than their actual feature-pack
  assemblies. The classification is corrected, with a focused regression test
  (8/8 Release tests passed). Four successive pinned rank-1 cold local audits
  on that diagnostic build passed source/text/structure/image/extent checks;
  a fifth rendered correctly but failed the required clean-exit gate with
  `0xC000027B` during WinUI window teardown. WER reports a COM wrong-thread
  error (`0x8001010E`); no retained dump identifies the callback yet. Native
  first-viewport times across the five were 351–701 ms (median 443 ms) versus
  same-byte Edge 141–169 ms (median 157 ms); native full traversal was
  702–1,673 ms (median 874 ms) versus Edge 255–285 ms (median 271 ms).
  These were unbalanced cold diagnostics with noticeable machine variance,
  **not** a qualified release benchmark. The browser-relative 110% gate is
  still unmet even after the role-mask fix, and the shutdown crash is open.
- The temporary per-property theme probes used for that diagnosis were removed
  after the role-mask correction; app Release x64 and audit automation builds
  pass with zero errors/warnings, the focused role-mask tests pass 8/8, and
  audit snapshot contract tests pass 57/57. Excluding the not-yet-integrated
  ordinary-raster tile planner from the shipping WinUI assembly (while keeping
  its 13/13 linked pure tests) brought the managed Core+WinUI pack down to
  1,260,544 bytes. This remains **2,253 bytes above** the unchanged 1,258,291-
  byte ceiling, so the package gate still fails. It is not a size waiver or
  evidence that oversized rasters are now tile-bounded in production.
- The source-bound Edge audit now uses one bounded DevTools transport for both
  corpus capture and raw-Markdown replay. Connection loss, malformed replies,
  and unanswered commands fail the case explicitly instead of hanging a shard;
  browser-close cleanup no longer waits for a missing acknowledgement. All 46
  focused audit Node tests, including the actual offline Edge replay, pass
  locally. This is harness reliability, not a renderer speedup or release-gate
  pass.
- September 28 local pack E: the Core+WinUI Release x64 NuGet pair now passes
  both unchanged lean ceilings: 1,256,960/1,258,291 managed bytes and
  530,693/537,600 compressed bytes. The combined focused Release suite passes
  50/50 tests, including nested deferred-marker provenance, cancellation,
  lifecycle, theme/role equivalence, and custom-renderer fallback. The full
  strict 14-package compliance gate also passes locally with zero license
  findings, six verified selected-RID native assets, and a reproducibility
  manifest; public API baseline tests pass 39/39. Packaged SVG workers remain
  unsigned development artifacts, so production signing, current-head CI,
  same-byte corpus, and interactive release benchmarking remain open.
- The rebuilt Pack E x64 Release app and audit automation build with zero
  warnings. A pinned rank-1 same-byte source replay passes native text (100%),
  structure (99.93%), zero-unavailable-image, and full-page extent
  (12,824/13,704) checks. Native/Edge image-ready is 352/129 ms (2.74x), and
  full traversal is 490/243 ms (2.02x). This is one local diagnostic, not a
  counterbalanced performance result or the required current-head top-500
  release corpus; it confirms the unchanged 110% speed goal remains unmet.
- September 28 local deferred-scene review: publishing one prepared scene now
  leaves sibling scene work alive while ordinary full/restyle invalidations
  still cancel it; deferred callbacks retain the producing extension lifetime
  even when a document is adopted by another engine. A disposed producer
  yields the readable source fallback instead of invoking captured resources.
  The full x64 Release GitHub renderer suite passes 444/444 and the unchanged
  Core+WinUI lean package caps pass at 1,257,984/1,258,291 managed bytes and
  530,818/537,600 compressed bytes. The fast Core suite passes 992/992; its
  external-process fixture and current-head app/audit replay are still being
  verified. Scene materialization still incurs a full theme/layout rebuild,
  so these correctness fixes are not a browser-relative speed pass.
- September 28 coordinated local gate: the current x64 Release JitHub app,
  audit automation, and sample build with zero warnings/errors; the app's
  postbuild tests pass 79/79, the full WinUI suite passes 3,169/3,169, and
  the GitHub renderer suite (including public API baselines) passes 444/444.
  All 47 focused README-audit Node tests pass, including actual offline Edge
  replay. Two clean normalized pack sets pass the unchanged strict 14-package
  reproducibility, size, license, SBOM, and six selected-RID native-asset
  checks: the Core+WinUI pair is 1,257,984/1,258,291 managed bytes and
  530,842/537,600 compressed bytes. Packaged resvg workers remain unsigned
  development artifacts; production signing is still required. The external
  Core gate fixture passes 485/485, the recompiled fast Core suite 994/994,
  and the conformance, HTML, Math, Mermaid, SVG, and syntax-highlighting suites
  all pass. These local checks do not
  qualify the live 500-case same-byte audit or counterbalanced benchmark.
- The schema-3 source-bound audit now gates the replay against the captured
  GitHub article in both directions using ephemeral-key HMAC token multisets,
  independently gates stable GitHub/source structure (headings, tables, task
  checkboxes, authored details), and has the 500-case merger recompute and
  check those scores rather than trusting a case's `passed` status. Its
  positive complete-500 and mutation fixtures pass, including a same-text
  table-versus-paragraph false-pass regression. This is audit validity, not a
  product rendering or performance pass; the full current-head 500-case run
  remains pending.
- Fresh schema-3 x64 Release focused replays against the pinned corpus pass
  rank 1 (`codecrafters-io/build-your-own-x`) with 100% GitHub/source text in
  both directions, 100% GitHub/source stable structure, 99.74% native/source
  structure, zero unavailable images, and a clean exit. Native/Edge
  image-ready is 454/127 ms and full traversal 633/240 ms, so the 110% speed
  goal remains unmet. The first rank-60 (`airbnb/javascript`) replay failed
  the unchanged height envelope at 81,398/95,085 (0.856), but its 151 code
  blocks exposed an oracle CSS error: source replay used 16px/24px code lines
  while the captured GitHub article used 13.6px/19.72px. Those 2,523 code
  lines account for essentially the entire 10,529px article/replay height
  difference. Matching the captured code-line metrics in the source-bound
  Edge replay made a fresh rank-60 case pass at 81,398px native and 0.968
  native/source extent, with 100% text, 99.89% structure, 19/19 images,
  zero unavailable images, and a clean exit. No fidelity threshold changed.
  Native/Edge image-ready and full-traversal ratios still failed performance
  at 4.90x and 3.23x. Rank 203 (`d2l-ai/d2l-zh`) was a separate actual native
  typography-density failure at 2,336/2,668 (0.876), with only one code block.
  Changing the app's Markdown body-size, line-height, and paragraph-spacing
  tokens initially made rank 203 pass at 2,685/2,623 (1.024), but then
  regressed rank 1 to 16,642/13,704 (1.214): its 391 list items amplified
  the doubled bottom margin. Restoring the original 8px margin while retaining
  16px body text and 1.5 line height made both fresh x64 Release replays pass:
  rank 1 is 13,466/13,704 (0.983), with 100% text and 99.92% structure;
  rank 203 is 2,429/2,623 (0.926), with 100% text, 99.74% structure, and
  5/5 images. Both have zero unavailable images and clean exits. Their native/
  Edge image-ready ratios remain 2.69x and 3.28x, so the 110% speed goal is
  open. The updated-typography rank-60 audit exceeded the 180-second native
  deadline after 107 viewports; a subsequent explicit-percent audit stalled
  after its first viewport and was stopped. Neither attempt is a current-head
  rank-60 pass, and the UIA traversal regression is being repaired. These
  isolated timings are not the qualified counterbalanced benchmark or full
  500-case verdict.
- September 28 local traversal diagnosis: an audit `ScrollPattern.Scroll`
  call blocked after the first viewport on this unattended desktop. The
  90-second wrapper watchdog terminated and verified the audit process tree
  and wrote an `infrastructure-timeout` artifact with the performance gate
  unevaluated. The interactive desktop's invisible `GameInputSvc` foreground
  window then prevented JitHub activation; both a wheel message posted to
  JitHub's top-level HWND and global wheel input over its verified hit target
  produced no measured movement. Those one-case attempts are harness failures,
  not README rendering or performance verdicts. The replacement audit-only
  in-app viewport command now completes a pinned rank-1 x64 Release traversal:
  25 settled viewport positions reach the document bottom with contiguous
  overlap, zero unavailable images, and a clean app exit. The native first
  render, image-ready, and full-traversal times were 364, 420, and 618 ms.
  The source-bound Edge replay then failed before reporting a comparison, so
  this is traversal reliability evidence, **not** a same-byte or performance
  pass. A current-head x64 NativeAOT publish and artifact verification also
  pass; its follow-up audit is recorded below.
- September 28 strict pack recheck after the scoped-theme capture change:
  the 14-package reproducibility, license, SPDX/notices, and six selected-RID
  native-asset checks pass, but the Core+WinUI managed payload is
  1,259,008/1,258,291 bytes (717 bytes over the unchanged ceiling).
  `MarkdownRenderer.dll` alone grew 1,024 bytes from the last passing pack.
  This candidate remains blocked; the size gate is not waived.
- September 28 NativeAOT rank-1 checkpoint: the source-bound Edge replay now
  follows the same 25 native capture positions and then captures the 238-DIP
  Edge-only tail with 373 DIPs of overlap, preserving the 0.90 step and
  0.90–1.10 page-height gates. All 54 audit Node tests pass. The first AOT
  audit traversed successfully but hung during synchronous UIA window-close;
  after replacing that audit-only close with a process-verified `WM_CLOSE`
  request and a bounded 12-second exit wait, the second pinned run exited
  cleanly. Its exact-byte text coverage is 100%, structure 99.92%, page-height
  ratio 0.983, and unavailable-image count zero. NativeAOT first images-ready
  was 134.9 ms versus source-bound Edge 106.6 ms (1.266×), still above the
  unchanged 1.10 goal. Native parse/theme/layout/publication measured
  52.1/15.3/15.8/0.20 ms. The reported full traversal was 217.6/769.3 ms,
  but Edge's repeated settlement-frame waits may inflate that denominator;
  it is under audit and is **not** a qualified browser-relative pass. This is
  one local case, not the 500-case corpus or counterbalanced release run.
- September 28 timer audit: a controlled source-bound Edge replay measured
  786.6 ms raw full traversal, including 299.9 ms of successful native-position
  stability-confirmation frames and 12.2 ms of corresponding tail confirmation
  frames. Those repeated checks are audit-only: the native timer separately
  subtracts its UIA settling probes. The source replay now preserves raw wall
  time and reports a charged timer that excludes only measured successful
  post-target confirmation and terminal-proof frame waits; parsing, first
  paint, movement to the first target-correct sample, image decode, and
  post-decode paint remain charged. The source-bound comparison and complete-
  500 merger use the charged field with the unchanged 1.10 performance and
  0.90–1.10 extent gates. All 54 Node tests and the PowerShell merge/mutation
  suite pass; the current-head C# build, new pinned audit, and full corpus are
  still pending. No browser-relative speed conclusion follows from the older
  raw full-traversal ratio.
- The latest lean Core+WinUI package recheck is 1,258,496/1,258,291 managed
  bytes, still 205 bytes over the immutable cap; its compressed pair is
  531,166/537,600 bytes. The strict 14-package recheck has not been repeated
  on this candidate. An optional parse-preparation stage trace is under test
  to attribute the 52.1-ms rank-1 interval before changing the renderer;
  neither an over-cap package nor an unmeasured optimization is accepted.
- A fresh x64 NativeAOT rank-1 diagnostic with the charged source-bound Edge
  timer completed 25 native viewports, zero unavailable images, 100% text,
  99.92% native/source structure, a 0.983 page-height ratio, and clean exit.
  Native first-images-ready/full traversal were 280.3/452.4 ms; Edge first-
  images-ready/raw/charged full traversal were 123.8/781.6/466.1 ms. Thus the
  one-case first-image ratio is 2.264 (fails 1.10), while the charged full
  ratio is 0.970. The latter is **not** a corpus or qualified benchmark pass.
  The app's raw audit signal reported parse 118.4 ms, theme snapshot 37.2 ms,
  and layout 27.3 ms, but all new optional parse-stage fields were zero: the
  NativeAOT event-listener path did not capture them. The audit runner had
  inadvertently used an older RID-specific DLL and therefore ignored the
  new fail-closed fields. The RID-specific runner is rebuilt with those
  validations; the zero-stage NativeAOT event path must be fixed and this
  case repeated before attributing or accepting an optimization.
- A further fresh x64 NativeAOT rank-1 run uses a statically rooted,
  session-scoped, audit-only parse-stage callback instead of the event-listener
  transport that produced zero fields under AOT. The exact-byte page again
  passed its one-case content, image, extent, and clean-exit checks. The new
  trace reports 56.80 ms outer parse time, comprising 10.22 ms engine parse,
  0.03 ms scene-plan preparation, 0.10 ms role demand, 10.37 ms total session
  work, and 46.43 ms between session completion and the renderer's parse-end
  mark. The latter interval includes the UI-dispatcher continuation; it is
  not evidence that Markdown parsing itself takes 56.80 ms. Native first-
  images-ready was 145.46 ms versus 119.50 ms in same-byte Edge (1.217x,
  above the unchanged 1.10 gate); full traversal was 235.76 ms versus Edge's
  460.70 ms charged traversal (0.512x). A one-case content PASS explicitly
  does not evaluate the 500-case p95 performance gate. First-viewport image-
  wait diagnostics were missing from the sanitized result despite collection
  in the runner; that forwarding is being repaired before the next audit.
  The lean Core+WinUI pack is back below its unchanged managed and compressed
  caps at 1,257,984/1,258,291 and 530,925/537,600 bytes. The fresh strict
  14-package gate subsequently passed: locked restore, two normalized clean
  pack passes with 14/14 identical hashes and sizes, SPDX 2.3 SBOM and
  notices, reviewed licenses with zero unknowns, and six selected-RID native
  asset checks. This is development-package evidence, not production signing
  or a performance-release verdict. The audit runner now forwards the image-
  wait diagnostic through its sanitized result and its focused contract test
  passes; the subsequent pinned rank-1 run verified live output.
- That second fresh x64 NativeAOT rank-1 replay passed 25 native viewports,
  100% text, 99.92% structure, zero unavailable images, a 0.983 page-height
  ratio, and clean exit. It did not reproduce the UIA stall. The audit now
  records its last post-render UIA stage and terminates its owned process tree
  after 90 seconds without progress. Crucially, its first-image-ready value
  was 346.43 ms while first render was 95.09 ms, yet the UIA image check
  found no loading images on its only poll and consumed just 0.57 ms. The
  intervening ~250 ms is unclassified harness setup/wait, not demonstrated
  renderer work; the resulting 3.023x Edge ratio is **not a valid renderer
  attribution**. The current host audit starts UIA after a fixed 300-ms
  activation delay. An app-side, generation-bound image-ready timestamp is
  required before the first-viewport 1.10 gate can be fairly evaluated.
  Full traversal was 339.27 ms native versus 466.60 ms charged same-byte
  Edge (0.727x), but one case still cannot evaluate the 500-case p95 gate.
- A read-only review of the pre-dispatch 500-case consolidation found that
  it classified absent READMEs from shard results rather than the pinned
  top-500 manifest, and did not compare each shard's commit identity to that
  manifest. A malformed shard could therefore evade same-byte checks or
  reduce their denominator. The merge must fail closed against the pinned
  rank/full-name/commit/README identities, with negative mutation tests,
  before any full same-byte run can count as release evidence.
- The local merger now binds each available/absent README to the pinned
  rank, repository, commit, path, byte size, and blob identity; it requires
  complete per-shard Release executable/dependency fingerprints and checks ten
  previously slow ranks individually. Full visual artifacts are retained
  separately from the consolidation metadata, with bounded screenshot storage
  and a pre-upload check for every expected native/Edge tile. Positive 500-case
  and negative identity, missing-tile, fingerprint, quota, and slow-rank
  mutations pass locally. This repairs qualification integrity only; a fresh
  current-head same-byte 500-case run has not yet passed.
- The opt-in rank-1 shutdown trace showed one nested Markdown viewer still
  loaded before shared-provider retirement even though the outer page's unload
  wait had completed. Shutdown now detaches and waits for the nested shell page,
  waits for tracked Markdown renderer disposal, and retries any failed disposal
  after outer-page unload. Shared providers are retired only after every
  tracked renderer has disposed; unresolved failures are diagnosed and the app
  closes without an unbounded wait. Fresh rank-60 and rank-203 x64 Release
  audit traces report zero loaded-view residue, zero disposal/unload timeouts,
  and zero forced disposals. This removes the observed ordering race but does
  not establish the cause of the earlier WinUI wrong-thread fail-fast; a
  repeated shutdown/fault-injection campaign remains open.
- The local same-byte summary and 500-case merger now use the offline
  source-bound Edge timings for first-image-ready and full traversal whenever
  same-byte capture is requested. The merger independently recomputes both
  native/Edge ratios from the recorded timing fields, rejecting missing,
  non-finite, or inconsistent stored ratios. The complete-500 positive and
  mutation fixtures pass. This repairs verdict integrity; it does not turn
  the current 2–5x focused slowdowns into a performance pass.
- WinApp CLI 0.7 sandbox smoke now runs the current x64 Release JitHub build
  after verifying and, when absent, installing the locked Microsoft-signed
  Store Engagement framework package in the guest. Its unauthenticated
  sign-in control was reachable at 96 DPI and the smoke-owned process exited
  cleanly. The separate sample UI suite navigated Math, Mermaid, HTML, SVG
  stress, and progressive-scenes pages at 100% scale. Neither smoke covered
  authenticated README content, touch selection, 150%/200% scales, Dark, or
  High Contrast; those release checks remain open.
- A targeted GFM 0.29 parse-path change avoids materializing ordinary literal
  text merely to reject email autolinking, and avoids table-preparation line
  enumeration when the source contains no raw pipe. Source-span and
  punctuation regressions pass in the recompiled Core suite, but a controlled
  uncached parse comparison is still needed before claiming a speedup.
- The live top-500 GitHub README audit for PR #99 head `1831ed1` finished
  successfully in Actions run `36361277068`: all 20 shards and the complete-
  500 consolidation passed. This is valid live-source coverage for that commit,
  not a same-byte source-bound comparison or evidence for subsequent local
  changes. Re-run after the pending fixes land.
- A source-bound schema-12 x64 Release reference harness was prepared in an
  isolated worktree from renderer checkpoint `1a403a2`, with only publication
  timestamp observations added to that renderer. Its measurement/schema,
  orchestration, gate, and visibility tests pass locally; no live benchmark
  was started. The PR-base `ddd0068` renderer lacks the harness's required
  APIs, so this reference can show regression against `1a403a2`, not against
  the pre-1.0 PR base. A same-machine quiet foreground R1,C1,C2,R2 run still
  has to qualify all four legs against the unchanged 120 Hz and absolute
  gates before it provides release evidence.

## Implementation checkpoint

- Done: borrowed per-account performance session and explicit builder/control
  registration; JitHub passes it to both Markdown viewport modes and retires
  it on account reset and shutdown.
- Done: post-parse, layout-concurrent image-source prefetch; no 2,048-source
  cutoff; bounded fetch slots with visible reservation and queued-prefetch
  promotion; partitioned source LRU, document-local unkeyed entries, cancellation,
  cross-document single-flight, memory-pressure trim, and privacy-safe counters.
- Locally verified, pending current-head CI: speculative image-source fetch
  admission now rotates between documents instead of allowing the first
  document to monopolize newly freed slots. Visible fetches still bypass this
  queue; paused remote work does not hold a speculative slot. Cancellation and
  grant races, queue order, the 1,800-image storm, and a two-document session
  scenario passed locally. Raster CPU preparation now uses the same document-
  fair admission without changing the public API or the visible fetch reserve;
  session retirement cancels queued preparations and drains active leases.
  The full 397-test GitHub renderer suite and five repeated cross-document
  fetch-order trials passed, as did the x64 Release app and x86/ARM64 optional
  assembly builds. The optional compressed pack is 25,527 bytes against the
  unchanged 128 KiB cap. Scene preparation fairness remains open.
- Done: opt-in display-sized WIC raster decode with EXIF orientation, color
  management, a lowerable output-pixel cap, bounded concurrent preparations,
  bitmap-cache metadata, and paint-only publication when geometry is unchanged.
- Done: directional lazy-layout and image lookahead, off-UI dirty-block image
  reflow, public API baseline, tests including a 1,800-image source storm, x86/
  ARM64 library builds, and x64 JitHub NativeAOT publish.
- Done: indexed adoption of realized hosted-element and code-action plans during
  relayout, removing the quadratic UI-thread match on image-heavy documents.
- Done: code-block highlighting queries measured viewport bands rather than
  scanning every measured block on each progressive scroll; a cached smaller
  raster remains visible until the exact larger bitmap is ready, with an
  independent `UseCachedRasterPreview` opt-out.
- Locally verified, pending current-head CI and benchmark: cached code-block
  highlighting now skips repeat span reconstruction/repaint for an unchanged
  block and reuses the stable text hash already computed while building code
  metadata instead of rehashing the code on every scroll. A completed highlight
  publishes through the visible-band scheduler;
  an oversized, uncacheable result publishes directly to matching blocks in
  that band, without immediately requeuing the same work. The previous
  whole-measured-document completion scan remains only in the non-progressive
  path. This removes avoidable scroll/publication work, but it is not evidence
  that the full 60/120 Hz release gate passes.
- Locally verified, pending current-head CI and benchmark: opt-in code-block
  highlighting now enters a shared document-fair scene-preparation admission
  before invoking the highlighter. The separate lowerable scene concurrency
  ceiling prevents several visible controls from multiplying CPU work; a
  session lease links cancellation to session retirement and is released even
  on provider failure. The non-opt-in control path is unchanged. Fairness,
  cancellation, independent raster admission, and drain tests passed, as did
  all 401 GitHub renderer and 939
  fast managed tests, public API baseline verification, the x64 Release sample
  build, and x86/ARM64 optional-provider builds. This is the admission
  foundation only: Math/Mermaid deferral and a visible-first priority queue
  remain open, and no full benchmark verdict is claimed.
- Locally verified, pending CI: a code-highlight completion rejected after
  image-driven relayout now clears its in-flight key and reschedules the current
  visible band. Cancellation or session retirement before publication also
  clears the key; a replaced provider never retries. The completion decision
  has focused tests, and the x64 Release renderer and sample builds, 944 fast
  managed tests, and 401 GitHub renderer tests pass locally. This fixes a
  stale-work hole, not the still-open Math/Mermaid scheduling or release
  latency gate.
- Done: JitHub tries the credential-free GitHub raw CDN for repository media
  before consuming authenticated Contents API budget, retaining the existing
  private/LFS fallback. The audit captures per-tile native traversal clocks and
  no longer charges a five-second UIA no-op scroll as renderer work.
- Locally verified, pending CI: JitHub now reads a known-length image response
  directly into its final bounded byte array, avoiding MemoryStream growth and
  a second full-size `ToArray` copy. The 83 image-service tests cover the normal
  path and reject both short and excess bodies relative to the declared length;
  all 3,048 JitHub unit tests passed. This is an allocation reduction, not the
  still-open 64 MiB in-flight source-byte admission gate.
- CI at `d4bdd27` found one failure in the unrelated
  `RepoFileCacheService` cancellation test: after proving the per-key lock had
  been released, its follow-up disk write exceeded the test's two-second
  cancellation timer. The paired Get/Put follow-up operations now use a bounded
  ten-second timer; lock-release and cancellation checks remain unchanged.
  All 3,048 Debug x64 tests passed locally. At `3b46860`, the NativeAOT
  contract/unit tests and x86/x64/ARM64 publish, code-viewer tests, and
  product-plan validation passed; the full preview suite and interactive
  benchmark were still running/queued. This is not a release verdict.
- Locally verified, pending PR CI: the progressive source session now ships in
  an explicit optional managed pack, and resvg's inert-image normalization and
  security preflight travel with its optional provider. The Core + WinUI lean
  pair remains below the unchanged 525 KiB compressed and 1.2 MiB managed
  gates (525,642 and 1,249,792 bytes from a fresh local pack); the
  pack retains public XML API docs.
  All 14 shipping packages passed local size/license/native-asset compliance,
  normalized two-pack reproducibility, x64 NativeAOT managed-pack smoke from
  both project references and the newly packed NuGet artifacts,
  x86/ARM64 provider builds, 937 Core tests outside the slow external-gate
  mutation fixture, and the 389 GitHub plus 56 resvg tests. The full Core
  external-gate fixture remains for CI; this is package/architecture evidence,
  not a performance benchmark or release pass.
- The first CI run after the optional-package split found a test-only SVG
  renderer missing the new source-preparation contract. Commit `4a5a373`
  updates that fake. On later head `8d5fb9f`, code-viewer, NativeAOT contract,
  product-plan validation, and x86/x64/ARM64 publish jobs passed; the preview
  package job was still running and the interactive benchmark jobs were queued.
  These earlier-head checks do not establish a release verdict for newer changes.
- Verified: the pinned top-500 live README audit at renderer commit `a200b59`
  passed 500/500 with zero valid image-unavailable cases. Native/Edge p95 ratios
  were 0.491 first render and 0.364 full traversal. Five individual full-page
  ratios exceeded 1.10; their per-tile timelines coincide with delayed
  external Camo/OpenCollective image responses (including a browser-broken Vue
  contributor image). A same-byte replay is still needed to isolate client
  work conclusively. This does not substitute for the same-byte offline
  Edge oracle or the architecture/device release matrix below.
- The subsequent `69c89fc` live audit encountered GitHub's secondary
  `gitmon ... fail-fast:network` rejection while JitHub loaded the
  `NousResearch/hermes-agent` repository tree (rank 19); Edge had rendered
  the page, but native Markdown rendering had not begun. The audit now retries
  only this exact upstream admission failure with bounded delays and reports
  an infrastructure failure if all attempts are rejected. This is not counted
  as a renderer pass or as qualified 500/500 evidence; the full current-head
  audit must pass again.
- The `a376a47` live audit failed rank 348 (`marktext/marktext`) because one
  Shields/Camo SVG badge was unavailable natively. The resolver successfully
  returned 469 SVG bytes; Edge showed that badge, and the same URL later served
  1,303 bytes. A one-case pinned Release replay at `8d5fb9f` passed with zero
  unavailable images. This suggests changing upstream/CDN content, but the
  failing bytes were not captured, so the cause is not proven and no exception
  is granted. The current-head 500-case audit must pass in full.
- The later `bab176b` top-500 run failed 2 of 500 cases. At rank 401
  (`Textualize/rich`), seven SVG assets resolved to nonempty bytes but became
  natively unavailable roughly 16 seconds later; the typed SVG failure was
  not recorded, so a cold-worker deadline is only a hypothesis. At rank 402
  (`twentyhq/twenty`), the native render had zero unavailable images but Edge
  timed out waiting for the GitHub document to become interactive. Separate
  pinned Release one-case replays passed both cases, which does not prove the
  failing conditions are fixed. The Edge audit now makes one bounded fresh
  navigation attempt only after a document-readiness timeout, records the
  retry and full wall time, and excludes first-attempt CPU/layout counters
  from the successful-attempt browser comparison. Renderer-process counter
  resets use the fresh counter rather than subtracting an unrelated old
  process baseline. This does not waive a second timeout or the unresolved
  rank-401 native SVG failure. The full
  current-head 500-case audit must pass again.
- The `d4bdd27` audit exposed another valid SVG failure at rank 153
  (`louislam/uptime-kuma`): Edge loaded a 1,877,124-byte, 1200×8120 sponsor
  SVG and JitHub resolved a payload of the same length, then reported it
  unavailable about seven seconds later. A separate translation badge was
  broken in Edge too (zero natural size) and correctly excluded by the audit.
  A pinned one-case Release replay loaded the sponsor SVG successfully, but
  byte identity and the original native failure category were not recorded.
  The image-unavailable event now carries an optional typed SVG failure
  category into the automation evidence, so the next full audit can identify
  whether a repeat is a timeout, resource limit, unsupported content, or
  worker fault. SVG failure publication also reports unavailability only
  after verifying the image is still current and lacks a retained good bitmap;
  stale/disposed failures previously raised the event before that check.
  This removes a false-unavailable path, but neither this change nor the
  one-case replay establishes the original cause or a 500/500 pass.
- The `3766f88` live audit reproduced the rank-153 failure on the same sponsor
  asset: Edge rendered it, and JitHub resolved 1,877,124 bytes with SHA-256
  `9881971D893267A10A872DB575522AF0C20E0E64F1EF87F6E45CF5272C9E69AC`
  in 132 ms, then reported typed SVG `Timeout`. The isolated worker's 3,000-ms
  `open` deadline expired while reading its response, after progress
  `font-gate-passed`; process CPU was 484 ms, private commit 18,332 KiB, and
  page faults 60,726. A fresh Camo fetch locally produced exactly that hash;
  the SVG contains 576 embedded JPEGs (380 unique), 1,152 text nodes, and 576
  clip paths. This is a native worker-opening failure, not an image-download
  failure or a false-unavailable report. The existing synthetic sponsor test
  passes locally but does not cover this cold hosted behavior. The new focused
  hosted image diagnostic must be run, followed by a real fix and a full
  current-head audit; no waiver or deadline increase is accepted.
- The `51ab849` top-500 audit reported 499/500 passes. Rank 465
  (`xai-org/grok-1`) failed before Markdown rendering: its authenticated
  repository-root API request was rejected by the organization's IP allow
  list, and the anonymous root retry did not recover. Edge rendered the pinned
  README. JitHub now permits a freshly retrieved canonical, nonbinary README
  to populate a clearly incomplete one-file navigation view when that exact
  public-data authorization/quota failure affects only the root listing;
  missing/stale READMEs still fail, and the app does not claim a complete tree.
  The focused navigation/page-view-model tests and all 3,051 app unit tests
  passed, including an end-to-end view-model assertion that the README remains
  visible, the root is not authoritative, and reconciliation does not retry
  the denied listing; a local
  pinned one-case Release replay passed with zero unavailable images, but it
  did not reproduce the CI runner's IP policy. A full current-head 500/500
  audit is still required. The one-case report's styled-viewport SSIM was
  0.050 despite 100% text and 99.90% structure, so it is not proof of visual
  fidelity parity either.
- The later `95519bd` live audit (Actions run `35942389322`) consolidated
  500/500 passes with zero reported unavailable images. Native/Edge p95 ratios
  were 0.486 first render and 0.352 full traversal. This is a live-network
  pass on an earlier renderer head, not the current-head or same-byte release
  gate. Two individual cases still exceed 1.10: rank 60
  (`anthropics/claude-code`) has a 1.57 full-page ratio, and its 11,002,760-
  byte GIF resolved about six seconds after the badges; delivery is a plausible
  contributor, not a proven waiver. Rank 203 (`d2l-ai/d2l-zh`) has 2.10 first-
  render and 1.79 full-page ratios with 4,969 ms first-render CPU, so native
  client work remains a concrete suspect. Preserve both as same-byte replay
  targets; do not infer parity from the aggregate p95.
- Locally verified at `199050d` plus audit instrumentation: the production
  README audit now records privacy-safe `MarkdownPerformanceSession` counters
  at first render and after the last native traversal tile, plus each resolved
  image's start and elapsed resolver time. Its rank-203
  (`d2l-ai/d2l-zh`) focused Release replay passed with zero unavailable images,
  100% text coverage, 99.65% structure, and native/Edge timings of 557/1102 ms
  first render and 588/1926 ms full traversal. At first render, five source
  resolutions had begun and two remained active; cumulative resolver time was
  628 ms across requests. By traversal completion, resolver time was 1500 ms,
  cumulative raster-preparation slot time 150 ms, and scene-preparation slot
  time 1 ms. This was a live-network replay on a different run, not an
  identical-byte proof that the earlier 7.2-second outlier was external. A
  second focused rank-203 run after the image-duration change passed at 524 ms
  first render and 583 ms full traversal; all five image resolutions began
  within about 80 ms of each other and took 247-354 ms each. The same final
  build passed rank 60 (`anthropics/claude-code`) at 456 ms first render and
  488 ms full traversal, versus Edge's 1151/2586 ms. Its 11,002,760-byte GIF
  started alongside the badges and resolved in 801 ms, whereas the earlier
  run had it arriving about six seconds late. This supports transient delivery
  as an explanation but cannot prove it without identical-byte replay. Keep
  both ranks in that set. The diagnostic audit counters are not a substitute
  for the qualified release benchmark.
- The older-head `cd45168` top-500 run (`35947027862`) failed its 201-250
  shard at rank 247 (`666ghj/MiroFish`): the valid 20,087-byte
  `star-history-light.svg` resolved, then the isolated resvg path reported a
  `Timeout` and JitHub showed one unavailable image. The other 49 shard cases
  passed. A focused Release rerun of the same pinned rank on the later local
  build passed with 21/21 image/media observations and no unavailable image,
  but this is not a waiver for the hosted timeout. It may be a cold font/worker
  initialization or a three-second content-render deadline; current audit
  evidence does not distinguish them. The audit now listens for a privacy-safe
  resvg worker deadline event that distinguishes process startup, font
  catalog, open, render, and maintenance, preserving it with the case artifact.
  The 57 Release x64 resvg tests, 3,053 Debug x64 app tests (including an
  event-to-privacy-safe-file contract test), Release app/audit-harness builds,
  and a focused rank-247 audit pass locally. This trace must still be verified
  on a hosted recurrence; the timeout is unresolved and remains a release
  blocker.
- The same older-head audit has another passed-but-slow individual outlier:
  rank 235 (`caddyserver/caddy`) took 24.36 seconds native versus 7.41 seconds
  Edge for full traversal (3.62×), with 19.48 seconds of charged delay before
  the first native tile and another 3.68 seconds before the second. Its
  resolver evidence shows the listed remote assets completed within about
  0.6 seconds, so this cannot be dismissed as slow delivery. A focused local
  replay of the exact CI manifest commit passed in 743 ms full traversal;
  first/final session snapshots showed 11 fetches and 47 ms cumulative CPU
  preparation slot time. The 20-second visible-image wait needs per-image
  and SVG-phase evidence on a recurrence, followed by same-byte replay. Rank
  235 is a live outlier, not a benchmark waiver. The audit now records any
  visible-image wait of at least 500 ms with its tile index and elapsed time;
  on a 20-second deadline it also counts still-loading image peers. This
  probes the UIA tree only once on timeout and charges that diagnostic walk
  to harness overhead. The Release audit harness builds and
  the exact pinned rank-235 focused run passes locally with the new evidence
  field present (zero timeouts); its effectiveness awaits a recurrence.
- The later `44543e1` live audit (`35957188043`) passed ranks 201-250,
  including ranks 235 and 247. Rank 235 completed full native traversal in
  1.23 seconds (native/Edge ratio 0.188) with no visible-image wait at or above
  500 ms; rank 247 completed in 1.20 seconds (ratio 0.353) with all 21 images
  observed, zero unavailable, and no recorded worker timeout. The 50-case
  shard passed, but this is another live-network sample on an earlier head,
  not same-byte proof or a waiver for the earlier 20-second wait and SVG
  timeout. The full audit and current-head run remain pending.
- The same `44543e1` full audit finished at 499/500: rank 465
  (`xai-org/grok-1`) failed during the first native capture when Win2D
  rejected a code-block line-number `DrawText` call with `E_INVALIDARG`.
  An exact-manifest local replay with the hosted Edge evidence passed with
  100% text coverage, 99.90% structure, and zero unavailable content, so the
  hosted failure is intermittent or machine-specific, not cleared. The code
  path now preserves that failure while adding privacy-safe line, rectangle,
  viewport, and font details to the exception on recurrence. The subsequent
  current-head audit (`35958672728`) is running; neither a local replay nor
  diagnostic instrumentation counts as a 500/500 pass.
- That same shard confirms rank 203 (`d2l-ai/d2l-zh`) remains a native-work
  outlier: first native render took 5.25 seconds (1.65 times Edge), full
  traversal 5.27 seconds (1.30 times Edge), and first-render process CPU was
  4.63 seconds. All five source requests resolved in 28-116 ms, with only
  357 ms aggregate resolver time. At first render two raster CPU preparations
  were active; by traversal completion their cumulative admission time was
  6.42 seconds. Network delivery cannot explain this run's delay. Per-raster
  decode/upload phase evidence and a same-byte replay are required before a
  performance fix can be attributed or this outlier cleared.
- Preview validation at `65c640f` passed its x86/x64/ARM64 NativeAOT publish
  checks but failed the strict package-compliance gate: `System.Memory 4.6.0`
  is declared by a shipping package for a legacy target framework and its
  archive was not restored by the net10-only project graph, leaving the
  transitive license as `NOASSERTION`. A separate, nonshipping exact-version
  restore project and checked-in lock now hydrate that package before the
  unchanged fail-on-unknown gate. The local locked restore and strict notice
  generation pass; hosted validation of this fix is pending.
- The `65c640f` top-500 audit (`35958672728`) finished at 495/500, with
  five failures. Its aggregate native/Edge p95 ratios were 0.467 for first
  render and 0.399 for full traversal, but those statistics cannot waive
  failed cases or individual native stalls. Rank 153
  (`louislam/uptime-kuma`) resolved the
  1,877,124-byte sponsor SVG but the isolated worker exceeded the unchanged
  three-second **open** deadline; a separate badge resolved to no bytes and
  is browser-broken. Rank 189 (`D4Vinci/Scrapling`) reported an unavailable
  Asciinema/Camo image after a 20-second visible-image wait; no matching
  image-resolution event was recorded, so that source-delivery path still
  needs diagnosis. Neither case is cleared. The worker now reuses the XML
  document parsed by its authoritative security walk for unthemed content,
  avoiding a second XML parse and source copy. A deterministic offline
  sponsor-shaped fixture with 576 image elements, 380 unique embedded PNGs,
  and roughly 1.8 MB of source passes, as do all 58 x64 Release
  provider tests and 16 native Rust tests. An exact live sponsor-byte local
  diagnostic measured about 0.55 seconds to open on both old and new workers;
  it does **not** establish a hosted deadline fix. The pinned x86/x64/ARM64
  worker rebuilds match their staged binaries, and a local all-RID package
  is 4,462,166 compressed bytes. These are unsigned development workers;
  production signing remains a separate release gate. Hosted recurrence and
  the rank-189 delivery trace are required before either failure can be
  resolved.
- That same in-progress `65c640f` audit also failed rank 348
  (`marktext/marktext`): a Camo badge resolved to 469 SVG bytes and received
  an `UnsupportedContent` rejection, while a later retrieval of the same
  Camo URL returned a different 1,303-byte SVG. The rejected exact bytes
  were not retained, so this remains an upstream-content hypothesis, not a
  waiver. Rank 368 (`spring-projects/spring-framework`) rendered with zero
  unavailable images and no recorded render failure but the app did not
  close cleanly after the case; the audit artifact has no exit classification.
  Both failures remain open. The ranks 451-500 shard also failed rank 465
  (`xai-org/grok-1`) with the same Win2D line-number `E_INVALIDARG` as the
  preceding hosted audit. That `65c640f` run predates the new geometry
  diagnostic; the latest head must capture it. A local replay still passes,
  so this is reproducible on the hosted runner but not yet explained.
  Rank 203 completed quickly in this run (0.205 first-render and 0.184
  full-page native/Edge ratios), which shows the earlier CPU outlier is
  variable, not disproven. Rank 74 (`farion1231/cc-switch`) exceeded Edge's
  full-page time by 2.28 times and needs a same-byte stage trace.
- Pending hosted verification: the rank-189 Camo URL encodes a valid HTTPS
  Asciinema origin. JitHub previously awaited as many as three 20-second Camo
  transport attempts before trying that origin, which can outlast the audit's
  visible-image window. Canonical Camo images now start the origin under its
  untrusted third-party policy after a 750 ms hedge, retain a fast Camo result
  without an origin request, accept either successful representation, and
  cancel/await the losing waiter without canceling other shared callers.
  The six focused fallback tests, all 3,057 x64 Release app tests, and the
  x64 Release app build pass locally. The earlier completed audit predates the
  hedge. The `186f6ef` ranks 151-200 shard has since passed 50/50. Rank 189
  (`D4Vinci/Scrapling`) resolved 42 distinct image/media sources, reported
  zero unavailable or still-loading images and no render failure, and closed
  cleanly. Its native/Edge first/full ratios were 0.464/0.300. This verifies
  the problematic page in one hosted recurrence, not the complete 500-case
  verdict or a same-byte client-rendering comparison.
- The older-head `398e132` top-500 run (`35975382655`) finished at 499/500
  with first-render/full-page native/Edge p95 ratios of 0.504/0.478. Only
  rank 54 failed; ranks 74, 153, 189, 203, 235, 247, 348, 368, and 465
  passed in this particular live-network run, which does not erase their
  earlier failure or same-byte performance evidence. Rank 54
  (`langgenius/dify`): 17 valid Camo SVGs resolved to nonempty bytes, but one
  worker **open** exceeded the unchanged three-second deadline and 16 others
  reported `WorkerFailure` immediately afterward. This is a failure cascade,
  not 17 independent invalid images. Windows documents that `TerminateProcess`
  [returns before termination completes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-terminateprocess);
  the worker pool previously launched replacements immediately against its
  one/two-process Job Object limit. Exceptional worker disposal now waits
  boundedly for the terminated process before the slot is reused, including
  startup cleanup. All 59 x64 Release provider tests pass, including a
  repeated one-process-job restart test. The job-limit race is a plausible
  cause, not proven from the existing audit (which recorded only typed image
  failures); a full hosted recurrence must establish whether the cascade is
  gone. The initiating three-second SVG open timeout also remains a separate
  release blocker even if unrelated queued images recover.
  The x86 and ARM64 Release provider builds also pass locally; architecture
  runtime and fault-injection evidence remain outstanding.
  On the latest-head `186f6ef` run (`35985150159`), ranks 51-100 passed
  50/50. Rank 54 resolved 36 image assets, reported zero unavailable images
  and no worker timeout, and rendered with 100% text/99.01% structure. This
  confirms that shard passed under CI, but does not isolate the restart change
  as the cause or replace the still-running full 500-case verdict and forced
  timeout/fault-injection tests. In the same run, the ranks 101-150 hosted
  runner lost communication with GitHub after approximately 54 minutes,
  before the always-on evidence upload could execute. GitHub's check
  annotation cites runner termination, CPU/memory starvation, or network
  isolation as possible causes; without job logs or case evidence this cannot
  be classified as a renderer pass or a renderer defect. The next workflow
  divides the same mandatory 500 ranks into twenty 25-case shards while
  retaining four-way parallelism and exact-rank consolidation, reducing
  per-runner lifetime and narrowing any future lost-runner interval. A
  contract test checks contiguous, nonoverlapping 1-500 coverage and that
  the command count derives from each matrix range. The `186f6ef` run later
  consolidated 450/500 reported cases, all passing, with live first/full
  p95 ratios 0.457/0.357; the 50 missing ranks were precisely 101-150. Its
  failed runner produced neither job logs nor case artifacts, so it is not a
  500/500 verdict. A successful full rerun and investigation of any
  recurrence are required.
- The first 25-case audit at `a98f9bd` (`35995469652`) has a concrete
  renderer recurrence at rank 153 (`louislam/uptime-kuma`): one valid
  1,877,124-byte, 1200×8120 sponsor SVG resolved in 132 ms, but the isolated
  worker's **open** request exceeded the unchanged three-second deadline and
  the image became unavailable. A separate Weblate badge had no resolved
  asset; the audit's browser comparison excluded that broken reference. The
  sponsor failure is not a transport or image-resolution delay. A live local
  fetch returned the same byte length; after host sanitization, diagnostic
  timings on this workstation were about 2 ms XML parse, 7 ms security
  inspection, 68 ms font initialization, and 220 ms tree conversion. These
  measurements do not explain or waive the hosted timeout. The deterministic
  sponsor-shaped provider fixture now includes 576 embedded images, 1,152
  text nodes, and 576 clipping rectangles rather than images alone; all 59
  x64 Release provider tests pass locally under the existing hard deadline.
  The hosted recurrence and a stage-level worker/CPU trace remain necessary
  before considering the valid-content timeout fixed.
  That completed audit reported all 500 ranks: 499 passed and rank 153 failed.
  Its live native/Edge first-render/full-page p95 ratios were 0.440/0.366;
  these page-level measurements neither waive the missing image nor establish
  the mandatory same-byte Edge parity or qualified release benchmark. The
  `9a94a7a` full rerun (`36007051224`) subsequently reported all 500
  ranks: 499 passed, with only rank 101 failing; rank 153 passed with no
  unavailable image. Its live first/full native-to-Edge p95 ratios were
  0.453/0.377. The prior sponsor timeout remains intermittent and unresolved,
  and these live ratios still do not establish same-byte parity.
- That `9a94a7a` rerun's ranks 101-125 shard reported 24/25 passing. Rank
  101 (`immich-app/immich`) had two small, valid Camo SVG badges resolve to
  622 and 2,561 bytes in about 61 and 367 ms respectively, then both became
  unavailable after separate **font-catalog** initialization timeouts at the
  unchanged 15-second deadline. This is neither image transport nor the
  three-second content-open timeout. The affected full-page native/Edge ratio
  was 1.252. The audit-only worker CPU evidence landed at `00f5c68`, after
  this run; the next current-head recurrence must distinguish CPU-heavy font
  enumeration/warmup from off-CPU stalling before changing the font path or
  its deadline. The 750 ms responsive cold-text target remains open.
- Worker deadline evidence now includes the isolated worker process's CPU
  milliseconds during each timed-out transaction, with `-1` when Windows
  cannot provide a reading. CPU sampling is enabled only when an audit event
  listener is attached, so normal SVG requests gain no process query. The
  timeout sample is taken before worker termination and logged alongside phase
  and unchanged deadline, without source bytes or URL.
  It distinguishes a CPU-bound recurrence from time spent off-CPU but is
  diagnostic only: it does not relax the three-second ceiling or fix the
  sponsor SVG. The x64 Release provider suite (59 tests) and Debug x64 app
  suite (3,057 tests) pass locally. One duplicate hosted NativeAOT run at
  `9a94a7a` failed the Camo hedge test on a callback-completion assertion
  while the other run passed; the test now inspects the canceled token itself
  after the pipeline drains the loser, removing the separate callback signal
  from that assertion. A hosted recurrence is still required.
- CI at `a98f9bd` exposed a CRLF-sensitive workflow-matrix contract test;
  `44f9afe` validates both LF and CRLF while preserving the exact 1-500
  coverage assertion. The local 3,057-test Debug x64 suite and one hosted
  code-viewer and NativeAOT run pass with that change. A duplicate hosted
  NativeAOT run then timed out in the Camo hedge test's two-second
  post-result wait. The pipeline already cancels and drains its losing
  request before returning; the test now asserts that completed-cancellation
  invariant directly, without an unrelated wall-clock timer. The exact
  3,057-test Debug x64 suite passes locally with coverage. Hosted recurrence
  is still needed.
- The `3cab771` preview CI reached all 1,419 Core tests, then failed three
  `SharedCanvasTextFormatCacheTests`: parallel tests changed the process-wide
  cache between absolute-count assertions, while DirectWrite returned
  case-normalized locale tags (`en-us` rather than `en-US`) on that runner.
  The cache tests now run in an isolated xUnit collection and compare locale
  tags case-insensitively. Cache keys also treat equivalent locale casing as
  one descriptor without allocating a lowercase string on every hot-path
  lookup. The complete 401-test Release x64 GitHub suite passed four local
  runs after this change; Preview Validation passed on `89d1d08`.
- The completed `3cab771` top-500 audit (`36019774373`) reported 499/500
  passes. Rank 395 (`h5bp/html5-boilerplate`) rendered with 100% text token
  coverage, 99.29% structure, all four images observed, zero unavailable
  images, and no renderer exception, but its app process did not close cleanly.
  The old audit only recorded a boolean, so it cannot distinguish a 12-second
  shutdown timeout from a nonzero launcher exit. The audit now records a
  privacy-safe close classification, verifies the actual app process exit
  code as well as the launcher, and retains the strict failed-case verdict.
  An audit-only, atomic last-stage signal now distinguishes dialog dismissal,
  background/diagnostics drain, performance-session retirement, engine/GPU/SVG
  disposal, the final window close, and process-exit cleanup; the production
  path does no stage I/O. The Release x64 app and audit-harness builds and 37
  focused automation/bridge tests pass locally. A hosted recurrence is needed
  to identify and fix the
  shutdown cause; local live replay requires the read-only audit token and
  account partition, which are not configured on this machine. The subsequent
  `c42f60f` audit below predates this added classification.
- The `c42f60f` live audit (`36031465708`) finished at 499/500; rank 395
  closed cleanly, but rank 348 (`marktext/marktext`) again marked one valid
  browser-rendered Windows-download badge unavailable. JitHub resolved 469
  bytes from its Camo URL in 20 ms, then SVG host preflight classified the
  content as `UnsupportedContent`; Edge displayed a 124×20 badge, and a fresh
  Camo/origin fetch returned a different 1,303-byte SVG. The original 469
  bytes were not retained, so neither malformed upstream content nor an
  overly strict renderer rule is established. An audit-only preflight event
  now records the precise policy reason, received byte length, and SHA-256
  identity without URLs or source bytes. The existing unavailable-image gate
  remains strict. All 60 Release x64 resvg provider tests, 38 focused app
  tests, and Release x64 app/audit-harness builds pass locally; the hosted
  current-head recurrence is needed to diagnose and fix this case. Preview
  Validation passed on `89d1d08`, confirming the locale-cache test fix under
  hosted CI, but no full performance release verdict follows from it.
- The subsequent `89d1d08` top-500 run (`36037412566`) exposed an audit-harness
  defect, not a renderer verdict: the first 25-case shard failed 25/25 after
  rendering because `Process.ExitCode` throws for the PID-attached JitHub
  process. The shutdown check now retains a native process handle before
  requesting window close, reads its actual exit code through
  `GetExitCodeProcess`, and closes the handle on every path. This preserves the
  strict nonzero-exit and timeout gates. The Release x64 automation harness
  builds and all 35 focused source-contract tests pass locally. A new hosted
  500-case run is required; the failed run provides no valid renderer or
  performance pass count.
- The corrected `c9dc3c3` top-500 run (`36057504209`, first attempt) reached
  499/500 passes. Rank 348's badge passed on this run, but that does not explain
  its previous 469-byte response. Rank 371
  (`CorentinJ/Real-Time-Voice-Cloning`) had complete text/structure, its one
  image rendered, and zero unavailable images; the app then exited with
  `0xC000027B` during SVG-renderer shutdown, with the last audit stage at
  `svg-renderer-disposal-started`. Twenty exact pinned-README local native
  replays closed cleanly, so this intermittent hosted crash is not yet fixed.
  The audit now retains the app's existing exception logs whenever shutdown is
  unclean, not only when the harness itself throws; the strict clean-exit gate
  remains. A failed-shard rerun of the original commit later completed. The
  earlier Preview Validation separately failed one cancellation-retirement
  ordering test because its arbitrary two-second callback-scheduling timeout
  elapsed under CI load. That non-performance timeout is now ten seconds; the
  product performance deadlines are unchanged. Twenty focused Release x64 test
  iterations, the automation-harness build, and 35 harness contract tests pass
  locally. Preview Validation passed on `e817da9`. The failed-shard rerun
  completed cleanly and the consolidated original-commit
  evidence reports 500/500 with zero unavailable assets, first-render ratio
  p95 0.435, and full-page ratio p95 0.333. This combines attempts rather than
  proving a crash fix; the rank-371 stowed exception remains unexplained. The
  audit also has two individual ratios above 1.10: rank 102
  (`jaywcjlove/awesome-mac`, full-page 1.525) and rank 395
  (`scutan90/DeepLearning-500-questions`, first/full 2.664/1.732). The rank-395
  hosted case had no image sources or scene preparations and spent 5.06 seconds
  of process CPU before its 10.35-second first render, so an image-network
  explanation does not apply. Four local Release replays of its identical
  pinned README SHA with the original Edge evidence passed at 443–530 ms first
  render and 821–940 ms full traversal. This exposes large environment/run
  variance, not a proven fix or a waiver for the hosted outlier. Rank 102's
  full-page outlier includes a 20-second visible-image wait for one
  OpenCollective contributor SVG: both native fetch attempts returned no bytes
  after about 31 seconds each, and the browser evidence classifies the asset
  as broken as well. Its other recorded image responses completed below
  500 ms. This identifies an upstream-delivery component in that live run,
  but still requires the offline same-byte replay to isolate client work.
  A current-head audit, full same-byte corpus, and qualified release benchmark
  are still required.
- The `e817da9` audit's rank-181 case (`macrozheng/mall`) reported two valid
  browser-rendered Chinese-text SVG badges unavailable after separate worker
  `open` transactions hit the immutable three-second content deadline. Worker
  CPU during those transactions was only 0 and 15 ms; the source fetches
  succeeded. The worker's independent cold text initialization primed Latin,
  Arabic, and emoji fallback, but not the CJK family orders authored by these
  badges. The warm-up now primes those two CJK fallback orders under the
  separate initialization deadline without relaxing the per-content deadline.
  All three pinned worker architectures rebuilt; 16 Rust and 61 Release x64
  provider tests pass, as do x86/ARM64 managed builds and three exact pinned
  rank-181 Release replays with zero unavailable images. The three-RID
  development package is 4,463,298 compressed bytes, below the unchanged
  5 MiB cap; workers remain unsigned development artifacts, not a production
  signing pass. Because the hosted timeout was intermittent and did not occur
  on a pre-change local replay, this is a targeted mitigation, not a proven
  fix. A new hosted 500/500 audit and cold/contended text SVG evidence are
  required.
- The subsequent `8dfdc90` current-renderer-head live audit (Actions run
  `36080015819`) completed 500/500 with zero failed cases and zero valid
  unavailable images. Its consolidated native/Edge first-render ratio was
  p50 0.246, p95 0.468; full-page ratio p50 0.199, p95 0.350. Rank 181's
  Chinese-text SVG badges and the earlier rank-371 shutdown case both passed
  this full run. Rank 102 (`jaywcjlove/awesome-mac`) still had a 1.432
  full-page ratio, the only individual ratio above 1.10. Its case artifact
  records a 20.01-second visible-image wait and two roughly 31.5-second
  fetches returning no bytes for the same Camo/OpenCollective contributor SVG;
  the Edge capture marks that exact URL complete with zero natural dimensions
  (broken there too). The other recorded native image responses were below
  325 ms. This attributes the observed live outlier to an upstream asset
  failure, not measured native decode/layout work, but it is not a same-byte
  client-rendering comparison or an exception to the image-availability gate.
  This audit also does not substitute for the deterministic Edge corpus,
  qualified counterbalanced benchmark, or architecture/device release matrix.
- Measured: the full x64 repeated-construction release benchmark is not yet
  qualified. Both reference and candidate failed its stationarity contract;
  the candidate's full run dropped to about 121 observed Hz on a configured
  240 Hz display and exceeded the 1 MiB first-viewport budget. A controlled
  12,000-frame scroll run with fewer first-viewport constructions sustained
  about 240 Hz at 4.46 ms frame p95, while a 20-presentation diagnostic
  allocated about 28-35 MiB per 1 MiB presentation. Reduce construction and
  native-object/GC pressure, then rerun the frozen full counterbalanced gate.
- A new frozen schema-10 R1,C1,C2,R2 attempt on `8dfdc90` stopped after the
  first reference leg, as required by the unmodified gate. The Release x64
  reference harness produced a complete R1 report but failed its own absolute
  qualification: the 1 MiB mixed-README first-viewport p95 was 413.58 ms
  uncached and 350.87 ms cached against 250 ms budgets, and five of six
  first-viewport conditions failed stationarity. Its 12,000-frame scroll had
  only 32.20 observed Hz on a configured 240 Hz display (33.03 ms frame p95
  versus 8.33 ms), while measured renderer UI work was 0.26 ms p95, Energy
  Saver was off, and effective power mode was MaxPerformance. The reference
  report and fail-closed partial verdict are under the local temporary
  `jithub-perf-schema10-20260925-0148` run directory. No candidate leg or
  counterbalanced verdict exists; this is a failed release gate, not an
  improvement or parity result. The 32 Hz presentation bottleneck and the
  high/unstable construction cost must be isolated before another qualified
  full run; do not change the frame, stationarity, or absolute budgets to
  accommodate this attempt. A separate, explicitly non-gating 120-frame
  diagnostic on both frozen executables reproduced the presentation limit:
  reference and candidate observed 32.09 and 32.07 Hz with 32.65 and
  32.48 ms frame p95, respectively, while renderer UI work remained 0.30 and
  0.27 ms p95. The console session was active and Windows reported a 239 Hz
  NVIDIA display. A separate in-flight foreground-window probe found Edge,
  not the harness, in the foreground while the harness was running. That is
  a plausible explanation for throttled presentation, not proof of causation;
  the source of this roughly 30 Hz cadence remains unverified. A qualified
  rerun needs a dedicated, unattended interactive desktop where the harness
  stays visible and foreground on a measured 120 Hz-or-faster display. These
  shortened runs are not release evidence.
- Locally verified, pending current-head CI and a qualified benchmark:
  ordinary document construction and relayout no longer create the collapsed
  selection Image, drag-shield Border, or two native Button handles and glyphs.
  They are allocated only when selection, hover/focus, or dragging needs them.
  The x64 Release sample build and all 11 scripted touch-selection checks pass,
  including handle creation, drag, RTL relayout, unload, native task controls,
  and ancestor-owned viewports; a mouse-drag selection also rendered correctly.
  The 938 fast managed and 398 GitHub tests pass; the slow external-gate
  mutation fixture is still running separately. The sample gives the renderer
  the TextBox-normalized source used by its task command; previously CRLF-to-CR
  normalization shifted task source ranges and disabled the checkbox. The
  test explicitly asserts the editable checkbox is enabled before tapping it.
  This avoids native control construction on the common path. A later
  20-presentation diagnostic at `21e96fd` still allocated about 36.8 MiB per
  uncached 1 MiB presentation and 29.4 MiB with a parse-cache hit. Its timing
  failed stationarity while the external-gate mutation suite also ran on the
  machine; neither its timing nor the difference from an older diagnostic is
  qualified release evidence. The construction/layout bottleneck remains open.
- Locally verified, pending current-head CI and a qualified benchmark:
  measured image plans now use the existing subscription identity
  set for constant-time deduplication and a reusable vertical viewport index.
  Scroll-time source activation visits only nearby images rather than every
  measured image, including after image-driven relayout. The aggregate UIA
  visible-loading-image status now queries that same index rather than scanning
  all measured images on each accessibility or audit poll (which can occur every
  10 ms). This targets the 1,800-image storm and long fully traversed READMEs.
  The x64 Release renderer build, 939 fast managed tests, and 401 GitHub
  renderer tests, including the indexed 1,800-image reflow case, passed locally
  after the UIA change. The earlier x64 Release sample build and all 11 scripted
  touch/hosted-control UI checks passed for the source-activation index, and one
  High Contrast screenshot was inspected. The UIA change still needs hosted
  verification. None of this establishes the first-viewport, frame, or memory
  release gates by itself.
- Locally verified, pending current-head CI and a qualified benchmark:
  semantic-tree depth-first traversal now uses one iterative ancestor path
  instead of a recursive iterator per node. It preserves preorder, handles
  20,000 nested nodes without stack overflow, and allocates under 32 KiB
  while traversing 20,000 wide siblings in a focused test (3/3 pass). The
  939 fast managed and 96 pixel tests pass on x64 Release. In
  identical reduced 20-presentation local diagnostics, median managed
  allocation per 1 MiB presentation changed from 35.04 to 33.76 MiB without
  a parse-cache hit and 26.90 to 25.61 MiB with a hit. Both runs failed
  timing stationarity, so their latency differences are not release evidence;
  the full counterbalanced release protocol remains required.
- CI at `95519bd` found an account-cancellation drain race in JitHub's
  `ApplicationTaskCoordinator`: a completed operation was removed from the
  tracked set just before its returned task was signaled, allowing the
  cancellation snapshot to observe no work and return early. Snapshotting and
  that removal/completion transition now share one lock. All 14 coordinator
  tests and all 3,051 Debug x64 app tests pass locally. At `a95189a`, hosted
  code-viewer CI then exposed a separate test synchronization bug: two stale
  root-listing tests observed `Tree.RootReconciliationTask`, which is already
  complete when the page owns the network refresh. They now assert that the
  page's `TreeRefreshTask` is pending and await that task before checking the
  authoritative result. The affected tests passed 20 consecutive local runs,
  and the exact coverage-enabled 3,051-test CI command passed locally after
  the test change. The hosted code-viewer job passed at `cd45168`, confirming
  both fixes under CI. This is CI correctness work, not a performance-gate
  pass; the preview, live benchmark, and current-head top-500 jobs were still
  running or queued at this checkpoint.
- Open: qualifying actual source-byte in-flight admission under concurrent
  memory storms and live workloads. The renderer session now passes a weighted
  per-source admission to JitHub's HTTP/authenticated fetches, but the full
  memory and no-false-unavailable gate has not been measured. Global scene
  queue fairness across documents and copy-on-write layout publication with a
  measured ≤2 ms UI commit also remain open.
- Completed foundation, now wired through the production resolver below: a weighted, document-fair
  source-byte admission primitive now enforces a total byte ceiling, a
  speculative sub-ceiling that reserves visible capacity, strict queued
  visible priority, cancellation-safe grants, and idempotent leases. Six
  focused Release x64 tests pass, including a cancellation/grant race; the
  complete 407-test Release x64 GitHub renderer suite and x86/ARM64 optional
  package builds passed with zero warnings, and the local optional compressed
  pack was 28,515 bytes under its unchanged 128 KiB cap. Those foundation
  tests did not measure the production HTTP/authenticated path or peak memory;
  the subsequent integration and its remaining gates are tracked below.
- In progress, pending full validation: the optional
  resolver contract can now receive a per-request weighted byte admission from
  the progressive session. Its immutable options lower (but cannot raise) the
  architecture-specific 64/32 MiB total ceiling and reserve a visible slice;
  admissions are tied to document fairness and session cancellation. Focused
  tests cover visible/speculative limits, ordinary-resolver compatibility,
  cross-document visible supersession of active speculative reads, typed
  deferral when a source exceeds the speculative ceiling, and disposal while a
  byte request is queued. JitHub now carries leases through HTTP buffer
  reads and cache storage, keeps admitted visible/speculative flights separate,
  uses bounded memory or delete-on-close spill for unknown-length bodies, and
  reserves the authenticated image ceiling before Contents/Blob materialization.
  The authenticated LFS pointer releases its lease before the recursive media
  fetch. An admitted stale-cache revalidation currently completes within the
  tracked resolution instead of spawning a detached task that could outlive
  account/session retirement; its latency needs the release benchmark, and a
  future session-owned refresh scope may restore stale-while-revalidate without
  losing byte accounting. A privacy-safe current/peak/pending admission snapshot
  is exposed to the README audit. Focused known/unknown-length HTTP tests, a
  synthetic 1,800-source admitted storm, 412 renderer and 3,066 app tests,
  public API baseline verification, and x86/x64/ARM64 Release app builds pass
  locally. Actual HTTP/authenticated peak-memory storms, account isolation,
  live README and no-false-unavailable checks remain. The authenticated client
  still materializes its API response before
  the local decoded-size check, so additional profiling is needed to establish
  the real peak including its Base64/JSON overhead. This is not yet an
  in-flight source-memory gate pass.
- Follow-up: admitted HTTP reads now reserve source bytes before calling
  `HttpContent.ReadAsStreamAsync` for both declared and chunked lengths. A
  custom content regression test asserts this ordering, including an
  implementation that materializes its stream at that call; the 3,068-test
  Release x64 app suite passes. This closes one pre-admission allocation gap,
  not the real concurrent HTTP/authenticated peak-memory or no-false-unavailable
  release gates.
- Follow-up, local validation only: concurrent app-service tests now force
  eight distinct declared-length 1 MiB images through a four-image admission
  and two chunked 1.2 MiB images through the scratch-to-final spill path.
  Every image resolves, the tracked active reservations return to zero, and
  neither test exceeds its 4 MiB test admission. All 3,070 Release x64 app
  tests pass. These injected-handler tests exercise fetch, admission, and
  cache storage together but do not measure process peak memory, authenticated
  API materialization, live network variability, or the full release storm.
- Earlier-head live evidence: the `fb121aa` top-500 audit (Actions run
  `36104116630`) completed 500/500 with zero failed cases. Its consolidated
  native/Edge p95 ratios were 0.447 first render and 0.368 full traversal;
  rank 102 `jaywcjlove/awesome-mac` still had a 1.536 individual full-page
  ratio, so same-byte replay remains necessary. The audit reader was silently
  dropping the three new source-byte counters even though the app emitted
  them. It now deserializes them as required evidence and rejects negative,
  internally inconsistent, or over-64-MiB source/cache byte snapshots. Nine
  focused contract tests and the 3,079-test Release x64 app suite pass
  locally, as does the zero-warning Release x64 audit-harness build. This
  enforces a reported admission ceiling in future audits, not real process
  peak-memory or current-head top-500 completion.
- Local validation, pending current-head CI and live memory measurement:
  admitted JitHub image resolutions now reserve source bytes before a cold
  disk-cache hit materializes its payload, including cache-only policy paths
  and the 304 revalidation read. The concrete cache store rechecks its
  immutable payload generation and exact file length after waiting for the
  weighted grant; it never holds a cache stripe gate while waiting. A changed
  generation is retried without treating a valid cache entry as unavailable.
  Tests cover cold hits, speculative deferral followed by visible success,
  cache-only reads, generation replacement, and cancellation while a writer
  uses the same gate. The 3,084-test Release x64 app suite and zero-warning
  x64 Release app build pass locally. Warm in-memory hits reuse the existing
  bounded source cache without a new source allocation. These tests close a
  disk-read admission hole, but they do not establish actual process peak
  memory, authenticated API materialization cost, or the full live storm gate.
- The first 25-case shard completed on `7da5360` (ranks 76–100, 25/25
  passing), but every reported peak in-flight source-byte counter was zero
  despite nonzero image fetches. This is an instrumentation-and-behavior gap,
  not proof of zero source allocation: the audit and lifecycle resolver
  wrappers exposed only `IMarkdownImageResolver`, hiding the production
  resolver's source-byte-admitted capability from the progressive session.
  Both wrappers now forward admitted resolution to the inner capability,
  preserving fixture and legacy fallbacks. Three focused wrapper-chain tests,
  all 3,087 Release x64 app tests, and a zero-warning Release x64 app build
  pass locally. A new full live audit must confirm nonzero admission where
  bytes are actually materialized and retain the strict 500/500 outcome;
  earlier zero-valued counters cannot qualify the memory gate.
- The `7da5360` top-500 run (`36122469834`) has exposed two real
  no-false-unavailable failures, so it cannot qualify even if its remaining
  shards pass. Rank 154 (`infiniflow/ragflow`) resolved a valid 1,012-byte
  Shields SVG but its isolated worker timed out in `open` after the 3-second
  content deadline with 0 ms measured worker CPU; Edge displayed all 25
  images. Rank 301 (`shanraisshan/claude-code-best-practice`) resolved its
  assets, then three successive isolated workers timed out during
  `font-catalog` initialization at 15 seconds (15/31/0 ms worker CPU),
  disabling the provider and leaving 335 of 341 SVG images unavailable;
  Edge displayed all 341. These are SVG-worker liveness failures, not image
  download failures or valid content rejections. The worker currently blocks
  on Windows system-font enumeration and its text warmup before replying to
  `HELLO`, and `WarmUpAsync` starts only the process, not that font phase.
  The same run also failed rank 367 (`remotion-dev/remotion`) because the Edge
  oracle received `net::ERR_NO_BUFFER_SPACE`; that is separately recorded as
  an oracle/network failure, not a native-renderer pass or a waiver for ranks
  154 and 301. The worker protocol has since been changed to use a
  nonblocking `Hello` probe while catalog loading and text warmup run on its
  background thread. A responsive worker now survives a font-readiness
  deadline instead of restarting the entire cold scan, so a late-completing
  catalog can be used by later requests. Three initialization deadlines in
  60 seconds still trip the required temporary provider circuit breaker.
  A text-readiness wait still holds its worker scheduler lease, so unrelated
  queued non-text SVGs are not yet fully isolated from a slow catalog on
  one-worker systems. The immutable 3-second content deadline and 15-second
  font initialization deadline remain intact,
  and timed-out content is not replayed. This is a liveness mitigation, not
  yet a no-false-unavailable or latency-gate pass: the off-CPU stall still
  needs diagnosis and a full live rerun. The `62675d6` full audit
  (`36127727158`) has now passed 500/500 with zero unavailable images that
  Edge rendered, and native/Edge p95 ratios of 0.430 first render and 0.407
  full traversal. It is the first complete run with the fixed source-byte
  admission instrumentation, but predates this worker mitigation. Its six
  individual ratios above 1.10 (ranks 102, 179, 399, 400, 451, and 478)
  still require same-byte replay; the aggregate p95 must not hide them.
  Rank 399 (`jellyfin/jellyfin`) had a 3.03 full-page ratio and two external
  translation/Camo SVG resolutions lasting 35.2 and 39.5 seconds; Edge also
  failed to render those two translation badges. Rank 400
  (`twentyhq/twenty`) had a 1.36 full-page ratio while two small relative
  repository SVGs took 4.5 and 5.2 seconds to resolve through the raw CDN.
  Rank 102 (`jaywcjlove/awesome-mac`) waited 20 seconds at the bottom on one
  still-loading image, and rank 451 (`lllyasviel/Fooocus`) waited 10.3 seconds
  on its second tile even though its seven source resolutions each finished
  within 202 ms. Those delays need decode/worker/publication stage traces;
  source fetch alone does not explain rank 451. Ranks 179
  (`Panniantong/Agent-Reach`) and 478 (`justjavac/wechat-miniapp-radar`)
  took 7.8 and 6.0 seconds before first render with only 1.5 seconds of
  aggregate image fetches and no images, respectively. Both contain CJK text,
  but font/layout attribution is only a hypothesis pending a same-byte
  controlled replay. These traces do not prove native client work meets the
  same-byte gate. The `d6e9e2f` full audit (`36145602432`) finished with two
  failing cases out of 500, so it does not qualify.
  Rank 348 (`marktext/marktext`) received a 469-byte Camo payload labeled
  `image/svg+xml`; host preflight identified invalid XML while Edge showed
  the 124×20 badge. The same URL subsequently served a complete 1,303-byte
  SVG. JitHub now validates fetched SVG XML before caching and retries only
  malformed GitHub Camo payloads under the existing three-attempt GET limit.
  The malformed body's source-byte lease is released before each retry; a
  persistent malformed response fails without entering the cache. The early
  XML check is limited to 64-KiB badge-sized payloads so large SVGs are not
  fully parsed twice; their authoritative security check remains in the
  isolated renderer. All 105
  focused image-service tests, the 3,088-test Release x64 app suite outside
  the slow mutation fixture, and the zero-warning Release x64 app build pass
  locally. A pinned rank-348 replay using the failing run's saved Edge oracle
  passed with zero unavailable images and its Windows badge visible, but the
  live CDN served complete bytes during replay; only the injected test proves
  recovery from a malformed first response. Rank 398
  (`zylon-ai/private-gpt`) failed in native audit capture because Win2D
  rejected a finite 24.56×17.29-DIP code line-number `DrawText` rectangle
  with `E_INVALIDARG`. Ten local pinned replays passed, so its intermittent
  cause is unresolved; do not mask it as a browser/network failure or count
  those replays as a fix. Code line-number painting now retries only this
  finite-geometry `E_INVALIDARG` case with the same size, weight, and style
  but a system monospace font; a failed retry still surfaces both exceptions.
  The normal renderer path passes 412 GitHub-renderer and 3,088 app tests plus a
  zero-warning Release x64 app build, but the exceptional fallback has not
  been exercised on the local machine. Both failing READMEs passed local
  pinned one-case replays after the final build, without reproducing their
  original transient inputs. A new full current-head audit and
  qualified interactive benchmark remain required; the latter is still
  queued for an online unlocked `jithub-interactive` runner.
- Locally verified, pending current-head CI and a live audit: the production
  README evidence now records the last committed renderer presentation's
  parse/extension, theme/context setup, initial background-layout wall, and
  UI-publication wall times, alongside its generation and source length. The
  capture is internal and allocation-free per rebuild; canceled/superseded
  builds cannot replace it. The audit reader requires every stage, rejects
  invalid values, and advances the resumable case schema so older cases cannot
  silently satisfy this evidence gate. Per-case summaries expose all four
  stages. All 3,104 Release x64 app and 412 GitHub renderer tests passed, and
  Release x64 app/audit builds have zero warnings. This is attribution
  instrumentation only: no current-head live outlier has been reclassified,
  the existing `RenderCompleted` signal precedes first paint, and neither
  same-byte parity nor the ≤2 ms publication gate is proved by these tests.
  A local pinned rank-478 (`coreyhaines31/marketingskills`) production-app
  audit confirmed that the new stage fields reach first and full evidence:
  parse/extension 101.8 ms, setup 177.6 ms, background-layout wall 90.5 ms,
  and UI publication 12.2 ms. Edge exited before DevTools became ready on
  that host, so this case has no browser comparison; the one-shot local
  publication measurement is not a qualified release sample and exceeds the
  2 ms target. It makes UI publication a concrete next profiling target, not
  a passed gate or a waiver.
- Local Edge-oracle retries on this host failed before the DevTools port file
  appeared; no browser-relative timing from them is valid. Investigation found
  that Edge's spawned PID could exit with code zero while eight descendants
  retained the oracle's unique temporary profile. The oracle now terminates
  only `msedge.exe` processes whose command line names that exact generated
  profile before removing it. A local integration check stopped all eight
  processes for one stale audit profile while leaving another profile's eight
  untouched; a subsequent failing-oracle smoke test left the total audit Edge
  process count unchanged (24 before and after). After stopping the remaining
  stale audit-owned profiles, the same smoke test left zero before and after;
  the local DevTools startup failure itself remains unresolved, and hosted
  Edge parity must still run. The workflow now runs the profile-lifetime tests.
- The `0d80017` top-500 audit failed multiple shards on valid
  images marked unavailable. Saved rank-170 (`ByteByteGoHq/system-design-101`)
  evidence shows four 64×64 Creative Commons SVGs visible in Edge but rejected
  before native rendering. Their Camo payloads carry an inert external SVG 1.0
  DOCTYPE; the small-SVG download completeness check prohibited all DTDs even
  though both authoritative renderer preflights already admit inert external
  SVG DOCTYPEs and reject internal subsets. Rank-235 (`caddyserver/caddy`)
  likewise has a valid signed GitHub user-attachment SVG with an external
  DOCTYPE. The download check now parses that declaration with a null XML
  resolver and a one-character entity-expansion ceiling, preserving truncated
  Camo retries and leaving authoritative SVG security checks unchanged.
  Direct-Camo and signed-attachment regression tests pass, as do all 3,106
  Release x64 app tests and a zero-warning Release x64 app build. This fixes a
  demonstrated false-unavailable path but does not establish that every
  failing shard has the same cause; the new head still needs the full 500-case
  audit and same-byte/interactive release gates.
- The `0d80017` audit has since finished with 12 failed cases across seven
  shards. Each failed case includes a valid native-unavailable SVG whose live
  URL now serves an inert external SVG DOCTYPE; the original failing response
  bodies were not retained, so the `cad7eea` current-head rerun is the required
  confirmation of the download fix, not an assumed 500/500 pass.
- The `cad7eea` audit started but its ranks 201–225 job failed before auditing
  any README: the Edge profile-cleanup contract subprocess timed out after
  15 seconds initializing a Win32_Process CIM query on a busy runner. Cleanup
  now skips that query when no Edge process exists and retains a bounded
  30-second watchdog when command-line inspection is actually needed. The
  generated-profile validation and exact-profile process targeting are
  unchanged; all 13 local Edge launch/navigation/lifetime tests pass. The
  older run's remaining shards were canceled after the newer head was pushed;
  it cannot provide a 500-case verdict. The current-head workflow must verify
  this CI fix as well as every README.
- The local Edge oracle's persistent startup failure was a Windows process
  handoff: its launched PID exited successfully before a child wrote
  `DevToolsActivePort`, but the audit immediately treated that zero exit as a
  browser failure. It now waits the bounded 20 seconds for the port after a
  zero-exit handoff while still failing immediately on nonzero exit, reporting
  the latest stderr, and reaping only its exact profile. Thirteen navigation,
  launch, and profile-lifetime tests pass. A full local Edge capture then
  succeeded with zero audit-owned Edge processes left behind. A pinned rank-478
  production-app audit passed one case with 100% text, 99.43% structure, zero
  unavailable images, and native/Edge ratios of 0.517 first and 0.262 full.
  Its UI-publication stage was 12.9 ms: this single unqualified case still
  exceeds the ≤2 ms target and does not replace the same-byte corpus, 500-case
  rerun, or counterbalanced release benchmark.
- Locally profiled, pending current-head CI: the internal
  publication clock now partitions commit, overlay reset, realization-plan
  construction, visible realization, and final notification, with visible
  realization further separating embeds, highlighting, and adornments. The
  production audit requires finite, additive phases and advances its case
  schema so stale evidence cannot satisfy the new trace. A pinned rank-478
  (`coreyhaines31/marketingskills`) one-case local replay measured 11.7 ms
  total publication: 3.5 ms constructing plans and 5.8 ms realizing visible
  content, including 4.2 ms scheduling the highlight band. Text-only inline
  containers now skip image/embed rectangle discovery, reducing the same
  case's measured plan-construction phase to 1.8 ms and total publication to
  8.6 ms on a fresh local run. Progressive
  highlighting now enters provider work on a worker thread, preserving the
  highlighter's documented thread-safe contract and marshaling only the
  result back to the UI dispatcher. This single live-network sample passed
  content checks but had styled-viewport SSIM 0.132; it is neither a fidelity
  pass nor a qualified latency sample. An image-bearing rank-170 replay also
  passed with 3/3 image observations and no unavailable content; 944 fast
  Core, 412 GitHub renderer, and 3,129 app tests passed locally, as did the
  zero-warning Release x64 app and audit builds. The slow external-gate
  mutation fixture was not completed locally and remains a CI requirement.
  Publication remains above the ≤2 ms target; the remaining plan-construction
  and highlight-band costs require further work.
- Locally verified, pending current-head CI: inline containers now retain
  sparse image/embed run offsets as they are built. Publication registers only
  those image runs, and rectangle enumeration asks DirectWrite only about the
  corresponding character slots rather than rescanning every text run in a
  mixed paragraph. A mixed UTF-16 emoji, two-image, two-hosted-embed geometry
  test preserves source order and visual rectangles; all 413 GitHub renderer,
  944 fast Core, and 3,129 app tests pass locally after one independently
  passing rerun of a transient repository-view-model test. The zero-warning
  Release x64 app build and pinned rank-170 production-app audit pass with
  3/3 image observations and no unavailable content. Its one-shot publication
  was 12.0 ms versus 12.9 ms before this sparse index, a difference too small
  and uncontrolled to qualify as a latency-gate pass. The slow external-gate
  mutation fixture and full benchmark remain open.
- Preview validation at `1121a72` failed the unchanged lean managed-package
  limit: Core + WinUI contained 1,265,664 bytes against the 1,258,291-byte
  (1.2 MiB) gate. The local follow-up removes generated record-struct methods
  from internal transport-only values and stores raw publication timestamps,
  converting audit-only phase durations when JitHub captures evidence instead
  of during every renderer commit. A fresh two-package local pack contains
  1,255,424 managed bytes and 528,559 compressed bytes, below the unchanged
  1.2 MiB and 525 KiB gates. The Release x64 JitHub build has zero warnings;
  944 fast Core, 413 GitHub renderer, and 3,129 app tests pass locally. The
  local schema-2 pinned rank-170 production-app audit also passed with no
  unavailable images and additive phase evidence. Its one-shot UI publication
  was 91.5 ms, with 75.7 ms in visible embed realization, so it is explicit
  evidence against—not for—the ≤2 ms gate. Those CI jobs were pending at
  this checkpoint; the size fix itself does not establish a performance gate.
- CI on `c8257ea` passed preview validation, including the unchanged package
  gate, and consolidated the current-head live README audit at 500/500 with
  zero valid images unavailable. The live native/Edge first-render p95 ratio
  was 0.453 and full-traversal p95 was 0.360. This closes that head's live
  correctness audit, not the same-byte Edge corpus, individual outlier
  attribution, counterbalanced interactive benchmark, or device matrix.
- Locally profiled after `c8257ea`, pending full CI and release benchmarks:
  the opt-in visible image path now starts host resolution on a worker. The
  same resolver already runs off-thread for speculative prefetch, and this
  prevents a synchronous source-cache hit from occupying the UI publication
  commit. On one pinned 39-image README replay, UI publication fell from
  91.5 to 10.7 ms and visible embed realization from 75.7 to 1.8 ms, with
  all 39 image observations and zero unavailable images. A second post-change
  run measured 12.9 ms publication and 1.8 ms embed realization, again with
  zero unavailable images. The default no-session path keeps its existing
  caller-thread resolver behavior; an opt-in thread-contract regression test
  verifies the off-thread path. All 944 fast Core, 415 GitHub renderer, and
  3,129 app tests pass locally; the Release x64 app build has zero warnings.
  A fresh lean two-pack remains below its unchanged gates at 1,255,936 managed
  and 528,603 compressed bytes. These local before/after diagnostics are not
  a counterbalanced result or a ≤2 ms pass; residual publication work and
  burst/cancellation behavior remain to verify.
- The rank-367 Edge `net::ERR_NO_BUFFER_SPACE` failure now has one bounded
  fresh-navigation recovery after stopping the failed load and waiting one
  second. The successful attempt gets a post-wait CPU/layout baseline; retry
  count and total wall time stay in evidence. A second buffer error, any
  other navigation error, or an incomplete page still fails. Eight navigation
  contract tests pass locally. This addresses an oracle infrastructure
  failure mode without excusing a native-renderer failure or qualifying the
  browser-relative release gate.
- On `62675d6`, the new ranks 76–100 shard passed 25/25. The audit now reports
  nonzero peak admitted source bytes for all 24 cases with image sources; the
  only zero case, `nodejs/node`, has no image source or fetch. The largest
  observed peak in this shard is 8,217,590 bytes, below the 64-MiB admission
  ceiling. This confirms that the previous wrapper instrumentation hole was
  closed for this shard, not that the full 500-case correctness, actual peak
  process memory, or release benchmark gates passed.
- Local virtualization checkpoint, pending current-head CI and qualified
  benchmark: hosted-embed and code-copy realization now query vertical plan
  indexes instead of scanning every measured plan on each scroll event. Active
  plans retain document order for hit testing, pending hosted factories remain
  cancelable when they leave the band, and no offscreen controls are created.
  A focused 300-embed sample UIA round trip verifies bounded realization,
  recycling at the bottom, and restoration at the top. That probe exposed a
  separate lazy-layout scroll-anchor race: during an animated return, layout
  commits repeatedly retargeted the moving viewport, stranding it about 9%
  down the document. Anchor restoration now requires both an unchanged offset
  and a non-intermediate scroll state. Seven focused policy tests, 951 fast
  Core tests, 415 GitHub renderer tests, and the sample UIA round trip pass
  locally; the Release x64 renderer build has zero warnings. A local two-pack
  measures 1,257,984 managed and 529,716 compressed bytes, still below the
  unchanged lean gates (with only 307 managed bytes of headroom). The slow
  external-gate mutation suite was not completed in this local run. This is
  correctness and algorithmic evidence, not measured scroll-frame,
  publication, or browser-relative performance evidence.
- The superseded `09fcb08` top-500 run failed rank 103 (`d3/d3`) on native
  shutdown, not rendering fidelity: text coverage was 100%, both images were
  available, and the captured `0xC000027B` WinUI exception showed a late
  scroll callback reading `TextMateCodeBlockSyntaxHighlighter.Revision` after
  JitHub had disposed that shared service. The window now removes its page tree
  and awaits its `Unloaded` event before retiring shared Markdown providers.
  The Release x64 app and automation harness build without warnings; all 3,129
  app tests pass. Four local replays of the same pinned rank-103 corpus passed
  with clean exits, 100% text coverage, and zero unavailable images. This is
  a focused shutdown regression check; the new-head 500/500 audit and the
  browser-relative performance gates remain required.
- Local code-highlight scheduling checkpoint, pending current-head CI and benchmark:
  documents with no measured code blocks now skip the highlight scheduler on
  scroll and viewport changes. Each layout-plan publication resets and rebuilds
  the measured-code flag, including nested blocks; a newly measured code block
  therefore resumes scheduling. The x64 Release renderer builds without
  warnings, 951 fast Core tests pass, and a fresh lean two-pack remains within
  the unchanged gates at 1,257,984 managed and 529,691 compressed bytes. This
  is an avoidable-work fix, not a qualified scroll or Edge-relative verdict.
- The `758e4d5` top-500 run completed 499/500; rank 226
  (`unslothai/unsloth`) failed before README rendering when FlaUI's initial
  cross-process tree query returned COM timeout `0x80131505`. The archived
  startup evidence shows a live, responding app, and four local replays of the
  exact CI-pinned rank passed with 100% text coverage and zero unavailable
  images. The audit now retries only that UIA timeout within its unchanged
  45-second open deadline, includes the elapsed time in native timing, and
  fails explicitly if the host never becomes available. The Release x64
  automation harness builds without warnings and three post-change pinned
  replays pass. This does not convert the failed shard into a pass; a new-head
  full 500/500 audit is still required.
- Verified on PR #99 head `de6d99b`: the consolidated live top-500 README
  audit passed 500/500 with zero failed cases and no valid unavailable image
  reported. Native/Edge live first-render and full-traversal p95 ratios were
  0.420 and 0.339. Preview validation, code viewer, website, and the
  x86/x64/ARM64 NativeAOT jobs also passed on this head. This closes the
  current-head live-audit gate only; dynamic GitHub/CDN delivery means it is
  not the same-byte offline Edge oracle, and the qualified counterbalanced
  release benchmark remains queued and unverified.
- The same run still has four individual full-page ratios above 1.10, which
  the aggregate pass does not waive: ranks 101 (`immich-app/immich`, 1.371),
  102 (`jaywcjlove/awesome-mac`, 1.406), 135
  (`hacksider/Deep-Live-Cam`, 1.106), and 164
  (`PanJiaChen/vue-element-admin`, 1.171). Rank 102 spent its 20-second
  visible-image wait on an OpenCollective/Camo SVG whose two native fetches
  returned no bytes after about 31 seconds each; Edge also recorded that
  image broken. Rank 135 fetched a valid 11.4-MB GIF in 2.8 seconds while
  a separate Camo badge was broken in both clients. Rank 164 waited 4.8
  seconds at its first tile while a Camo SVG fetch took 5.5 seconds and
  returned no bytes; Edge recorded that and another badge broken. These are
  live upstream-delivery contributions, not same-byte client verdicts.
  Rank 101 had seven successful fetches of at most 162 ms, but recorded
  1,305 ms of CPU raster preparation and a 4.1-second first-tile wait on
  the hosted runner. Three exact pinned local replays had 37–72 ms CPU
  preparation, no visible-image wait, 100% text, zero unavailable images,
  and full-page ratios 0.27–0.39. That large variance remains unexplained;
  the same-machine, same-byte Edge/client replay must isolate it before
  declaring the individual outlier resolved.
- Publication-phase evidence from the current-head run's archived ranks
  101–175 (75 case result files) makes the absolute UI-commit gap explicit:
  all 75 first publications exceed the unchanged ≤2 ms target. The minimum
  was 6.33 ms, median 18.83 ms, nearest-rank p95 38.73 ms, and maximum
  41.57 ms. Mean plan construction, visible embed realization, and highlight
  scheduling were 5.44, 4.73, and 4.93 ms respectively. These are
  one-shot production-audit phase measurements, not the qualified
  counterbalanced benchmark or a same-byte Edge comparison. They show that
  deferring network resolution alone cannot close the publication gate;
  realization-plan preparation and visible UI work need a bounded,
  generation-fenced commit path. The live 500/500 audit must not be
  interpreted as satisfying this separate ≤2 ms release requirement.
- Local release-harness work advances the first-viewport evidence contract to
  schema 11: every measured presentation now pairs its first-paint sample
  with the renderer's generation/source-matched UI-publication interval. The
  in-process evaluator and independent PowerShell gate recompute each trial
  and scenario maximum and reject missing, inconsistent, or >2 ms measured
  commits; quick mode checks shape but remains non-gating. The x64 Release
  harness builds with zero warnings, 958 non-external managed tests pass,
  six focused external-gate publication mutations are rejected, and one
  local quick UI run captured all six conditions (0.11–0.56 ms maxima).
  That synthetic quick run is not qualified release evidence and does not
  erase the production README publication gap above. Existing schema-10
  baselines cannot qualify a schema-11 candidate; a fresh, fully
  counterbalanced reference/candidate run is still required.
- Verified on PR #99 head `610866f`: the consolidated live top-500 audit
  (Actions run `36243722238`) passed 500/500 with zero failed cases and no
  valid image marked unavailable. Native/Edge live first-render and full-page
  p95 ratios were 0.460 and 0.351. The same head passed preview validation,
  including schema-11 gate tests and the unchanged package compliance checks,
  plus x86/x64/ARM64 NativeAOT publish jobs. This is current-head live-network
  correctness evidence, not a same-byte offline Edge comparison, a physical
  architecture/device matrix, or a qualified counterbalanced release result.
  Three individual live ratios remain above 1.10: rank 102
  (`jaywcjlove/awesome-mac`, 1.487 full page) spent 20 seconds waiting for an
  OpenCollective/Camo SVG that returned no bytes in two roughly 31-second
  native attempts and was also broken in Edge; rank 295
  (`bradtraversy/design-resources-for-developers`, 1.452 first render) used
  4.84 seconds of native first-render process CPU, with 0.924 seconds parse,
  2.862 seconds setup, and 2.079 seconds layout wall phases; rank 476
  (`multica-ai/multica`, 1.863 full page) waited 4.34 seconds for a visible
  image on tile zero although its eight source resolutions each completed in
  at most 403 ms and aggregate raster/scene admission was 218/3 ms. The
  latter two are unresolved native-work suspects, not network waivers.
  Same-machine, identical-byte replay is required before attributing or
  clearing any of these individual outliers. The two required interactive
  benchmark jobs remain queued for the `jithub-interactive` runner, whose
  availability this task cannot inspect; no release performance pass is claimed.
- Locally verified after `2f48718`, pending current-head CI: a pinned rank-295
  Release app replay passed with 100% text coverage, 99.64% structure, and no
  unavailable images. Setup-phase instrumentation measured 211.157 of 232.474 ms
  in theme/context creation, versus 0.930 ms registry and 1.643 ms image scope.
  A second production-app replay with narrower, fail-closed audit telemetry
  measured 199.117 of 219.916 ms in `ThemeResolver.CreateSnapshot` itself;
  first-render and full-page native/Edge live ratios were 0.363 and 0.103.
  The first replay's theme-key-cache candidate did not show a repeatable gain
  in a same-machine pinned-source ABBA comparison (patched setup 219/229 ms; baseline
  254/217 ms), so it was removed. The retained snapshot timing adds only one
  internal measurement, passes 3,132 app tests and 415 renderer tests, and
  preserves the unchanged lean size gates (529,689 compressed and 1,257,984
  managed bytes). The CI outlier's 2.862-second setup has not been reproduced
  locally, and this diagnostic does not resolve it or prove same-byte parity.
- A pinned rank-476 (`multica-ai/multica`) Release replay on the local machine
  passed with 100% text, 99.49% structure, zero unavailable images, and no
  visible-image wait; native/Edge live ratios were 0.528 first render and
  0.234 full page. Theme snapshot took 188.011 ms of 195.239 ms setup;
  publication took 10.507 ms, with a 0.958 ms commit. This does not reproduce
  or clear the hosted audit's 4.34-second image wait or satisfy the ≤2 ms
  publication release gate. A temporary rank-295 probe that bypassed app-level
  `MarkdownRenderer.*` resource lookups measured 185.124 ms theme snapshot
  versus 199.117 ms in the normal local replay, but changed the styled result;
  it was removed, not adopted as a customization or performance tradeoff.
- Locally verified, pending current-head CI: `ThemeResolver` now captures the
  host-to-root scoped resource-dictionary ancestry once per synchronous theme
  snapshot while still reading resource values live, preserving nearest-scope,
  merged-dictionary, and Light/Dark/High Contrast precedence. A pinned-source
  rank-295 Release replay sequence (candidate/baseline/baseline/candidate)
  measured theme-snapshot intervals of 154/227/189/157 ms; the final compact
  candidate measured 168 ms, passed 100% text and 99.64% structure with zero
  unavailable images, and matched the baseline first-tile visual on inspection.
  All 415 GitHub renderer and 3,132 app tests passed, the x64 Release app and
  audit harness built without warnings, and the unchanged lean gates passed
  (529,685 compressed and 1,257,984 managed bytes). These noisy local replays
  indicate reduced setup work, not a qualified counterbalanced release result
  or resolution of the hosted 2.862-second outlier; same-byte and device/theme
  gates remain open.
- Locally verified, pending current-head CI: scoped renderer/platform resources
  are now captured once per synchronous snapshot using the existing
  relevant-key graph traversal; application resources remain finite point
  lookups, so the large application/XamlControlsResources key projection is not
  enumerated. The superseded per-key graph resolver and its test-only wrapper
  were removed, with precedence, theme-fallback, ambient-value, and cycle
  assertions retargeted to the active capture path. A pinned rank-295 Release
  replay measured 64.427 ms theme snapshot versus 168.480 ms on the preceding
  implementation, with 100% text, 99.64% structure, zero unavailable images,
  and a byte-identical first native tile. This single noisy local comparison
  is not a counterbalanced release result. All 415 GitHub renderer and 3,132
  app tests passed; the lean Core+WinUI packages measured 527,990 compressed
  and 1,255,424 managed bytes, within unchanged 525 KiB/1.2 MiB gates.
  Publication remained 7.553 ms in this replay, above the separate 2 ms gate;
  same-byte Edge, full top-500, and physical matrix gates remain open.
- The `a7a3299` live top-500 run (`36262576598`) completed 499/500 with zero
  unavailable images; the aggregate native/Edge live ratios were 0.338 p95
  first render and 0.283 p95 full traversal. Rank 1
  (`codecrafters-io/build-your-own-x`) rendered with 100% text coverage and
  99.93% structural fidelity, but the app exited with WinUI stowed-exception
  code `0xC000027B` after `window-final-close`. No managed exception log was
  captured. Its 6.108-second initial-layout wall interval and 3.562
  first-render ratio are separate unresolved cold-host outliers. Seven exact
  pinned-README Release replays locally (the initial run plus six reusing the
  same Edge evidence) exited cleanly with zero unavailable images; this does
  not prove the hosted shutdown race fixed or qualify the 500/500 gate.
  Preview validation and NativeAOT checks passed on that head. The next audit
  run captures only the app's Windows Application Error faulting module, code,
  offset, process ID, and UTC time when a shard fails; it does not change the
  mandatory clean-exit verdict or upload raw process dumps.
- The same live run had four further individual full-page ratios above 1.10:
  rank 102 (`jaywcjlove/awesome-mac`, 1.569), rank 257
  (`CompVis/stable-diffusion`, 1.319), rank 414
  (`Alishahryar1/free-claude-code`, 1.216), and rank 447
  (`bmad-code-org/BMAD-METHOD`, 1.169). The latter three have zero unavailable
  images but recorded visible-image waits of 1.55/1.70, 5.95, and 7.36
  seconds respectively. Their successful source resolutions include 2.7–3.2,
  6.59, and 8.02-second individual network fetches, despite other assets in
  the same documents arriving in tens to hundreds of milliseconds. This is
  live-delivery evidence, not a waiver: identical-byte local replay must still
  isolate client work, especially where the visible wait exceeds fetch time.
- The next pinned live audit (`36271533386`, head `2d4f995`) failed its
  ranks 1-25 shard at rank 2 (`sindresorhus/awesome`): the valid 1,310-byte
  `banner2-direct.svg` resolved in 63 ms but became the only unavailable
  native image after a three-second isolated-worker `open` deadline. The
  worker used only 31 ms of process CPU during that transaction, and the app
  exited cleanly; Windows Application Error had no event for this case. A
  local Release replay using the pinned manifest passed with zero unavailable
  images and 100% text coverage. Neither the replay nor the low CPU time
  explains or waives the hosted timeout. Audit-only deadline evidence now
  records whether the request was writing, flushing, or waiting for a
  response, the request-write duration, and whether the worker had exited;
  it adds no source bytes or URLs and does not relax the three-second hard
  deadline or replay failed content. Focused renderer/app tests and the x64
  Release app build passed locally; a hosted recurrence and a complete
  current-head 500/500 audit remain required.
- Consolidation of that run confirmed 499/500 with no other failed case;
  aggregate live native/Edge p95 ratios were 0.323 first render and 0.276
  full traversal. Individual full-page ratios above 1.10 remained at rank
  102 (`jaywcjlove/awesome-mac`, 1.463), rank 126 (`angular/angular`,
  1.477), and rank 458 (`ionic-team/ionic-framework`, 1.250). Rank 458's
  first-tile image wait was 5.98 seconds while its logo request took 6.60
  seconds, consistent with live delivery. Rank 126 waited 4.25 seconds at
  its first tile even though its four assets all resolved within 147 ms;
  its initial layout took 1.12 seconds and publication 30 ms, making native
  work or scheduling a concrete suspect. A local pinned rank-126 Release
  replay passed without an image wait (173 ms layout, 21 ms publication,
  857 ms native full traversal). The replay does not clear the hosted
  outlier: retain it for identical-byte, same-machine investigation.
- Locally verified, pending current-head CI: JitHub now recognizes authored
  `https://github.com/<owner>/<repo>/raw/<ref>/<path>` Markdown image URLs
  as repository media, so the existing credential-free raw CDN is attempted
  before the private/LFS fallback. The trust promotion is restricted to HTTPS,
  GitHub's exact host and default port, with no URL user info; insecure or
  lookalike hosts remain under the ordinary external-image policy. The pinned
  rank-458 Release replay resolved Ionic's 10,361-byte logo from
  `raw.githubusercontent.com` in 113 ms, showed no visible-image wait, and
  finished native full traversal in 659 ms with zero unavailable images. The
  earlier hosted run fetched the GitHub web `/raw` route in 6.60 seconds; a
  different-network replay does not by itself prove a stable speedup or waive
  the individual 1.250 live ratio. The resolver's targeted 24 tests and x64
  Release app build passed; same-byte and current-head gates remain open.
- The next earlier-head pinned live audit (`36275652370`, head `b8572c2`)
  passed all 500 cases with zero valid unavailable images and native/Edge
  p95 ratios of 0.341 first render and 0.303 full traversal. It also exposed
  four individual full-page ratios above 1.10, so the stricter outlier goal
  is not met: rank 19 (`NousResearch/hermes-agent`, 1.118), rank 26
  (`ossu/computer-science`, 2.474 full and 4.488 first), rank 102
  (`jaywcjlove/awesome-mac`, 1.104), and rank 401 (`twentyhq/twenty`,
  1.830 full and 3.259 first). Rank 26 spent 5.830 seconds in background
  layout while its five successful image resolutions each took at most
  255 ms; this is not attributable to a slow image download. Three pinned
  Release replays of the same README on the local machine spent 131-140 ms
  in layout, 498-524 ms to first render, and 644-695 ms in native full
  traversal, with zero visible-image waits. Rank 401's hosted initial layout
  was only 143 ms but first render took 3.360 seconds; a pinned local replay
  took 611 ms to first render with no image wait. These different-machine
  replays show the stalls are intermittent, not that they are fixed or
  external. A same-byte, same-machine replay with worker scheduling/CPU
  evidence is still needed. The `c545490` audit was still running at this
  checkpoint; neither this earlier-head pass nor isolated replays qualified it.
- The complete pinned live audit at `c545490` (Actions run `36278951232`)
  passed 500/500 with zero valid unavailable images. Native/Edge p95 ratios
  were 0.327 first render and 0.288 full traversal. Four individual full-page
  ratios still exceeded 1.10: rank 102 (`jaywcjlove/awesome-mac`, 1.430),
  rank 310 (`ZhuLinsen/daily_stock_analysis`, 1.337), rank 352
  (`termux/termux-app`, 1.323), and rank 430 (`odoo/odoo`, 1.347).
  Rank 102 waited 20 seconds at a late tile for a Camo image that eventually
  failed after two roughly 31-second fetch attempts; Edge also reported that
  image broken, so it is not a valid-image-unavailable regression, but the
  live timing outlier is retained. Ranks 352 and 430 waited 11.6 and 7.9
  seconds for visible images whose individual Camo responses took 12.4 and
  8.5 seconds. Rank 310 instead spent 6.335 seconds in background layout
  with no image wait. A pinned local Release replay of the identical README
  SHA, source length, and viewport width took 148 ms in layout, so the cause
  of that intermittent hosted stall is not yet established. Browser-relative
  aggregate success and a different-machine replay do not satisfy the
  same-byte outlier or interactive release gates.
- Locally verified, pending CI: opt-in layout builds now measure the CPU time
  of their single background worker thread alongside wall time. The audit
  preserves both values in machine-readable evidence and its summary so a
  recurring layout spike can be distinguished from queueing/descheduling
  without recording source content. The new clock's two focused tests, 960
  renderer tests outside the long external-gate fixture, and all 3,141
  Release x64 app tests passed; x64 Release app and automation
  builds completed with zero warnings, and a pinned rank-310 full UI audit
  passed with 123 ms layout wall time and 125 ms measured thread CPU time
  (within timer granularity). This is diagnostic evidence, not a performance
  fix or release-gate waiver.
- The first full preview validation of that head (`da0bd1a`, Actions run
  `36291285727`) failed one of 1,441 Core tests: disposal of an engine-owned
  extension resource was not observed within five seconds after a canceled
  parse. The callback lifetime uses asynchronous continuations, so a retired
  lease could be complete while its notification was still queued. The cleanup
  path now observes the completed callback lifetime directly before deciding
  whether owned resources may be released, without reducing the test's timeout
  or allowing release while callbacks remain active. The 961-test fast Core
  suite, including a direct lease-retirement regression test, passes locally;
  a full hosted suite rerun is still required. This is
  a lifecycle/CI fix, not a performance-gate pass.
- The completed `da0bd1a` pinned live audit (Actions run `36291285713`)
  passed 500/500 with no reported unavailable image; native/Edge live p95
  ratios were 0.338 first render and 0.292 full traversal. Individual
  full-page outliers still exceeded 1.10 at ranks 101 (`immich-app/immich`,
  2.667), 102 (`jaywcjlove/awesome-mac`, 1.508), 301
  (`facebook/docusaurus`, 1.881), and 401 (`twentyhq/twenty`, 2.969).
  This earlier-head live pass is neither same-byte parity nor a qualified
  interactive release benchmark.
- The next audit at `466a135` (Actions run `36296516592`) failed rank 172
  (`gin-gonic/gin`): Edge rendered a 153×20 Sourcegraph badge, while JitHub
  reported it unavailable about 45 seconds after resolution began and recorded
  a 20-second first-tile image wait. No resolver outcome was recorded for that
  asset. Inspection found that the Camo/origin hedged resolver awaited its
  losing request in `finally`, so a successful fast hedge could still be held
  behind a stalled network loser until the image resolver's hard deadline.
  Losing requests are now canceled and observed through deferred retirement,
  without blocking the winner or disposing their token sources early. A
  deterministic unresponsive-loser regression test, all 3,142 app unit tests,
  and the x64 Release app build pass locally. A pinned rank-172 UI replay
  passed with zero unavailable images, but its Camo response arrived in
  144 ms and did not reproduce the hosted slow-path. Do not count that replay
  as a waiver: the full post-fix 500-case audit is still required.
- The `da0bd1a` audit's rank 101 (`immich-app/immich`) spent 8,157.8 ms in
  initial layout wall time but only 93.75 ms of layout-thread CPU. Its seven
  image resolutions accumulated 1,508 ms and the first tile waited 1,848 ms
  for images, so image delivery alone does not explain the layout interval.
  Audit-only layout timing now partitions that interval into queue delay,
  worker wall time, and UI-continuation delay; the parser requires all three
  finite nonnegative values to sum to the recorded layout total. The 3,149
  app tests and x64 Release app/automation builds pass locally. An exact
  pinned-README rank-101 UI replay passed with zero unavailable images and
  measured 0.5 ms queue, 90.9 ms worker, 19.7 ms continuation, and 111.1 ms
  total layout wall time. This different-machine, live-asset replay did not
  reproduce or clear the hosted eight-second stall. A hosted recurrence with
  the new phase evidence is needed before choosing a scheduling or layout fix.
- The current-head `897fc85` pinned live audit (Actions run `36302490737`)
  failed rank 153 (`louislam/uptime-kuma`) in its 151-175 shard. The
  1,877,124-byte sponsor SVG resolved successfully in 139 ms and was visible
  in Edge at 1200×8120, but the isolated worker timed out during `open` at
  its unchanged three-second hard deadline. The request reached response-read,
  the worker had not exited, and it accumulated only 578 ms of process CPU;
  JitHub therefore showed one valid image as unavailable. Rank 172
  (`gin-gonic/gin`), which failed on an earlier head, passed in this shard
  with zero unavailable images. The sponsor failure is a recurrence of the
  earlier rank-153 issue, not an external-download failure. The existing
  offline sponsor-shaped fixture matches 576 embedded images, 380 unique
  payloads, 1152 text nodes, and roughly 1.8 MiB of source but uses PNG
  rather than the live SVG's JPEG payloads. A host/worker phase and memory
  trace plus a same-byte, same-machine replay are still needed to distinguish
  worker descheduling, font/image processing, and resource pressure. The
  live gate remains failed; a different-machine or synthetic-fixture pass
  must not waive it.
- Locally verified, pending CI: explicit audit launches now record SHA-256 of
  each successfully resolved image payload alongside its existing byte length
  and timing. This is audit-only and stores no payload bytes, so a future
  hosted timeout can be tied to an exact local replay even when the remote
  asset changes between runs. All four focused worker/audit-evidence tests
  pass. This is instrumentation, not a fix for the sponsor timeout or proof
  of same-byte parity.
- Locally verified, pending CI and a live recurrence: worker protocol v5 now
  reserves a 64-byte shared-memory trailer outside the source and exact raster
  output. During `Open`, the isolated worker records bounded progress after
  mapping, hash verification, XML parsing, security inspection, the font gate,
  tree construction, and document attachment. Only on a hard timeout with a
  listener enabled does the host read that content-free phase into the audit
  signal, alongside its existing process-CPU and transport timings. This can
  distinguish a font wait from SVG tree/image processing without increasing
  the immutable three-second deadline or logging image bytes. The pinned
  x86/x64/ARM64 workers rebuilt; all 18 pinned Rust unit tests, 63 x64 resvg
  tests, 3,150 x64 app tests, and the x64 Release app build passed locally.
  The all-RID development pack is 4,696,184 bytes, below the unchanged
  5 MiB compressed ceiling; its unsigned worker artifacts are not release
  signing evidence.
  The phase of the live sponsor
  failure is not known yet, and memory-pressure evidence, a same-byte replay,
  the current-head top-500 audit, and the qualified release benchmark remain
  open. This diagnostic is not a timeout fix.
- Locally verified, pending CI and a failing live trace: timeout-only SVG
  worker evidence now also samples process working set, private commit (KiB),
  and cumulative page faults through source-generated Windows interop. These
  are numeric process counters, never SVG bytes or URLs; normal rendering
  does not query process memory. A live-worker interop test, all 63 resvg
  tests, all 3,150 x64 app tests, x86/ARM64 provider builds, the Release app
  build, and an x64 NativeAOT publish plus artifact verification passed. No
  same-byte sponsor replay, memory-pressure verdict, or qualified release
  performance result has been obtained yet.
- The earlier-head `16419d0` pinned live audit (Actions run `36305907644`)
  completed 500/500 with no valid image marked unavailable. Its aggregate
  native/Edge first-render and full-page p95 ratios were 0.317 and 0.298;
  rank 153 passed on this run. Rank 102 (`jaywcjlove/awesome-mac`) still had a
  raw full-page ratio of 1.614. The detailed trace shows one 20,004-ms
  visible-image wait at tile 125 for an OpenCollective Camo SVG; the same URL
  completed in Edge with natural size 0×0, and JitHub's one raw unavailable
  image was excluded as browser-broken. That is concrete external-delivery
  evidence for this outlier, not a waiver for any valid image failure or a
  same-byte client-rendering comparison. The `18679a3` current-head audit is
  reported below; the separate interactive benchmark remains queued.
- The `18679a3` rendering-head pinned live audit (Actions run `36312263988`)
  completed 500/500 with no valid image unavailable and no failed cases. Its
  aggregate native/Edge first-render and full-page p95 ratios were 0.311 and
  0.259. Three individual full-page ratios exceeded 1.10, while no
  first-render ratio did. Rank 102 (`jaywcjlove/awesome-mac`, 1.62) again
  spent 20 seconds waiting for the browser-broken OpenCollective Camo SVG.
  Rank 235 (`caddyserver/caddy`, 3.02) spent 10.6 seconds in a visible-image
  wait while a 2,364-byte Sourcegraph badge took 11.4 seconds to resolve.
  Rank 344 (`coollabsio/coolify`, 1.57) spent 5.6 seconds waiting while its
  298-byte relative logo took 6.6 seconds to resolve from GitHub raw CDN.
  All three images resolved or were browser-broken; the recorded waits and
  resolver timings identify live delivery as the dominant charged delay, not
  SVG raster time. This does not prove same-byte Edge client parity, guarantee
  that the intermittent sponsor SVG timeout cannot recur, or satisfy the
  qualified release benchmark, which remains queued on an interactive runner.
- The subsequent docs-only-head live audit at `39e67c2` (Actions run
  `36321754144`) exposed two valid-image failures even though the rendering
  code matched `18679a3`. Rank 54 (`langgenius/dify`) resolved a 1,012-byte
  Korean Shields/Camo badge; Edge displayed it at 43x20, but the isolated
  worker exceeded the unchanged three-second **open** deadline after the
  font gate and before completing SVG tree construction. The timeout-only
  trace recorded zero worker CPU milliseconds during the wait, 6,492 KiB
  working set, and 2,216 KiB private commit. The exact SVG bytes and SHA-256
  were recovered and added as an offline cold-worker regression test. Hangul
  glyph fallback is now primed with the worker's one-time text pipeline,
  outside the content deadline; the exact-byte test passes locally under a
  500-ms content deadline, as do all 64 x64 provider tests and 18 pinned Rust
  tests. This targets a missing cold font path, but does not prove that the
  hosted stall or general worker starvation is fixed. Rank 201
  (`spring-projects/spring-boot`) separately failed a valid SVG before its
  font catalog finished initialization: the final font-catalog probe had
  a sub-millisecond remaining deadline, recorded zero CPU milliseconds in
  that probe, and saw only 5,356 KiB working set. This trace does not measure
  CPU consumption over the entire 15-second initialization window. That failure
  is not explained by Hangul fallback and remains open. The completed audit
  reported all 500 ranks: 498 passed, 2 failed, with aggregate native/Edge
  first-render and full-page p95 ratios of 0.310 and 0.294. No earlier
  500/500 pass waives either recurrence. Three additional passed cases exceeded
  the 1.10 individual full-page ratio: rank 24 (`vuejs/vue`, 1.13) waited
  4.34 seconds at the last image band while a browser-broken OpenCollective
  Camo URL took 5.05 seconds and yielded no bytes; rank 390
  (`FoundationAgents/OpenManus`, 3.00) charged a 20-second visible-image
  wait while three URLs were broken in Edge too. Rank 301
  (`facebook/docusaurus`, 1.37) is different: its image sources resolved in
  at most 414 ms with no unavailable asset, while one native CPU-preparation
  lease lasted 4.22 seconds and a late visible-image band waited 3.51 seconds.
  That is a client-preparation suspect needing exact-byte replay; the aggregate
  p95 does not waive it. The three RID workers have been rebuilt and the
  unsigned all-RID development pack is 4,697,877 bytes, below the unchanged
  5 MiB ceiling. Production signing, current-head full audit, and release
  benchmark remain mandatory.
- Locally verified, pending hosted timeout recurrence: the font-catalog
  timeout event now attributes the final HELLO pipe probe to the whole
  15-second initialization window. Previously that probe could report a
  zero-millisecond deadline and zero CPU even though the worker had spent
  nearly the entire window initializing fonts. The event retains the same
  content-free fields and is emitted only on timeout; all 65 x64 Release
  provider tests pass, including the whole-window attribution contract.
  The x64 trimmed NativeAOT provider smoke publishes and runs successfully.
  This improves diagnosis of rank 201 and does not
  make its valid image available or relax either deadline.
- The `8df730b` hosted rerun (Actions run `36325860405`) completed its
  consolidated top-500 audit: 500/500 cases passed, no valid image unavailable,
  first-render and full-page native/Edge p95 ratios 0.325 and 0.294. Rank 54
  (`langgenius/dify`) and rank 201 (`spring-projects/spring-boot`) both rendered
  every valid image. Rank 301 (`facebook/docusaurus`) passed with one 160-ms
  CPU-preparation lease, versus 4,218 ms in the prior hosted audit. The only
  individual ratio above 1.10 was rank 102 (`jaywcjlove/awesome-mac`, full-page
  1.65): its OpenCollective Camo URL returned no bytes after two 31–33-second
  resolver attempts, Edge reported the same image complete at natural size
  0×0, and JitHub charged a 20-second visible-image wait. This is external
  delivery evidence for that outlier, not a waiver for valid images or a
  same-byte client-rendering comparison. The queued `de91f61` and `401e303`
  runs were superseded and canceled after later pushes; the current
  `8c5d97c` audit is in progress. This earlier-head pass does not substitute for a
  current-head 500-case verdict or prove intermittent stalls impossible.
- A focused Release x64 replay of rank 301 at the same pinned commit used by
  the 4,218-ms outlier fetched the exact same 8,948-byte PNG (SHA-256
  `C2E9351FFB8B7BDFB8129ADA3C71AC3BA0CC9EB0CB7717D24C1B81FE6DBB9587`)
  and passed text/structure/image gates three times. Its CPU-preparation leases
  were 65, 30, and 25 ms. Opt-in, content-free raster stage events attributed the
  first lease to 8.96 ms stream write, 26.02 ms decoder creation, 17.50 ms
  WIC pixel decode, 1.73 ms Win2D upload, and 0.11 ms cache publication;
  second lease was faster at every stage. The final replay additionally
  measured preparation admission and continuation scheduling at 0.55 ms.
  Thus the prior 4.22-second
  client-preparation stall is not a persistent cost of those bytes. Its root
  cause remains open until captured under the stall condition; the focused
  diagnostic mode is explicitly excluded from qualifying performance gates.
  A fourth pinned replay without the opt-in passed and produced no raster
  diagnostic file (26-ms CPU-preparation lease), confirming that ordinary
  audits leave the extra observer disabled.
- The `8c5d97c` preview package job found the lean Core + WinUI managed pack
  717 bytes above its unchanged 1.2 MiB gate (1,259,008 versus 1,258,291
  bytes). Its sibling NativeAOT contract and x86/x64/ARM64 publish checks
  passed. The audit-only raster event provider and option validation now live
  in the opt-in Performance assembly, while the lean renderer retains only
  internal session hooks. A fresh local pack passes the unchanged size gate:
  Core + WinUI is 1,257,472 managed bytes and 529,119 compressed bytes; the
  optional Performance pack is 30,711 compressed bytes. All 416 GitHub
  renderer tests (including 39 focused performance/raster cases) and five app
  listener tests pass. One clean local pack of all 14 shipping packages passes
  size, SBOM/notice, locked-license, and native-asset compliance. A second
  normalized local pack matches all 14 reference package hashes and sizes;
  the CI managed-pack NativeAOT smoke still needs to run.
  A one-case
  Release audit of pinned rank 301 (`facebook/docusaurus`) passes with no
  unavailable image and emits raster stage evidence from the relocated
  provider. The pre-fix full Core suite passed 1,442/1,442 in 1h04m; full
  current-fix CI and release benchmarks are still required.
- The `8c5d97c` hosted top-500 audit (Actions run `36335465601`) completed
  500/500 with no valid image unavailable. Native/Edge p95 ratios were 0.321
  for first render and 0.277 for full traversal. Four individual full-page
  ratios still exceeded 1.10: rank 26 (`ossu/computer-science`, 1.67) had a
  4.01-second native visible-image wait and 2.48 seconds of raster CPU
  preparation even though all five source responses arrived within 228 ms;
  rank 201 (`spring-projects/spring-boot`, 2.30) waited 7.19 seconds on two
  SVGs whose sources resolved within 329 ms. Both are native-preparation
  suspects and require stage-level same-byte replay, not a waiver. Rank 149
  (`sherlock-project/sherlock`, 1.73) waited 7.03 seconds while one 99-KiB
  JPEG source resolution took 8.25 seconds. Rank 102
  (`jaywcjlove/awesome-mac`, 1.59) again waited 20 seconds for a browser-broken
  image while two resolver attempts returned no bytes after about 32 seconds.
  Those latter two have external-delivery evidence, but neither substitutes
  for the required offline same-byte Edge comparison. This audit is an
  earlier-head pass after the package-size fix and must be rerun on its new
  rendering head.
- On the pushed `3766f88` package-fix head, four pinned rank-26 local replays
  fetched exactly the same five image hashes as the 2.48-second hosted
  preparation case. All passed with zero unavailable images; raster CPU
  preparation measured 156, 94, 117, and 129 ms, and no visible-image wait
  occurred. The diagnostic trace on the first replay attributed the 15,810-
  byte WebP to 8.56 ms stream write, 93.97 ms decoder creation, 23.50 ms
  WIC decode, and 1.97 ms upload. Three pinned rank-201 local replays likewise
  fetched the exact same two SVG hashes as the 7.19-second hosted wait; all
  passed with full traversal around 420–476 ms and no image wait. These
  same-byte native replays show that neither hosted stall is a persistent cost
  of those bytes, but do not prove whether the intermittent cause is WARP,
  worker startup, runner contention, or another native path. Hosted
  stage-level capture and the mandatory same-byte Edge comparison remain open.
  A focused hosted image-diagnostic dispatch is prepared on the existing
  top-500 workflow so it can run from this PR branch. It replays one rank from
  a validated pinned corpus with optional raster-stage tracing and repeated
  fresh attempts. It has not run in CI, does not pin
  changing CDN image bytes, and its tracing cannot qualify the release timing
  gate. The normal audit retains tracing off; its pinned Windows App Runtime
  installer is shared with the diagnostic to avoid setup drift.
- The original rank-153 audit failure in run `36342837396` was a native-only
  timeout while opening a valid 1,877,124-byte sponsor SVG (SHA-256
  `9881971D893267A10A872DB575522AF0C20E0E64F1EF87F6E45CF5272C9E69AC`).
  Edge rendered those bytes. Five focused hosted replays in run `36349877520`
  all passed, so the intermittent timeout is unresolved, not waived. A
  separate Weblate badge returned zero bytes and was broken in Edge as well;
  it is not this native-only failure. The rank-26 hosted diagnostic
  (`36349885686`) also passed all five replays without reproducing its older
  image-preparation stall. The 20-shard audit (`36349875408`) has since
  completed ranks 151–175 at 24/25: rank 153 failed again on the same
  exact-byte sponsor SVG, fetched in 106.6 ms and rejected only after the
  isolated worker's unchanged 3-second `open` deadline. The worker remained
  alive, used 406 ms CPU, and last reported progress immediately before
  usvg tree conversion (`font-gate-passed`); working set was 23,164 KiB and
  private commit 17,064 KiB. This is a reproduced intermittent native-only
  liveness failure, not an external asset problem or a 500/500 pass. The
  other shards were still running at this checkpoint.
- To reproduce shard context without weakening the full gate, the focused
  diagnostic now accepts up to four preceding pinned ranks per attempt. A
  rank-153 replay with two preceding cases can reproduce the original
  151–153 order; the earlier five single-rank passes cannot rule out
  preceding-case or runner-pressure effects. This is diagnostic only and
  does not replace the full audit or an offline same-byte Edge corpus.
- A local Release audit from an isolated checkout of the exact failing
  renderer commit `79f7199` and the run `36349875408` pinned manifest replayed
  ranks 151–153 in order. All three passed with zero native unavailable
  images, including rank 153. This rules out a deterministic failure caused
  simply by those preceding README cases on this machine, but it does not
  resolve the reproduced hosted worker timeout or qualify the full gate.
- The focused hosted 151–153 replay on instrumented head `0c00ec8` (run
  `36354332623`) failed one of five independent attempts at rank 153. The
  exact sponsor SVG hash and 1,877,124-byte size matched the earlier failure;
  the worker timed out after the unchanged 3,000-ms `open` deadline with only
  390 ms process CPU while reading the response. Its new last progress marker
  was `usvg-tree-build`, narrowing the stall to `usvg::Tree::from_xmltree`
  rather than font-gate setup or the theme transform. The four other hosted
  attempts passed. Phase 11 is the input-ready checkpoint immediately before
  `usvg::Tree::from_xmltree`; phase 12 is recorded only after conversion, so
  this does not identify a substep inside usvg. The 390 ms process CPU during
  roughly 3 s wall time leaves scheduler ready time, blocking/page-in, and
  intermittent conversion cost as competing causes. A diagnostic-only WPR
  trace of a pinned 151–153 replay (sampled CPU, ready/wait time, hard faults)
  is the next falsifiable step; it must not affect the normal release run or
  weaken the worker deadline. This is a confirmed intermittent native-only
  timeout, not a resolved case; it needs a real fix and another full 500-case
  pass.
- Local gate setup on this head exposed a committed restore-lock mismatch:
  the x64 performance-gate lockfile has a `win-x64` target while its project
  did not declare that runtime identifier, causing NU1004 before any timing
  could start. The project now declares `win-x64`; a fresh Release x64
  `-PlanOnly` invocation builds without warnings and produces the frozen
  560-case plan. This validates build and plan generation only, not a live
  qualified benchmark. The 35-test audit-source contract suite and all 3,151
  Release x64 app tests pass locally after updating the pinned-runtime
  assertion to inspect the shared installer script used by the workflow;
  current-head CI is still required.
- The release harness now records measurement-scoped foreground, desktop,
  visibility, work-area, and occlusion evidence from a separate WinEvent
  message-pump thread. Schema-12 in-process and external gates reject focus
  loss, sampling gaps, missing hooks, and unobstructed-visibility failures;
  120/240 Hz and latency thresholds are unchanged. The x64 Release harness
  build and 275 focused managed/external-gate tests pass locally. This closes
  a qualification hole in earlier diagnostics, but no new full live run has
  passed and the low observed cadence still needs measurement on a quiet
  foreground desktop.
- Focused x64 Release tests now pin the existing oversized ordinary-raster
  behavior: a 2048×8193 PNG just beyond the full-frame limit is classified
  for a bounded static preview; under a 65,536-pixel session output ceiling,
  the WIC/Win2D bitmap stays within that ceiling while the 200-DIP intrinsic
  aspect/geometry remains stable and completion publishes once. The pure
  budget suite passed 5/5 and the integration case passed 1/1. These tests
  cover output/GPU bounds, not every codec's internal decode allocations or
  visible-tile fidelity; the latter remain to be evaluated separately.
- Open: oversized raster tiling and session-owned SVG/document/GPU preparation
  caches.
- September 28 copy-on-write layout audit: the current `BlockBox` owns mutable
  measurement, arrangement, bounds, and disposal state. Image completion
  relayout mutates the dirty box, trailing placements, height cache, indexes,
  and revision under the live snapshot lock. Copying only a bounds array would
  not produce a consistent published revision; cloning boxes without explicit
  leases could double-dispose native image/SVG resources. The safe pilot is a
  detached dirty-image-block measurement plus suffix-placement revision with
  resource-sharing leases, generation/base-revision validation, and one atomic
  publication. Tests must prove the old revision remains readable during
  preparation, cancellation/stale candidates leave it unchanged, and resources
  retire only after the final revision owner. This milestone remains open; the
  existing lock-serialized relayout is not being mislabeled copy-on-write.
- Partially implemented: the opt-in presentation path defers Math/Mermaid
  scenes while public `ParseAsync` stays eager, and focused tests cover scene
  diagnostics, callback lifetime, cancellation, and source fallback. It still
  schedules only the measured viewport; a completed scene invokes a full
  theme/layout rebuild rather than publishing a detached dirty-block revision.
  Directional lookahead, visible-first jump priority, revision-safe publication,
  and a measured many-scene release case remain open. Ahead-of-viewport
  highlighting remains a separate open goal.
- Open: expanded deterministic corpus and live top-500/Edge same-byte release
  gates, device/theme/DPI and x86/ARM64 NativeAOT runtime matrices. The live
  audit requires the read-only GitHub audit credentials specified by its runner.
- The separate product live-performance jobs for PR #99 remain queued on the
  `self-hosted, Windows, X64, jithub-interactive` runner label set after their
  contract jobs passed. The current GitHub credential receives HTTP 403 from
  the repository runner-status API, so availability cannot be verified from
  this task. An online, unlocked interactive runner with those labels is
  required; queued jobs are not a performance pass.
- September 28 local working-tree readiness correction: a code review found
  that the first-viewport images-ready timestamp could be accepted after only
  one intersecting Win2D invalidation region, before the rest of the viewport
  painted. The app-owned audit probe now requires exact union coverage of the
  measured viewport after visible raster/SVG resources settle, resets on
  placeholder epochs and viewport/snapshot/revision/DPI changes, and samples
  on a 16 ms rather than 2 ms UI timer. A lean Core post-paint callback and
  app-owned PNG audit capture kept the unchanged Core+WinUI managed pair at
  1,257,984/1,258,291 bytes (530,929/537,600 compressed); focused readiness
  and visible-SVG-tile tests pass. The current-source x64 NativeAOT publish and
  artifact verification pass. With a freshly rebuilt audit runner, a pinned
  rank-1 same-byte AOT replay recorded the app paint signal (generation 4,
  measured 611-DIP viewport, two polls), traversed 25 viewports, passed text
  100%, structure 99.92%, extent 0.983, zero unavailable images, and clean
  exit. Native/Edge first-image-ready was 196.5/144.6 ms (1.36x, **above**
  the unchanged 1.10 gate); charged full traversal was 406/491 ms (0.83x).
  This is one local diagnostic, not the qualified counterbalanced benchmark or
  current-head 500-case corpus. The strict current-source 14-package gate now
  passes two normalized clean packs with identical archive-entry hashes and
  sizes, 14 SPDX 2.3 SBOMs and notices, zero license findings, and all six
  selected-RID Mermaid/resvg native assets. The Core+WinUI pair is
  1,257,984/1,258,291 bytes managed and 530,846/537,600 bytes compressed.
  Workers are unsigned development artifacts, not production-signing evidence.
  The earlier-head CI Core run failed one obsolete schema-11 assertion and
  two wall-clock-sensitive tests during parallel stress. The assertion now
  pins schema 12; the two deadline/cancellation test classes run in a
  nonparallel collection without changing their production deadlines, and
  their focused current-source Release run passes 3/3. Current-head CI, the
  full regression suite, the first-viewport performance gap, and the release
  matrices remain open.
- September 28 local schema-12 release-run attempt on the pinned reference
  and candidate started an R1,C1,C2,R2 sequence in a WinApp CLI sandbox, but
  R1 failed closed after producing its complete report, so C1/C2/R2 were not
  run and there is **no qualified counterbalanced verdict**. The reference
  report recorded a configured/measured 32.00/32.01 Hz display and 7,841
  visibility samples with the target window never foreground. Its warm-scroll
  renderer UI work p95 was 0.2544 ms, but frame p95 was 32.3948 ms against
  the unchanged 8.333 ms 120 Hz budget. The first-viewport evidence contract,
  scroll gate, cancellation gate, and self-verification also failed; the
  cancellation report's aggregate p95 was 0.6163 ms with zero stale commits,
  but one trial-local first-call warmup took 16.5907 ms, exceeding its 16 ms
  absolute contract. That is a real failed run, though the background/32 Hz
  environment prevents attributing it to the renderer alone. The run is
  retained at
  `E:\worktrees\artifacts\markdownrenderer-abba-7c1c06f\evidence-r1-failed`.
  The next local step is to establish a continuously foreground, measured
  120 Hz target before repeating the full sequence; no cadence or threshold
  is waived. The same-byte top-500 run `36474960050` is still in progress
  and already has failing shards, so it is not a current-head release pass.
- The current-source full x64 Release WinUI test suite initially failed one
  cancellation assertion that used a disposable token callback as its proof.
  During cancellation, `Task.Delay` can finish and dispose that registration
  before the callback is visited; the durable check is the transport token's
  canceled state plus queue drain. With that test correction and the two
  source-contract assertion updates, two subsequent complete local WinUI
  runs pass 3,209/3,209. This fixes false test evidence; it does not close
  the separately failed benchmark cancellation warmup above.
- September 28 follow-up: PR #99 head `571fe7a` passed the fresh Full Native
  AOT contract/unit job and x86, x64, and ARM64 publish/package jobs. This is
  build/package evidence, not runtime-device qualification. A local JitHub
  paragraph-spacing candidate keeps the existing compact 8-DIP margin inside
  lists while giving standalone paragraphs a semantic 16-DIP token, avoiding
  the earlier rank-1 list-density regression. Against the pinned corpus, a
  Release rank-83 (`kubernetes/kubernetes`) diagnostic passes 100% text,
  99.38% structure, zero unavailable images, and source-bound page extent
  1,964/2,048 (0.959), up from the earlier 1,812/2,048 (0.885). Rank 1
  (`codecrafters-io/build-your-own-x`) likewise passes content and extent at
  13,514/13,704 (0.986), versus the previous 13,466/13,704 (0.983). The
  complete local x64 Release WinUI suite still passes 3,209/3,209 after the
  style-contract update. These are one-case managed diagnostics and do **not**
  pass first-image-ready performance (rank 83 is 4.17x and rank 1 is 4.75x
  relative to source-bound Edge). Local rank-60 (`airbnb/javascript`) and rank
  203 (`d2l-ai/d2l-zh`) failures were mislabeled `visible-image-decode` by a
  stale replay stage: current runs decode all 19/19 and 5/5 captured images.
  Rank 203 passes strict source replay with its captured native viewport
  profile at the correct 623-DIP content width. Rank 60 also passes strict
  replay at 623 DIP; the earlier `native-movement` diagnostic used an
  incorrect 660-DIP replay width. Neither result is a full audit pass; the
  full 500-case source/asset replay and 110% performance gates remain open.
- September 28 same-byte replay follow-up: focused corpus/replay tests pass
  39/39, and the complete `eng/readme-audit` Node suite passes 58/58. The
  retained rank-39 (`CyC2018/CS-Notes`) source replay now passes with 18/18
  images verified and zero replay misses or blocked requests; rank 43
  (`ollama/ollama`) passes with one deterministic, unique-alt indexed image
  alias match and zero misses or blocked requests. These validate the pinned
  fixtures only; the full 500-case same-byte gate remains open. Rank 2
  (`sindresorhus/awesome`) is explained by the historical sanitizer deleting
  the `<picture>` subtree, including its fallback image; the immutable README
  parses to three images, one under a picture with two source candidates. The
  replay regression now covers that shape, drops unsupported source nodes,
  and verifies the fallback survives. Its Actions evidence omitted README,
  rendered HTML, and asset bytes, so a current full-corpus replay remains
  unavailable. Rank 7 (`donnemartin/system-design-primer`) remains an
  unresolved historical `image-map` failure: the sparse artifact has route
  counts but no captured indexed alt identities, so an ordinal mapping is not
  justified and the missing-alias gate stays fail-closed.
- The schema-12 sandbox retry now has a bounded, fail-closed foreground
  preflight in both pinned reference and candidate harnesses. On the local
  WinApp CLI 0.7 sandbox, foreground activation passed that preflight but
  configured refresh remained 32.00 Hz; the harness rejected it before
  trials, so this sandbox cannot supply 120 Hz release evidence. A separate
  physical-host diagnostic with the pinned reference window kept foreground
  for all 94 visibility samples and measured configured/observed 240/246.84
  Hz, AC power, and Energy Saver off. Its deliberately reduced 1/1/1/1/1
  populations fail the frozen release sample requirements, as intended; the
  result only qualifies the physical host as a candidate environment for a
  full counterbalanced run. No R1,C1,C2,R2 verdict follows from it.
- September 28 full physical-host attempt `run-20260928-222116498-360dace06ef2`
  retained its exact pinned reference/candidate hashes and completed the
  entire R1 reference population, but R1 failed closed; C1/C2/R2 were never
  admitted. The configured 240 Hz display yielded 119.26 Hz median measured
  scroll frames, below the unchanged configured-rate-relative qualification;
  the 4,389-sample visibility trace first lost foreground at 207.6 seconds
  and recorded 2,302 nonforeground samples. This is an invalid measurement
  environment, not a counterbalanced verdict. It also exposed reference
  first-viewport nonstationarity in all six conditions, a 10.013 ms scroll
  frame p95 against 8.333 ms, and one 29.634 ms cancellation trial against
  the unchanged 16 ms absolute gate. Do not silently retry or relax any gate:
  establish a quiescent, foreground physical session at a supported measured
  refresh mode, diagnose the reference's own absolute failures, then rerun
  the complete frozen R1,C1,C2,R2 sequence. Local correctness fixes made
  during this attempt were not in its hashed candidate binary.
- September 29 R1 source-lookup follow-up: its 4,096-call regression batches
  were only about 1.6 ms at the fast-trial rate, and trial-local p95s varied
  from 416 to 1,140 ns. The unchanged 20% robust-dispersion gate correctly
  rejects this evidence; a short scheduler pause or GC suspension can affect
  such a batch, but the retained report cannot distinguish that from a code
  regression. Additive, optional per-trial diagnostic fields now retain
  monotonic UTC bounds, Stopwatch duration, current-thread CPU ticks, thread
  cycle delta where supported, and Gen0/1/2 GC deltas alongside allocated
  bytes. Schema version and all p95, zero-allocation, sample-count, and 20%
  dispersion gates remain unchanged; older frozen same-schema reports remain
  readable. The focused compatibility/round-trip tests pass 6/6. The two
  cache-disabled extra-parse observations happened only after R1 had already
  lost foreground, so they need a qualified replay before changing the
  restyle/parse path. No new physical R1,C1,C2,R2 verdict exists.
- September 28 current local fidelity checkpoint: a pinned x64 Release
  rank-34 (`massgravel/Microsoft-Activation-Scripts`) source-bound replay now
  passes the unchanged case gates at 99.64% text coverage, 99.88% stable
  structure, 5/5 headings, 13/13 distinct links (including linked images),
  8/8 images, zero unavailable assets, and a clean exit. This repaired a
  Markdown heading swallowed after an HTML void tag and blank line, and made
  native/Edge link accounting symmetric for image-only anchors. The same
  local case still **fails performance**: native/Edge first-image-ready is
  470/82 ms (5.73x) and full traversal 460/144 ms (3.20x). Its initial
  pipeline records 83 ms parse, 87 ms setup including 79 ms theme snapshot,
  100 ms layout, and 22 ms UI publication. The Core+WinUI lean x64 pack
  passes the unchanged caps at 1,257,984/1,258,291 managed bytes and
  530,994/537,600 compressed bytes. The current full local WinUI, GitHub
  renderer, and README-audit suites pass 3,220/3,220, 460/460, and 58/58;
  the fast Core suite passes 1,002/1,002. Nested Markdown inside safe HTML
  now excludes provably unrelated simple ASCII reference definitions before
  reparse, avoiding needless repeated definition expansion while retaining
  conservative replay for escaped, entity, Unicode, or unusual-whitespace
  labels. The external-process Core fixture is under separate verification.
  These are local correctness and diagnostic measurements, not a qualified
  110% browser-relative result, full top-500 audit, or counterbalanced release
  benchmark. Investigate theme/resource lookup and layout work next.
- After merging current main, the signed-out README query path returned a
  generic preview whose SHA correctly failed the same-byte source guard.
  The audit-only public-token path now obtains the path from the already
  validated, commit-bound fixture and verifies the exact README bytes; an
  authenticated response with a mismatched SHA still fails closed. A fresh
  rebuilt Release rank-34 replay passes 99.64% text, 99.88% structure,
  8/8 images, zero unavailable assets, and clean exit. The merged WinUI suite
  passes 3,239/3,239 and the app rebuild has zero warnings/errors. The local
  replay ran alongside subprocess gate tests and its timing is diagnostic
  only (first/full 6.17x/4.32x), not a qualified performance verdict. The
  previous 500-case dispatch was canceled because it was pinned to the known
  bad commit; a fresh full run is required on this fix.
- September 28 local rank-24 source check: the pinned `vuejs/vue` GitHub
  article itself shows a broken 16-by-16 contributor image in Edge. The
  article's GitHub Camo URL returns HTTP 404, while the authored origin
  currently returns a 40,401,720-byte (38.5 MiB) SVG, above the immutable
  8 MiB SVG admission ceiling. The browser oracle correctly fails closed with one
  unavailable visible image out of 18. This is not evidence of a native
  decoding regression, and neither the image gate nor the SVG safety ceiling
  has been waived. The release contract needs an explicit disposition for
  upstream-broken images before a literal 500/500 no-unavailable verdict can
  be claimed; all images GitHub successfully decodes still require exact-byte
  replay and zero false-unavailable results.
- September 29 local source-bound checkpoint: the x64 Release app and audit
  harness rebuild with zero warnings/errors, and the current WinUI/Core/GitHub
  suites pass 3,262/3,262, 1,003/1,003 (fast Core), and 466/466. The pinned
  rank-95 (`justjavac/free-programming-books-zh_CN`) replay still fails closed:
  its final renderer generation 4 reports `markdown-unchanged` source admission
  after a later settings callback, so no native/Edge timing verdict is valid.
  The raw README has six image tokens; GitHub's rendered article exposes five
  images and omits the defunct Travis badge, which the native source path
  currently reports `Offline`. Neither source-admission timing nor that image
  discrepancy has been waived. The older-head full 500-case workflow
  `36501559942` completed failed; it is not current-candidate release evidence.
- September 29 continuation (current unpushed worktree): the source-admission
  failure was traced to assigning Markdown while the renderer was unloaded,
  before its first pipeline generation advanced. A pending exact-source
  admission now binds to the matching document's completed generation instead
  of being discarded as `generation-not-advanced`; unrelated Markdown hosts
  cannot overwrite the target admission. Focused source-admission tests pass
  29/29. A rebuilt x64 NativeAOT rank-95 replay reached full native traversal
  with 100% visible-text coverage, 99.55% structure, zero native unavailable
  images, 428.6743 ms source-admitted first-viewport readiness, and
  1019.0232 ms charged full traversal. These are **not** Edge-relative verdicts:
  offline Edge source replay stopped at native viewport 40/42 because its
  physically measured maximum was 35.283 viewport units while the native
  profile requested 36.000; the native final offset was 36.134. The page
  extents differ by about 2.35%, inside but still subject to the unchanged
  0.90–1.10 fidelity envelope. A strictly terminal, evidence-recorded replay
  clamp is under verification; no arbitrary mid-document movement is waived.
  The full current-head 500-case same-byte run and qualified physical release
  benchmark remain open.
- The top-500 workflow now builds one verified NativeAOT x64 app artifact and
  distributes identical full-file hashes to every shard; consolidation rejects
  mixed executables or dependencies. The ordinary PR/scheduled run is
  explicitly named a *live diagnostic*, not a same-byte release qualification.
  Only a fresh all-500 `same_byte_release` dispatch can earn that named gate.
  The current PR still points at older commit `1c00c83`; the older-head
  workflow `36501560943` is not evidence for these local fixes.
- September 29 local continuation: a verified fresh x64 NativeAOT rank-95
  diagnostic used the exact `36501559942` pinned manifest and reached all 42
  native viewports with 100% visible-text coverage, 99.55% structural score,
  and zero native unavailable images. The source-bound Edge replay succeeded
  through its physical bottom with four recorded terminal-clamp observations.
  The audit had wrongly compared the mean of those clamped Edge offsets with
  the unclamped native mean; it now validates the reported mean against the
  actual captured Edge offsets while the independent contract still verifies
  every requested movement, clamp, extent envelope, and final tail. Focused
  WinUI audit tests pass 45/45 and the full Node replay suite passes 74/74.
  The corrected rank-95 comparison fails the unchanged 98.5% bidirectional
  captured-GitHub/source text gate: source-to-GitHub is 0.982995574 over
  4,293 source tokens, GitHub-to-source is 1.0. A privacy-safe numeric artifact
  records this discrepancy. Comparing the exact captured README with GitHub's
  rendered article accounts for all 73 extra source-replay word tokens: the
  pinned Edge-side Markdown parser leaves 71 `:worried:` and two `:100:`
  shortcodes literal, whereas GitHub and the native parser render them as
  emoji. A general, licensed, pinned shortcode mapping is being added to the
  offline Edge parser; neither captured Markdown nor the text gate is altered. The
  local AOT artifact has no build provenance sidecar and is expressly
  diagnostic-only. A separate diagnostic using an older pinned manifest
  accidentally selected `rust-lang/rust` at rank 95; it exposed a real
  source-bound page-height ratio of 0.867 and an overlapping `<picture>` logo,
  both still open, but is not the justjavac rank-95 result. Removing a
  redundant comparer allocation from the shared text-format key brings the
  default Core+WinUI pack to 1,254,912/1,258,291 managed bytes and
  529,996/537,600 compressed bytes; this passes the unchanged lean package
  caps in the coordinated diagnostics-off build. The additional numeric SVG
  stage trace is compile-time diagnostic-only; its 14 focused tests pass, and
  the trace is absent from this shipping package. The physical R1 leg lost
  foreground and failed; its fail-fast harness
  fix passes 38 focused tests, but no R1,C1,C2,R2 verdict exists. The full
  current-head 500-case same-byte audit and release matrix remain open.
- September 29 paint-evidence follow-up: six cases in the retained older-head
  ranks 201–225 shard timed out with the same post-arm 660×189 Win2D region,
  while the audit demanded coverage of a 660×499 ScrollViewer viewport; none
  had a visible pending image at the timeout. The readiness probe now
  intersects the viewport with the snapshot-sized CanvasVirtualControl before
  tracking exact painted-region and pending-image-mask coverage. It continues
  to fail closed on an unpainted pixel inside that intersection and rearms on
  snapshot/revision/DPI changes. Focused readiness tests pass 27/27, audit
  source-contract tests pass 41/41, and the current Release x64 JitHub app
  builds with zero warnings/errors. The older artifact did not record the
  canvas/snapshot extent, so this correction is **not yet proof** that those
  six cases are resolved; a fresh exact-source replay and current-head full
  audit remain required.
- September 29 local continuation: the preserved current-source, pinned
  `justjavac/free-programming-books-zh_CN` rank-95 NativeAOT replay traverses
  all 42 viewports and passes 100% text, 99.76% structure, and zero native
  unavailable images. Visual inspection of native tile 0 nevertheless shows
  a large inline image overlapping the following heading and prose; a
  content-score pass is not a visual-fidelity pass. The same-byte Edge/native
  first-viewport ratio is still
  1.815x, above the unchanged 1.10 target; charged full traversal is 0.927x.
  This one case does not qualify the aggregate release gate. A bounded
  EventPipe attempt attached to the diagnostic AOT process but did not flush
  a usable trace; its rerun also encountered an intermittently unavailable
  external Travis SVG, so no per-SVG stage attribution is claimed. The
  previous green case remains intact. The short-document paint-coverage fix
  now reads snapshot size coherently; the combined focused Release x64 WinUI
  readiness/source-contract tests pass 68/68. These tests do not replace the
  six exact-source shard replays, full 500-case audit, or physical R1,C1,C2,R2
  run.
- September 29 offline-oracle follow-up: the pinned GitHub gemoji MIT alias
  adapter now expands ordinary Marked text tokens without rewriting code,
  HTML, link destinations, or unknown aliases. The exact rank-95 source has
  71 prose `:worried:` and two `:100:` tokens that require expansion; a
  separate code-span shortcode remains literal. The vendored alias map,
  extension, and license are hash-pinned; the loader uses bounded reads and
  rejects symlinked files, while the generator rejects same-file, hardlink,
  and Windows case-variant output paths. The real-Edge focused integration
  tests pass 23/23, and the full strict 500-case merger/mutation fixture
  passes, including unchanged 1.10 sentinels. These are oracle/gate tests,
  not a fresh 500-case native/Edge release result.
- September 29 local NativeAOT diagnostic for pinned ranks 1–25 (no clean
  provenance sidecar) completed 19/25. The six failures were two case-sensitive
  README/tree handoffs (ranks 2 and 8), a source replay that counted a raw
  `<form>` GitHub removed (rank 5), an Edge scroll-anchor displacement after
  image decode (rank 7), HTTP 429 acquiring the pinned Linux README (rank 18),
  and an already-broken OpenCollective contributor SVG in GitHub's own Vue
  article (rank 24). None is waived or counted as a release pass. The first
  three software causes now have targeted changes: 38/38 focused ViewModel
  tests, a hash-checked canonical-article replay that passes rank 5, and a
  bounded target reissue that passes the exact rank-7 viewport profile. The
  pinned raw-fetch 429 path now has a single authenticated Contents-API
  fallback with exact blob/size verification (29/29 corpus tests); the full
  same-byte Edge suite passes 16/16. Ranks 2, 8, 18, and 24 still require
  end-to-end re-audit or external-source disposition. Several passing cases
  remain materially over the unchanged first-viewport 1.10 ratio, including
  rank 19 at 7.81; native readiness in the slow cases arrived 208–661 ms
  after RenderCompleted, often with one visible image still pending. Resolver
  timing alone does not explain that delay; uncontended per-stage SVG/decode/
  publish evidence is needed before choosing a pipeline fix. The complete
  500-case release audit and physical R1,C1,C2,R2 benchmark remain open.
- September 29 local continuation: the pinned rank-18 Linux README was
  recovered through the single authenticated GitHub Contents-API fallback
  after an injected raw HTTP 429; its exact 6,034-byte Git blob and SHA-256
  matched the manifest. The end-to-end rank-18 replay refused an older AOT
  binary, as intended by the freshness gate, and remains to rerun on a fresh
  app. The current top-500 merger had compared URL-alias rows with unique
  payload counters; the validator now deduplicates by payload SHA-256 while
  rejecting duplicate URL hashes, conflicting sizes, fractional sizes, and
  altered manifests. Its complete 500-case mutation suite passes. Historic
  artifacts had this mismatch in 196 of 216 retained available README
  manifests across ranks 26–275; that finding is not a current 500-case pass.
  Fresh Release/x64 packages for all 14 shipping projects pass the unchanged
  size gate (Core+WinUI 530,522/537,600 compressed bytes, managed payload
  1,255,936/1,258,291 bytes); the full GitHub renderer suite passes 485/485.
- An uncontended, provenance-unqualified NativeAOT rank-11 replay passed
  content with 100% text, 99.89% structure, zero unavailable images, and
  173 traversed viewports. Its source-bound first-images-ready interval was
  447.46 ms versus same-byte Edge 345.80 ms (1.294x, still above 1.10),
  while charged full traversal was 1,876.01/2,630.50 ms. The first SVG
  EventListener trace produced no records under NativeAOT, so an opt-in,
  statically rooted numeric callback replaced that transport. A fresh AOT
  rank-22 replay then emitted 80 stage records for eight first-viewport SVGs.
  Tracing on yielded 383.21/99.90 ms native/Edge first-images-ready; the
  same executable with tracing off yielded 413.31/113.00 ms (3.66x), both
  with 100% text, 98.66% structure, zero unavailable images, and two complete
  viewports. Worker open/render and bitmap upload/cache contribute, but the
  synchronous trace writer perturbs scheduling and these runs are diagnostic,
  not release-speed evidence. The SVG burst needs a measured architectural
  optimization; the full 500-case release audit, physical counterbalanced
  benchmark, and rank-24 external broken-image disposition remain open.
- September 29 current-source local NativeAOT follow-up (still no clean
  provenance sidecar): a fresh pinned rank-95 replay traversed 46 viewports,
  passed 100% text and 99.69% structure with zero unavailable native images,
  and measured 0.936x first-images-ready and 0.794x charged traversal against
  exact-byte Edge. The previously reported overlap was absent in the fresh
  bitmap and is covered by static and late-image layout regressions; this one
  diagnostic case is not an aggregate release pass. Fresh rank-22 trace-off
  improved to 197.8054/104.8 ms (1.887x) first-images-ready, with an 8.0652 ms
  theme snapshot, but still misses 1.10. A separate trace-on run was slower at
  258.1592/99.6 ms; eight visible SVGs all used worker instance 1, and the
  bounded worker-queue stage reached 45.52 ms for opens and 28.45 ms for
  renders. There was no cold secondary startup or large font-catalog wait in
  this trace. It cannot yet distinguish an Energy Saver decision from the
  two-concurrent-render eligibility threshold; synchronous tracing is not a
  benchmark. Rank 19's strict offline Edge source replay found 28 visible
  GitHub-injected, accessibility-hidden Octicons for heading permalinks and
  code-copy controls, although the authored README has no SVG markup. The
  captured-article checker now excludes only those identified GitHub controls;
  the authored-source checker still rejects unsupported SVG, including forged
  Octicons. The full Node README-audit suite passes 84/84, and an exact-byte
  rank-19 Edge replay now reports complete semantics. Its existing native
  diagnostic still had a 604 ms first-image boundary while concurrent fixture
  tests were active; no quiet performance verdict or full rank-19 audit pass
  follows. The current-head 500-case audit, source-matched visual gate,
  package/current-head CI checks, and physical R1,C1,C2,R2 benchmark remain open.

- September 29 qualification follow-up: the top-500 visual comparator now
  labels live GitHub screenshots as cross-style diagnostics and computes a
  separate source-replay SSIM only when raw README hash, viewport DIPs, DPR,
  and color scheme match; absolute viewport position replaces a mutable
  ScrollPercent mapping. Its focused policy tests pass, but no visual-fidelity
  release verdict follows until fresh native/source tiles cover the cases.
  The optional secondary SVG worker now retains a failed-slot sentinel while
  its visible queue drains, preventing an immediate retry loop; the unchanged
  two-visible-render eligibility is verified by the 32/32 worker-scheduling
  tests. The performance-gate fixture accepts the new numeric source-lookup
  trial schema without relaxing unknown-field rejection or thresholds; the
  focused Core filter passes 18/18. The much larger external-process fixture
  class was stopped after an accidental broad selection and remains
  **incomplete** locally, not a release pass. The WinApp CLI sandbox sample
  smoke script now also accepts `-Configuration Release` while retaining Debug
  by default and its existing positional arguments; its invocation contract
  test passes, but the Release sandbox UI run and DPI/theme matrix have not
  been performed. PR #99 still points at the previous committed head
  `1c00c83`; its 19 failed top-500 shards and queued product benchmarks are
  not current-tree verdicts. The current tree must be committed, pushed, and
  independently requalified without weakening any gate.

- September 29 fresh local x64 NativeAOT diagnostic (verified native payload,
  but dirty-tree/no-provenance and therefore not release evidence): after an
  older executable was correctly rejected by the app-freshness guard, the
  current-source publish passed pinned, exact-byte rank 19
  (`NousResearch/hermes-agent`) with 100% text, 99.87% structure, eight
  visible images, zero unavailable, and complete Edge source semantics. Its
  source-bound first-image time was 188.03/112.10 ms native/Edge (1.677x),
  while charged full traversal was 296.86/275.60 ms (1.077x). Rank 22
  (`TheAlgorithms/Python`) also passed content with 100% text, 98.66%
  structure, zero unavailable; first-image time was 221.543/89.5 ms (2.475x)
  and charged full traversal 133.13/130.40 ms (1.021x). Both first-image
  cases still miss the unchanged 1.10 gate. Source-matched rank-19 screenshot
  coverage was 12/13 viewports with mean SSIM 0.190; it is diagnostic only,
  not a visual-fidelity pass. The rank-22 SVG stage trace shows one distinct
  tiny source spending 63.95 ms in host admission while seven others did not;
  the 128-striped content gates likely collided, because their leases span
  rendering and cache publication. A bounded exact-key gate replacement and
  before/after audit are underway; neither this inference nor these two
  partial cases qualify the full current-head top-500, package/architecture,
  visual, or physical benchmark gates.

- September 29 local continuation: the exact-key SVG admission replacement
  passes 34/34 focused gate, image, and cache tests. It preserves the bounded
  overflow path, cancellation rollback, and the existing 16-provider cap, but
  its fresh default Core+WinUI managed package total is 1,260,032 bytes,
  **1,741 bytes above** the unchanged 1,258,291-byte cap; compressed packages
  total 532,261/537,600 bytes. It is not package-qualified, and the earlier
  NativeAOT diagnostic predates this change. The complete local offline Edge
  replay tests pass 84/84. A separately pinned schema-12 reference harness
  has a fresh Release x64 build with stable output hashes and 121/121 focused
  tests, but no physical R1 leg or counterbalanced verdict has run. The full
  500-case release workflow already supports manual dispatch on a final pushed
  head (`diagnostic_rank=0`, `same_byte_release=true`); PR-triggered audits
  are diagnostic only. The pinned `vuejs/vue` contributor SVG currently has a
  GitHub Camo 404 and a 40,401,720-byte authored origin, exceeding the fixed
  8 MiB SVG ceiling. That external broken asset remains a strict 500/500
  disposition blocker; it is not silently waived.
  The local complete-500 merger and adversarial mutation fixture passes, as do
  the NativeAOT provenance and native-artifact verifier test scripts. A WinApp
  CLI 0.7 x64 Release sandbox smoke reached Math, Mermaid, HTML, SVG, and
  progressive-scene pages; its first theme sweep exposed a real sample-shell
  Dark-mode navigation/source-editor contrast defect. A sample-owned opaque
  `ApplicationPageBackgroundThemeBrush` shell root now fixes the translucent
  Dark-over-Light composition: the targeted x64 Release build has zero
  warnings/errors, the five-page sandbox smoke passes, and Light/Dark/forced
  contrast *preview* screenshots are legible. The preview is not OS High
  Contrast, and the sandbox screenshot DPI was not established; 100/150/200%
  and actual OS High Contrast remain unverified. These focused results do not
  substitute for the current-head release corpus or device/theme matrix.
  A diagnostic-only SVG secondary-worker policy trace now records bounded,
  numeric eligibility/queue/startup snapshots without changing the two-visible
  render threshold. Its focused diagnostic tests pass 4/4 and the default
  Resvg Release build succeeds with zero warnings; a fresh current-source
  rank-22 NativeAOT trace is still required before making a worker-policy
  change. Earlier rank-22 traces did not record Energy Saver at the decision
  point and therefore cannot prove why only one worker handled eight SVGs.

- September 29 fresh current-source rank-22 follow-up: a verified x64
  diagnostics-enabled NativeAOT app and pinned, exact-byte Edge replay passed
  content (100% text, 98.66% structure, eight visible SVGs, zero unavailable)
  but **failed performance and visual qualification**. Host admission wait was
  at most 0.03 ms across the eight SVGs, consistent with removing the prior
  63.95 ms striped-key collision; this is a different cold run, not a controlled
  before/after speedup claim. Source-bound first-image time was
  1,444.11/72.40 ms native/Edge (19.946x); charged full traversal was
  1,458.99/108.70 ms (13.422x). The 0.446 source-matched SSIM covered only
  one of two native viewports because the native 698 px extent exceeds the
  Edge 682 px extent and the bottom crop was not fully covered. The visual
  result is incomplete, not a pass. Numeric worker-policy tracing establishes
  x64/24 logical processors, Energy Saver off, and secondary eligibility:
  the second worker was requested after two visible renders queued, started,
  and became ready only after the tiny render batch drained, so all eight
  content transactions stayed on worker 1. Cold font-catalog wait on worker 1
  was 783.89 ms; pool scheduling waits summed 5,651.17 ms across 16 queue
  events (maximum 804.72 ms). This run has no clean-tree provenance sidecar and
  is diagnostic only; neither its content pass nor its policy evidence clears
  the unchanged full-500 or physical release gates. The source changes since
  this diagnostic include a range-rectangle iterator consolidation that puts
  a fresh default Core+WinUI package at 1,257,472/1,258,291 managed bytes
  and 531,388/537,600 compressed bytes, with zero native payload. Those caps
  must be remeasured after subsequent changes; the focused geometry suite
  passed 11/11, while broader edge-case coverage and the full suite remain.

- September 29 local validation continuation: pinned Rust worker tests pass
  21/21 with strict Clippy; the full Edge-required SVG pixel suite passes
  96/96 in a fresh isolated artifact directory. The renderer managed suites
  passed Core 1,012/1,012 plus two isolated deadline-sensitive cases, Math
  76/76, Mermaid 115/115, SVG 80/80, TextMate 61/61, and Conformance 9/9;
  the app passed 3,307/3,307 normal and 3,310/3,310 diagnostics-enabled
  tests before the latest same-byte/payload-link edits. Reproducibility
  packing produced all 14 packages, but the strict Core+WinUI managed-size
  check measured 1,263,104/1,258,291 bytes, **4,813 bytes over**; a
  reviewed immutable HTML-scope struct trim reduced that result to
  1,261,056/1,258,291 bytes, still **2,765 bytes over**. The final-source
  first-viewport and payload-relative-link tests passed 28/28 in both normal
  and diagnostics-enabled Release/x64 configurations with zero build warnings;
  two further semantically bounded HTML-scope trims pass the focused safe-HTML
  suite 82/82 and reduce the strict Core+WinUI result to
  1,259,520/1,258,291 bytes, still **1,229 bytes over**. Removing unused
  private-record generated code in four SVG data carriers then brought the
  final lean pair to 1,253,888/1,258,291 managed bytes and
  530,680/537,600 compressed bytes; the unchanged size gate passes. Both
  reference and candidate directories packed exactly 14 packages after locked
  restore. Focused SVG/HTML tests pass 109/109. The strict 14-package
  compliance/reproducibility check subsequently passed: SPDX/notices,
  pinned Rust source/checksum/license policy, six selected-RID native assets,
  and matching normalized packages across both directories. Full JitHub
  Release/x64 tests pass 3,308/3,308 normal and 3,311/3,311 diagnostics-enabled,
  with zero build warnings. The worker payloads are still unsigned development
  assets; production signing, the source-matched 500-case corpus, and the
  physical R1,C1,C2,R2 benchmark remain open. These local passes do not qualify
  those release gates.

- September 29 default x64 NativeAOT continuation: a diagnostics-off Release
  app published after locked restore and passed native-artifact verification.
  Its first pinned rank-22 same-byte Edge replay failed **before measurement**:
  the Markdown control received its source while initially unloaded, leaving
  `AutomationPipelineGeneration` at zero; the source-admission contract
  correctly rejected later render/paint evidence with
  `assignment-generation-unavailable`. Edge captured the 2,550-byte README
  and eight assets, and the app rendered/published viewport readiness, but no
  native/Edge ratio or fidelity verdict is valid from this run. The renderer
  assignment/lifecycle binding is being repaired without weakening the
  generation fence, followed by an exact-byte rerun. This local AOT artifact
  has no clean-tree provenance sidecar and is diagnostic only.
- September 29 follow-up: deferring the source assignment until the renderer's
  `Loaded` event moved the rank-22 replay past the zero-generation failure, but
  it still failed **before measurement** with
  `assignment-generation-superseded`. A bounded admission trace recorded source
  assignment to generation 3 and render completion at generation 4; the
  required binding rejected the mismatch. The traced artifact is
  `E:\worktrees\artifacts\pr99-rank22-loaded-assignment-trace-20260929`.
  The initial layout may advance the pipeline after `Loaded`, so source
  admission is being moved to the first completed positive-size layout, while
  retaining the exact generation fence. No native/Edge performance or fidelity
  result is claimed from either failed run.
- September 29 verified layout-admission continuation: the shared Markdown
  viewer now waits for its renderer's first completed `LayoutUpdated` at
  positive finite width and height, and detaches the one-shot handler on
  admission/disposal. Its focused Release/x64 contract tests pass 6/6; the
  full app suites pass 3,309/3,309 normal and 3,312/3,312 with diagnostics.
  A fresh diagnostics-off x64 NativeAOT app passes artifact verification.
  Its pinned rank-22 same-byte replay now passes the **unchanged** source/
  generation fence, 100% text, 98.88% structure, all eight image assets,
  and zero native unavailable images. This is still one dirty-tree diagnostic,
  not a release pass: native/Edge first-images-ready is 2.143x (above 1.10),
  charged traversal is 0.947x, and source-matched viewport SSIM is 0.507 with
  complete tile coverage. The native and Edge screenshots visibly differ in
  background, link/heading colors, typography, and heading rules; exact cause
  and an appropriate fidelity treatment require further analysis rather than
  waiving the result. Evidence:
  `E:\worktrees\artifacts\pr99-rank22-layoutupdated-default-aot-20260929`.
  The full current-head 500-case, qualified physical counterbalanced, and
  source-matched visual release gates remain open.
- The same verified local AOT app also passed a pinned rank-2
  (`sindresorhus/awesome`) exact-byte content replay: 100% text, 99.54%
  structure, all three images, zero unavailable, and 52 native viewports.
  Its first-images-ready ratio is still 1.336x, above 1.10; only 48/52
  source-matched visual tiles were comparable, so visual fidelity was not
  qualified. A separate rank-8 (`nilbuild/developer-roadmap`) replay rendered
  its exact README and assets without a native failure, but the strict Edge
  source traversal failed at a physical bottom clamp: native requested 7.2
  viewport heights at capture index 8/10, while Edge's measured maximum was
  6.938. The existing arbitrary 90%-of-capture-index condition rejects that
  earlier terminal suffix despite native/Edge extents being within the
  unchanged 0.90–1.10 gate. A read-only content audit found that the apparently
  differing code/table/image counts mixed distinct sources, decorative
  images, and UIA control instances; the authored 14 images are present and
  empty-alt separators are correctly absent from UIA. Its one extra distinct
  link is raw-relative versus Edge-resolved URL counting, not a missing
  clickable link. These two cases do not close the 500/500 gate.
- The rank-8 terminal replay is now source-bound to matching Git blob, raw
  SHA-256/byte count, and decoded-text SHA-256. It accepts only a stable,
  contiguous physical-bottom suffix with the original 0.90–1.10 full-page
  extent gate; the arbitrary capture ordinal was removed. The focused live
  Edge Node suite passes 18/18, and the WinUI link/clamp contracts pass
  12/12; full WinUI Release suites pass 3,317/3,317 default and 3,320/3,320
  diagnostic. The fresh x64 NativeAOT artifact verifies, and its pinned
  exact-byte rank-8 local replay now content-passes with 100% text, 99.55%
  structure, 12/12 distinct assets, zero unavailable images, and 117/117
  distinct canonical link destinations (123 clickable native link instances).
  This is **not** a performance/fidelity pass: native/Edge first-image-ready
  is 10.752x, full traversal 3.521x, same-byte viewport SSIM is 0.270, and
  only 8/10 visual tiles match because the terminal positions differ. The
  single-case runner reports content PASS but does not enforce aggregate
  release p95/visual gates. Artifact:
  `E:\worktrees\artifacts\pr99-rank8-terminal-exact-20260929`.
- A second pinned rank-8 local replay reproduces the initial-layout queue
  stall (278.7 ms waiting, 20.7 ms worker wall; the first run waited
  294.8 ms), so the 10.752x first-image ratio is not explained by layout
  computation alone. Rank 5 (`EbookFoundation/free-programming-books`) also
  now content-passes on the fresh AOT app after the paint-ack change, with
  100% text, 99.63% structure, zero unavailable images, but its first-image
  ratio is 5.896x and layout queue time is 318.8 ms versus 29.6 ms worker
  wall. Its visual tile coverage is only 9/11 (81.8%). Rank 18
  (`torvalds/linux`) still fails the strict source-admitted readiness pair
  with `document-identity-missing` after 15 seconds; that failure is being
  diagnosed and must not be waived. None of these single-case content passes
  establishes the 500/500 or 110% release goals.
- Rank 8's first-images-ready tail is independently bounded: 486.3 ms after
  render-complete. Twelve source resolutions overlap and finish within a
  76 ms start window; the slowest visible source takes 56.4 ms, while the
  ready paint follows the final source result by about 236 ms. The large
  `contrib.rocks` SVG is below the fold and cannot explain this first-viewport
  tail. Existing release artifacts do not carry per-visible-image worker
  stage IDs, so the remaining decode/raster/upload/paint attribution is open;
  no provider has been blamed from source-fetch timing alone.
- A fresh pinned rank-149 (`sherlock-project/sherlock`) local replay also fails
  the strict source-admitted readiness pair with `document-identity-missing`.
  Both rank 18 and rank 149 paint generation 4 with a 145-character
  (`SourceUtf16Bytes=290`) cached-preview placeholder instead of their pinned
  6,034/5,562-byte README sources. This is a reproducible navigation/source
  publication error, not a missing paint callback or an image download; the
  exact-document identity gate correctly rejects it. The cause is the
  signed-out audit account's synthetic preview tree: it lacks the pinned
  README path while the dedicated README request has verified corpus bytes.
  Normal fallback then selects `preview-readme` and overwrites the real
  prepared README. A same-byte-only, validated prepared-README standalone
  fallback and regression are being implemented; ordinary navigation must
  retain its existing authority rules.

- September 29 visual-evidence gate hardening: same-byte merge now fails closed
  unless the comparison identifies the source-matched viewport reference,
  covers every native viewport (matched and total counts agree and coverage is
  100%), and includes finite mean/minimum SSIM values in [0,1] with a coherent
  minimum/mean ordering. Adversarial merge fixtures exercise missing, partial,
  mismatched, non-finite, and out-of-range evidence. This prevents missing or
  incomplete pixel-comparison evidence from being mistaken for a qualified
  case. No numeric SSIM fidelity floor is established by this change; the
  source-matched visual fidelity release gate remains open pending calibration
  and documented threshold selection.
- September 29 same-byte continuation: fresh diagnostics-off x64 AOT artifact
  verification passed at
  `E:\worktrees\artifacts\pr99-scheduler-source-default-aot-20260929`; this
  is a dirty-tree local diagnostic without canonical workflow provenance.
  Rank 18's pinned README source-view case now passes exact source identity at
  `E:\worktrees\artifacts\pr99-rank18-scheduler-source-exact-20260929`.
  Rank 149's corrected alert-oracle replay now passes the exact-source content
  contract (99.79% visible text, 99.71% structure, zero unavailable images) at
  `E:\worktrees\artifacts\pr99-rank149-alert-oracle-exact-20260929`, but
  first-image-ready is 3.241x, source-matched SSIM is 0.447, and only 7/8 native
  viewports are comparable; it is not a performance or visual pass. Rank 8's
  first post-scheduler sample overlapped the CPU-heavy merger suite and is
  contaminated. Two subsequent quiet samples show the scheduler queue reduced
  to 0.174/0.166 ms and worker wall to 12.4/12.5 ms, yet native first-image
  readiness remains 15.3x and 10.84x Edge, and full traversal 4.38x and 3.74x
  (`E:\worktrees\artifacts\pr99-rank8-scheduler-quiet1-20260929` and
  `E:\worktrees\artifacts\pr99-rank8-scheduler-quiet2-20260929`). Quiet1
  source-matched SSIM is 0.270 with only 8/10 viewports covered. These results
  identify substantial post-queue image/readiness work, but do not qualify
  performance. The completeness-gate mutation suite passed before the current
  sentinel expansion; tests for adding ranks 2, 5, 8, and 22 to the existing
  per-case 110% sentinel list are updated but not rerun while the quiet
  benchmark lane is reserved. No full-500, release-performance, or
  source-matched pixel-fidelity gate is closed by these local cases.
- A further quiet current-AOT rank-5 same-byte replay content-passes with
  100% text, 99.63% structure, all 18 image sources, and zero unavailable
  images. Its native first render/image-ready is 333/856 ms, layout queue is
  0.161 ms and worker wall is 24.4 ms, but the UI continuation is 268 ms;
  first-image readiness remains 7.295x Edge and full traversal 3.238x.
  Source-matched tile comparison covers only 9/11 viewports (mean SSIM
  0.262), so both speed and visual completeness remain unqualified. Evidence:
  `E:\worktrees\artifacts\pr99-rank5-scheduler-quiet-20260929`. A separate
  rank-8 raster-trace diagnostic shows its twelve source resolutions complete
  within roughly 96 ms of the first start, but the final ready paint follows
  the last source result by roughly 265 ms. This isolates additional
  scheduling/raster/paint time; it does not identify one provider as the cause.
- A test-only WIC memory qualification harness now generates BMP, GIF,
  ICO/PNG, JPEG, and PNG inputs, exercises JPEG EXIF orientations 1–8, and
  checks preview/tile dimensions in serial and two-way batches. A further
  test decodes two distinct near-input-limit 4092/4088-by-2048 BMPs
  concurrently and applies an architecture-specific test-host ceiling; it
  passes with about 136 MiB 2 ms-sampled private-byte growth on x64. The
  Release/x64 GitHub renderer suite passes 533/533 after the prefetch
  caller-thread regression was added. Earlier sampled growth was about
  69 MiB for one near-limit BMP and 20 MiB for JPEG. These samples are
  diagnostic, not worst-case peak or production guarantees. WebP and AVIF remain
  unqualified without approved deterministic fixtures or encoders; the
  production WIC transform still lacks a proven per-codec memory ceiling.
- The README audit's full local Node suite passes 85/85 after the narrowly
  captured warning-alert Octicon allowance, and the full synthetic top-500
  merge/mutation suite passes after adding ranks 2, 5, 8, and 22 to its
  unchanged 110% per-outlier sentinels. These exercise harness contracts,
  not a live 500-repository current-head run. The visual merger now checks
  reported coverage against the actual native tile array; an adversarial
  claimed-1-of-1/actual-two-tiles fixture is rejected, and the full synthetic
  merger suite passes after this addition.
- A fresh double-pack of the current local 14 shipping NuGet projects passes
  normalized byte-for-byte reproducibility, strict unknown-license and
  reviewed-license policy, generated SPDX/notice checks, all six selected-RID
  native-asset inspections, and the unchanged package-size caps. The
  current post-session-fix Core+WinUI pair is 1,254,912/1,258,291 managed
  bytes and 531,298/537,600 compressed bytes. All 34 tracked package lock
  files match the Git index after rehydrating their three-RID graphs; the
  workflow's locked restores pass. Evidence:
  `E:\worktrees\artifacts\pr99-current-pack-postsession-20260929`. This
  local tree remains uncommitted, and its resvg workers are unsigned
  development artifacts, so production signing and current-head CI are open.
- WinApp CLI 0.7.0 reports an attached, input/capture-ready x64 Windows
  Sandbox. The existing Release sample's `-NoBuild` sandbox script passes all
  five Math, Mermaid, Safe HTML, SVG, and Progressive Scenes navigation/UIA
  checks and captures screenshots at
  `E:\worktrees\artifacts\pr99-winapp07-sandbox-smoke-20260929`.
  This validates the new sandbox automation path and that earlier built sample,
  not the latest scheduler edits or the required 100/150/200% Light/Dark/High
  Contrast device matrix; rebuild and rerun on a stable current tree.
- A fresh diagnostics-enabled local x64 NativeAOT payload after the scheduler
  edits verifies at `E:\worktrees\artifacts\pr99-diagnostic-aot-postscheduler-20260929`.
  Its source-matched rank-8 SVG trace
  (`E:\worktrees\artifacts\pr99-rank8-svg-image-stage-diagnostic-20260929`)
  shows four first-wave small SVGs spending about 219–225 ms apiece in
  provider open/render, including about 163–166 ms of worker scheduling wait;
  actual render transactions are only 1.65–2.56 ms and host UI dispatch and
  publish are below 1.2 ms. The first visible PNG resolves in 55.2 ms, while
  the full viewport-ready wait after render-complete is 314 ms. The 291 ms
  layout-continuation metric is elapsed time awaiting UI-context resumption,
  not measured UI execution; publication itself is below 0.4 ms. This points
  to scheduling/competition, not expensive SVG pixels or a slow final swap.
- The speculative prefetch collector and resolver now run together on a worker
  so a synchronously completed collector cannot resume host-policy work inline
  on the UI caller; both the opt-in session and legacy explicit-prefetch path
  use that boundary. A dedicated-thread regression passes, and the full
  Release/x64 GitHub and app suites pass 533/533 and 3,323/3,323. Fresh local
  AOT rank-8 diagnostics at
  `E:\worktrees\artifacts\pr99-rank8-prefetch-quiet-20260929` and
  `E:\worktrees\artifacts\pr99-rank8-prefetch-quiet3-20260929` content-pass,
  with first-viewport image waits of 269/290 ms versus 596 ms in the two
  prior scheduler samples. Rank 5 at
  `E:\worktrees\artifacts\pr99-rank5-prefetch-quiet-20260929` content-passes
  with a 138 ms image wait versus 474 ms previously. This is a useful local
  diagnostic, not a qualified controlled performance result: native/Edge
  first-image-ready remains roughly 13–15x on rank 8 and 7.5x on rank 5,
  layout continuation sometimes remains near 300 ms, visual completeness and
  fidelity are still below the release gate, and the dirty-tree AOT build has
  no canonical provenance sidecar.
- One repeat rank-8 audit stalled inside the redundant pinned-README UIA open
  query even though the app had already published exact current-process,
  current-generation render and image-ready evidence. The external 90-second
  watchdog terminated that incomplete run; it was not counted as a pass
  (`E:\worktrees\artifacts\pr99-rank8-prefetch-quiet2-20260929`). The harness
  now avoids only that redundant query when the source-bound render signal
  matches process, host, generation, expected README Git blob SHA, raw SHA-256,
  and byte count. Missing or mismatched evidence still takes the normal exact
  tree-item path, and all later source, paint, text, traversal, and comparison
  checks remain mandatory. The source-identity mutation test and next live
  rank-8 run content-pass; a full 500-case current-head audit remains required.
- The source-bound fast path also checks repository, immutable ref, and exact
  case-sensitive README path; the later render/viewport-ready pair now applies
  the same context check, not just byte and blob hashes. A rank-8 live run at
  `E:\worktrees\artifacts\pr99-rank8-sourcecontext-20260929` exposed a real
  mismatch: the pinned Git tree has `readme.md`, while the preview tree returned
  `README.md`. The fast path correctly refused that context and the old UIA
  route completed, but relative-resource resolution would use the wrong case.
  Audit-only early-README preparation now falls back to the exact pinned
  endpoint path when a preview-tree candidate differs by case. A fresh local
  diagnostics-enabled x64 NativeAOT rank-8 replay at
  `E:\worktrees\artifacts\pr99-rank8-sourcepath-dispatchdiag2-20260929`
  content-passes with exact `readme.md` source-path admission, 100% visible
  text coverage, 99.55% source structure, and zero unavailable images. The
  exact-context fast path skips the redundant UIA tree open. This is not a
  qualified speed/fidelity pass: first-viewport images take 976 ms versus
  67 ms in the same-byte Edge replay, and source-matched tile SSIM is 0.270
  with only 8 of 10 viewports comparable.
- The current Release sample was rebuilt for WinApp CLI 0.7 sandbox execution.
  All five Math, Mermaid, Safe HTML, SVG, and Progressive Scenes navigation/UIA
  checks passed; screenshots are at
  `E:\worktrees\artifacts\pr99-winapp07-sandbox-current-20260929`. Three
  screenshots were visually inspected without obvious unavailable content.
  This is a smoke check, not the required DPI/theme/device matrix.
- The intermittent cached-tree refresh race is fixed by resnapshotting the
  active preview after the asynchronous tree load before deciding whether to
  refresh README content. A deterministic delayed-old-blob regression covers
  that interleaving. The full Release/x64 app suite passes 3,331/3,331;
  Core's current-preview-workflow subset passes 1,023/1,023, GitHub renderer
  533/533, SVG provider 80/80, and the README audit Node suite 85/85. This
  does not substitute for current-head PR CI or a full top-500 replay.
- Rank-8 dispatcher diagnostics after the source-path fix put initial layout
  worker construction at 13.30 ms, worker admission wait at 0.25 ms, and
  elapsed worker-completion-to-layout-continuation at 309.05 ms. The audit-only
  dispatcher sentinel ran 0.36 ms after enqueue and its heartbeat maximum gap
  was 28.28 ms, so the apparent continuation delay is not itself evidence of
  a 309 ms UI-thread block. A second replay showed the same pattern with a
  290.64 ms continuation and 0.087 ms sentinel. The scheduler now returns its
  work-item task directly on immediate admission.
  The fresh x64 AOT rank-8 replay at
  `E:\worktrees\artifacts\pr99-rank8-scheduler-fastpath-20260929` still
  measured 307.97 ms continuation, 13.39 ms worker layout, 0.83 ms sentinel,
  and 25.20 ms maximum dispatcher heartbeat gap, with 1,021 ms first-image
  readiness versus 67 ms in the same-byte Edge replay. The direct task alone
  did not close the gap; a session-level async wrapper remains under
  investigation. This diagnostic result is not a qualified benchmark.
- Fresh AOT focused same-byte audits also content-pass for rank 149
  `sherlock-project/sherlock` and rank 214 `coder/code-server` at
  `E:\worktrees\artifacts\pr99-rank149-scheduler-fastpath-20260929` and
  `E:\worktrees\artifacts\pr99-rank214-scheduler-fastpath-20260929`.
  The latter admits the exact `docs/README.md` source path, resolves all eight
  distinct images including the two `./assets/` PNGs, and reports zero
  unavailable images; rank 149 also reports zero unavailable. These focused
  replays do not replace the current-head 500-case audit or close fidelity and
  performance gates.
- The session-level `ScheduleLayoutWorkAsync` still awaited the scheduler task
  with `ConfigureAwait(false)` after the scheduler fast path, introducing the
  observed thread-pool completion hop. It now returns the original task and
  retires its linked cancellation/work lease on task completion. Focused
  session lifecycle tests pass 31/31, the full GitHub renderer suite 536/536,
  the full app suite 3,331/3,331, and the Core preview subset 1,024/1,024.
  The fresh diagnostics-enabled x64 NativeAOT rank-8 replay at
  `E:\worktrees\artifacts\pr99-rank8-session-directtask-20260929`
  content-passes with 13.59 ms layout, 0.15 ms completion-to-continuation,
  and 58.97 ms initial renderer completion, versus 321.60/307.97/366.60 ms
  in the preceding local rank-8 diagnostic. First-viewport image readiness
  remains 1,026 ms versus 116 ms in its same-byte Edge replay, and the native
  content extent remains 7.5% taller with source-matched tile SSIM 0.270 and
  only 8/10 comparable viewports. Resolver observations and the first paint
  cluster about 600 ms after renderer completion, but the resolver evidence
  can be from speculative prefetch rather than visible `ImageBox` activation;
  this first trace alone cannot distinguish late viewport realization from
  thread-pool dispatch delay. That gap and true style/layout divergence remain
  open. These are uncontrolled one-case
  diagnostics, not a qualified counterbalanced release result.
- The activation-stage replay at
  `E:\worktrees\artifacts\pr99-rank8-image-activation-20260929`
  resolves the first part of that gap: `RealizeVisibleEmbeds` and
  `ImageBox.EnsureLoading` run at the renderer commit with a measured 660×611
  viewport, but the opt-in progressive visible resolver's `Task.Run` waits
  290.29 ms between queue and entry on the thread pool. That run's layout is
  14.05 ms, first renderer completion 63.45 ms, and first-viewport images
  ready 819 ms (same-byte Edge 76 ms). Prefetch image-resolution records
  alone cannot prove visible activation, which is why the bounded stage trace
  was required. A bounded, prioritized visible-start scheduling fix is under
  investigation; image-ready parity remains unclaimed.
- The session-owned bounded visible image activation lane has now replaced the
  per-image `Task.Run` start on the progressive path. It uses one general worker
  on constrained/x86 systems and an optional visible-only second worker on
  other hosts; a failed second startup still drains overscan, while a failed
  first startup retires the request for a later bounded retry. The real Release
  GitHub renderer suite passes 554/554, including a 500-visible-image burst,
  cancellation, overscan eviction, partial startup, and retry tests. A fresh
  diagnostics-enabled x64 NativeAOT rank-8 replay at
  `E:\worktrees\artifacts\pr99-rank8-visible-lane-20260929` measured only
  0.54 ms from visible activation queue to entry versus 290.29 ms before the
  lane; layout stayed 14.69 ms and first renderer completion 59.56 ms. But
  first-viewport image readiness was still 981 ms, so the 110% Edge gate is
  **not** closed. The concurrent source-bound Edge replay failed at
  `native-terminal-profile-bottom`; this run has no valid same-byte timing or
  SSIM verdict. Downstream resolver/asset-read/worker attribution and a clean
  repeat source replay remain required.
- Rank 33 in the older 500-case dispatch failed while opening an atomically
  replaced first-viewport readiness file. The readiness wait already retries
  transient Windows sharing violations; the subsequent audit-evidence copy
  now also takes a single-file snapshot with replacement-safe sharing and a
  bounded retry, publishing the snapshot atomically. Focused exclusive-writer
  and persistent-lock regressions pass 3/3. Missing optional evidence is not synthesized, and a
  persistent read failure still fails the audit. This repair needs a fresh
  rank-33/full-corpus replay before the old failure can be marked resolved.
- The rank-8 visual gap is genuine layout/style divergence, not a DPI or
  screenshot-crop mismatch: both sides use a 660×611 Light viewport at scale
  1, but native document extent is 5,214 px versus the offline Edge source
  replay's 4,850 px; matching nearby scroll offsets does not restore SSIM.
  The current offline source oracle is a pinned Marked + locally authored
  Primer-like CSS page, not a screenshot of the captured GitHub website's
  actual stylesheet. It uses white surface, `#1f2328` text, and `#0969da`
  links in Light mode; the current JitHub README uses the app's `#F3F4F4`
  canvas, `#1F2321` text, and `#256B52` accent links. The existing optional
  GitHub app palette still differs in typography and role styling. Before
  treating pixel parity as qualified, define a fixed versioned Markdown-only
  GitHub/Primer style profile shared by the offline Edge fixture and native
  parity run, record profile/font fingerprints in both evidence streams, and
  retain separate default-app/Dark/High-Contrast product-theme QA. Do not tune
  the Edge oracle dynamically to the app palette or waive the SSIM/coverage
  gates; a separate live-website visual audit is needed for literal GitHub
  page fidelity.
- A fixed opt-in `github-primer-light` v1 profile now pairs the native README
  style sheet and audit-only JitHub host surface with the pinned offline Edge
  CSS fixture. The stylesheet's SHA-256 is recorded by both sides; ordinary
  app rendering remains on the JitHub theme, and High Contrast retains system
  colors. The current Release/x64 app build succeeds, the focused native
  evidence/snapshot tests pass 31/31, and the full offline README-audit Node
  suite passes 86/86. Native and Edge now report the same pinned profile ID,
  version, and canonical CSS SHA-256; the release merger rejects absent or
  mismatched identities and requires evidence that Edge loaded the CSS with
  matching integrity from loopback. The merge mutation suite passes. These
  are contract/build results, not a visual-fidelity or speed pass.
- A fresh dirty-tree x64 NativeAOT rank-8 source replay with the corrected
  profile and tight-list spacing passes content checks (100% text, 99.80%
  structure, zero unavailable images, clean exit) and records the exact
  native/Edge profile identity. Native/Edge extent is 4,939/4,850 px, but
  source-matched tile SSIM is only 0.128 and coverage only 8/9; first-image
  and charged full-traversal ratios are 12.32x and 3.17x. Linked text inside
  native headings incorrectly used body-sized link typography; inheriting the
  heading font fixes that visible defect and passes the full 559-test GitHub
  renderer suite, yet a second NativeAOT rank-8 replay still measures SSIM
  0.128, coverage 8/9, first-image 11.68x, and full traversal 3.14x. Its
  malformed nested-paragraph HTML remains a concrete vertical-spacing gap
  under investigation. No one-case timing or extent result qualifies the
  500-case or visual-fidelity release gate.
- The rank-33 (`microsoft/vscode`) dirty-tree NativeAOT source replay now
  completes the former readiness-evidence snapshot path without the Windows
  sharing exception, with 100% text, 99.88% structure, zero unavailable
  images, and clean exit. It is one successful reproduction of that failure
  path, not a current-head 500-case pass: source-matched visual coverage is
  only 5/6, mean SSIM 0.424, and first-image ratio 1.97x.
- Correlated timing review of the activation-lane rank-8 trace confirms the
  visible `ImageBox` enters its document-scope resolver 0.59 ms after enqueue,
  but the earliest underlying host-resolver entry in that run is about 611 ms
  later. Existing logs do not share a per-request identity, so the gap cannot
  yet be attributed to that specific visible image. The document-scope
  fetch-slot/in-flight path is a hypothesis under instrumentation, not a
  proven bottleneck. Four SVG provider opens also accumulate up to about
  226 ms of worker scheduling; GPU publication is not the dominant stage.
- The next correlated rank-8 diagnostic exposed an evidence-recorder defect:
  it discarded newly added image activation stages 7–15. After correcting
  that range and testing the 64-record cap, a fresh x64 NativeAOT replay shows
  the first visible image entering document scope at stage 7, spending about
  693 ms before stage 12 (fetch-slot wait start), then acquiring the slot and
  hitting the newly populated source cache essentially immediately (stages
  13–14). Thus the delay is **before** fetch-slot admission, not a 693-ms
  network, worker, or GPU operation. The document/shared in-flight locks are
  being investigated; their exact contribution is not yet proven.
- September 29 image-flight follow-up: both document and session single-flight
  maps now publish an incomplete task under their lock, start the resolver's
  synchronous prefix after releasing that lock, and remove the flight together
  with result publication. A synchronous-blocking-resolver regression proves
  a second source can proceed, and the Release GitHub renderer suite passes
  560/560. Two fresh dirty-tree x64 NativeAOT rank-8 replays measure stage
  7-to-12 at 0.014 and 0.007 ms, respectively, versus the previous 693 ms;
  the specific pre-fetch-slot lock stall is resolved. End-to-end first-image
  ratios are still 7.73x and 10.16x against same-byte Edge (native 469 and
  623 ms; Edge 61 and 61 ms), and full-traversal ratios are 1.75x and 1.66x.
  Image-source resolution and serial SVG open/render work remain measurable;
  the 110% release gates remain open. A bounded HTML paragraph auto-close
  regression passes, but the rank-8 screenshot and 4,939/4,850 extent are
  byte-for-byte unchanged because the production HTML renderer flattens the
  relevant block tree; native margin stacking is under separate investigation.
- The current remote PR #99 head is still the older `1c00c83` commit; its
  preview failure is the test-only 1-second foreground-sample deadline, while
  the local test now uses 10 seconds without changing the production 30-second
  readiness policy. The old top-500 and same-byte dispatches did not pass.
  The product-performance workflow formerly dispatched both feature-branch
  push and PR events for the same SHA on the single interactive runner; local
  workflow changes restrict push to `main` while retaining PR, schedule, and
  manual coverage. GitHub currently reports no required status contexts or
  repository rulesets, so making the interactive release check required still
  needs repository administration and cannot be claimed from workflow YAML.
- September 29 local worker/visual follow-up (dirty-tree diagnostics, not a
  release verdict): the pinned Rust worker was rebuilt reproducibly for x86,
  x64, and ARM64; its 24 native tests pass, all 76 locked Rust dependencies
  retain accepted license selections, and the x64 selected payload is 2.98 MiB
  against the unchanged 3.5 MiB ceiling. A bounded exact font-fallback memo
  reduces the rank-8 badge-class worker render stage from roughly 55 ms to
  4–7 ms while retaining byte-identical RGBA in focused tests. The new x64
  NativeAOT app contains that worker (SHA-256
  `64e83ecfc4c68966782471969cc8d42fa87cd1309e8aebbd11319cfdf2b2e219`).
  Rank 8 passes content checks (100% text, 99.91% structure, zero unavailable
  images, clean exit), but its first-image-ready ratio is still 6.05x Edge.
  Full-traversal is 1.04x in this one unqualified replay; it cannot establish
  a top-500 p95 pass. The versioned Primer-light v2 fixture fixes an old
  645-versus-660-pixel content-width mismatch and raises rank-8 source-tile
  SSIM from 0.128 to 0.451, but coverage remains 8/9 and native/Edge document
  extents remain 4,923/4,837 px. Standalone safe-HTML break and image-only
  paragraph geometry are under investigation. The full JitHub Release unit
  suite passes 3,339/3,339; fast Core tests pass 1,030/1,030; package-tooling
  self-tests pass. At this checkpoint, all-package size/reproducibility, the
  full 500-case current-head audit, and the physical counterbalanced release
  benchmark were outstanding. The latter requires a stable unlocked foreground desktop;
  an earlier local run lost foreground and did not qualify.
- The standalone safe-HTML `<br>` immediately before rank-8's image had been
  measured as a two-line 48-pixel DirectWrite buffer plus a 16-pixel Body
  margin. It now occupies one 24-pixel line with no paragraph margin while
  retaining its source span and semantic newline for selection/UIA. The full
  Release GitHub renderer suite passes 565/565. Image-only Markdown paragraph
  promotion remains under separate loaded-image geometry investigation; an
  unresolved-placeholder fixture does not establish browser-equivalent
  intrinsic-image line-box behavior.
- A fresh local full 14-package x64 Release pack passes the exact package-set,
  unchanged size, SPDX/license, and six native-asset checks. The Core+WinUI
  lean pair is 532,437 compressed bytes and 1,257,984 managed bytes (307 bytes
  under its unchanged managed ceiling); a second normalized 14-package pack
  matches all package hashes. The rebuilt resvg executables are unsigned
  development artifacts, so production Authenticode signing and the full
  release/device matrix remain open.
- Fresh current-manifest rank-22 and rank-33 same-byte dirty-tree AOT replays
  pass content with zero unavailable images and no renderer exception. Rank 22
  (`TheAlgorithms/Python`) clears the old semantic/height failure: 100% text,
  99.60% structure, 9/9 links, and native/Edge extent ratio 1.0865, but its
  first-image/full-traversal ratios are 2.052/1.153 and visual tile coverage is
  only 1/2. Current rank 33 is `yt-dlp/yt-dlp`, not the previous manifest's
  `microsoft/vscode`: it records 100% text, 99.85% structure, 0 unavailable,
  extent ratio 1.0206, first-image/full-traversal ratios 1.725/0.762, and
  visual coverage 126/130 with mean SSIM 0.086. These are diagnostic
  reproductions without clean/canonical provenance, not a 500-case or visual
  release pass. The old vscode rank-33 failure has not been retested against
  its old manifest.
- The full x64 Release static-SVG Edge oracle passes 81/81 pixel fixtures with
  the required fidelity thresholds when its artifact output is directed to a
  fresh external directory. Its old default wrote comparison files back into
  tracked repository artifacts and encountered locked-file errors on this
  machine; the default and PR workflow now use separate temporary artifacts.
  The focused default-path rerun passes without touching tracked output. This
  is a test-harness hygiene fix, not a waiver of any SVG fidelity threshold.
- September 29 cold source-read follow-up: a fresh x64 NativeAOT rank-8
  same-byte replay on the older pinned nilbuild manifest split the audit-only
  resolver's file-read timer into exact read and EOF probe. The first six
  1.2–21 KiB assets spent 107–135 ms each in `ReadExactlyAsync`; index lookup,
  file open, allocation, SHA-256 verification, and the EOF probe were small
  (apart from one 16 ms EOF probe). Subsequent assets, including a 342 KiB
  SVG, read in under 0.2 ms. The case still passes text (100%), structure
  (99.95%), complete image resolution, and a 1.0095 native/source extent
  ratio, but first-image-ready is 4.11x Edge in this unqualified replay.
  Source-tile SSIM is only 0.169 with 8/9 viewport coverage, so neither
  visual fidelity nor speed is qualified. The bounded cold exact-read
  scheduling cause is under repair; dropping byte-length or hash validation
  is not an acceptable shortcut.
- Old PR #99 top-500 CI evidence was built from the remote `1c00c83` head via
  GitHub's synthetic merge commit, not this local candidate. It was a live
  diagnostic run without same-byte capture and passed 378/500 cases. Of 122
  failures, 114 ended in a two-second `waiting-for-paint` readiness timeout
  with no loading image or pending image mask; four left deferred placeholders
  on traversal, two hit visible-image/traversal waits, and ranks 149/214
  reported relative README assets as unavailable. This narrows current-head
  regression targets but does not qualify any release corpus. Fresh same-byte
  replays and the complete 500-case release dispatch are still required.
- An exact-byte 160×80 PNG fixture (SHA-256
  `7a658bc11f15e70a1e500525dfdb86a4eaebe1ed1c5b0157e0888f32094d9bf0`)
  under the pinned Primer-light v2 CSS at 660 DIPs and DPR 1 measures 96 DIPs
  of image-paragraph flow in Edge for both Markdown and safe HTML. Native
  measured 92 DIPs for promoted Markdown images and 96 for safe HTML. A
  narrow promotion-site change uses the resolved Body paragraph margin, and
  its focused six-fixture suite now measures 96/96; tight-list image-only
  paragraph suppression has a separate regression. The opt-in top-level
  positive-margin-collapse candidate also passes focused eager/lazy/reflow
  tests, but its first fresh Core+WinUI managed package was 1,259,008 bytes,
  717 bytes above the unchanged 1,258,291-byte cap. Subsequent API/code-size
  reductions reach 1,258,496 bytes, still 205 bytes over. This candidate is
  not release-qualified until the exact package gate and full tests pass.
- September 29 local audit-comparison correction: when the native page is
  slightly taller than the same-byte Edge page, Edge can validly clamp its
  final traversal capture before the native final offset. The old visual
  comparator cropped the full Edge screenshot at that native offset, falsely
  reporting incomplete source-tile coverage (rank 8: 8/9 viewports despite
  complete 4,837-pixel Edge capture). It also inferred each native tile's
  document top from rounded UIA scroll percent/view size while the renderer's
  exact capture top was discarded; rank 8's interior tiles could be misaligned
  by roughly 14–16 DIPs. The audit now records and validates exact renderer
  capture positions, crops source-page pixels at those positions, and uses a
  corresponding measured Edge terminal offset only for a documented clamp in
  the exact-byte-validated movement profile. The focused visual policy tests
  pass 15/15 across the plan's eight simulated raster scales, and the x64
  Release audit automation builds without warnings. This corrects comparison
  coverage only; it does not improve the renderer's actual pixels or satisfy
  the visual-fidelity or browser-relative performance gate. A fresh AOT replay
  is still required to quantify the remaining differences.
- September 29 continued local validation: the private SVG raster-plan value
  carrier no longer emits unused record equality/formatting members. A fresh
  default Core+WinUI pack is 1,257,472/1,258,291 managed bytes (819 bytes
  headroom); the unchanged package-size script and 122 focused renderer tests
  pass. The x64 Release JitHub app and audit automation build cleanly, the
  complete WinUI test suite passes 3,379/3,379, and the combined x64 NativeAOT
  artifact passes architecture verification. These do not qualify production
  signing, the full architecture matrix, or browser-relative speed.
- The fresh dirty-tree NativeAOT same-byte diagnostic replays for old-manifest
  ranks 8, 22, 33, 149, and 214 all pass their source/semantic/zero-unavailable
  checks and exit cleanly. This reproduces and resolves the old remote-head
  relative-image failures at 149/214, but is neither a current-head nor a
  complete-500 release audit. Rank 8 now has 9/9 same-byte visual tiles after
  exact renderer-top cropping, yet mean SSIM is only 0.358, minimum 0.043,
  and native first-image-ready is 1,682 ms versus Edge 67 ms (25.1x); the
  fidelity and latency gates remain open. The renderer itself first published
  in 59 ms, and exact-byte resolver reads were below 1 ms; the visible-image
  completion path is being traced. Native audit capture is fixed at 96 DPI,
  which still prevents qualified >100% DPR visual comparison and needs repair.
- September 29 DPI/fidelity correction in the current worktree: native audit
  captures now use the live XamlRoot scale and measured fractional viewport
  dimensions. Real Win2D target tests pass at 100%, 125%, 150%, 200%, and
  400%, including a shared 643×507 physical viewport. The source-bound audit
  takes its canonical integer CSS viewport from the renderer's captured
  logical dimensions and rejects changing/mismatched host geometry; focused
  visual-policy tests pass 24/24 and automation builds cleanly. The unordered
  list marker now centers in the existing gutter in both default and
  declarative layouts without moving text or ordered markers (focused 9/9).
  A fresh pair including those renderer changes still passes the unchanged
  strict managed-size cap at 1,257,984/1,258,291 bytes, with 307 bytes
  headroom. Physical-pixel source/native viewport matching at fractional DPI
  is being completed; the simulated and unit tests do not replace the
  required physical scale/theme/device audit.
- September 29 continuing local work: exact physical native viewport bounds
  are now carried through the source-bound comparator, which requires each
  native PNG to match the measured host within one physical pixel and crops
  Edge to the admitted PNG's exact dimensions. The logical CSS viewport,
  source/DPR identity, movement, and terminal-clamp checks remain enforced;
  focused visual-policy tests pass 30/30, including a fractional 400% case.
  The private code-line value carriers were simplified, bringing the last
  fresh Core+WinUI pair to 1,255,936/1,258,291 managed bytes before the next
  fidelity edits. A 5-DIP image exposed a real paragraph-flow shortfall:
  promoted Markdown and safe-HTML images occupied only 5 DIPs where the
  authored 24-DIP line box was required. The shared inline metrics and
  promoted-image measurement/vertical placement now agree on 24 DIPs, with
  17/17 focused geometry/RTL/inline-object tests passing. A fresh package and
  same-byte replay are still needed to quantify the combined candidate.
  Two diagnostic-only AOT rank-8 runs completed first-viewport images in
  424 and 402 ms, but repeatably left 255–302 ms between exact-byte resolver
  completion and SVG source preparation. The older 1,682-ms release result
  was not reproduced; the suspected redundant background dispatch is being
  directly instrumented. None of these runs qualifies the counterbalanced
  release performance gate or the full top-500 audit.
- September 30 continuing local work: an opt-in Primer-profile quote-tail
  margin collapse now handles both ordinary and promoted image-only paragraphs
  without changing default layout behavior. The full x64 Release GitHub
  renderer suite passes 606/606. SVG host preflight avoids an extra thread-pool
  dispatch when already off the UI thread, while retaining asynchronous
  offload for UI callers; a diagnostic-only dispatch-to-entry timer and
  focused tests pass 11/11. Neither change has yet been measured by a fresh
  AOT same-byte replay. Earlier stage-enabled rank-8 replays remained far
  outside the first-image-ready and visual gates (about 243–245 ms native
  versus 64–65 ms Edge, mean SSIM 0.231). The existing R1 physical
  counterbalanced run failed foreground/occlusion, observed-refresh, and
  renderer absolute gates before reaching C1/C2/R2. The prior 500-repository
  CI audit is diagnostic, with 378/500 passes and 19/20 shard failures; a
  fresh canonical NativeAOT same-byte 500 run is still required. These are
  open release gates, not qualified completions.
- The next source candidate is introducing a versioned Primer V4 comparison
  profile, with a dedicated list-container style role, CSS-sized quote accent
  geometry, and retained V1–V3 behavior. The same-byte Edge fixture now pins
  V4 CSS by SHA-256 and its Node suite passes 81/81. Readiness publication is
  being hardened against a stale painted layout revision, canceled generation,
  and a newer ready signal arriving while an older write drains. A separate
  CI cross-job artifact freshness check is under review because extracted
  NativeAOT artifact timestamps cannot establish source identity. These C#
  changes have not yet passed a final build, fresh AOT replay, or the release
  corpus; no renderer fidelity or speed result is inferred from the Node tests.
  The canonical V1–V4 profile CSS files now have explicit LF checkout rules;
  this prevents Windows line-ending conversion from invalidating the pinned
  byte hashes after CI checkout.
- September 30 validation in progress: the full x64 Release WinUI contract
  suite passes 3,402/3,402 after the painted-revision/cancellation and
  provenance changes, including the V4 Viewer routing and current-corpus
  workflow contracts. The V4 same-byte corpus suite passes 30/30 with its
  exact pinned CSS hash. A focused renderer run found and fixed sparse-theme
  list fallback, first-block margin, trailing quote accent extent, and generic
  heading-outline regressions; a final V4 customization test and complete
  renderer rerun are still pending, so V4 is not yet qualified. The top-500
  same-byte release workflow now fails closed if live metadata is unavailable;
  only diagnostic runs may use the validated historical snapshot. Search-stage
  rate limiting is classified without printing raw CLI responses. The current
  sandbox blocks `gh` network access, and its process policy blocks the
  browser-process Node test (`spawn EPERM`); neither a current PR CI run nor
  the release Edge/top-500 gate can be claimed from these local checks.
- September 30 final local source checkpoint: the V4/customization and
  bitmap-lifetime follow-ups now pass the full x64 Release GitHub renderer
  suite (624/624) and WinUI suite (3,403/3,403). The latter includes 47/47
  audit-source contracts. The unchanged strict package gate passes for the
  freshly packed Core+WinUI pair: 1,257,984/1,258,291 managed bytes and
  534,087/537,600 compressed bytes; the five optional packages were reused
  only after byte-for-byte SHA-256 verification. A current Release x64
  performance harness builds with zero warnings/errors (executable SHA-256
  `9CF373631F365E3B3D17F33A86D7D81B0C51D427D31CCE071C15592000E64FD3`).
  A fresh app NativeAOT publish compiles the managed app but cannot reach the
  linker: this sandbox cannot create/connect the SDK's .NET x64
  `ComputeManagedAssemblies` task host (`MSB4216`), including when the SDK
  apphost is invoked directly with `DOTNET_HOST_PATH` set. The broad Node
  audit suite likewise hits `spawn EPERM` in its browser-process test; the
  focused V4 same-byte corpus remains 30/30. The earlier physical
  counterbalanced attempt stopped after R1 with foreground/occlusion,
  observed-refresh, stationarity, scroll-frame, and cancellation failures.
  This checkpoint is not a NativeAOT, live same-byte Edge, 500/500 README,
  or qualified R1,C1,C2,R2 performance result; all of those release gates
  remain open. The isolated Rust worker's locked offline suite passes 24/24
  (one manual microbenchmark intentionally ignored), and the package-
  compliance tooling self-tests pass.
- Two independently packed and normalized current-source 14-package Release
  x64 sets passed the then-current compliance pipeline: exact package IDs,
  unchanged size limits, fail-on-unknown-license SPDX/notice and compatibility
  checks, 76 locked Rust dependency checks, six selected-RID native-ABI
  inspections, and byte-for-byte reproducibility. A subsequent PE inspection
  exposed a missing managed-architecture gate: Core, Math, and Mermaid were
  packaged as Amd64 rather than promised RID-neutral AnyCPU IL. These sets
  are **not** cross-architecture release candidates. Serial Rebuild produces
  I386/PE32 ILOnly outputs for all three projects on x86, x64, and ARM64;
  a package-level managed-PE guard and a fresh two-pack compliance pass are
  required. The worker packages are also unsigned development artifacts.
- The complete x64 Release Core test project passes 1,531/1,531, including its
  long external-process gate fixture, but this test host loaded the earlier
  Amd64 Core output. Re-run the suite after the final AnyCPU package/source
  candidate is frozen; this pass does not validate the corrected binary.
- The checked-in x86, x64, and ARM64 resvg worker executables currently
  report `NotSigned` under Authenticode. Preview diagnostics may inspect them,
  but production signing and trusted-certificate verification remain mandatory
  before the SVG release gate can pass.
- The WinApp CLI 0.7 sandbox sample smoke-test configuration passes its
  static contract check, and the installed 0.7.0 CLI exposes the sandbox
  launch and UI commands used by the script. This does not validate a live
  sandbox launch, UIA/ScrollPattern behavior, Mermaid/SVG paint, or screenshots;
  those require an unlocked Windows 11 24H2+ interactive runner with Windows
  Sandbox and virtualization enabled and a subsequent visual review. A fresh
  September 30 `winapp target snapshot sandbox --json` attempt fails before
  launch with `sandbox_start_failed`: its local gRPC connection to
  `localhost:80` is denied (`UnauthorizedAccessException`) by this execution
  environment. No live sandbox UI result can be inferred from the static check.
- The host-side SVG worker token now puts the Restricted Code SID in
  `SidsToRestrict`, as required by the Windows API, rather than incorrectly
  listing it among SIDs to disable; a source-contract regression passes. This
  local workspace process itself has an already-restricted token, and direct
  `CreateRestrictedToken` probes fail with Win32 87 even with no new SID list.
  Consequently the current SVG provider suite is 56 passed/29 worker-start
  failures, and the pixel suite is 15 passed/81 worker-start failures. These
  are not pixel-fidelity results; a normal-token isolated-worker runner is
  still required, with no in-process or less-restricted fallback.
- September 30 corrected package checkpoint: the strict release compliance
  path now checks the exact 14-package ID set and inspects every managed DLL
  under package `lib`/`ref` runtime assets for I386/PE32 IL-only AnyCPU flags.
  It also requires each of the 12 code-bearing packages to contain its
  expected runtime DLL and keeps the two metapackages DLL-free. Its self-tests
  reject missing/unexpected assets as well as the earlier package set's Amd64 Core,
  Math, and Mermaid DLLs. Five private Core snapshot/value carriers with no
  equality callers were simplified without changing public API. Two newly
  packed and normalized current-source x64 Release sets now pass the unchanged
  Core+WinUI limits (1,251,840/1,258,291 managed bytes and
  531,982/537,600 compressed bytes), 12 managed-asset architecture checks,
  six selected-RID native checks, locked Rust/license/SPDX/notices policy,
  and byte-for-byte reproducibility for all 14 packages. The final-source
  Core, Math, and Mermaid projects also rebuild without warnings for x86 and
  ARM64, with all six outputs inspected as I386/PE32 IL-only AnyCPU. This is
  cross-architecture compile/PE evidence, not a native runtime test. The
  preview CI pack step now uses the same serial MSBuild flags that produced
  the reproducible local pair; current PR CI remains unverified because the
  GitHub connection is unavailable here. At the package checkpoint, x64 Release GitHub
  624/624, WinUI 3,403/3,403, HTML 10/10, conformance 9/9, TextMate 61/61,
  Math 76/76, Mermaid 115/115, and package-checkpoint Core 1,531/1,531 suites
  pass. The Core suite exited normally after 1h17m with no failures or skips.
  This local package result does not sign the native
  workers, validate NativeAOT or the
  isolated worker under a normal token, or satisfy the physical and current-
  head same-byte 500-case performance/fidelity release gates.
  Focused Math, SVG-resvg, and TextMate release-evidence scripts now select
  their own fixed 2-, 4-, and 5-package scopes explicitly; the default strict
  path still requires all 14. Tooling self-tests reject a focused set under
  the default scope, and all three real-package component compliance runs
  pass SPDX/license, managed architecture, and applicable native checks.
  The sample x86/x64/ARM64 and audit-automation x64 Release builds succeed
  without warnings,
  and the WinApp CLI 0.7 sandbox-sample static contract passes. The JitHub
  x86, x64, and ARM64 Release compiles also succeed without warnings when the
  release security target is skipped for diagnosis; the unmodified x64 build
  cannot complete its locked NuGet vulnerability audit because this sandbox's
  network policy blocks restore. The local ReleaseSecurity test filter passes
  117/117, but it does not replace the live vulnerability audit. The security
  target remains mandatory for a qualified build and has not been waived in
  project configuration.
- September 30 viewport scene-scheduling checkpoint: the progressive block
  scene plan now withdraws queued or active Math/Mermaid preparation when a
  freshly measured viewport no longer contains its marker. Per-work identity,
  cancellation, and a publication-time visible-band check prevent stale
  results from surfacing after a rapid jump; re-entry can schedule fresh work.
  Focused deferral tests pass 18/18. After a forced product/test rebuild from
  the final scene source, the GitHub renderer suite passes 627/627; Math 76/76
  and Mermaid 115/115 also pass. The exact-source replacement 14-package x64
  Release set passes strict license, managed AnyCPU, selected-RID native, and
  package-size checks (1,252,352/1,258,291 managed bytes;
  532,009/537,600 compressed bytes). A second independently packed set is
  byte-for-byte reproducible for all 14 packages. The full source-fresh x64
  Release Core suite passes 1,531/1,531 in 1h25m; it compiles linked Core
  sources rather than `MarkdownProgressiveScenePlan.cs`, which is covered by
  the separate forced product/integration rebuild and GitHub suite. Inline Math remains
  eager because it requires an inline placeholder and paragraph-reflow design;
  this scheduling fix has no qualified live performance or fidelity result.
- September 30 SVG shared-memory lifetime checkpoint: source mappings are now
  created after worker admission and released after each Open/rare
  `DocumentMissing` reattach transaction; documents retain sanitized source
  bytes, not a second persistent native mapping. Output raster mappings are
  created only after render worker/font readiness, remain live through a
  bounded reattach/retry, and transfer to the returned raster only after
  metadata validation. This removes the former queue-depth multiplication of
  up to 8 MiB source copies and 64 MiB output mappings; it does not raise
  worker count, queue depth, or safety ceilings. The final SVG provider x64
  Release build has zero warnings/errors. Two final-source normalized 14-
  package sets pass strict Core+WinUI size (1,252,352/1,258,291 managed bytes;
  532,009/537,600 lean compressed bytes), managed/native architecture,
  locked license/SBOM, and byte-for-byte reproducibility checks. New source-
  and output-mapping focused tests pass 10/10 after an isolated serial rebuild.
  A test-only source marker was updated after the render method split. The
  isolated worker runtime remains unverified because this sandbox's already-
  restricted token causes worker creation to fail with Win32 error 87; a
  normal-token runner is required. Package compliance and focused lifetime
  tests do not establish live peak-memory or latency gates.
- A September 30 direct-process Node pass covers 78/78 non-browser assertions
  across eight fully passing README-audit test files, including the 30-case
  same-byte corpus and 19 source-replay contract assertions. The live Edge
  loopback case was deliberately excluded because browser child-process launch
  is denied here; one vendor-generator test and one browser-profile cleanup
  test independently fail with child-process `spawn EPERM`. These are test-
  environment gaps, not a same-byte Edge fidelity or full-500 pass.
- The current top-500 release mode is fail-closed when dispatched: it
  requires live metadata, all 500 cases, shared NativeAOT/source provenance,
  same-byte offline Edge replay, visual evidence, and passing per-case and
  aggregate timing gates. A separate manually dispatchable physical-release
  workflow now requires a configured, clean, ancestor, schema-12 frozen
  reference commit, calls the same-run reusable fresh NativeAOT/same-byte
  top-500 workflow, and conditionally runs the physical R1,C1,C2,R2 sequence
  on a dedicated interactive Windows 11 x64 runner. Its evidence join checks
  exact candidate/reference commits, clean source-tree identities, workflow
  run/ref, summary hashes, and exactly 500/500 README and same-byte cases;
  missing or failed evidence fails closed. The reusable audit publishes its
  attempt-qualified summary artifact name so a partial physical-job retry
  joins the correct earlier successful audit attempt; all release visual
  artifacts are attempt-qualified. Typed JSON pass/provenance checks reject
  string-valued `"false"` rather than accepting PowerShell truthiness.
  Release-evidence mutation tests,
  PowerShell/YAML static checks, 15/15 focused orchestration contracts, and
  48/48 WinUI automation source contracts and the rebuilt full WinUI suite
  (3,404/3,404) pass locally. This is workflow
  implementation, not an executed release result. The workflow is not yet on
  the default branch or a required release status; a repository administrator
  must select and set `MARKDOWN_RENDERER_RELEASE_REFERENCE_SHA` to an eligible
  committed schema-12 ancestor, and an eligible foreground/unoccluded physical
  runner is not available here. Runner hardware identity is an operator-
  configured attestation through its dedicated self-hosted label; the workflow
  verifies active Windows console state but cannot independently prove bare
  metal. Its release verdict states that limitation explicitly. The current
  PR's CI remains unverified because
  GitHub access is blocked in this environment. The existing
  `product-performance-gate` measures different JitHub routes and does not
  substitute for this physical sequence.
- September 30 SVG retention/cancellation checkpoint: disposing a document now
  waits for both cancellation callbacks and active render leases before
  releasing its lifetime source. Failed/canceled queue admission unregisters
  its cancellation callback outside the pool lock. Documents retain only the
  immutable sanitized snapshot, not the caller's potentially much larger
  backing buffer. Cancellation after shared-memory allocation prevents the
  worker operation and releases that mapping; pre-canceled preflight skips the
  full source copy. `ImageBox` also retires an unclaimed disposable result when
  cancellation wins even if the provider task has already completed. The
  rebuilt GitHub renderer integration suite passes 631/631, including four
  result-ownership regressions; the SVG lifetime/admission/preflight/gate suite
  passes 32/32. These are managed ownership proofs, not live memory benchmarks.
- The native parsed-resource LRU now retires all document aliases that pinned
  an evicted tree; TRIM clears both cached resources and live bindings. Alias
  metadata has a 1,024-entry LRU bound, and at most one currently admitted
  uncached over-budget tree survives cache hits; it is retired before another
  cache miss. This is retention eviction with transparent `DocumentMissing`
  reattachment, not an image-count rejection. Cache cost remains an estimate;
  the one uncached tree is bounded by admission/job ceilings, not falsely
  charged as a 64 MiB aggregate-cache guarantee. A per-worker async gate keeps
  TRIM out of the complete missing-document probe/attach/retry transaction,
  while distinct workers remain independent. Cancellation/font invalidation
  after a successful OPEN now compensates with CLOSE instead of leaving an
  unreturned binding. Rust tests pass 25/25; the separate pre-existing manual
  font microbenchmark is ignored and is not required fixture evidence. All
  three pinned offline Release worker builds pass; independent fresh-target
  builds also pass the repository's normalized PE payload comparison for x86,
  x64, and ARM64. This does not establish execution on all three architectures.
- Two normalized v10 14-package sets containing the rebuilt retention workers
  pass exact package-set, license/SBOM, managed AnyCPU/native RID, unchanged
  size, and package reproducibility checks. Core+WinUI remain at
  1,251,328/1,258,291 managed bytes and 531,663/537,600 compressed bytes.
  Artifacts are under `MarkdownRenderer/artifacts/packages-anycpu-v10-svg-retention-20260930`
  and the matching `-repro-` directory. These are unsigned development
  packages, not signed release evidence. The newly added real-worker
  concurrent TRIM/reattach test fails before rendering in this sandbox with
  `CreateRestrictedToken` Win32 error 87; its TRX is under
  `MarkdownRenderer/artifacts/svg-trim-runtime-attempt-20260930`. A normal-token
  Windows runner must execute the actual worker regression/fault/pixel suites.
  No in-process fallback, token relaxation, or runtime-test waiver was added.
- The physical evidence producer now publishes its exact attempt-qualified
  artifact name as a job output; a join-only retry consumes the successful
  producer's output rather than recomputing the retry's attempt number.
  Release-evidence mutations and the full top-500 merge/mutation fixture suite
  pass, and the rebuilt WinUI automation source contracts pass 48/48. Fixture
  500-case success is validator evidence only, not a live README audit. At that
  checkpoint, the configured GitHub proxy blocked CLI access; the later
  connector checkpoint below supersedes that PR-status limitation. The frozen
  reference/eligible physical runner, clean
  committed candidate, live same-byte Edge/top-500 evidence, trusted worker
  signing, and full device/DPI/theme/AOT matrix remain required; the recorded
  fidelity/latency failures have not been relabeled as external-only blockers.

- September 30 scene-lifecycle checkpoint: deferred block Math/Mermaid
  completions now coalesce into one follow-up while a pipeline is building;
  source/theme/width supersession keeps its immediate generation fence. The
  published pipeline cancellation scope survives ordinary completion, and a
  pre-published task barrier handles synchronous `RenderCompleted` reentrancy.
  Valid scene completions no longer rescan every cached result or allocate/sort
  diagnostics on the UI callback; actual diagnostic results retain source/code
  ordering. The compiled coordinator/scene tests and renderer public-API suite
  pass 648/648, with no failures or skips, in
  `MarkdownRenderer/artifacts/github-scenes-final-20260930`.
- First-viewport paint acknowledgement and full-traversal root UIA status now
  wait for visible registered Math/Mermaid source placeholders to be replaced,
  not merely for their worker result to finish. Checks use the current snapshot,
  generation, layout revision, measured zero-overscan band, and exact registered
  marker IDs; offscreen and unregistered code do not delay readiness. Disposed
  sessions retain only weak-owner-keyed marker strings until eager replacement
  or owner release, closing the disposal/notification gap without retaining
  renderer resources. Empty measured wrappers are skipped; invalid geometry
  fails closed. Existing peers receive listener-guarded native property refresh
  on status changes, including a one-shot post-lock paint retry after a busy
  snapshot; unload/disposal clears that retry. The rebuilt app suite passes
  3,406/3,406 in `MarkdownRenderer/artifacts/winui-scenes-status-final-20260930`.
  These include compiled pure behavior and explicitly source-wiring checks,
  not a live Narrator/UIA-event or device/theme/DPI qualification.
- The latest SVG managed lifetime/mapping/admission subset and token source
  contract pass 33/33 in
  `MarkdownRenderer/artifacts/svg-managed-lifetimes-final-20260930`; real-worker
  pixel/fault/reattach tests remain required on the normal-token runner. The
  current JitHub x64 Release code, including its readiness probe, compiles with
  zero warnings, but the standard build fails at the mandatory dependency-
  security gate's locked restore/audit. The gate was not disabled. This is not
  a successful Release build or NativeAOT-publish qualification. Completed-
  scene LRU alone would not bound the active graph: visited `VectorSceneBox`
  instances also retain eager native `VectorSceneDrawing` resources until their
  snapshot retires. Immutable scene retention and lazy/budgeted drawing leases
  must be designed and qualified together; those and copy-on-write publication
  remain open rather than being hidden by these coalescing/readiness fixes.

- The final scene-lifecycle sources and rebuilt SVG retention workers are now
  included in two freshly packed, normalized v11 14-package sets. Exact package
  IDs, MIT-compatible license/SBOM checks (including all 76 locked Rust
  packages), managed AnyCPU/native RID checks, unchanged size gates, and
  byte-identical package reproducibility pass. Core+WinUI are
  1,257,472/1,258,291 managed bytes and 534,703/537,600 compressed bytes; the
  remaining managed headroom is only 819 bytes and must not be treated as room
  for unrestricted API growth. Evidence is under
  `MarkdownRenderer/artifacts/packages-anycpu-v11-scene-lifecycle-20260930`, its
  `-repro-` counterpart, and the matching package-compliance directories.
  Workers remain unsigned development assets, not production-signed release
  packages. This packaging pass does not satisfy the physical benchmark,
  same-byte Edge/top-500, device/theme/DPI, or NativeAOT runtime gates.

- September 30 current-PR connector checkpoint: read-only GitHub access confirms
  PR #99 remains open at committed head
  `1c00c83ddfd2e8b65e05cb57e359a3f092b37271`; these local changes have not been
  published or run by its CI. The renderer preview run `36501560954` stopped
  with 999/1,000 broad Core tests passing: the sample-sequence readiness test
  hit its hardcoded one-second test deadline under hosted load. Readiness
  policy tests now use an injectable monotonic clock/delay seam; production
  still uses `Stopwatch` plus cancellable `Task.Delay`, with the unchanged
  30-second release preflight timeout, poll interval, and five consecutive
  qualified samples. The rebuilt focused suite passes 25/25; the CI's broad
  Core partition passes 1,030/1,030, and both separately executed deadline-
  sensitive Core tests pass. TRX evidence is under
  `MarkdownRenderer/artifacts/visibility-clock-ci-fix-20260930` and
  `MarkdownRenderer/artifacts/core-ci-visibility-clock-fix-20260930`. This is a
  local CI-test fix, not a successful remote rerun, physical benchmark, or
  repeat of the previously passing 501-case external-gate partition.
- The same committed head's latest ordinary top-500 run `36501560943` remains
  failed. Nineteen failing 25-case shard summaries report 122 failed cases;
  the remaining 25-case shard is green, and consolidation fails. Those logs
  use `AUDIT_REQUIRE_SAME_BYTE_CORPUS=false`, so they cannot qualify the fresh
  same-byte release corpus even independently of their failures. Returned logs
  are truncated; no claim is made that every case/tile was inspected. The
  connector can supply artifact file references, but the local download still
  fails through the configured network proxy. Four observed rank-151–175
  failures reached the two-second app paint-readiness signal wait; blank result
  fields and `native unavailable=-1` mean no native result was returned, not
  proof of missing images. Their preserved readiness progress JSON is required
  to distinguish pending paint, loading content, stale generation, process
  exit, or evidence-write failure. Existing and new local fixes must not be
  attributed to this older run. Worker Clippy also passes offline with all
  targets and warnings denied; actual isolated-worker, signed/AOT, physical,
  DPI/device, and browser-relative gates remain open.
- The audit's ordinary and source-admitted readiness waits now detect process
  exit using a retained native exit handle and preserve their two- and
  fifteen-second deadlines. Failure messages include only the last complete
  allowlisted progress row from a bounded 32 KiB tail, with a 4 KiB record cap;
  partial records, malformed counters, unknown strings, and document/URL fields
  are not echoed. Review also found four legitimate probe reasons were rejected
  by the app's progress writer, including pending/unverified deferred scenes.
  Those fixed tokens are now persisted and covered without misreporting scenes
  as loading images or relaxing readiness. Pure formatter/writer tests and
  explicitly source-wiring tests are included in the freshly rebuilt
  3,414/3,414 WinUI suite:
  `artifacts/audit-readiness-failure-diagnostics-20260930/jitHub-winui-tests-final2.trx`.
  The x64 Release automation build passes with zero warnings/errors. The latest
  app code also compiles with zero warnings, but its standard Release build
  still fails at the mandatory locked dependency restore/audit gate; no gate
  was bypassed. These diagnostics have not identified or fixed the 122 older-
  head audit failures without their progress artifacts or a fresh replay.

- September 30 local raster-memory checkpoint: the ordinary-raster path now
  borrows the already admitted immutable source buffer through a read-only
  seekable WinRT stream adapter instead of copying it into a second full-size
  stream. Both wrappers stay alive through decode/upload completion; cancellation
  is checked between native stages, never by disposing an in-use decoder stream.
  The earlier display-sized integration suite passes 15/15 in
  `MarkdownRenderer/artifacts/raster-borrowed-stream-20260930/raster-borrowed-stream.trx`.
  The rebuilt stream/WIC diagnostic passes 5/5 in
  `MarkdownRenderer/artifacts/wic-borrowed-stream-20260930/wic-borrowed-stream-final.trx`.
  It includes a deterministic self-owned 2048-by-8193 WebP fixture, reproduced
  byte-for-byte using a locked offline MIT-compatible test-only encoder. Actual
  local Windows decoding accepts the borrowed stream for that codec. The probe
  sampled 137,142,272 bytes (about 131 MiB) of private-byte growth for its serial
  WebP decode despite a 256-by-1024 preview. This is diagnostic evidence of
  decoder intermediates, not a precise transient peak or a bounded production
  memory result. BMP/GIF/ICO-PNG/JPEG/PNG/WebP are covered; AVIF and production
  tile-bounded decode memory remain unqualified.
- Large-original-source/bounded-preview CPU preparations now request exclusive
  weighted admission from the existing opt-in fair document scheduler. This
  prevents multiple such native decoders from consuming the session's ordinary
  preparation slots concurrently; it does not bound one decoder's intermediate
  memory or change default non-session behavior. Cancellation, grant races,
  fairness, retirement/drain, and actual eager Win2D stream integration tests
  have been added and await the coordinated source-frozen build. The initial
  integrated build compiled the shipping assemblies, but test compilation
  failed on two new residency-test issues (since repaired), and the newly built
  Core+WinUI pair exceeded its unchanged managed-size cap by 10,445 bytes.
  Optional marker traversal and drawing-residency policy are being moved into
  the opt-in Performance package; a fresh build/pack must pass before claiming
  this candidate satisfies the package gate. Prior v11 package results do not
  qualify these newer sources. Inline-Math atomic source-map/UIA continuity,
  native scene band residency, and unchanged-geometry relayout are under the
  same integrated verification; no browser-relative or complete-500 pass is
  inferred from these local improvements.

- September 30 integrated progressive-architecture checkpoint: the first
  source-frozen renderer run passed 673/674 tests. It caught an unchanged-height
  relayout shortcut incorrectly accepting noncanonical committed block
  placement; the shortcut now verifies neighbor/document continuity before
  bypassing suffix arrangement. The next integrated run passed 676/677; its
  sole failure was a new linked-Math test incorrectly treating the intentional
  separator between adjacent atomic labels as a duplicated formula. The
  corrected focused tests pass 2/2, checking exactly one Math semantic node,
  one formula text span plus its semantic boundary, keyboard activation
  metadata, and source wiring for Hyperlink/Invoke/focus on that same peer.
  Source-wiring assertions are not live UIA validation. Full native-layout
  cancellation retention and inline native-resource residency are undergoing
  additional implementation/review before the next integrated run.
- The rebuilt fast Core suite passes 1,032/1,032 (the long-running external
  gate fixture is explicitly excluded, not waived) in
  `MarkdownRenderer/artifacts/progressive-core-fast-20260930/progressive-core-fast.trx`.
  The rebuilt full WinUI managed suite passes 3,414/3,414 in
  `artifacts/winui-progressive-architecture-fixed-20260930/winui-progressive-architecture-fixed.trx`.
  These results include the updated optional readiness delegation and linked
  atomic input routes, but precede the later cancellation/residency work.
  A fresh all-fourteen v12 pack fails the unchanged size gate:
  538,246/537,600 compressed bytes and 1,266,688/1,258,291 managed bytes.
  That failure prevents claiming its license/native/reproducibility checks
  passed; older v11 packs do not qualify the newer candidate.
- Read-only GitHub connector access now exposes the older-head ordinary
  top-500 job logs and artifact metadata. Run 36501560943 remains failed; its
  consolidation explicitly used `AUDIT_REQUIRE_SAME_BYTE_CORPUS=false`, so it
  is not same-byte release evidence. Its artifacts are unexpired, but the
  local artifact download still fails with a refused connection. Selected
  shard logs identify failed repositories but omit the underlying readiness
  cause; they cannot prove a renderer fix or reclassify failures as upstream.
  The audit now emits only the existing bounded, allowlisted progress summary
  and capture-boundary booleans for a failed case, never raw exception text,
  README content, asset URLs, or local paths. This diagnostics change is under
  verification; it does not relax completeness, performance, or fidelity gates.

- September 30 continuing local qualification: the integrated x64 Release
  renderer suite passes 692/692 in
  `MarkdownRenderer/artifacts/progressive-architecture-final-native-band-fixed-20260930/progressive-architecture-final-native-band-fixed.trx`.
  Native drawing leases now follow final arranged bands for block scenes,
  nested inline Math, list markers, and table cells. Height-changing
  measurement reaches a final-band fixed point; incoming preparation is
  transactional with respect to outgoing native pins, cancellation releases
  the incoming prefix, paint excludes retirement, and a text-only band retires
  previous scene leases. Ordinary eager builds remain eager, including when
  borrowing a performance session. This is native drawing residency, not a
  hard bound on immutable completed-scene graphs. Inline readiness still
  scans runs/queries character regions; its indexed replacement is the next
  local milestone, so no warm-scroll frame-budget claim is made.
- Legitimate private-state simplifications remove unused record-generated
  equality/hash/printing helpers while preserving constructor parameter names,
  defaults, field semantics, and required `with` behavior. The EXIF-oriented
  raster planning copy explicitly preserves original source-admission metadata.
  The isolated rebuilt fast Core suite passes 1,033/1,033 in
  `MarkdownRenderer/artifacts/progressive-core-final-types-20260930/progressive-core-final-types.trx`.
  The long external-process gate partition is still running separately; its
  omission from this fast run is not a waiver. A fresh fourteen-package v13
  strict compliance pass succeeds: 531,182/537,600 compressed lean bytes and
  1,243,648/1,258,291 managed lean bytes, all fourteen SBOMs, compatible locked
  licenses (including all 76 Rust packages), six selected-RID native assets,
  and twelve architecture-neutral managed assemblies. Evidence is under
  `MarkdownRenderer/artifacts/compliance-anycpu-v13-progressive-architecture-20260930`.
  Its hash manifest is not an independent two-build reproducibility proof;
  workers are still unsigned development artifacts. The next indexed candidate
  needs a fresh compliance/reproducibility pair rather than inheriting v13.
- The bounded failed-case terminal-readiness diagnostic passes the rebuilt
  full WinUI managed suite (3,416/3,416) and automation Release x64 build. A
  subsequent review found that JitHub's wrapper decremented active-renderer
  accounting before asynchronous snapshot retirement finished. The wrapper
  now keeps providers alive until the drain and UI tracking release complete,
  and retains unresolved accounting on failure/hang. That change is under
  integrated verification. It does not prove the earlier COM wrong-thread
  fail-fast is fixed, nor cover unpublished in-flight build results. Full COW
  publication, completed-graph hard bounds, oversized-codec memory, same-byte
  500/500 rendering/fidelity, and the physical R1,C1,C2,R2 release benchmark
  remain open; no gate is weakened or declared complete from these checks.

- September 30 indexed-readiness checkpoint: a fresh x64 Release renderer
  build and full suite pass 696/696 in
  `MarkdownRenderer/artifacts/progressive-indexed-readiness-hotpath-20260930/progressive-indexed-readiness-hotpath.trx`.
  Inline geometry is a generation-stamped, scalar-only, active/candidate-band
  index, not a retained DirectWrite layout. Warm readiness and deferred-marker
  probes do not issue native character-region queries, rebuild the index, or
  walk scene commands for estimated memory weights. The regression asserts
  all three counters remain unchanged through 100 readiness probes plus a
  marker scan. Stale geometry fails closed; canceled incoming preparation
  keeps the prior index and native pins. Arranged RTL/X-origin changes and
  150% rasterization use the current snapped origin. A distant band in a tall
  measured paragraph can still require one background geometry recapture;
  genuinely new bands still estimate requested scene weights under the
  snapshot lock. These costs remain unqualified against the physical frame
  and browser-relative gates; this is not a completed-graph byte bound.
- Both freshly built and normalized v14 fourteen-package sets pass exact
  package identity, unchanged size ceilings, SPDX/notice/license policy,
  native-RID, and managed AnyCPU checks. Lean Core+WinUI are
  532,956/537,600 compressed bytes and 1,247,744/1,258,291 managed bytes.
  Every complete archive SHA-256 matches its independent reproduction.
  Evidence is under
  `MarkdownRenderer/artifacts/compliance-anycpu-v14-indexed-readiness-20260930`
  and its `-repro` directory, with packages in the corresponding
  `packages-anycpu-v14-indexed-readiness-20260930` directories. Production
  worker signing and actual NativeAOT/runtime qualification remain required;
  these unsigned development packs are not a release qualification.
- The long Core external-process gate-enforcement partition also finishes
  501/501 with no skips after 1h30m in
  `MarkdownRenderer/artifacts/progressive-core-external-gate-20260930/progressive-core-external-gate.trx`.
  Together with the separately rebuilt 1,033-test fast partition, this covers
  current Core behavior and the unchanged rejection fixtures. The 501 cases
  are synthetic evidence/gate mutations, not 501 repositories or measured
  physical performance. A fresh default-isolation Node invocation is denied
  process creation (`spawn EPERM`) on this sandbox; any in-process pure-harness
  checks must remain separate from actual Edge/oracle qualification.
- The shutdown-wrapper drain change now passes the rebuilt full WinUI
  managed suite (3,420/3,420) in
  `artifacts/winui-retirement-drain-guarded-20260930/winui-retirement-drain-guarded.trx`.
  An independent lifecycle audit found that renderer teardown discards
  cancellation-retirement tasks for unpublished pipeline results and that
  pre-snapshot layout-construction failures can strand already-created boxes.
  Per-control task/resource drain and explicit construction ownership are
  being implemented next. Until verified, the wrapper fix alone must not be
  presented as complete provider-teardown ordering or a fix for the earlier
  COM fail-fast. Full immutable-graph budgets, COW layout publication,
  oversized-codec memory, qualified same-byte 500/500 audit, and the physical
  counterbalanced release remain open.

- September 30 retirement-barrier checkpoint: the rebuilt fast Core partition
  passes 1,036/1,036 with no skips in
  `MarkdownRenderer/artifacts/progressive-core-retirement-barrier-20260930/progressive-core-retirement-barrier.trx`.
  The integrated renderer candidate builds, but its first full verification
  passes 724/726 in
  `MarkdownRenderer/artifacts/progressive-retirement-barrier-fixed-20260930/progressive-retirement-barrier-fixed.trx`.
  The two failures are a stale single-image-work-count assumption and a
  machine-speed-dependent scene-preparation cancellation window. They are
  being replaced with actual admission-queue/native-creation synchronization,
  retaining all cancellation, old-pin preservation, and drain assertions.
  No failed artifact is treated as a passing release gate.
- The retirement work now tracks actual image/provider/native completion,
  accepted UI publications, failed unpublished layout construction, and old
  snapshot retirement through the exact rebuild lifetime. A follow-up review
  found partial HTML/GFM construction ownership, exception-safe image cleanup,
  and session plan detach-versus-shutdown registration gaps; these are under
  implementation and require fresh integration and package verification.
  The previous v14 reproducible package pass does not qualify this candidate.
- Audit-harness launcher failures are now explicit: child-process test results
  must prove successful launch before examining a generator's exit status.
  An in-process Node diagnostic passes 66/68; the two remaining cases cannot
  spawn the PowerShell/Node child process (`EPERM`) in this environment.
  The actual Edge replay is separately unqualified, not silently excluded from
  the mandatory gate. PR #99 still reports the earlier committed head and
  ordinary top-500 failures, not results for these local changes. Full COW,
  immutable-graph byte bounds, oversized-codec memory, qualified same-byte
  500/500, and physical counterbalanced release measurements remain open.

- A further frame-path review found that `LayoutRevision` was read before a
  nonblocking paint lease but itself waited on the background mutation lock.
  Size, revision, and measured-count reads now use an immutable committed
  metadata tuple, published only at successful transaction boundaries. A
  candidate/canceled measure cannot publish these scalars, and unchanged
  metadata does not allocate a replacement. Controlled blocked-measure and
  cancellation tests are being verified. Mutable block/native/UIA geometry
  remains lease-protected; this is not full copy-on-write publication.

- September 30 continuing lifetime/publication checkpoint: the final v17
  renderer integration candidate passes 747/747 with no skips in
  `MarkdownRenderer/artifacts/progressive-v17-lifetime-20260930/progressive-v17-lifetime.trx`.
  Its rebuilt fast Core partition passes 1,036/1,036 with no skips in
  `MarkdownRenderer/artifacts/progressive-core-v17-lifetime-20260930/progressive-core-v17-lifetime.trx`.
  These include deterministic admission-queue/native-creation cancellation,
  committed-metadata reads during blocked/canceled background measurement,
  actual provider/native-work retirement, and image-publication ownership.
  The 501-test long external-gate fixture partition completed earlier in this
  run; it validates synthetic evidence/gate mutation, not 501 live repositories
  or a fresh single-build full Core run. No browser or physical-frame-budget
  qualification is inferred from these managed results.
- The shared construction scope now follows published Stack/List/Table/Code
  descendants by reference identity while retaining cleanup responsibility for
  unpublished roots. Partial safe-HTML construction releases a real cached
  image lease on cancellation. Summary staging boxes detach transferred images
  and child roots before disposal, preserving published images while retiring
  discarded text/vector resources. The image path has a real `<details>` build
  regression; vector cleanup is a direct ownership test plus narrow source
  wiring, not an end-to-end `<details>` math rendering qualification.
- Recursive container disposal and retirement collection attempt every sibling
  despite a child failure. Diagnostic aggregation retains a bounded prefix and
  explicitly bounds nested aggregate traversal. Image tile cancellation uses
  asynchronous callbacks whose actual completion is retained by the retirement
  barrier. Accepted tile publication transfers the lease only after successful
  dictionary insertion; pre-transfer failures release both the incoming lease
  and a detached previous tile, while post-transfer dropped cleanup cannot
  dispose the dictionary's lease. Retired SVG source state clears only after
  tracked work drains, retaining alt/title/description metadata for old UIA
  projections. Session plan retirement is reserved under the owner-map lock
  before detachment, and shutdown includes the whole parse/plan transaction.
- The rebuilt app managed suite passes 3,420/3,420 with no skips in
  `artifacts/winui-v16-indexed-lifetime-20260930/winui-v16-indexed-lifetime.trx`.
  Stale source-contract assertions now require the indexed, generation-matched,
  no-realization readiness probe and existing root-peer notifications. These
  source checks are backed by renderer behavioral tests, but are not live UIA
  events, Narrator, touch, or DPI/device evidence. Earlier failed test artifacts
  remain preserved.
- The first post-fix v16 pack fails the unchanged compressed lean ceiling:
  538,267/537,600 bytes in
  `MarkdownRenderer/artifacts/packages-anycpu-v16-lifetime-20260930`.
  Unused generated record members on private/internal carriers were removed,
  preserving actual cloning, cache-key, and public API semantics. Both fresh
  v17 14-package executions pass strict size/license/architecture checks:
  537,386/537,600 compressed bytes and 1,252,864/1,258,291 managed bytes.
  Their complete archive/entry manifests match. Artifacts are under
  `MarkdownRenderer/artifacts/packages-anycpu-v17-lifetime-20260930` and
  `MarkdownRenderer/artifacts/packages-anycpu-v17-lifetime-20260930-repro`, with
  corresponding `compliance-anycpu-v17-lifetime-20260930*` directories.
  Compressed headroom is only 214 bytes; every further change must rerun the
  gate. The matching pair reused incremental compiler outputs and does not
  qualify independent-build reproducibility. Two subsequent forced full
  shipping-graph rebuilds also pass strict size/license/architecture checks,
  but their complete archive manifests FAIL comparison: nine WinUI-consuming
  assemblies differ. Evidence is retained in the corresponding
  `packages-anycpu-v17-lifetime-20260930-rebuild-repro*` and
  `compliance-anycpu-v17-lifetime-20260930-rebuild-repro*` directories, including
  `forced-rebuild-comparison.json`. No reproducibility waiver is accepted.
  Development workers remain unsigned, so this is not signed-release, MSIX,
  Store, or NativeAOT-publish evidence.
- Independent source-generation diagnosis retains source captures and binlogs
  under `MarkdownRenderer/artifacts/winrt-repro-20260930`. The active Windows
  SDK reference 10.0.26100.57 carries CsWinRT generator 2.2.0.48161. Identical
  serial MSBuild graph rebuilds change eleven generated files: generic helper
  order, global lookup order, and the representative type selected for shared
  CCW vtable shapes. Compiler-visible editorconfigs are unchanged. An isolated
  experiment with the already-app-used SDK reference 10.0.26100.87 / CsWinRT
  2.3.1.54291 also changes eight base-renderer generated files and the DLL hash;
  it is not a fix. That experiment's restore additionally reports NU1900 because
  the vulnerability service is unreachable, so it is not release-qualified.
  Shipping dependency pins, AOT optimizer, CCW generation, and all comparison
  gates remain unchanged. `ConcurrentBuild=false` is not wired to the installed
  SDK's Csc task and is not evidence of serialized Roslyn execution. Two further
  base-renderer rebuilds pass an actual `/parallel-` compiler response file
  (verified in the compiler invocation/binlog), but still change eight generated
  source files and the DLL hash. SDK .87's debug-determinism key comparison has
  matching compiler/runtime/options, 194 references and MVIDs, 289 analyzers,
  thirteen generator identities, and all 157 handwritten syntax-tree checksums;
  only the eight generated-tree checksums differ.
  The faithful resolution requires deterministic generator traversal and shared
  vtable representative selection, not sorting package manifests or disabling
  AOT generation. CI must rebuild the full shipping graph before each pack lane
  so reused incremental DLLs cannot mask this failure.
- The next bounded-cleanup candidate passes 753/753 renderer integration tests
  with no skips in
  `MarkdownRenderer/artifacts/progressive-v18-bounded-cleanup-20260930/progressive-v18-bounded-cleanup.trx`.
  Vector drawing resources and snapshot root cleanup now reuse the bounded
  failure-detail collector, attempting every cleanup and publishing all
  retirement tasks before surfacing faults. A real snapshot test with 100
  throwing roots proves a trailing blocked barrier delays retirement completion;
  vector disposal uses pure faulting resources plus production source wiring,
  not injected COM disposal failures. Its fresh fourteen-package strict check
  passes at 537,131/537,600 compressed and 1,252,352/1,258,291 managed bytes, with
  artifacts in `packages-anycpu-v18-bounded-cleanup-20260930` and
  `compliance-anycpu-v18-bounded-cleanup-20260930`. This is not an independent
  rebuild comparison or a signed/NativeAOT release result.
- CI packaging now validates that the fourteen direct shipping projects form a
  closed topologically ordered graph, then forcibly rebuilds each exactly once
  per reference/candidate pass with implicit reference builds disabled. It emits
  per-project generated sources, saves each pass's binlogs/source captures and
  failure report, packs with `--no-build`, and retains the existing full archive
  comparison. Compiler outputs are never redirected to a shared project path.
  A local run of the exact workflow step independently rebuilds all fourteen
  direct projects successfully in both passes. Both fresh package sets pass
  strict size/license/architecture checks (537,132 and 537,075 compressed bytes;
  both 1,252,352 managed bytes), but their full archive comparison FAILS with
  nine differing DLL entries and no other differing archive entries. All
  reports, fourteen per-pass binlogs, and per-project generated-source captures
  are retained in
  `MarkdownRenderer/artifacts/preview-rebuild-local-v18-20260930`, including
  `independent-rebuild-comparison.json`. This confirms the actual local workflow
  mechanism exposes the source-generation failure; it is not a hosted-CI pass.
  The final renderer suite after this guard refinement again passes 753/753,
  no skips, in
  `MarkdownRenderer/artifacts/progressive-v18-final-cleanup-20260930/progressive-v18-final-cleanup.trx`.
  The final application managed regression suite also passes 3,420/3,420, no
  skips, in seven seconds; its result is retained in
  `artifacts/winui-v18-final-cleanup-20260930/winui-v18-final-cleanup.trx`.
  These are managed/source-contract results, not foreground UI, AOT-publish,
  browser-relative performance, or full README corpus qualification.
- PR #99 was rechecked at 23:50 UTC and again at 00:15 UTC on October 1: its head remains
  `1c00c83ddfd2e8b65e05cb57e359a3f092b37271`, and the ordinary top-500
  consolidation/most audit shards plus preview job are still failed. Previously
  green packaging/AOT checks at that head do not qualify the dirty local v18
  source. No rerun, remote write, bypass, or gate downgrade was performed.
- The copy-on-write accessibility gap now has concrete call-path evidence:
  text-range bounding rectangles, ScrollIntoView, RangeFromPoint's nearest-text
  scan, and inline/link/semantic-node bounds can call mutable inline native
  layouts without the snapshot read lease. Background Measure clears/swaps
  those layouts and disposes the previous one; stale UIA peers can also retain
  retired boxes. Immutable semantic text alone does not protect native geometry.
  The required fix is a versioned, atomically published managed UIA geometry
  view, preserving prior committed range/bidi/grapheme geometry while candidate
  work runs and when old peers survive retirement. Returning empty geometry on
  a busy lock, adding a blocking UIA lock, or only updating committed scalar
  extents does not close this milestone. This is a confirmed source-level race,
  not a captured live Narrator/COM crash reproduction.
  The geometry view must commit with layout metadata even after an equal-size
  reflow, use block-local geometry plus shared position indexing, and replace
  only affected immutable segments. Arbitrary partial ranges cannot be derived
  reliably from only a whole-line rectangle: exact disjoint character-region
  behavior must be compared with DirectWrite for bidi, wrapping, graphemes, and
  atomic inline content. A naive retained per-character map costs roughly
  36–40 bytes per UTF-16 unit before indexing, so it needs an explicit active
  geometry budget and staged offscreen ScrollIntoView realization. Stale text
  ranges need a captured document generation or explicit invalidation; rebinding
  old offsets silently to a replacement document is not valid.
- Ready ImageBox retirement now forces the cancellation await to yield without
  capturing the caller's context, even when no work is pending. Two real-image
  barrier tests inject a blocked ready SVG document and verify the dedicated
  caller returns, cleanup runs on the pool, and completion/failure is tracked
  exactly once. The first fresh renderer suite reports 754/755 with no skips:
  the unchanged warmed contextual-style zero-allocation gate measured 32,664
  bytes. Its focused 84-test suite then passes, and an unchanged full recheck
  passes 755/755, no skips, in 31 seconds. All three reports remain in the
  `progressive-v19-retirement-yield-20260930`,
  `progressive-v19-theme-allocation-diagnosis-20260930`, and
  `progressive-v19-retirement-yield-recheck-20260930` artifact directories.
  The cache-hit code has no identified allocation source yet; the inconsistent
  allocation failure is unresolved, not waived or fixed by the passing rerun.
  Fresh fourteen-package strict checks pass at 537,177/537,600 compressed and
  1,252,352/1,258,291 managed bytes in the corresponding
  `packages-anycpu-v19-retirement-yield-20260930` and
  `compliance-anycpu-v19-retirement-yield-20260930` directories. This is neither
  independent-rebuild nor signed/AOT/browser-performance qualification.
- The final-lease/cache-reference split passes 757/757 renderer tests, no skips,
  in 36 seconds in
  `MarkdownRenderer/artifacts/progressive-v20-cache-retirement-20260930/progressive-v20-cache-retirement.trx`.
  Its real weighted-LRU regression attempts 100 throwing evictions and retains
  the new live entry without stranding ownership. The corresponding strict
  fourteen-package check passes at 537,246/537,600 compressed and
  1,252,864/1,258,291 managed bytes. These failure probes use the production
  disposal helper with managed resources, not injected CanvasBitmap COM faults.
  Review then identified that scalar test diagnostics alone were not a
  production health signal. A subsequent numeric-only Warning trace event and
  typed performance-sink hook pass the full 757/757 renderer suite, no skips,
  in 32 seconds in `progressive-v21-cache-health-20260930`; the actual harness
  listener is linked into that test lane. Linking it alongside the Math pack
  exposed a namespace collision, fixed with a scoped System.Math alias before
  the tests ran. The harness retains only one
  sanitized health error, so an eviction storm cannot grow its failure report
  and cannot qualify as a clean run. Faulting trace listeners remain contained
  at the eviction boundary. Ready-image retirement fan-out (one queued
  continuation per image) still needs an image-storm scheduling measurement.
  Detached-work registration after finalization is an invariant-failure
  diagnostic, not permission to start new native work after its parent lease
  ends; current callers register while the parent lease is live.
  The final fresh fourteen-package strict check passes at 537,352/537,600
  compressed (248 bytes headroom) and 1,252,864/1,258,291 managed bytes in
  `packages-anycpu-v21-cache-health-20260930` and
  `compliance-anycpu-v21-cache-health-20260930`. No cap was increased. The
  source-generation independent-rebuild failure and earlier intermittent
  zero-allocation test failure remain unresolved; these passing checks do not
  qualify independent builds, signed workers, NativeAOT, or browser parity.
  The rebuilt application managed suite passes 3,420/3,420 with no skips in
  eight seconds in `artifacts/winui-v21-cache-health-20260930/winui-v21-cache-health.trx`.
  The x64 Release performance harness builds successfully with zero warnings
  and zero errors in 19.52 seconds after the typed sink change. Building the
  harness and linking its real listener into tests are not foreground runner
  qualification or proof of the unresolved allocation test's cause.
- Remaining local architecture work is still real: immutable completed-scene
  byte bounds, full copy-on-write block/native/UIA publication, and hard
  single-codec oversized-raster memory bounds are not complete. Final live
  bitmap-lease disposal faults now reach image retirement tracking; LRU cache-
  reference disposal faults are contained and counted with a scalar HResult,
  without retaining exception/source data or interrupting trailing evictions.
  Ready-image retirement yields off the caller before cleanup. These source
  fixes still require injected native-disposal and frame-budget qualification,
  beyond the deterministic managed disposable/helper tests. PR #99
  still targets the earlier committed head; these dirty local changes have not
  been pushed. A write-enabled clean candidate and eligible frozen reference,
  normal-token foreground Win11/Edge runner with network/security-audit access,
  production signing material, and the physical architecture/DPI/device matrix
  are required for the qualified counterbalanced benchmark and same-byte
  500/500 audit. The automation stays active; no release goal is marked met.

### October 1 local completion-cache and accessibility follow-up

- PR #99 still targets `1c00c83ddfd2e8b65e05cb57e359a3f092b37271`;
  the fresh check read shows no published-head change or new passing release
  evidence. Its failed ordinary top-500 consolidation is not replaced by the
  earlier one-case diagnostics or this dirty local implementation.
  A further October 1 read confirms the same committed head and failed
  preview/top-500 state; no current-candidate CI or release run was published.
- A session-wide conservative weighted completed-scene LRU is wired into
  the progressive plan. `SceneCacheBudgetBytes` has an immutable architecture
  ceiling (64 MiB x64/ARM64, 32 MiB x86), and may be lowered to zero. The cache
  excludes current layout owners and transient visible handoffs. Scene-free weak
  descriptors preserve exact content metadata and can reuse a scene still held
  by a block or inline run; they do not retain commands or native resources.
  Reconstruction-only metadata has a separate 4 MiB x64/ARM64 / 2 MiB x86
  aggregate LRU and a 1 MiB per-result copy ceiling, including when the strong
  graph cache is disabled. Metadata ownership transfers before publication;
  layout lookups only read cache/weak-result references, and restoration runs
  on a worker. Retention/Trim must never erase an unconsumed first-layout
  handoff or turn eviction into a permanent rendering failure. These are partial cache-owner
  bounds, not completion of the active/off-band managed-graph memory milestone.
- The active native-scene band now reuses the immutable weight of an owner that
  remains pinned instead of walking its command graph again on each scroll.
  Block and inline tests exercise 100 actual band exchanges, not only warm
  readiness probes. The first focused integrated run passed 122 behavior cases;
  its only failure was indentation in the intentional new API baseline, now
  corrected. After sharing and child-provider fixes, the final full integrated
  run passes 805/805 with zero skips. This is not a frame-budget or process-memory qualification.
- Deferred diagnostic contributions use a persistent sorted immutable index.
  The worker replaces only the changed marker's contribution; its acknowledged
  dispatcher callback swaps roots without scanning completed scenes or sorting
  all diagnostics. Valid completions preserve the diagnostics snapshot identity.
  The shared contribution avoids duplicate descriptor/publication arrays.
  Cancellation and retirement include producers awaiting acknowledgement, so
  teardown cannot dispose their synchronization gate early; undrained and late
  callbacks, reentrant retirement, reverse ordering, bulk errors, and marker
  replacement have focused regression coverage. Reporting diagnostics intentionally
  survive cache trimming and are not part of the graph/metadata cache caps.
- Logical UIA text ranges now carry an epoch bound to source, semantic text and
  ordered projection identity, plus parser/provider/culture configuration.
  Fingerprints are computed on the worker and compared in constant work at
  commit; stable reflow keeps valid offsets, while replacement or a changed
  projection invalidates old ranges rather than applying old offsets to new
  text. This does not solve the separate native-geometry/COW release gap.
- Retained virtual child peers now carry a weak semantic-projection identity
  as well as the logical generation. A retired node, block, hyperlink,
  linked-image or code-copy provider cannot invoke/toggle/scroll/focus a prior
  document, query retired geometry, or create a fresh range from its old box.
  Projection replacement clears root and inline-child peer caches so fresh
  discovery does not return a fenced peer for a box reused by reflow. Stable
  logical text ranges still survive same-document reflow; old child providers
  become unavailable and must be rediscovered. The managed helper and source
  wiring tests are not a live UIA-client or DPI/theme validation.
- `GetDocumentRects` now starts at a binary-indexed span rather than scanning
  the document prefix and stops after the requested range. An exhaustive
  vector-boundary oracle preserves inclusive endpoints, zero-length spans,
  degenerate/expanded ranges and the document end; an 8,192-span tail query
  reads at most 20 indexed entries. Native range rectangles are unchanged.
  The iterator still reads mutable native layout, so this search fix does not
  resolve the concurrent-measure race. The next sound slice is a managed-only
  grapheme/bidi geometry atlas with single-version block placement, atomically
  published at the committed-metadata seam. A volatile per-box cache, UI-thread
  locking, whole-document per-scroll copying, or full-string rectangle capture
  would not satisfy that contract; uncaptured offscreen geometry must remain
  explicitly pending/unavailable rather than falsely complete.
- Cache trimming/disposal now replaces backing dictionaries and recency lists
  in constant work, releasing high-water arrays; retiring the final owner also
  releases empty containers. Both LRUs charge a conservative 160-byte entry
  overhead in addition to payload weight. A 4,096-entry reset/reuse regression
  covers Trim, last-owner retirement and disposal; diagnostics and externally
  held layout content remain usable. This is not the full 100-cycle native
  handle/private-memory or pending-publication budget qualification.
- Fresh local Release verification: 805/805 renderer integration/API tests,
  1,036/1,036 fast Core tests, 76/76 Math, 115/115 Mermaid and 3,420/3,420
  application tests, all with zero skips. The unchanged 485-case external-gate
  fixture is excluded from this fast Core count; its earlier full run is
  historical, not a current-candidate benchmark. The Release performance-harness
  build succeeds with zero warnings/errors. Two intermediate compile issues
  (missing imports and test `System.Math` qualification), a blocking test-await
  analyzer finding, API baseline whitespace, and an exact-weight fixture
  mismatch were repaired; failed and later passing artifact directories are
  retained separately. Current passing TRX files are under
  `MarkdownRenderer/artifacts/renderer-v22-full4-20261001`,
  `core-v22-final3-20261001`, `math-v22-final-20261001`,
  `mermaid-v22-final-20261001` and `app-v22-scene-cache-20261001`.
- Node's six audit test-file processes failed to start in this managed execution
  environment (`spawn EPERM`); no test cases executed. The same-byte/Edge corpus
  cannot be qualified by this failure or by earlier diagnostics. No process
  restriction, audit, input, or release gate was bypassed.
- The first fresh lean Core+WinUI probe failed both immutable caps
  (540,177/537,600 compressed bytes and 1,260,032/1,258,291 managed bytes).
  Narrow internal non-key carrier compaction produced a subsequent normalized
  probe at 537,535 compressed and 1,250,816 managed bytes, but that had only
  65 compressed bytes of headroom and preceded the final child-provider fix.
  A later child-provider probe failed at 537,945/537,600 compressed bytes;
  additional private accessor compaction brought it to 537,427 without removing
  functionality or changing the caps. None of these two-package probes is the
  required final fourteen-package verdict.
- The final fresh fourteen-package Release check passes the unchanged size,
  unknown-license, native-asset and managed-architecture gates. Lean Core+WinUI
  is 537,541/537,600 compressed bytes (59 bytes headroom) and
  1,250,304/1,258,291 managed bytes; the optional Performance package is
  85,103 compressed bytes. SPDX 2.3 and notice reports exist for all fourteen
  packages, and the policy verifies 76 locked Rust dependencies plus six
  selected-RID native assets and twelve managed assemblies. Evidence is under
  `MarkdownRenderer/artifacts/packages-anycpu-v22-scene-cache-20261001` and
  `compliance-anycpu-v22-scene-cache-20261001`. The deterministic hash manifest
  is a single-build baseline, not independent-rebuild qualification; the
  existing generator nondeterminism remains a failed release gate. No size cap,
  dependency version, license policy or AOT setting was relaxed.
- Follow-up source-cache review found that a payload-byte cap alone allowed
  tiny assets to retain unbounded bookkeeping, and a zero-byte budget could
  still cache empty assets. The source LRU now has a separate conservative
  4 MiB x64/ARM64 / 2 MiB x86 metadata ceiling, charges retained key/MIME/URI
  and document-context strings, and skips retention for an oversized single
  metadata entry. This is eviction/miss policy, never an image rejection.
  `SourceCacheBytes` keeps its payload-only meaning. Memory-pressure Trim and
  final unkeyed-owner retirement replace private cache roots, releasing their
  high-water arrays without a full cache scan on Trim. A 10,000-tiny-source
  probe, zero-budget empty assets, oversized host metadata, capacity reset and
  reuse pass alongside the existing session suite: 41/41 Release tests, zero
  skips, in `MarkdownRenderer/artifacts/source-cache-v23-recheck-20261001`.
  The preceding attempt was stopped by an xUnit collection-size analyzer
  finding, corrected without suppressing the analyzer. This extends source-
  cache owner bounds; it does not bound arbitrary host-resolver state or codec
  working memory, nor replace the pending publication/active-layout milestones.
- The source cache now uses a session-local weak-key resolver identity table.
  Cached keys retain only an identity token, not the host resolver or arbitrary
  plugin state. Distinct resolver instances remain separate security/cache
  partitions even when their value equality and hash codes match. Live document
  scopes still own the resolver needed for ongoing operations. New collection
  and equal-but-distinct identity tests pass in the integrated run below; this
  does not promise a bound on state intentionally owned by the application.
- Prepared scene graphs now hold their shared scene-admission slot through
  dispatcher acknowledgement, across plans. A clearable publication envelope
  owns the queued payload; queued cancellation drops it before completing the
  acknowledgement. An executing callback retains its slot until its `finally`
  completes, so generation, owner and session retirement cannot acknowledge
  early. Rejection, enqueue exceptions, duplicate/reentrant dispatch and callback
  faults are contained and observed by the producer's retirement barrier.
  Worker preparation timing stops before dispatcher waiting. This bounds the
  number of prepared graph-bearing publications to the unchanged architecture
  slot limit; it does not bound per-marker admission waiters or consumed results
  transferred to layout owners.
- Virtual child UIA providers also hold their node/box/run/parent payloads
  weakly. The earlier weak lifetime fence alone was insufficient: a strong
  semantic node could retain its old tree through parent links. Existing root
  peers now reconcile on every snapshot publish or clear without creating a
  peer or walking native geometry, and replace node-peer dictionary roots on
  projection changes. Stale notifications resolve through the lifetime fence
  and return quietly; stale actions/patterns remain element-unavailable. These
  source/GC contracts are not live UIA-client, Narrator or mixed-DPI evidence,
  and do not solve mutable native-geometry access during measurement.
- The first follow-up integrated suite compiled production but found a test
  weak-reference temporary kept alive across GC. A non-inlined liveness probe
  fixes that test-only root. The next full run found an obsolete source-wiring
  assertion for the new guarded weak lookup; it now checks the lookup and its
  current-lifetime guard. Additional deterministic reject/throw-after-dispatch-
  start cases caught a test-model mistake: a standalone plan constructed
  directly is not session-registered, so session retirement waits for its lease
  but does not aggregate that plan's fault. The tests separately assert explicit
  plan retirement observes the fault, without relaxing either callback barrier.
  Failed TRX/compile attempts remain separate from passing evidence.
- A fresh fourteen-package follow-up initially failed the unchanged lean size
  cap at 538,119/537,600 compressed bytes. Internal non-key text-span and raster
  metadata compaction alone still exceeded the cap (538,128 and 537,768 in two
  narrower probes). Constructor-only code-block metadata accessor compaction
  preserves its exact values, including the string cache key; no public API,
  equality key, feature, diagnostic, dependency or gate was removed. The final
  fresh fourteen-package Release set passes at 537,431/537,600 compressed bytes
  and 1,249,280/1,258,291 managed bytes. SPDX/license, all 76 locked Rust
  dependencies, six native RID assets and twelve managed assemblies pass.
  The optional Performance package is 87,051 compressed bytes. The lean
  compressed headroom is still only 169 bytes and requires a fresh gate after
  further source changes, not inference from this checkpoint.
  Evidence is under
  `MarkdownRenderer/artifacts/packages-anycpu-v23-final-retention-20261001` and
  `compliance-anycpu-v23-final-retention-20261001`. Workers are unsigned
  development assets; the single-build hash manifest is not independent
  reproducibility or signed release qualification.
- Additional ownership tracing confirms the active/off-band graph milestone
  remains open. `VectorSceneBox.Content`, box/run scene fields and
  `VectorSceneDrawing` retain immutable commands independently of native drawing
  residency. Clearing only a drawing or scene field would leave other roots.
  Terminal retirement must detach every alias only after readers drain;
  reversible off-band/device-resource release must preserve re-preparation.
  Same-snapshot eviction additionally needs stable deferred-marker ownership
  and an asynchronous re-render/publication path. No live graph was blanked or
  unsafe partial detachment introduced by this checkpoint.
- Final follow-up source-frozen Release verification passes 831/831 renderer
  integration/API tests, 1,036/1,036 fast Core, 76/76 Math, 115/115 Mermaid and
  3,420/3,420 application tests, all with zero skips. Passing artifacts are
  `MarkdownRenderer/artifacts/renderer-v23-final2-20261001`,
  `core-v23-final-20261001`, `math-v23-final-20261001`,
  `mermaid-v23-final-20261001` and `app-v23-final2-20261001`.
  The final x64 Release performance-harness build also succeeds with zero
  warnings/errors. WinUI lifetime/dispatcher review found no additional
  concrete handoff defect; the documented native-geometry race remains open.
  The unchanged 485-case external-gate fixture is explicitly excluded from
  fast Core; its earlier full execution remains historical validator evidence,
  not a current benchmark. The first follow-up app run passed 3,419/3,420 and
  caught an outdated `_run.AltText` source assertion. Its corrected contract
  follows the guarded run through the naming helper and still verifies authored
  alt text supplies the linked-image name. The failed artifact is retained
  separately under `app-v23-final-20261001`. None of these managed tests
  establishes foreground frame budgets, real-worker fault/pixel behavior,
  NativeAOT runtime, full same-byte 500/500 fidelity, or counterbalanced latency.
  Required external execution remains precise: a clean committed candidate
  (this task cannot write `.git`), an eligible schema-12 frozen reference SHA,
  and a normal-token, foreground/unoccluded Windows 11 26100-or-newer Edge
  runner at 120 Hz or higher with working locked dependency audit, artifact
  retrieval and child-process launch. Production worker/MSIX signing material
  and the required x86/ARM64, hardware/WARP, DPI/theme/HDR device matrix are
  separate release requirements. Current restrictions still prevent restricted-
  token worker startup, Node/Edge children, sandbox broker access and NativeAOT
  task-host startup; no restriction or release gate was bypassed. Active/off-
  band graph ownership, COW publication and codec-intermediate memory are also
  genuine implementation/qualification gaps, not relabeled runner-only issues.
  The heartbeat stays active and no performance goal is marked complete.
- The independent-package nondeterminism diagnosis now maps to exact official
  CsWinRT source: the SDK Ref .87 generator's `GetVtableAttributeToAdd` copies
  `HashSet` interface enumeration directly into its immutable model, and vtable,
  generic-initializer, and lookup emission also consume unordered sets. See
  [the commit-pinned generator source](https://github.com/microsoft/CsWinRT/blob/ff5227a4ffca06dfa6de8ca0a5c127c04f294291/src/Authoring/WinRT.SourceGenerator/AotOptimizer.cs),
  specifically `GetVtableAttributeToAdd` and `GenerateVtableAttributes` (crawler
  line offsets are not source line numbers). The
  [official release](https://github.com/microsoft/CsWinRT/releases/tag/2.3.1.260716.1)
  identifies this exact commit and the .87 Windows SDK Ref projections.
  Stable input/model traversal needs an upstream generator fix; `/parallel-`
  and deterministic compiler output do not stabilize changing generated source.
  No generator pin, AOT switch, output-normalization rule, or package gate has
  been changed. This explains the .87 diagnostic; the shipping .57 failure and
  exact full rebuild evidence remain retained, not marked resolved.

### October 1 retired-scene command ownership follow-up

- Terminal disposal now detaches the vector drawing's scene and the block's
  `MarkdownContent` alias, including native-cleanup failure paths. Block/inline
  duplicate scene fields were removed and block command-index arrays are reset.
  Snapshot retirement still drains existing paint/interaction readers before
  disposing children. Reversible native-resource eviction keeps the scene for
  offscreen re-entry and device recovery; this is not same-snapshot command
  eviction, an active-band aggregate cap, or proof of a process-memory bound.
- Review caught direct UIA paths that do not take the snapshot native-reader
  lease. Block semantic bounds/items/link lookup now retain only independent
  immutable semantic records and the viewport value, not scene commands.
  Source fallback is captured as a `SourceSpan`. Effective vector text styles
  are captured by the semantic builder on the layout worker; UIA formatting
  reads those scalar-style projections instead of a retired command tree.
  Stale-peer generation checks remain in place. General inline/native geometry,
  mutable placement publication, and `RangeFromPoint` still require the separate
  COW geometry milestone; they are not marked solved by this narrow change.
- The initial 129-case scene/accessibility/progressive regression passes. The
  initial fourteen-package checkpoint passes at 537,570/537,600 compressed
  bytes and 1,249,792/1,258,291 managed bytes, with only 30 compressed bytes of
  headroom. It predates the semantic-projection follow-up and is not its final
  qualification. Artifacts are under `renderer-v24-pretest-20261001`,
  `packages-anycpu-v24-retirement-20261001`, and
  `compliance-anycpu-v24-retirement-20261001` beneath
  `MarkdownRenderer/artifacts`. A subsequent 837/837 renderer integration/API
  run passes with the final semantic projection, including actual Win2D
  release/reprepare, paint and interaction reader-drain, and weak-reference
  collection of the scene, command collection, first command and path data
  while retired owners remain alive. The native-disposal-failure test is
  explicitly source-contract-only, not injected production native-fault proof.
  Its passing artifact is `renderer-v24-final3-20261001`. The earlier focused
  and initial full attempts caught test-fixture compile errors (theme access,
  float height and registry namespace); they are not passing test evidence.
  Final frozen verification also passes 1,036/1,036 fast Core, 76/76 Math,
  115/115 Mermaid and 3,420/3,420 application tests, all with zero skips;
  the x64 Release performance harness builds with zero warnings/errors.
  This is 5,484 selected managed tests, not release performance qualification.
  The unchanged 485-case external-gate fixture is explicitly excluded from fast
  Core; it is validator-fixture evidence, not a benchmark. Passing artifacts
  are `core-v24-final-20261001`, `math-v24-final-20261001`,
  `mermaid-v24-final-20261001` and `app-v24-final-20261001` alongside the
  renderer artifact above. Lifetime/metadata and immutable-payload review
  found no additional concrete regression. No live UIA, DPI/HDR/device matrix,
  production worker fault, NativeAOT runtime, current same-byte 500/500 or
  counterbalanced browser result was inferred from these checks.
- Private SVG cache payload accessors were compacted to constructor-initialized
  readonly fields, preserving the exact source, security, provider-generation
  and bitmap values. This is not a cache-key/equality change or public API
  removal. The copy contract also gains an actual immutable-payload copy test,
  not just a source assertion. The final fresh fourteen-package shipping set
  passes at 537,315/537,600 compressed bytes and 1,248,256/1,258,291 managed
  bytes, leaving 285 compressed bytes of headroom. The optional Performance
  package is 87,053 bytes. SPDX/license, all 76 locked Rust dependencies, six
  selected-RID native assets and twelve managed assemblies pass. Evidence is
  under `packages-anycpu-v24-final2-retirement-20261001` and
  `compliance-anycpu-v24-final2-retirement-20261001` beneath
  `MarkdownRenderer/artifacts`. The intermediate projection/compaction set also
  passed (537,360 compressed bytes) before readonly link-wrapper/source-range
  hardening; its `v24-final-retirement` artifact is retained separately. Workers
  remain unsigned development assets and the hash manifest is a single-build
  baseline, not independent rebuild/signing qualification. No size limit is
  relaxed and the known WinRT-generation reproducibility issue remains open.
- PR #99 was read again in this heartbeat. Its published head remains
  `1c00c83ddfd2e8b65e05cb57e359a3f092b37271`; failed ordinary top-500 and old
  preview checks are unchanged. No current same-byte 500/500 audit or qualified
  R1/C1/C2/R2 release benchmark exists. The normal-token runner, clean committed
  candidate/reference, signing and physical device requirements recorded above
  remain required; no restriction or release gate was bypassed.

### October 1 indexed range and scene-weight follow-up

- `ScrollIntoView` image lookup now starts at the existing binary-indexed text
  span window rather than walking the entire document. Enclosing-node and
  immediate-child lookup binary-search only ordered top-level semantic ranges;
  nested/vector children retain their historical scan and output order. Vector
  parent labels do not necessarily enclose descendant label ranges. Inclusive
  image boundaries, empty spans, final carets, decorative-image omission and
  trailing-newline trim behavior are preserved. An 8,192-span tail query reads
  at most 20 spans; the root search reads at most 14 children. These are
  operation-count regressions, not browser-relative timing qualification.
- Native-scene resource estimates are memoized by immutable scene identity in
  a process-wide weak-key table holding only scalar values. Cold estimation is
  serialized per scene; warmed lookup neither re-walks commands nor allocates.
  Block and inline owners can leave/re-enter the native residency band without
  repeating the command walk, including shared scenes across layouts. Disposal
  clears the policy's block/scratch references even when native cleanup fails.
  Mutable residency operations still require the existing snapshot lock; the
  policy is not independently thread-safe. This is not active/off-band command
  eviction or a native/process-memory cap.
- Actual native block/inline re-entry and weak scene/command collection tests
  pass. Sixteen concurrent policies compute one exact scalar estimate, and
  10,000 warmed lookups allocate zero bytes. Range regressions match linear
  oracles at every boundary and exercise actual mixed-block semantic building
  with RTL, combining text, ZWJ emoji, tables, and vector descendants outside
  their parent's label. Private code-line payload accessors were compacted to
  constructor-initialized readonly fields; exact constructor/field values are
  tested and public APIs/cache-key semantics are unchanged.
- The frozen x64 Release candidate passes 849/849 renderer, 1,036/1,036 fast
  Core, 76/76 Math, 115/115 Mermaid and 3,420/3,420 application tests: 5,496
  selected managed tests with zero skips. The harness builds with zero warnings
  or errors. Artifacts are `renderer-v25-final-20261001`,
  `core-v25-final-20261001`, `math-v25-final-20261001`,
  `mermaid-v25-final-20261001` and `app-v25-final-20261001` beneath
  `MarkdownRenderer/artifacts`. The 485 external-gate validator fixtures remain
  explicitly excluded from fast Core, not counted as a performance run. Initial
  focused attempts exposed test-fixture compile issues; the passing combined
  98-case regression is retained separately in `renderer-v25-pretest4-20261001`.
- Fresh strict FullRelease checks pass for all fourteen packages at
  537,043/537,600 compressed lean bytes and 1,247,232/1,258,291 managed bytes;
  the optional Performance package is 87,407 bytes. SPDX/license checks,
  76 locked Rust dependencies, six selected-RID native assets and twelve
  AnyCPU managed assemblies pass. All twelve packaged managed DLL hashes also
  match the assemblies used by the frozen test/harness build. Artifacts are
  `packages-anycpu-v25-range-estimates-20261001` and
  `compliance-anycpu-v25-range-estimates-20261001`. Workers are unsigned
  development artifacts; this is a single-build baseline, not independent
  rebuild, signing, NativeAOT runtime or architecture/device qualification.
- Further read-only audits identify two open architecture issues. WIC CPU
  admission is session-local; absent/multiple sessions and distinct decode keys
  lack an aggregate process-wide weighted decoder policy. Native awaits must
  retain their slot/stream until actual completion even after cancellation.
  The sampled oversized-WebP intermediate-memory result above is still not a
  single-codec bound. GitHub REST base64 deserialization/conversion also needs
  streaming/weighted peak-memory qualification; inspection did not establish
  an ordinary-path DTO retention leak across subsequent awaits. No unsupported
  blanket throttle, provider-owned DTO mutation or safety-limit reduction was
  introduced as a substitute for those fixes.
- COW geometry remains open. The installed Win2D API exposes cluster advance
  widths, but not an exact arbitrary-range position map. A full-range rectangle
  list plus cumulative widths is not qualified to replace native partial-range
  geometry, particularly with bidi, ligatures, wrapping and inline objects.
  Microsoft documents [range geometry](https://microsoft.github.io/Win2D/WinUI3/html/M_Microsoft_Graphics_Canvas_Text_CanvasTextLayout_GetCharacterRegions.htm),
  [DirectWrite range hit testing](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritetextlayout-hittesttextrange),
  and [cluster advances](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/ns-dwrite-dwrite_cluster_metrics).
  Any compact capture algorithm must first match the native oracle for partial
  clusters, mixed bidi, spacing/justification and atomic placeholders, then
  publish with immutable snapshot placement under a bounded memory policy.
  No approximate reconstruction, success-shaped empty geometry on contention,
  or blocking UIA lock was added.
- PR #99 was read at the start of this heartbeat and remains at
  `1c00c83ddfd2e8b65e05cb57e359a3f092b37271` with failed ordinary top-500/old
  preview checks. No current same-byte 500/500 audit or qualified R1/C1/C2/R2
  result exists. A clean committed candidate/reference and eligible normal-token
  Windows 11/Edge foreground runner with child-process, locked dependency-audit
  and artifact access remain required, along with signing and the physical
  device matrix. Restrictions, package limits and release gates are unchanged.

### October 1 aggregate raster admission and lifetime follow-up

- Ordinary-raster cache misses now share one process-wide, document-fair decode
  admission, including controls without a progressive session and empty cache
  keys. Scheduling weights account for encoded bytes, source pixels and possible
  output/upload buffers. Defaults are 128 MiB/two active decodes on x64/ARM64
  and 64 MiB/one on x86. Valid oversized requests consume the whole weight and
  run alone rather than being rejected. Exact bitmap-cache hits bypass the gate;
  optional per-session CPU admission remains independently stricter. This does
  not bound single-codec native intermediates or queued encoded arrays.
- A session raster-work barrier is registered before the keyed decode wait,
  which now observes both image and session cancellation. The barrier remains
  active through non-cancelable WIC/Win2D awaits, failed-output cleanup, keyed
  gate release and both CPU/process permits. Session retirement cannot finish
  before that cleanup. UI publication has the image owner's separate retirement
  barrier. Source-byte fast admission also rechecks cancellation after entering
  its lock, preventing a caller canceled while waiting for the lock from being
  granted a reservation.
- Regression tests cover mixed weights, owner fairness, full-budget exclusivity,
  queued/grant/locked cancellation, weak owner release, and synthetic active
  cancellation without prematurely revoking a permit. Actual ImageBox tests
  cover absent/multiple sessions, empty keys, cache hits, image/session retirement,
  the pre-CPU keyed wait, native malformed-PNG failure and subsequent recovery.
  They do not inject native COM cleanup faults or establish real codec peak-memory
  limits. Private immutable payload accessors were compacted without changing
  public APIs or cache-key equality; field shape and constructor values are tested.
  Eighty-three private/internal immutable payload members now use readonly fields;
  four private lock-owned queue-state properties became fields. Init properties,
  mutable public behavior, equality, record semantics and serialization contracts
  remain unchanged. Controls without a session reuse their image cancellation
  token instead of allocating a redundant linked source on a raster cache miss.
- Initial x64 Release results are 871/871 renderer, 1,036/1,036 fast Core,
  76/76 Math, 115/115 Mermaid and 3,420/3,420 application tests, zero skips,
  with a clean harness build (`*-v26-final-20261001` under artifacts). After the
  source-lock cancellation guard, `renderer-v26-final2-20261001` passed 870/872:
  an immediate active-linked-token assertion raced asynchronous cancellation
  propagation, and the format-boundary zero-allocation test observed 7,304 bytes.
  The cancellation test now waits up to five seconds for its distinct signal and
  preserves the drain assertions. A four-case follow-up passed; all 32 diagnostic
  chunks for the wrapper, integer binary search and no-op baseline measured zero.
  That does not explain the prior allocation. Its zero-byte gate is unchanged,
  the diagnostic source/TRX are retained, and that variance remains unresolved.
  The final frozen candidate passes 873/873 renderer, 1,036/1,036 fast Core,
  76/76 Math, 115/115 Mermaid and 3,420/3,420 application tests: 5,520 selected
  cases, zero skips. The harness builds with zero warnings/errors. Evidence is
  `*-v26-final5-20261001`; fast Core uses a fresh isolated binary output so it
  does not overwrite the assembly used by the separate external fixtures.
- The first fourteen-package pack is retained separately in
  `packages-anycpu-v26-process-admission-20261001`. Strict compliance stopped at
  538,572/537,600 compressed lean bytes (972 over); no size gate was relaxed.
  Two intermediate packs are also retained, at 537,826 and 537,639 lean bytes.
  The final fourteen-package set passes at 537,182/537,600 compressed lean bytes
  (Core 100,204; WinUI 436,978) and 1,245,696/1,258,291 managed bytes. Performance
  is 87,605 compressed bytes. SPDX 2.3/notices, 76 locked Rust dependencies,
  six selected-RID native assets and twelve AnyCPU managed assemblies pass.
  All twelve packaged managed DLL hashes match current built and test/harness
  dependency copies. Evidence is `packages-anycpu-v26-final5-process-admission-20261001`
  and `compliance-anycpu-v26-final5-process-admission-20261001`, including
  `tested-managed-assembly-binding.json`. Workers remain unsigned development
  artifacts; the hash manifest is one build, not independent rebuild/signing or
  NativeAOT/runtime qualification. The separately executed external-gate validator
  partition subsequently passes 501/501, zero skips, after 1h51m22s (05:25:25 to
  07:16:47 UTC). Evidence is
  `core-v26-external-gate-fixtures-20261001/core-v26-external-gate-fixtures-20261001.trx`.
  This started before the v27 semantic-hit slice; the fixture source and gate
  script did not change, and both emitted fixture inputs SHA-256 match the fresh
  v27 output. Its synthetic report mutations are neither repositories
  nor renderer-speed measurements and do not qualify a counterbalanced benchmark.
- PR #99 still publishes `1c00c83ddfd2e8b65e05cb57e359a3f092b37271` with
  failed ordinary top-500/old preview checks. A current-candidate same-byte
  500/500 result and qualified R1/C1/C2/R2 benchmark remain absent. The clean
  committed candidate/reference, normal-token foreground Windows 11/Edge runner,
  locked dependency audit and artifact access, signing and physical device matrix
  requirements above remain unchanged.

### October 1 scene-free semantic-hit follow-up

- The v27 candidate removes the remaining drawing-scene reads from block-vector
  hit testing, selection eligibility, text endpoint lookup and point-to-link
  lookup. It borrows the already-validated immutable depth array as internal
  `ReadOnlyMemory<int>` beside the existing semantic/link metadata, without
  retaining a scene or copying a second depth array. The existing O(semantics)
  scan, O(1) depth/link lookup, eligibility, deepest-first/smallest-area ordering
  and endpoint mapping are preserved. No public API, native-resource eviction
  policy, text-style resolution or measurement path changes.
- Retirement tests compare those results against the pre-retirement scene
  lookup and retain the box while asserting scene/command/path collection.
  Added LTR/RTL nested-overlap cases cover depth, same-depth area ranking,
  decorative/noninvokable children, parent-only selection and out-of-viewport
  null links. The frozen v27 candidate passes 875/875 renderer, 1,036/1,036
  fast Core, 76/76 Math, 115/115 Mermaid and 3,420/3,420 application tests:
  5,522 selected cases, zero skips. The harness builds with zero warnings/errors.
  Evidence is `*-v27-semantic-retirement-20261001`; fast Core uses an isolated
  output, preserving the separate external-gate fixture host and all v26
  results. The prior allocation variance is still unexplained; no gate changed.
- Fresh strict checks pass for all fourteen normal packages: lean compressed
  537,405/537,600 bytes (Core 100,306; WinUI 437,099), managed compressed
  1,246,208/1,258,291 bytes, optional Performance 87,610 bytes. SPDX 2.3/notices,
  compatibility of 76 locked Rust dependencies, six selected-RID native assets
  and twelve AnyCPU managed assemblies pass. All twelve packaged managed DLLs
  are SHA-256 bound to the built and test/harness dependency copies. Evidence is
  `packages-anycpu-v27-semantic-retirement-20261001` and
  `compliance-anycpu-v27-semantic-retirement-20261001`, including
  `tested-managed-assembly-binding.json`. This remains single-local-build
  evidence, with unsigned development workers; independent reproducibility,
  signed deployment, NativeAOT/runtime and physical-device gates are not implied.
- This is only a graph-detachment prerequisite. Current-document boxes still
  own compiled scenes until terminal retirement, and UIA native geometry still
  needs committed immutable layout publication. It does not close aggregate
  active/off-band ownership, real image-storm memory, or browser-relative gates.
- Read-only re-audit confirms UIA `GetBoundingRectangles`, semantic node bounds
  and `ScrollIntoView` can read mutable inline native geometry without the
  snapshot interaction lock. `RangeFromPoint` is lock-protected but can wait
  behind background measurement. Native-reader safety and successful nonblocking
  UIA both require the planned committed immutable geometry root: exact leased
  DirectWrite layouts, placement/source mapping and transaction-only publication.
  Delaying layout disposal alone does not freeze mutable bounds/run placement.
  Empty/approximate rectangles or falsely unavailable UIA elements are not a fix.
- PR checks re-read at 06:43 UTC still show the same published head
  `1c00c83ddfd2e8b65e05cb57e359a3f092b37271`: 42 failures, 15 successes,
  two skips and two canceled checks. This local candidate is not published;
  the failed ordinary top-500 result is unchanged. The current-candidate
  same-byte 500/500 and qualified counterbalanced release verdict remain open.
- A second ownership re-audit confirms that full traversal can retain off-band
  ordinary image GPU leases, SVG document handles, last-window SVG tile leases
  and SVG raw byte arrays until snapshot retirement. Shared bitmap/source LRUs
  do not bound those box-owned references; fully offscreen boxes are not painted,
  so visible-paint tile pruning is not an eviction mechanism. This is a real
  implementation gap, not merely missing memory telemetry or a runner issue.
  The next image-memory slice must be opt-in, band-driven resource residency
  using the existing ordered image index, stable intrinsic/semantic geometry,
  old/new resident-set reconciliation and per-residency epochs/cancellation/drain.
  Stale callbacks must release unpublished bitmaps/documents, SVG documents must
  outlive actual provider use, and source bytes need accounted resource-store
  leases rather than permanent per-box aliases. Re-entry must use the existing
  exact bitmap cache and document/session resolver path, not URL reconstruction
  or a second cache. Visible overlap is retained until the membership transaction
  commits under paint/read exclusion. Even that slice will not establish a hard
  aggregate visible-memory ceiling or solve single-codec tiling by itself.

### October 1 off-band image residency and data-URI follow-up

- Added the independent opt-in `ReleaseOffscreenImageResources` setting: false
  on new options, true on `Progressive`, already selected by JitHub's shared
  runtime. An internal policy reconciles the current band through the existing
  ordered image index. Only a plan rebuild supplies the full plan; scrolling
  does not revisit or retain every previously seen image. Stable overlap is
  untouched. Released entries lose drain subscriptions, and large band
  contractions compact sparse set/list storage rather than leaving a
  document-sized enumeration high-water mark.
- Each departing `ImageBox` closes work admission before asynchronous
  cancellation. Snapshot read exclusion protects resource detachment, and
  old native operations, cancellation callbacks, abandoned results and queued
  UI publications stay accounted until they actually drain. Dropped results
  release their owned resources. A new residency cannot restart over an old
  epoch. The captured dispatcher is taken on the UI thread; drain callbacks
  only request a coalesced dispatcher reconciliation. Bitmap/tile leases,
  SVG document handles and box-owned raw bytes are released, while intrinsic
  geometry, source/alt/title/description, selection and prior Loaded UIA state
  remain stable. Re-entry uses the existing exact device cache and authorized
  resolver/session path; eviction never manufactures an unavailable result.
- SVG documents have their own use leases through load/Info, queued tile
  admission, actual native render completion and unclaimed raster cleanup.
  Canceling the await does not release a non-cooperative native use. A retired
  document waits for its own users, not a newer document's unrelated work;
  asynchronous disposal is reserved in the retirement barrier before its last
  use is released. Weak-key retired state prevents stale handle reuse without
  retaining all former trees. An independent read-only review found no
  concrete disposal-order or false-success defect in this slice.
- The real warm-cache pipeline regression exposed valid PNG data URIs being
  passed to a WinRT URI stream reader that rejects them. Built-in raster and
  SVG data URIs now use one bounded worker-side decoder: a bounded header,
  pre-allocation exact decoded size, direct ordinary base64 span, streaming
  percent-escaped base64, byte-preserving binary percent escapes and strict
  UTF-8 literal Unicode. No whole-payload UTF-16 copy is made. Cancellation,
  raster/SVG hard byte ceilings and nested SVG security restrictions remain
  intact. The real image load/cache/re-entry test passes, not a seeded cache.
- The first v32 candidate passed 5,556 selected tests. After final internal
  immutable-payload compaction and added binary round-trip regressions, v34
  passes 909/909 renderer, 1,039/1,039 fast Core, 76/76 Math, 115/115 Mermaid,
  3,420/3,420 application, 10/10 safe HTML, 61/61 TextMate, 9/9 conformance
  harness and 18/18 selected resvg lifetime/source-mapping tests: 5,657 selected
  tests with zero skips. The harness builds with zero warnings/errors. Evidence
  is `*-v34-image-residency-20261001` under artifacts, with an isolated Core
  output; this is not the complete worker-fault/browser/device suite. The
  focused 37-case run includes non-cooperative cancellation,
  late and blocked SVG disposal, queued publication cleanup, warm device-cache
  entry, default-off compatibility, a 5,000-image traversal with reverse jump,
  compact tracking storage and exactly zero bytes for 1,000 stable-band
  reconciliations after warmup, actual base64 and percent-encoded PNG loading,
  and 256 exact-budget binary round trips in each data-URI encoding. This is
  not a full interaction-frame or
  browser-relative performance result. Failed v29/v30 XML fixtures and the
  pre-fix v31 warm-data-URI test remain preserved; no assertion was waived.
- The first v33 full renderer run failed the unchanged warm contextual-style
  zero-allocation assertion (1,784 bytes, 908/909 otherwise passing). The same
  frozen binary passed that case in isolation and the subsequent full v34 run;
  neither passing rerun explains the failure. Read-only review found no shared
  cache, eviction, mutation of this test's local keys, event hook or allocation
  on the ordinary warmed hit path. Runtime/test instrumentation is not proven
  as the cause. Exact allocation-origin evidence remains necessary, alongside
  the earlier 7,304-byte navigation variance. The zero-byte assertions, warmup,
  measurement intervals, thresholds and normal test parallelism are unchanged.
- The first normal fourteen-package attempt, v28, failed the unchanged lean
  compressed cap: 538,340/537,600 bytes. It remains failed evidence. Safe
  internal immutable constructor-payload fields remove private getter overhead
  without changing exported API. The public XML packaging filter now resolves
  exact metadata IDs and visibility for members, not just declaring types.
  Public/protected/protected-internal docs remain; private/internal members
  are removed. Generic/nested types, arrays/byref, indexers and conversions
  have fixture coverage; an unknown ID fails before replacing the input XML.
  v31 still failed at 538,107/537,600 bytes. A further 57 constructor-only
  getters across internal/private source-map, extension rollback, parse-cache,
  layout, code, table and SVG-intrinsic payloads became same-name readonly
  fields. Mutable/init/with members, reference identity and exported API are
  preserved and tested. No package cap, compression strategy, security gate or
  dependency pin changed.
- Fresh strict compliance passes for all fourteen v33 normal packages: lean
  compressed 536,539/537,600 bytes (Core 99,477; WinUI 437,062), managed
  1,248,768/1,258,291 bytes, optional Performance 89,321 bytes. SPDX 2.3/notices,
  compatible selections for 76 locked Rust dependencies, six selected-RID native
  assets and twelve AnyCPU managed assemblies pass. All twelve packaged managed
  DLLs match the built and primary test/harness copies by SHA-256; twelve further
  dependency copies match across the four extra test projects. Evidence is
  `packages-anycpu-v33-image-residency-20261001` and
  `compliance-anycpu-v33-image-residency-20261001`, including the primary and
  `additional-tested-dependency-binding.json` reports. These are unsigned
  development workers and single-local-build bindings in a dirty uncommitted
  tree, not independent reproducibility, signing, NativeAOT/runtime or physical
  deployment qualification.
- PR checks re-read during this run still name published head
  `1c00c83ddfd2e8b65e05cb57e359a3f092b37271`: 42 failures, 15 successes,
  two skips and two canceled checks. Current local changes are not committed
  or published. The failed ordinary top-500 audit is unchanged and does not
  qualify as the current-candidate identical-byte Edge audit. No current
  500/500 or qualified counterbalanced release verdict exists.
- This closes a real off-band box-reference gap, not aggregate visible resource
  ceilings, budgeted ownership of all encoded source buffers, native
  single-codec peak memory, full compiled Math/Mermaid graph detachment or exact
  copy-on-write UIA/layout publication. Those implementation milestones and
  live memory/frame/browser gates remain open. Local execution is still
  restricted: the SVG restricted-token launch fails with Win32 87, sandbox UI
  broker/Edge child launch are denied, normal app audit needs reachable locked
  package/audit feeds, and NativeAOT taskhost reports MSB4216. Qualification
  needs a clean committed candidate plus an eligible frozen schema-12 reference
  on a normal-token foreground Windows 11 runner (120 Hz or faster) with
  Edge/worker/broker/AOT permissions, locked dependencies/artifacts/signing,
  and the required physical architecture/device/DPI/theme matrix. No bypass,
  gate weakening or claim of completion was introduced.

### October 1 scene-independent projection follow-up

- Block-vector semantic tree construction, focus-plan creation, intrinsic
  measurement and semantic text styles no longer borrow the drawing scene or
  keep a second command-graph owner through declarative content. Source ranges,
  root role/name/description, semantic text, viewport and intrinsic dimensions
  are retained explicitly. Drawing ownership and terminal disposal remain
  explicit; disposed boxes cannot be remeasured or reanimated. No reversible
  active-band graph detachment or marker-rebind path is enabled by this change.
- Text/background commands and inferred reference typography share one
  cancellable command pass. Frozen styles preserve authored fonts, colors,
  High Contrast role resolution, link behavior and exact existing scale math.
  Iterative memoized background inheritance is linear even for reverse-ordered
  deep parents; semantics without indexed text keep the previous container
  fallback. Explicitly sized scenes without semantics skip the command pass.
  Warm style lookup reuses each frozen instance. Its reference array replaces
  the old two retained integer-index arrays, but indexed text styles add
  bounded metadata objects; construction is not allocation-free. Cancellation
  is checked at entry even for an empty scene.
- Fresh v37 reports pass 916/916 renderer, 1,039/1,039 fast Core, 76/76 Math,
  115/115 Mermaid, 3,420/3,420 app, 10/10 HTML, 61/61 TextMate, 9/9 conformance
  and 18/18 selected resvg lifetime/source-mapping tests: 5,664 selected tests,
  zero skips, plus a zero-warning/error harness build. The focused run passes
  116/116, including graph weak-reference collection after terminal retirement,
  semantic/focus reconstruction without native preparation, exact zero-byte
  warm style reads, 4,096 reverse-indexed background ancestors, cancellation of
  both empty and nonempty scenes, and existing image/data-URI regressions.
  Evidence is `*-v37-vector-projection-20261001` under artifacts. Initial v36
  fixture namespace build errors and its 115-pass/one-constructor-helper-failure
  TRX remain preserved; the helper now selects the intended overload by arity.
  No production assertion or release gate was weakened.
- The final frozen v38 follow-up passes 921/921 renderer, 1,039/1,039 fast Core,
  76/76 Math, 115/115 Mermaid, 3,420/3,420 app, 10/10 HTML, 61/61 TextMate,
  9/9 conformance and 18/18 selected resvg tests: 5,669 selected managed tests,
  zero failures/skips. The focused pass is 121/121 and the harness build has
  zero warnings/errors. A separate fresh external-gate validator completes
  501/501, zero failures/skips, after approximately 62 minutes; these mutation
  fixtures enforce the release protocol, not renderer-speed or browser parity.
  Reports are `*-v38-vector-projection-20261001` and
  `external-gates-v37-vector-projection-20261001` under artifacts.
- A further 27 internal constructor-only getters became same-name readonly
  fields across inline payloads, focus items, geometry records and cache entries.
  Tests preserve constructor defaults, value/reference identity and both bitmap
  cache constructors. Mutable/init, interface and exported API members remain
  intact. Two unrelated pre-existing ordinary structs retain their existing
  form and getters; they were not reverted to the index's record representation.
  Final v38 normal packages pass the unchanged strict caps: Core + WinUI are
  536,450/537,600 compressed bytes (1,150 bytes headroom) and
  1,247,744/1,258,291 managed bytes. Core is 99,477 compressed bytes, WinUI
  436,973, and the optional Performance package 89,321. All fourteen packages
  have compatible licenses, SPDX/notices and strict set/architecture checks;
  76 locked Rust dependencies and six selected-RID native assets are checked.
  Twelve primary packaged DLL hashes match built/test/harness copies, with
  twelve additional dependency-copy matches across the remaining test projects.
  Evidence is `packages-anycpu-v38-vector-projection-20261001` and
  `compliance-anycpu-v38-vector-projection-20261001`. This is one dirty local
  candidate, not an independent reproducible build. Development workers remain
  unsigned; production signatures and NativeAOT/live qualification are open.
- A read-only review found a separate pre-existing quadratic accessibility
  attachment walk across hidden/decorative semantic ancestors. Its linear,
  arbitrary-parent-order resolver passes parity, deep-spine and
  climb/unwind/pre-attachment cancellation tests in v38. Independent review
  found no concrete defect: upstream validates
  parent forests, each omitted ancestor is memoized, and attachment preserves
  semantic-index sibling order. Exact UIA/native geometry COW and full
  current-document graph detachment remain open, not covered by metadata safety.
- The v39 prerequisite now carries a scene-free readonly marker/slot binding
  through both recursive `LayoutBuilder` materialization paths. It is separate
  from unresolved-placeholder markers: attached materialized owners remain ready
  after cache `Trim` without accidentally requesting a second render. Inline
  owners freeze exact width/height/baseline/reference-font metrics, preserving
  the existing scaling formula and terminal-disposal behavior without reading
  the command graph. Multi-scene/nested fragments, rejected/canceled scope
  restoration, weak-reference graph retirement and ordinary unbound content
  are covered. The final v39 pass has 930 renderer tests and 5,678 selected
  managed tests overall, zero failures/skips, focused 155/155 and a zero-warning
  harness build. All fourteen normal packages pass: lean Core + WinUI are
  537,080/537,600 compressed bytes (520 bytes headroom), managed payload is
  1,249,280/1,258,291 bytes; Core is 99,477, WinUI 437,603 and Performance 89,317
  compressed bytes. Twelve primary and twelve additional tested DLL copies
  match package SHA-256s. Evidence is `*-v39-scene-binding-20261001`,
  `packages-anycpu-v39-scene-binding-20261001` and its compliance reports.
  Independent review subsequently found an unresolved nested marker could
  consume an outer slot, and staged inline resources lacked rollback ownership;
  the v40 follow-up fixes these findings and is not yet qualified by this v39
  evidence. No actual reversible graph detachment or refill is implemented yet.
- The frozen v40 rollback/binding follow-up passes 937/937 renderer tests,
  1,039/1,039 fast Core, 76/76 Math, 115/115 Mermaid, 3,420/3,420 app,
  10/10 HTML, 61/61 TextMate, 9/9 conformance and 18/18 selected resvg tests:
  5,685 selected managed tests, zero failures/skips. Focused tests pass 167/167
  and the harness builds with zero warnings/errors. The nested-marker regression
  covers normal, empty and whitespace attributes without changing placeholder
  classification. Cancellation and a second actual eager native-path constructor
  failure retire the first staged run; duplicate/throwing cleanup, idempotence,
  explicit parent adoption and reachable snapshot-transfer fallback are covered.
  Independent ownership review found no remaining concrete cleanup/ordering defect.
  The ledger shares one reference-identity set for block roots and staged
  disposable runs; block virtual disposal and async retirement remain distinct
  from synchronous inline disposal. It is cleared with construction, never retained
  by the snapshot. An initial v40 compile failure assumed blocks implemented
  `IDisposable`; the correction retains their existing contract and dispatches
  disposal by root type. The initial failure note/directory are preserved.
  All fourteen normal v40 packages pass unchanged gates: lean Core + WinUI
  537,201/537,600 compressed bytes (399 bytes headroom), managed payload
  1,249,792/1,258,291 bytes. Core is 99,477, WinUI 437,724 and Performance
  89,314 compressed bytes. License, SPDX/notices, exact package set, 76 locked
  Rust dependencies, six selected-RID native assets and twelve AnyCPU assemblies
  pass; twelve primary plus twelve additional dependency SHA-256 bindings match
  tested/harness assemblies. Reports are `*-v40-scene-ownership-20261001`,
  `focused-v40-final-scene-ownership-20261001`,
  `packages-anycpu-v40-scene-ownership-20261001` and its compliance directory.
  This is dirty local-candidate evidence, not independent rebuild, signed
  worker, NativeAOT, live browser/top-500 or counterbalanced release qualification.
- The remaining graph-residency slice must retain the scene-free identity after
  completed-cache eviction/`Trim`, while the progressive plan can restore a
  cached scene or re-render its retained work descriptor. Stage expensive work outside
  the snapshot lock, then validate the live plan/work generation, viewport
  admission, snapshot, binding and scene slot under its existing exclusion.
  A visual-only rebind also requires matching dimensions and semantic/style
  projections; never preserve old UIA metadata against a changed result by
  assumption. Dispose rejected staged results. Detached visible owners need
  explicit pending/source fallback and must not count as ready or paint blank.
  Cache acceptance is opportunistic, not a guaranteed permanent handoff, and
  unsupported fragment shapes stay attached until a safe path exists. This
  is an identified implementation route, not completed graph-residency evidence.
- A further read-only refill audit confirms that completed-cache `Trim` cannot
  release graphs rooted by current drawing owners. The existing visible probe
  does not enqueue a materialized block's binding marker, and the scheduler's
  completed-result short circuit prevents explicit refill. Both need coordinated
  detached-owner handling before any reversible detach. A detached visible
  `VectorSceneBox` currently paints nothing; provide an accessible bounded
  loading/source fallback (or explicitly budgeted last-good visual), and verify
  dimensions plus semantic/style/link projection equivalence before a staged
  swap. Reusing marker/ordinal alone is not proof. The initial complete slice
  can restrict eligibility to a trusted one-scene Mermaid block; other shapes
  must remain attached until their lifecycle is implemented. Repeat visits must
  not use the first-materialization full-document rebuild callback. No partial
  detach or readiness weakening was introduced in this prerequisite follow-up.
- The ignored v35 allocation-attribution probe used the unchanged 1,000-call
  warmup and 10,000-call measurement: five windows, contextual per-call samples,
  simple-style samples and no-op samples each observed zero bytes. The prior
  1,784-byte style failure and 7,304-byte navigation variance remain unexplained;
  neither this probe nor passing full runs establish their cause or waive gates.
- An in-process run of five selected same-byte audit Node files passes 61/62
  tests with no skips. The live Edge replay is the failure: Edge exits
  `0x80000003` before DevTools becomes ready. This supplements the earlier
  restricted child-launch/broker evidence; it is not a browser-parity result.
  The log is `same-byte-node-v35-20261001.log`. A current-candidate full 500-case
  audit and qualified counterbalanced release benchmark remain unavailable.
- PR #99 checks re-read at 11:33 UTC still refer to published head
  `1c00c83ddfd2e8b65e05cb57e359a3f092b37271`, with 42 failures, 15 successes,
  two skips and two canceled checks. No local changes were committed or pushed;
  this environment has read-only Git metadata. The source/codec/COW/graph
  implementation gaps and the normal-token runner, frozen reference, feed,
  signing and physical-matrix requirements listed above remain necessary.
- Follow-up source ownership review confirms that the host read-admission lease
  does not accompany the returned `MarkdownImageAsset.Bytes`. The host image
  cache, resolver result, session cache/single-flight and box generally alias
  one allocation, but session cache eviction or a zero/undersized cache budget
  can remove its cache accounting while consumers still retain it. Raster keeps
  the source through decoding; SVG box/cache references can keep it longer.
  A changed sanitized snapshot is an actual second allocation, and Resvg's
  independent immutable preflight source copy lasts with its document. An
  internal reference-identity consumer lease would make that subset's live
  ownership measurable, with distinct cache and consumer references and exact
  trim/cancellation/drain tests. It would not extend the hard read-admission
  ceiling across result return, account the provider's private copy, or prevent
  allocation before admission. That requires a result-lifetime lease-transfer
  contract with admission before allocation; do not relabel cache counters or
  reserve again after allocation and call the total-source-memory goal met.
  `DocumentScope` removes `_inFlight` entries on both success and failure before
  completing waiters; it has no permanent completed-result dictionary. Do not
  attribute the live-consumer ownership gap to an additional completed-task map.

### October 1 single-block command-graph residency and refill follow-up

- The first actual reversible command-graph slice is now implemented for a
  materialized block whose result is exactly one vector leaf at ordinal zero.
  Its source span and a boxed 32-byte SHA-256 projection identity must match
  the retained completion descriptor. The digest covers the complete immutable
  fragment, attributes, commands, text, transforms, paint, semantics and links;
  marker/ordinal equality alone never permits adoption. Inline and multi-leaf
  results deliberately remain attached. This is not an aggregate graph-byte cap.
- Leaving an eligible band releases native resources before detaching the
  drawing's graph. Scene-independent semantic/style/source/geometry records stay
  intact. Re-entry uses the existing lazy request, not first-materialization's
  full-document rebuild. It restores from the bounded cache or rerenders after
  eviction and rejects any changed projection. Worker preparation captures only
  immutable drawing inputs under snapshot exclusion, then renders, estimates
  and prepares native drawing resources outside that lease. Commit rechecks the
  snapshot, revision, policy, exact pending band, generation, request and token.
- Previous good band pins survive staging. Transient detached owners have
  separate pins; superseding a band retires obsolete transients without stealing
  the current band. Graph work participates in snapshot/document/generation
  retirement by its actual producer task, even when a provider ignores caller
  cancellation. Cooperative cancellation terminates the wait but is not reported
  as a failed graph or leaked producer. Cleanup faults remain failures.
- Detached visible owners get a bounded source/name fallback prepared on the
  worker, never during paint. A changed or failed result publishes typed MDP0002
  diagnostics and localized unavailable status. Null, throwing or canceled
  replacement factories preserve the prior fallback; rejected native candidates
  are disposed. Terminal failure is settled for scheduling but never render-ready.
  The fallback factory/orchestration and drawing recipe belong to the optional
  Performance package; the base box owns only adopted native resources.
- Root UIA now gets failed/loading readiness from one validated snapshot probe.
  Targeted scene peers are indexed by their vector owner, avoiding an unrelated
  document scan. Completion notifies status for new and already-applied layout
  revisions and failure paths; a busy lease retries after paint. Completed status
  changes drain even without a UIA listener. Source-wiring tests are not proof of
  live UIA property events, which still need the Sandbox/device release lane.
- Preserved pre-fix runs document generation-drain cancellation, stale reflection
  contracts, a double-scaled measurement fixture and a resource-key mismatch.
  The v41d renderer run passes 961/962 tests but fails the unchanged 1,024-byte
  navigation allocation gate at 7,720 bytes. A separate full diagnostic build
  passes 963/963; its attribution probe records one cache build and zero bytes
  in aggregate navigation, separate lookup/movement and counter-only windows.
  That does not explain or waive the failed gate. The earlier 24-byte reflective
  scene-estimate observation and prior style/navigation failures remain open
  allocation-origin evidence, not proven runtime noise.
- Initial fresh normal fourteen-package v41/v41d candidates fail the unchanged
  537,600-byte lean compressed ceiling (538,682 and 538,658 bytes). Internal
  immutable storage compaction, removal of duplicate/test-only accessors and
  optional-boundary cleanup preserve public API, readonly identity, native
  ownership and interface-required members. The v41j normal candidate now passes
  5,713 selected tests: renderer 965, fast Core 1,039, Math 76, Mermaid 115,
  app 3,420, HTML 10, TextMate 61, Conformance 9 and selected resvg lifetime/
  mapping 18, with zero failures/skips. The performance harness and a separate
  diagnostics-enabled optional-package build both have zero warnings/errors.
  Earlier failed artifacts remain intact, including the unexplained allocation
  failure; the passing run does not establish its origin.
- All fourteen fresh normal v41j packages pass the unchanged strict compliance
  lane: lean compressed 537,590 / 537,600 bytes and managed 1,250,816 /
  1,258,291 bytes. The 10-byte compressed headroom is not safe expansion room.
  Core is 99,477 compressed bytes, WinUI 438,113 and Performance 101,314.
  Twelve primary packaged managed assemblies match the built/test/harness copies,
  with twelve additional dependency-copy matches across all four extra test
  projects. SPDX/notices and compatible-license policy pass for all fourteen
  packages, along with 76 locked Rust dependencies, six selected-RID native
  assets and twelve AnyCPU assemblies. Development workers remain unsigned.
  This binds one local build only; known independent WinRT-generation
  nondeterminism and production signing remain open. No cap, assertion,
  dependency or compression strategy has been relaxed.
- App source-contract checks exposed missing integration for the new failure
  status: JitHub's paint acknowledgement now uses the shared typed readiness
  probe and accepts only `Ready`; failed, busy and unverified scenes never
  acknowledge final pixels. The new stable unavailable key is mapped in the
  host provider and all thirteen real/pseudo localization catalogs. Updated
  source-wiring tests pass; this is not live UIA-event or physical-theme evidence.
  The retained artifacts use `*-v41j-scene-graph-refill-20261001`,
  `packages-anycpu-v41j-scene-graph-refill-20261001` and
  `compliance-anycpu-v41j-scene-graph-refill-20261001`.
- WinApp 0.7 Sandbox readiness still fails before the application with
  `sandbox_start_failed` / localhost-broker `UnauthorizedAccessException`.
  Six unmodified same-byte Node files run in one process pass 67/69, with no
  skips: live Edge exits `0x80000003` before DevTools and the emoji-tamper child
  cannot spawn (`EPERM`). These are diagnostic results, not same-byte top-500
  fidelity/performance evidence. Readiness details are preserved in
  `v41-local-runner-readiness-20261001.json`.
- PR #99 still names head `1c00c83ddfd2e8b65e05cb57e359a3f092b37271`
  with 42 failed checks, 15 successes, two skips and two cancellations. A fresh
  managed-job log confirms its foreground-readiness timeout; the local test now
  uses a virtual clock without changing production qualification. A sampled
  ranks-451–475 job also fails mandatory screenshot completeness despite being
  below the storage quota. The returned audit log is truncated, so it is not
  sufficient to attribute missing tiles to a renderer defect. Local changes are
  uncommitted/unpublished under this environment's read-only Git metadata.
  Qualified full counterbalanced, same-byte top-500, signing, NativeAOT,
  architecture/device/theme gates and source/codec/COW/remaining graph ownership
  work all remain open.
- A separate ownership audit identifies the next source-memory escape precisely:
  a live image load retains `asset.Bytes` while waiting for the bitmap-key gate,
  CPU preparation and raster admission. Fetch slots have already been released;
  decode admission counts only granted working-set estimates. The source LRU's
  byte cap cannot release arrays pinned by those downstream waiters. A real
  fix must reserve end-to-end capacity before source resolution, hold it through
  decode/cancellation, and test active plus pending maximum-size arrays with
  visible priority and exact release. Counting bytes only after they are queued
  is not a bound. SVG, data-URI, local and host-cached routes need explicit
  coverage. The 1,800-image storm, native codec peaks and COW geometry publication
  remain implementation/qualification gaps, not merely unavailable runner gates.

### October 1 host-memory-pressure and real-app compilation follow-up

- JitHub's independent 64 MiB image-source LRU now listens for Windows
  `AppMemoryUsageIncreased` and clears its retained entries. The callback only
  changes the thread-safe cache; it does not mutate shared byte arrays, do
  network work or dispatch UI work. This complements session cache trimming;
  it does not bound encoded arrays pinned by active or queued consumers.
- A weak relay prevents the static OS event from retaining the image service
  if removal fails. Constructor rollback removes partial subscriptions and
  releases an owned HTTP client. Disposal is idempotent, attempts event removal
  once and completes owned-state cleanup before propagating an unexpected
  removal error. Expected platform-unavailable notifications preserve the
  ordinary bounded LRU. A failed OS removal may retain an inert relay delegate,
  but not the service; callbacks after disposal are harmless.
- Five new tests exercise actual byte-cache eviction with disk reuse and no
  extra HTTP fetch, repeated disposal and late callbacks, partially failed
  registration, cleanup after unexpected removal failure, owner collectability,
  and expected WinRT removal failure. The preserved hardened run initially
  failed two tests: release-only failure reporting and the collection probe's
  own temporary strong reference. The production path now uses the existing
  handled-failure reporter, and a non-inlined probe ends its temporary reference
  before collection. The final app integration run passes 3,425/3,425 with no
  failures or skips, replacing—not adding to—the earlier 3,420-test cohort.
  Combined with the eight unchanged v41j cohorts, this is 5,718 selected tests;
  it is not a full native worker/fault, browser or physical-device suite.
- Building the real Release x64 application exposed errors hidden by the
  headless test project's implicit imports: the disposal bridge lacked an
  explicit `System.Threading.Tasks` import. Its shared-base retirement field
  and signatures also needed narrowly documented MR1001 suppressions, matching
  the existing borrowed-control integration; no obsolete base is constructed.
  After those fixes the converter/XAML errors disappeared. The fresh build
  completes C# and XAML/PRI compilation with zero warnings and then fails the
  mandatory security target. A direct locked, audited restore confirms NU1900
  for the unreachable `https://api.nuget.org/v3/index.json` vulnerability feed.
  No feed, audit, analyzer or release-security setting was weakened. This is
  compile evidence only, not a successful Release build, NativeAOT publish or
  app run.
- A fresh post-integration binding check still matches all twelve packaged
  managed assemblies to their normal built/test/harness copies; the isolated
  app-build output does not replace them. Evidence is retained in
  `app-v41-final-integration-20261001`,
  `jithub-v41-fixed-compile-20261001.log`,
  `jithub-v41-locked-security-restore-20261001.log`, and
  `v41-host-memory-pressure-and-app-integration-20261001.json`. The earlier
  point-in-time v41j record remains intact. Runner, signing, reproducibility,
  top-500, counterbalanced release and remaining ownership/codec/COW gates
  remain open.
- A later connector read still reports the same published PR head and
  42/61 failed checks. Unlike the earlier truncated diagnostic response, the
  GitHub job-log endpoint returns the complete 357,225-character ranks-451–475
  log: all 25 cases ran and nine failed. Its case lines do not include the
  individual failure causes. The shard's detailed evidence artifact is present
  (`readme-audit-evidence-451-475`, 1,160,183 compressed bytes), but this machine
  cannot read its download endpoint: the socket connection is actively refused.
  Missing screenshot completeness and the nine case failures remain failures;
  the log is not proof that their cause is a renderer exception or image defect.

### Source-ownership implementation boundary (v41 design; v42 active-source implementation)

The read-only end-to-end trace rules out adding a semaphore after resolution or
holding fetch slots through decode. A post-resolution waiter already owns the
array; a maximum-size outer reservation can double-charge the resolver's
existing reservation and deadlock. The next implementation must instead:

- Add a compatible optional lease-aware resolver capability rather than change
  the positional `MarkdownImageAsset` record or existing resolver signatures.
  Its result transfers an immutable source-buffer owner with a ref-counted
  admission lease. Place the optional public transport in the Performance
  capability; the base control's internal consumption seam must remain
  engine-neutral and must not discover providers or reference the optional
  assembly. No new default-path feature flag or automatic discovery is needed.
- Admit bytes before HTTP/API materialization, file/WinRT reads and exact
  data-URI decoding. Preserve actual weighted reservations and visible priority;
  do not substitute a count of images or late diagnostic byte counter.
- Transfer one owner through both shared-flight and per-document fan-outs. Each
  ImageBox consumer must retain a handle before receiving the source reference
  and keep it through downstream admission and actual byte use. Root handoffs,
  canceled waiters, late non-cooperative resolver successes and successful
  hedge losers must all release their handles exactly once. Timeout of an await
  does not end the producer's buffer lifetime. Disposed carriers must also drop
  their source reference, rather than leave an uncharged array in a completed
  task/result wrapper.
- Keep fetch-slot and source-owner lifetimes separate. Preserve LFS's release of
  pointer bytes before recursive media resolution. Do not retain an active byte
  lease permanently in a source-cache entry: caches retain raw arrays under
  their separate cache budgets, while prefetch releases transient admission
  after cache population.
- For cache hits, peek scalar length/generation only, wait for admission, then
  reacquire and verify the cache entry before exposing its array. Eviction or
  replacement during the wait releases the grant and retries normally. Apply
  this to the session cache and JitHub's memory/disk, stale and refresh routes;
  obtaining an asset reference before waiting still pins an uncharged array.
- Preserve compatibility for arbitrary legacy resolvers, but do not promise a
  pre-materialization bound for providers that neither admit nor transfer
  buffer ownership. Their hidden allocations cannot be bounded by a semaphore
  in the caller. The strict guarantee must name participating capabilities and
  built-in routes explicitly; legacy return-byte counting is diagnostic only.

Required regressions include maximum-size source storms blocked behind decode,
same-source fan-out, visible progress with saturated background capacity,
metadata-first cache-hit eviction races, local/data-URI reads, known/unknown
HTTP lengths and chunk spooling, stale/refresh, LFS recursion, hedge losers,
timeout/late success, cancellation/grant races and zero owned bytes after drain.
They must sample active plus pending retained arrays, not merely granted codec
work. Fresh public-API, strict package-size, AOT and full release checks remain
mandatory; the historical ten-byte lean-package margin must not be bypassed.

### October 1 v42 source-ownership checkpoint (not release qualification)

- Added the explicit optional `IMarkdownLeasedImageResolver` and disposable,
  ref-counted `MarkdownImageResolutionLease` transport. The base control uses
  an internal engine-neutral seam; no reflection, optional-package reference,
  discovery, positional-record change or default-path feature flag was added.
  Final disposal drops the source reference before returning the reservation.
- Session shared-flight and per-document completion tasks now carry only a
  completion signal. Each consumer retains an independent handle before root
  handoff retirement; fetch slots end at resolution, while source reservations
  survive decode admission and actual source use. Cache hits reserve using
  scalar metadata and verify the generation after the wait. Detached late
  successes, consumer lifetime and cancellation-callback retirement participate
  in shutdown. A scoped optional `IMarkdownImageSourceWorkTracker` registers a
  source-free barrier before nested/hedged producer startup, covering losing
  transport work even before it reserves any source bytes. The scope owns one
  pre-registered session drain unit, closes when resolution returns, observes
  child faults without source-bearing messages, and drains only after all
  registered actual work and cancellation callbacks settle. Registration after
  closure is rejected. Cancellation/completion interleavings pass focused tests
  and independent ownership review, not live stress qualification.
- Built-in file, WinRT and exact data-URI reads admit capacity before managed
  allocation. Strict base64 validation/decoded-length measurement precedes the
  allocation, and native `DataReader` overlap is charged conservatively. SVG
  provider Open awaits actual completion before releasing caller bytes; a
  canceled wait is not treated as completed producer work.
- The same-byte offline resolver and lifecycle/audit wrappers preserve the
  capability conditionally. Legacy resolvers remain compatible without falsely
  advertising source ownership. The internal production image service has an
  owned HTTP/cache/shared-flight route. The production GitHub resolver adapter
  now transfers ownership through public/raw-CDN/Camo/origin/cache-only,
  authenticated Contents/Blob and LFS/hedged candidates; pointer admission is
  released before recursive LFS media fetch. Production uses the leased facade
  only for a capable image service; legacy/custom paths remain compatible.
  Cancellation-callback exceptions are isolated and callback retirement is
  awaited before CTS/lease teardown. A shared flight registers its source-free
  retirement signal before producer startup, including cancellation before any
  byte grant. One lazy facade per provider preserves resolver identity across
  views and therefore cache/shared-flight reuse. Unknown-length/chunked HTTP
  tests verify admission before first read; the spill path clears scratch-buffer
  and one-byte-probe references before returning initial quota and awaiting the
  final exact-size grant. The real app build is still blocked before
  compilation, so passing source-linked seams do not prove that the entire
  GitHubService compiles. Authenticated API base64/UTF-16 response intermediates,
  SVG provider copies are separate open
  aggregate-memory work; the source-payload quota is not a total-process cap.
- SVG boxes now retain weak source identity and scalar metadata instead of a
  raw source-array field. After weighted source-cache eviction, a new raster
  variant re-enters the normal authorized admitted source path; an exact GPU
  bitmap hit remains worker-free. Separate non-async publication factories
  receive a typed weak/scalar metadata carrier, preventing C# merged closures
  from retaining raw bytes after caller admission retires. Metadata reuse
  requires the current security-partition hash. Collection/eviction/re-entry,
  changed-partition and publication-carrier regressions pass, as does independent
  ownership review. These tests deliberately use a provider without retained
  source copies and do not qualify real provider memory.
- `core-v42-full-fast-i-20261001` passes 1,090 tests with zero warnings/errors,
  including canonical base64 validation and binary/escaped/whitespace variants;
  the 501-case external-gate validator partition was excluded from this fast
  cohort, not counted as a current pass. `app-v42-ownership-final-f-20261001`
  passes all 3,486 headless tests with zero
  warnings/errors, including leased cache/service/LFS seams, hedge losers,
  blocking/throwing cancellation callbacks and late cleanup. An earlier app
  run's two failures were stale assertions for the
  supported worker stages 117–120; exact stage/identity assertions now pass and
  malformed/unknown evidence remains rejected. A prior alternate-output run
  failed repository-root discovery and is retained, not counted as a pass.
  Two follow-up cancellation-test timeouts exposed test-owned registration
  disposal racing ahead of `CancelAsync`; moving registration cleanup to the
  test's final teardown exercises the intended callback without changing its
  timeout or assertions. Three fresh repeated service/provider/transfer cohorts
  pass 16/16 each, including the spill scratch-buffer GC assertion.
- `renderer-v42-source-ownership-staged-diagnostic-g-20261001` passes 997 tests,
  including all twelve API baselines, source fan-out/eviction/cancellation,
  actual SVG Open lifetime, detached-work shutdown and tile publication. The
  earlier broader run passed 981/982: its one source-contract assertion still
  used a removed private class as a search boundary. Updating that boundary
  preserves the ownership checks. A later eviction/re-entry test correctly hit
  the still-valid exact GPU bitmap and timed out waiting for an unnecessary
  reopen; changing its display size forces a real cache miss without disabling
  the warm path. The subsequent 997-test run passes. Its provenance explicitly
  records reused unchanged feature assemblies and a failed normal build:
  this is correctness diagnostics, not normal-build, security, AOT or release
  evidence. The normal renderer build remains blocked by NU1900 in transitive
  content-copy targets. No audit/analyzer/feed setting was weakened.
- Fresh full packaging (`packages-anycpu-v42-c-source-ownership-20261001`)
  creates five packages before stopping at the HTML project's mandatory NU1900
  error. The unchanged size checker accepts the present lean/Performance caps
  but fails for the missing Resvg package; this is not an all-package pass.
  Core+WinUI measure 536,214 compressed bytes and 1,251,328 managed bytes,
  below unchanged caps of 537,600 and 1,258,291 respectively, with no native
  payload. Performance measures 110,432 compressed bytes under its 131,072 cap.
  Prior over-cap runs through v42i are retained. Legitimate private compaction
  unifies task fault observation and canonical base64 parsing, and replaces
  private SVG wrappers with direct leases/tuple keys. Core now uses the same
  exact metadata-driven public/protected XML documentation filter as WinUI:
  all public/protected documentation is retained (522 Core, 630 WinUI members),
  while implementation-only documentation is omitted. No feature, public API,
  safety/size cap, or test assertion was removed. Fresh all-package/security/AOT
  checks remain required; prior v41 passes are not current v42 qualification.
- `MarkdownRenderer/artifacts/v42-b-source-ownership-checkpoint-20261001.json`
  hashes the final test TRXs/logs, staged provenance, present package contents,
  production source snapshot and preserved earlier failures. It explicitly
  separates correctness diagnostics, reused unchanged feature assemblies,
  diagnostics-enabled test builds and shipping package builds from normal
  production compilation/security/AOT/release qualification. Cohort counts are
  not aggregated because they overlap; source hashes do not prove that blocked
  production paths compiled. No current all-fourteen-package pass is claimed.
- PR #99 was re-read at 18:40 UTC and still points to
  `1c00c83ddfd2e8b65e05cb57e359a3f092b37271` with 42 of
  61 checks failed. The published top-500 failure remains unresolved and no
  current-candidate same-byte Edge or full counterbalanced release result exists.
  This machine's Sandbox broker, Edge startup, restricted worker startup,
  vulnerability-feed access, signing and NativeAOT runner failures remain
  precise external qualification needs. No local diagnostic substitutes for
  those gates, the physical DPI/device/theme matrix, or the complete 500 audit.

### October 1 v43 source-retirement/preflight and raw-Contents checkpoint

The v42 artifacts remain a preceding checkpoint. The current source and evidence
are identified in
`MarkdownRenderer/artifacts/v43-source-retirement-checkpoint-20261001.json`:

- Resvg clears its owned preflight/request source references only after every
  admitted render and cancellation callback retires. It clears the invoked
  cleanup delegate even when cleanup throws; a retained disposed document keeps
  metadata but no source-array pin. These are retirement guarantees, not a live
  aggregate provider-memory cap.
- The required provider snapshot is copied off a non-pool caller, while retaining
  the existing thread-pool fast path and borrowed-input lifetime contract.
  Correctly labeled embedded rasters skip the redundant DOM normalization pass.
  Inert external DOCTYPE removal reuses that private snapshot rather than making
  a second full-source array. The scanner is limited to a reader-validated UTF-8
  prolog declaration and preserves literal comment/CDATA markers. UTF-16-with-DTD
  remains fail-closed; recursive MIME repair inside embedded SVG data URIs and
  sanitizer/DOM peak-memory admission remain separate open work.
- Review also found that consuming `<style>`/`<desc>` text advanced the XML reader
  onto its next node, which the loop then skipped. The loop now inspects that
  current node. Compact adjacent scripts, event handlers and external-image
  references are rejected, and following text metadata/filter cost is counted.
  Independent worker validation remains unchanged.
- The production authenticated image client has an internal, explicitly
  implemented raw-Contents capability; `IGitHubClientService` and public renderer
  APIs remain unchanged. Its escaped path/ref, authentication and policy checks
  are preserved, but image reads no longer materialize JSON/base64 UTF-16 data.
  The shared admitted reader is reused for remote images and authenticated
  Contents. Known lengths reserve before reading; chunked sources stop at the
  applicable ceiling plus one proof byte, deferring speculative work rather than
  draining an image that cannot fit. Scratch/partial buffers are cleared before
  returning their reservations, including retained faulted tasks. LFS pointer
  carriers release their bytes/lease before recursive media acquisition. GitHub
  error-body parsing is capped at 64 KiB while retaining status/header-based
  authentication/rate-limit handling. Custom JSON-only clients remain compatible
  and outside the raw-source intermediate bound; small chunked reservations
  deliberately remain conservative.
- The final headless application build passes **3,507/3,507**, including three
  retained-task GC regressions. It compiles the client/shared reader and pure
  service seams, not the complete WinUI `GitHubService` integration. The earlier
  3,504-test pass preceded the final buffer cleanup and is not substituted for
  this final run. The fast core partition passes **1,090/1,090**; its separate
  501-case external-gate validator partition was not rerun in this checkpoint.
- Fresh exact-source isolated SVG compilation has **zero warnings/errors** and
  passes **58/58** selected managed tests. Three overlapping preflight/retirement
  repeats pass **28/28** each. The staged renderer/API suite passes **997/997**,
  including all **12** baselines, against this SVG assembly with identified reused
  v42 test/runtime/unchanged feature assemblies. Provenance records that the
  artifact-only compiler uses `net10.0`, existing runtime references and no
  PackageReferences. This is not a successful shipping Windows-TFM build, AOT/
  trimming analysis, native worker/fault qualification, or live speed verdict.
- Normal app/provider builds and both compiler-target SVG attempts stop before
  C# at mandatory **NU1900**; no audit setting or gate was disabled. Failed
  isolated/test compile attempts are retained beside the subsequent passing runs.
  Fresh normal packaging creates **5/14** packages and stops at HTML's same
  security error. Present Core+WinUI packages measure **536,214** compressed
  bytes against **537,600**, **1,251,328** managed bytes against **1,258,291**, and
  no native payload; Performance measures **110,432** against **131,072**. The
  mandatory package-size gate still fails for missing Resvg, so neither those
  partial sizes nor these tests establish full package/license compliance.
- PR #99 was rechecked in this run: published head
  `1c00c83ddfd2e8b65e05cb57e359a3f092b37271` remains unchanged, with 42 failed,
  15 successful, 2 skipped and 2 canceled checks. Its complete top-500 audit is
  still failed; this uncommitted local candidate is not that published revision.

Aggregate live provider/sanitizer copies, total live graph residency, copy-on-write
layout publication, oversized WIC memory qualification and the earlier unexplained
allocation failures remain open. Normal dependency-security/AOT/signing and
independent rebuilds, a Windows token/Sandbox/Edge-capable runner, the physical
architecture/DPI/device/theme matrix, a qualified full counterbalanced release
benchmark and the complete same-byte top-500 audit are still required. No release
threshold, source security policy or browser-relative gate is relaxed.

### October 1 v44 embedded-resource correctness and copy-reduction checkpoint

The v43 record remains a preceding checkpoint. The v44 evidence is recorded in
`MarkdownRenderer/artifacts/v44-embedded-data-checkpoint-b-20261001.json`
(the first record is preserved; b clarifies the sanitized-byte hash contract):

- Unknown-length HTTP/Contents downloads disable FileStream's unaccounted private
  write buffer. Fragmented transport reads batch into the existing admitted
  scratch, preserving the applicable ceiling plus one proof byte, cancellation,
  delete-on-close spooling and exact final admission. No second batching array is
  added. Retained-task tests cover successful spooling and a canceled final-grant
  wait. Network/repository download methods also clear their completed-task byte
  references before releasing an untransferred source lease. A malformed non-Camo
  SVG regression retains the failed fetch task and verifies both quota release
  and collection of its exact read buffers. The complete diagnostics-enabled
  headless application suite passes **3,511/3,511** (stage d). Earlier 3,509/3,510
  runs precede this follow-up; stage c's 3,507 pass omitted the four diagnostic
  tests and is not the final full cohort. These counts overlap and are not summed.
- Embedded data-URI decoding accepts spans rather than copying large UTF-16
  payload substrings. Percent decoding uses complete UTF-16 runs, preserving
  supplementary Unicode; base64 and percent paths count and enforce the lowerable
  decoded-byte ceiling before allocating one exact output array. Malformed
  escapes/padding, platform whitespace compatibility, pre-cancellation,
  surrogate replacement, equivalent UTF-8 deduplication and allocation boundaries
  are tested. These per-payload allocation assertions are not browser throughput
  measurements or a total-process memory cap.
- The native worker uses signature-first raster classification for its security
  walk and the actual usvg image resolver, including nested SVG and `feImage`.
  Only recognized PNG/JPEG/GIF/WebP signatures are sniffed; SVG still requires
  its explicit media type and authoritative recursive validation. The host no
  longer reparses/serializes the entire XML document to repair MIME headers. This
  removes a redundant DOM, payload-decode pass and expanded serialized source;
  source hashes identify the sanitized bytes handed to the worker. MIME headers
  are not rewritten; top-level encoding normalization may converge equivalent
  legacy encodings to the same UTF-8 source identity.
  CSS URL/header inspection also uses spans rather than payload-sized substring
  copies. CSS resource accounting remains tested, but CSS raster-image fidelity is not
  claimed where the pinned engine lacks a rendering surface for that property.
- Validated top-level non-UTF-8 XML is normalized to UTF-8 with a bounded streaming
  reader/writer, not a DOM. The declaration matches the new encoding; standalone,
  comments, inert processing instructions and metadata remain preserved. UTF-8
  sources retain the original-byte fast path. Output segments cannot collectively
  exceed the lowered source cap; the exact returned array is a separate transient
  copy, so this is not an aggregate memory bound. Failed/canceled inspection and
  normalization tasks release their snapshot/stream references. Nested SVG root
  admission no longer relies on a 512-byte prefix; long inert prologs are accepted
  while actual DTD/stylesheet instructions and wrong roots are rejected. Nested
  non-UTF-8 SVG remains explicitly unsupported, not rewritten; recursive body
  validation remains authoritative in the isolated worker.
- Worker XML validation inspects real parsed processing instructions with DTD
  parsing disabled. Harmless declaration spellings in comments/CDATA are no
  longer rejected by a whole-source lowercase substring heuristic. Both main
  and nested documents retain the actual DTD/stylesheet/network restrictions.
  Inspection also avoids UTF-8-boundary slicing of Unicode attribute names and
  eliminates source-sized lowercase copies for theme metadata scans.
- Fresh isolated managed compilation passes with **zero warnings/errors** and
  **121/121** selected tests. The broader staged renderer suite passes **997/997**,
  including **12** public-API baselines, with the identified reused v42 test and
  unchanged dependency assemblies. No public API or safety ceiling is changed.
  As in v43, the artifact-only compiler uses `net10.0` and explicit runtime
  references, not the shipping Windows-TFM/AOT analyzer graph.
  Compiler attempts d/e/f and the intermediate 111-pass/10-failure run are retained.
  The latter exposed declaration preservation and a weak-reference test harness
  holding its target during GC; both were corrected without removing assertions.
  Stage d's 99 passes used stale compile-c binaries after a failed compilation and
  are explicitly disqualified as evidence for the normalization change. The final
  compile-h/stage-f results and broader renderer stage-c identify the current source.
- The final locked/offline Rust suite and formatting check pass **35** tests;
  the one ignored test is the pre-existing manual font microbenchmark, not a
  required-fixture waiver. Direct, nested and filter-embedded incorrectly labeled
  PNG fixtures verify the actual resvg/usvg/tiny-skia pixels. Clippy passes with
  warnings denied. The first run's two failing new fixtures and failed formatting
  check are retained; the PNG fixture was repaired to contain valid pixels and
  the classifier now rejects nonsignature raster data, without weakening checks.
- The repository's pinned Rust 1.96.0/static-CRT/reproducible-linker build succeeds
  for all three architectures. Worker sizes are **3,120,128** x64, **2,933,248**
  x86 and **2,671,104** ARM64 bytes. These are worker-only sizes, not a full NuGet
  or selected-deployment size pass. All three unsigned development RID assets are
  refreshed; prior binaries are recoverable in the checkpoint's
  `workers-v44-embedded-data-20261001/preceding-development-assets` directory.
  No signed asset, app registration, installation or certificate trust is changed.
  Build/refresh provenance hashes identify the exact bytes. The existing native
  supply-chain verifier passes all **76** locked packages, sources, checksums and
  license selections; complete managed/package/signing compliance remains open.
- Three final provider end-to-end probes using compile-h's assembly and the fresh
  x64 worker fail before process
  startup with **CreateRestrictedToken Win32 error 87**. The normal SVG build again
  stops before C# at mandatory **NU1900**. Neither failure is suppressed or
  substituted by the native pixel tests. Production isolation, font/device/Win2D
  composition, NativeAOT, signed deployment, release performance and browser
  fidelity are unqualified.
  The host token already has a restricting-SID list without the required
  `S-1-5-12`. Windows intersects new restrictions with an already-restricted
  token's list, so this worker policy cannot be constructed from that token.
  A hashed, read-only `svg-v44-restricted-host-token-probe-20261001-r2` artifact
  reproduces error 87 with the current source's exact policy; no child is launched,
  privilege enabled or private SID value logged.
  The call order/signature matches compile-h and is not a stale-binary issue.
  Passing only Everyone or simply inheriting the host's list is not equivalent
  to the required worker policy and is not adopted. Qualification needs a runner
  token from which that policy can be created, plus suspended-child/Job Object
  support. See [the Win32 token contract](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-createrestrictedtoken).
- PR #99 remains at `1c00c83ddfd2e8b65e05cb57e359a3f092b37271` with 42 failed,
  15 successful, 2 skipped and 2 canceled checks. Its top-500 audit remains failed;
  this local source/binary candidate is not the published revision.

The next aggregate-source milestone must use a distinct weighted provider-source
owner before cloning, and retain that lease through preflight, worker queueing,
successful document ownership and actual render/cancellation drain. The existing
source-cache, per-request and worker parsed-cache limits do **not** cover this
owner and must not be silently reinterpreted. Bound byte and pending-request
counts, preserve visible priority and explicit retry/retirement semantics, and
avoid a second reservation that deadlocks behind the caller's original-source
lease. Duplicate sanitized snapshots can share immutable ref-counted owners, but
neither weak identity nor cache eviction alone proves live ownership is bounded.
An admission cap must not turn budget eviction into an unavailable valid image.
Caller originals, static-snapshot sanitizer intermediates, decoded temporaries
and transient shared mappings require separate accounting/qualification. Multiple
renderer instances also cannot be excluded merely because JitHub shares one.

The current off-band path already suspends the image, waits for its image-work
count and actual document renders to drain, then clears the provider snapshot.
That correct retirement path is not an aggregate admission proof. Admission must
also precede ImageBox's 16-permit process-wide SVG provider gate; otherwise queued
opens can occupy permits required by visible tile renders. A quota can still stall
when all retained tiled documents are visible, so arbitrary visible overcommit or
hard rejection is not an acceptable fix. First collect separately scoped live/peak
provider-snapshot ownership evidence (including failed/canceled opens), then
qualify a bounded backing/reacquisition policy for sources that cannot remain
resident. A document-only counter must not be mislabeled as total provider memory.

Aggregate live graph residency, copy-on-write layout, oversized WIC memory and
the earlier unexplained zero-allocation failures remain open. The required
external environment still needs reachable mandatory NuGet security data, a host
token permitting the required Restricted Code policy and suspended worker startup
with Job Objects, a functioning
Windows Sandbox broker and Edge/NativeAOT runners, approved production signing,
and the physical architecture/DPI/device/theme matrix. The full counterbalanced
release benchmark and complete same-byte top-500 audit remain required. No gate
or source policy is relaxed, and the automation remains active.

### October 1 v45 completed-task ownership and snapshot diagnostics checkpoint

The preceding v44 native/application results remain separately scoped evidence.
Current evidence is recorded in
`MarkdownRenderer/artifacts/v45-provider-snapshot-checkpoint-b-20261001.json`
(the preceding e/b checkpoint is retained):

- Open, preflight, render and worker mapping/admission helpers clear borrowed
  source-bearing arguments and callbacks on every terminal path. A clearable
  preflight holder lets callers retain the completed inspection task without
  pinning its source after document retirement. Actual admitted renders and
  cancellation callbacks still drain before the document retires that holder;
  caller-owned arrays are never mutated. Worker admission failure no longer
  masks its original exception by disposing an unacquired lease.
- Failure after a committed OPEN but before returning a document schedules
  best-effort worker-binding retirement and releases the private source owner.
  Detached CLOSE faults are observed. Transport/protocol/deadline failures or
  malformed CLOSE responses invalidate the worker; a queue-capacity rejection
  before exchange does **not** kill healthy unrelated work or quarantine it.
  A queue-full CLOSE can leave a native binding until bounded cache eviction or
  restart; this is not a claim that every binding closes synchronously.
  Cleanup validation itself is inside the terminal barrier, so a retained
  invalid-argument task cannot pin the other delegate's captured data.
- Diagnostics builds maintain per-renderer and process-aggregate numeric ledgers
  for the logical byte lengths of designated provider-private snapshots. Tickets
  start immediately before cloning, follow normalization and successful document
  ownership, and end only on actual source retirement. Failed/canceled opens and
  active render drain are covered. Reentrant diagnostic cancellation is checked
  again before cloning. Ledger and lease transitions are coherent under locks;
  EventSource callbacks run outside those locks, carry numeric fields only and
  are best effort. Events can arrive out of order: consumers use each scope's
  sequence, not arrival order. Process peaks are cumulative for the process.
- This ledger is observation, **not** weighted admission, allocated bytes, RSS or
  total provider memory. Caller/cache arrays, static-snapshot DOM/serialization,
  normalization input/output/segment overlap, decoded resources, shared mappings,
  worker trees and GPU ownership are excluded. It neither changes queue policy
  nor rejects an image on memory pressure. A metadata-only build check confirms
  ledger type names are absent with diagnostics disabled; it is not AOT proof.
- Final source-linked compile f passes with **zero warnings/errors** and
  **160/160** selected tests. Staged renderer c passes **997/997**, including all
  **12** public-API baselines, against that exact provider and explicitly hashed
  reused v42 test/runtime/unchanged feature assemblies. Counts overlap with prior
  cohorts and must not be summed. Compiler/source hashes are checked before
  staging; no tests run after a failed compile. Attempts b/c and d's 156-pass/
  2-failure run are retained: the repairs keep helper visibility internal, update
  the structural boundary after extraction, and assert both synchronous and
  dispatched cancellation. No assertion or gate was removed.
- Public API shape is unchanged. `SanitizedSvgBytes` documentation now explicitly
  requires caller memory to remain unchanged/alive until the asynchronous
  `OpenAsync` operation actually completes, including cancellation/failure;
  returning a pending `ValueTask` is not that completion. The XML documentation
  edit is source-reviewed, not a fresh shipping WinUI assembly build.
- Fresh normal provider build c still stops before C# at mandatory **NU1900**.
  The native source, token interop and all three development worker hashes match
  v44; its restricted-token error-87 evidence is reused, not rerun or qualified
  by pure managed tests. PR #99 still publishes
  `1c00c83ddfd2e8b65e05cb57e359a3f092b37271`, with 42 failed, 15 successful,
  2 skipped and 2 canceled checks. The visible CI readiness timeout failure is
  already fixed in the local virtual-clock candidate, not the published revision.

The next aggregate-source step remains bounded backing/reacquisition plus
weighted admission before cloning and before ImageBox's provider gate, preserving
visible priority without deadlock or turning valid-source eviction into an
unavailable image. The ledger is the scoped prerequisite, not completion of that
step. Sanitizer/transient and live-graph memory, copy-on-write layout, oversized
WIC qualification and unexplained allocation failures remain open. The normal
security/AOT/signing builds, required token/Sandbox/Edge runner, physical matrix,
qualified full counterbalanced release benchmark and complete same-byte top-500
audit are still required. No threshold or isolation policy is weakened; the
automation remains active.

### October 1 v46 source-preparation, measure-rollback and package checkpoint

The active local run continued through several implementation and verification
slices. Evidence is recorded in
`MarkdownRenderer/artifacts/v46-preparation-rollback-package-checkpoint-20261001.json`;
its final measurements completed just after midnight UTC on October 2. The v44
native and v45 ownership results remain separately scoped historical evidence.

- Static SVG preparation no longer does five independent full DOM traversals.
  One stackless discovery pass preserves each former finder's namespace,
  ancestor and ordering rules. Plain XHTML flattening, validation of discarded
  SVG graphics, and stylesheet text collection are also iterative; actual
  20,000-level fixtures pass without host recursive traversal. The existing
  16,384-character normalized-text limit is preserved, including whitespace-only
  input and trailing whitespace. SVG/XHTML scripts, unsafe attributes, unsupported
  branches, external references and entity policy are not relaxed.
- Switch fallback discovery is memoized by parent after script/animation removal.
  Replacing the first foreignObject with native SVG text updates the memo, so
  later siblings still see the newly authored fallback. Range-based viewBox and
  style parsing avoids Split arrays and overwritten/unsupported property string
  storms while preserving delimiter/Unicode trimming, malformed declaration,
  duplicate/case and last-wins rules. Marker, XML-input, CSS, animation, style and
  text scans observe cancellation periodically. One final `double.TryParse`
  invocation on a large numeric token is still not internally cancellable.
- Serialization uses 16 KiB segments and checks the immutable or lower configured
  prepared-source byte ceiling before copying/allocating a segment. Partial
  segments are cleared on failure and never returned as a valid document.
  `static-snapshot-output-bytes` is a typed resource-limit rejection; malformed
  XML retains its ordinary authoritative validation path. The lower ceiling
  applies to prepared output, not removable raw branches. This bounds output,
  **not** original/DOM/aggregate memory or overlapping final-buffer copies.
- Inline images now use immutable owner/generation measure checkpoints. Original
  native placeholder/caption layouts remain owned until commit; failed/canceled
  measures and successful speculative intrinsic-width queries restore available
  width, image/caption sizes, bounds and automation render destination. An earlier
  prefix remains recoverable when a later inline item fails. Disposal invalidates
  stale checkpoints and retires detached layouts without resurrection. Published
  text/image state is restored before native candidate cleanup; cleanup faults
  do not replace the original failure. Post-commit superseded native disposal
  faults are observed by the retirement path. This is local transaction correctness,
  not full immutable-graph/COW or live device-loss/UIA/DPI qualification.
- Final SVG source-linked stage f passes **240/240**, with zero warnings/errors;
  its `net10.0` explicit-reference compiler does not replace the shipping
  Windows-TFM/AOT/security graph. Final staged renderer f passes **1,002/1,002**
  with **12** API baselines, using freshly compiled WinUI/Core/tests and that
  provider, plus explicitly hashed unchanged feature/runtime dependencies.
  Normal WinUI compilation succeeds with two NU1900 warnings; the normal full
  test graph and provider still fail mandatory transitive NU1900. Conditional
  source compilation without diagnostics passes and excludes ledger metadata.
  These overlapping cohorts must not be summed or labeled a speed benchmark.
- A further **16/16** pure provider-contract partition passes against the same
  final SVG assembly: ordinary host rejection, immutable ceilings, protocol,
  public registration, environment allowlist and font-generation publication.
  Required worker/font/render fixtures remain unqualified, not skipped or waived
  by this explicitly selected diagnostic partition.
- The first renderer run's strict stable-residency test allocated **4,992** bytes.
  Five fresh-process reruns of the exact failing binary pass the unchanged
  zero-byte assertion; an isolated one-million-call probe and counter/no-op
  controls also report zero. Subsequent full runs pass, but no allocation-origin
  trace explains the earlier failure, so it remains open. No tolerance or assertion
  was changed. Intermediate fixture-order/compiler failures and their repairs are
  retained, and no tests were staged after a failed compilation.
- NuGet normalization now losslessly recompresses unsigned build artifacts with
  `SmallestSize`, retaining entry order, names, external attributes and payloads.
  Verification streams in 64 KiB chunks, comparing lengths, SHA-256 and computed
  ZIP CRC-32 against declared source/output CRCs before publication. Explicit CRC
  checks do not depend on the PowerShell host's framework version. DOS timestamp
  wall fields are compared without assuming ZIP stores a timezone. Signed,
  malformed-deflate and corrupted-CRC inputs are refused. A fully verified
  same-directory archive is published without a copy/delete fallback; artifact
  filesystem metadata inherits the output directory, not destination-specific
  ACLs/alternate streams. Locked publication leaves the original hash unchanged
  and cleans the exact temp file. This does not promise universal filesystem
  crash/metadata-merge semantics. The expanded compliance self-tests pass.
- The final normal fourteen-project pack attempt d stops at HTML's mandatory
  NU1900 after five fresh packages. Their source hashes match; independently
  recorded before/after ordered payload, CRC and metadata hashes also match.
  Core+WinUI is **530,133** compressed bytes (**7,467** headroom under **537,600**),
  **1,255,424** managed bytes (**2,867** headroom under **1,258,291**), with no
  native payload. Performance is **108,881/131,072** compressed bytes. Four managed
  assemblies across those five packages pass AnyCPU IL inspection. The unchanged
  full size gate still **fails** for the missing resvg package; optional/native,
  licensing, signing and independent reproducibility are not qualified. Earlier
  lean archives exceeded the cap by 414/295 bytes. The initial partial b report
  with managed size zero is invalid; its corrected `partial-size-evidence-a.json`
  and final d evidence are retained instead of silently replacing that failure.
- PR #99 still publishes `1c00c83ddfd2e8b65e05cb57e359a3f092b37271`: 42 failed,
  15 successful, 2 skipped and 2 canceled checks. The local candidate is not that
  revision, and its top-500 audit is still failed. No Git/PR mutation, signing,
  certificate trust, installation or weaker worker policy was performed.

The next architecture milestone remains policy-authorized backing/reacquisition
and aggregate weighted source admission before cloning and before ImageBox's
provider gate; private source snapshots retained for worker reattachment cannot
be counted as cache-only memory. Avoid same-pool lease/reacquisition deadlock and
all-visible tiled-source stalls without turning valid images unavailable.
The read-only code map confirms that the performance session's source admission
currently owns resolver-read leases, whereas provider preflight clones a distinct
snapshot retained by the SVG document and worker reattachment. Clearing the
64/32 MiB host SVG cache does not release those live document snapshots. The
smallest candidate seam is an explicit engine-neutral, byte-free backing recipe
that acquires immutable disposable read leases through the original authorized
resolver/context. It must revalidate asset cache identity, security partition,
policy generation and prepared-content hash, not treat a URL/hash as authority.
Use a distinct process-wide provider snapshot owner/admission pool, including
multiple renderer instances, before cloning and before the sixteen-permit
ImageBox provider semaphore. Contract/fake-path ownership and admission tests
can proceed locally, but a strict resident cap/eviction must not be enabled until
worker-affinity, healthy tree eviction, cancellation drain and crash/restart
reacquisition are implemented together and qualified. Healthy cache eviction is
not permission to replay content quarantined after active worker failure.
These are the next design constraints, not a newly shipped API or an enabled cap.
Sanitizer/DOM/transient and live-graph envelopes, immutable COW publication,
oversized WIC intermediates and the unexplained allocation origin remain open.
Required external qualification still needs the mandatory security feed, a token
permitting the existing Restricted Code worker policy with suspended startup/Job
Objects, functioning Sandbox/Edge/NativeAOT runners, approved signing/Store tests,
and the physical architecture/device/DPI/theme/HDR matrix. The qualified full
counterbalanced Release benchmark, same-byte Edge corpus and complete 500/500
README audit remain mandatory. No threshold or isolation policy was weakened;
the automation remains active and the performance goal is not complete.

### October 2 v47: admission fairness and worker reuse/power checkpoint

The local run continued through production fixes, independent reviews,
baseline-differential regressions, native architecture builds, package checks,
and the required worker probe. It did not stop after a diagnostic or relax a
release threshold. Evidence is recorded in
`MarkdownRenderer/artifacts/v47-admission-worker-checkpoint-20261002.json`.

Implemented changes:

- `SourceByteAdmission` reserves the first blocked same-priority waiter after
  at most one fitting bypass. Further fitting arrivals wait for active leases
  to drain or for that reservation to be canceled. Existing leases are never
  preempted; visible work still blocks new background grants, and fitting-only
  document rotation remains intact. The reservation applies to both priority
  queues. It guarantees bounded bypass, not a deadline for an active consumer
  that has not yet released its lease.
- Dequeued/granted/canceled waiters detach owner and priority-queue aliases
  before their asynchronous continuations drain. Retained-task GC checks are
  regression guards, not proof of a previously observed terminal-task leak:
  the exact preceding binary also passes those two checks on this runtime.
- A document records the worker-instance stamp of successful attachment as an
  atomic scheduling hint. Matching work may pass a same-priority FIFO head
  once, not indefinitely. An already-ready idle secondary may receive the
  actual oldest visible render explicitly bound to it before primary dispatch;
  visible opens and font-ineligible heads are not skipped. Stale/busy/retired
  hints fall back to the existing queue policy. `DocumentMissing` remains
  authoritative; healthy eviction still reattaches once, while transport
  failure still does not automatically replay active content.
- Energy Saver and unknown power state gate existing-secondary dispatch and
  font warmup, not only secondary startup. Primary work continues; active work
  drains and existing 30-second idle retirement remains. Recoverable power-query
  failures use conservative one-worker fallback rather than escaping after
  enqueue and orphaning a request. Fatal resource/process faults propagate.
- Native document aliases store their text tree's font generation, including
  uncached trees. A stale text render returns `DocumentMissing` before LRU
  bookkeeping/output-map access; geometry is unaffected. This is necessary
  stale-tree invalidation, not a complete `WM_FONTCHANGE` implementation.

Verification and limits:

- Fresh Release Core/WinUI/Performance compilation succeeds with three NU1900
  warnings; source-linked SVG compilation succeeds with no warnings/errors.
  The final managed partition is 310/310, and the staged renderer suite is
  1,008/1,008 with all twelve public API baselines. These overlapping correctness
  cohorts are not aggregated or presented as latency/CPU/memory benchmarks.
- Four admission fairness tests run against the preceding admission binary:
  three fail at the expected unbounded-bypass assertions and the fitting-order
  test passes. The current fourteen-test admission partition passes. The
  cancellation/grant accounting assertions and existing zero-allocation gates
  were not weakened.
- A new power-dispatch truth-table fixture initially had a mistaken blanket
  false assertion for its eligible case; it now asserts all eight explicit
  combinations. An existing synthetic source callback also retained its own
  capture while completion could inline the GC assertion. Its capture now
  retires in `finally` after its last read; source/mapping lifetime and strict
  post-drain collection assertions remain. The corrected exact-binary lifetime
  case passes in twenty fresh processes, without sleep/retry/threshold changes.
  Neither observation establishes a production memory leak.
- Diagnostics-disabled conditional compilation excludes snapshot-ledger
  metadata. This is not a NativeAOT/trimming or shipping-Windows-TFM verdict.
- Locked offline Rust tests pass 37 cases, with the one pre-existing manual
  font microbenchmark ignored; formatting and warning-denying Clippy pass.
  Pinned build scripts succeed for x86/x64/ARM64. Unsigned development worker
  assets were refreshed with verified recoverable backups; no certificate,
  trust, app installation, weaker token or in-process fallback was introduced.
  The locked native supply-chain verifier passes all 76 package records.
- The required same-document text/geometry font-rotation worker test is not
  skipped: it fails before worker startup with `CreateRestrictedToken` error
  87 on this already-restricted host. Real affinity/eviction/restart/fault and
  rendering performance/fidelity qualification remain open.
- A fresh normal fourteen-package attempt stops at HTML's mandatory NU1900
  and yields only five packages. CRC/SHA/payload-verified recompression preserves
  their contents. Core+WinUI is 530,200/537,600 compressed bytes (7,400 headroom)
  and 1,255,936/1,258,291 managed bytes (2,355 headroom), with no native payload.
  Performance is 109,519/131,072 bytes. The four assemblies in those five
  packages pass AnyCPU inspection. The unchanged full size gate explicitly
  fails because the Resvg package is missing; stale packages are not inserted.
- PR #99 remains at published head
  `1c00c83ddfd2e8b65e05cb57e359a3f092b37271`: 42 failures, 15 successes,
  two skipped and two canceled checks in the read-only snapshot. This local
  candidate is not published from the read-only Git worktree metadata.

Next safe architecture work remains authorized byte-free backing/reacquisition
and distinct aggregate provider ownership before cloning/provider admission;
the preceding v46 design constraints still apply. A backing recipe must bind to
the exact document scope, source, full immutable resolution context, consent,
resolver/cache identity and security partition. Reacquisition reruns normal
preparation/preflight and verifies the prepared hash; changed content re-enters
the normal image pipeline rather than attaching under an old document token.
An internal explicitly implemented capability using existing disposable source
leases is a candidate seam, not a shipped API. No backing interface, strict
snapshot cap or eviction was enabled by this checkpoint. Affinity alone does
not permit dropping the host bytes required for healthy `DocumentMissing`.

Font refresh also needs a versioned, generation-aware background catalog
protocol: coalesce scans per worker, retain the old font database until a new
snapshot is ready, invalidate DB-specific fallback IDs, keep geometry eligible,
and do not monopolize a worker lease while waiting for fonts. Merely changing
the cache key/generation does not reload the current one-shot font database.
This checkpoint does not claim the 750 ms cold-font or warmed browser gate.

Aggregate sanitizer/transient/live-graph ownership, immutable COW publication,
oversized WIC intermediates, priority-isolated catalog initialization, the
unexplained historical allocation origin and qualified independent rebuilds
remain open. Full counterbalanced Release benchmarking, the same-byte Edge
oracle, 500/500 README audit, NativeAOT/trimming, approved signing/Store execution,
and the physical architecture/device/DPI/theme/HDR matrix remain mandatory.
The next qualifying environment needs the normal security feed and a Windows
token supporting the fixed Restricted Code policy, suspended child startup and
kill-on-close Job Object, plus functional Sandbox/Edge/AOT runners. Automation
remains active; the performance goal is not complete.

### October 2 v48: font refresh, metadata and failure-isolation checkpoint

The local run continues through multiple implementation and review passes rather
than waiting for a later scheduled run after one small fix. Its final evidence
record is `MarkdownRenderer/artifacts/v48-font-refresh-checkpoint-20261002.json`;
the required live-worker failure remains a failure, not a skipped fixture.

Implemented changes:

- Protocol v6 carries the actual published font generation in the fixed response
  frame. Each worker coalesces refresh targets through one background scanner,
  keeps the prior font database until publication, retires cached and uncached
  text aliases in linear passes, and publishes a fresh fallback-ID cache with
  the replacement database. Generation-tagged acknowledgment prevents starting
  another scan while a completed candidate is still queued: at most the old
  published catalog plus one scanning/completed candidate are owned by this
  coordinator. This is an ownership design and unit-tested handoff, not a
  measured Windows font-memory envelope or a 750 ms cold-font pass.
- The host commits text work only against the current, exactly confirmed font
  generation. It releases worker leases between bounded HELLO probes, keeping
  geometry dispatchable, and releases the refresh gate before ordinary text
  admission once the catalog is ready. The fifteen-second readiness window
  includes gate and queue waits; individual exchanges retain the immutable
  request deadline. Source-free parked tickets preserve bounded admission and
  visible-render demand without admitting new work beyond the queue budget or
  deadlocking an all-parked queue. Cancellation drains late grants; a racing
  readiness deadline cannot transfer a canceled lease to ordinary content work.
- Healthy pending/failed font enumeration is environmental, not evidence of
  hostile SVG bytes. A malformed completed font probe invalidates its worker
  and reports an initialization failure without quarantining unsent content.
  Startup-failure accounting is not charged for ordinary queue/gate contention
  or a responsive catalog scan that remains pending. A secondary releases its
  slots during refresh, keeps geometry eligible and bounds same-generation
  environmental retries. Existing architecture/CPU/Energy Saver gates and
  thirty-second idle retirement are retained.
- `JitHubMarkdownRuntime` clears the pending dispatch marker before delivering
  a font-change invalidation. Only notifications awaiting delivery coalesce;
  a later notification can advance the generation again. The headless contract
  checks ordering and ownership, not actual `WM_FONTCHANGE` delivery.
- Streaming preflight propagates text and relevant theme dependencies from
  nested SVG data URIs in images, CSS resources and filter images. It reuses
  decoded buffers without a DOM/source-sized clone. Font metadata conservatively
  mirrors the worker's local-name handling, including foreign-namespace wrappers.
  Resource accounting remains deduplicated, but deeper uses of shared SVG bytes
  receive contextual validation; a deepest-successful-depth memo bounds repeat
  scans. Embedded depth includes the outer document as in the worker's reference
  budget. Nested network/file references, UTF-8/XML/depth checks and immutable
  limits remain enforced. CSS strings, transient decoded buffers and memo entries
  still require aggregate allocation/memory qualification.
- Quarantine is enforced for retained document handles as well as new opens,
  including work that waited in a queue and healthy `DocumentMissing` attachment.
  The guard contains renderer/identity references, not image bytes or resolution
  authority, and retires only after the document's admitted renders drain.
  The shared source-mapping helper validates both before mapping and after the
  factory returns, so quarantine published during mapping prevents attachment.
  Content faults are marked before invalidation and admission release; malformed
  successful metadata and operation-invalid statuses cannot escape that barrier.
  A completed faulty response still invalidates its exact worker when cancellation
  races it, without blaming canceled content. Only stale text work gets the font
  generation exemption; geometry carries its admitted generation into dispatch.
  Valid text-font pending/failure statuses stay environmental, while impossible
  future-generation metadata fails closed. No
  weaker token, in-process fallback, automatic replay after failure, new public
  API or strict aggregate snapshot cap is introduced.

Final exact-source diagnostic verification passes 381/381 selected SVG tests
(`svg-v48-font-contracts-h-20261002`) with zero warnings/errors and no skips.
The generation, wire, actual one-active/32-queued admission, nested metadata,
callback/mapping-guard and document-quarantine classes contribute 34, 4, 4, 12,
12 and 5 cases respectively. The actual pool test parks and reenters all 33
admitted requests with Energy Saver injected on; it is not a physical two-worker
capacity qualification. The 67-case font/admission/metadata/quarantine subset
passes twenty fresh processes with identical case names, no retries, no sleeps
and no threshold changes (`svg-v48-concurrency-repeat-b-20261002`). Repeated
executions are not additional unique tests or a live fault/performance suite.
The staged renderer suite passes 1,008/1,008 tests, including all twelve API
baselines (`renderer-v48-full-contracts-d-20261002`), and a separate compile
with diagnostics excluded is clean (`resvg-v48-without-diagnostics-d-20261002`).
The unchanged headless app input passes 3,512/3,512 tests; its dispatch contract
does not emulate real Windows font notifications. Interim bootstrap/compile
failures and superseded successful partitions remain preserved, not aggregated
or substituted for the release gate. Locked offline native verification passes 42 cases with
one pre-existing manual microbenchmark ignored; formatting, warning-denying
Clippy, and pinned x86/x64/ARM64 builds pass. The locked native supply-chain
verifier accepts all 76 package records. Final unsigned worker executables are
3,148,800 bytes (x64), 2,956,800 bytes (x86) and 2,693,120 bytes (ARM64); verified
recoverable development-asset backups remain. These executable-only sizes are
not the selected deployment or all-RID NuGet-size verdict. No signing, trust or
installation changes were made. The earlier v47 five-package lean result is
historical; the changed provider has no new full package/security pass.

PR #99 remains at published head
`1c00c83ddfd2e8b65e05cb57e359a3f092b37271` in the read-only snapshot: 42 failed,
15 successful, two skipped and two canceled checks. The local candidate is not
published from this read-only Git metadata. The normal app and SVG provider
builds fail at NU1900 before production compilation in the fresh `v48-b` logs;
the final required native-font test fails 0 passed/1 failed/0 skipped before
startup with `CreateRestrictedToken` error 87 (`svg-v48-required-font-worker-d-20261002`).
This required failure is not reclassified as a skip. Source-linked/headless
checks do not certify the shipping Windows TFM, AOT, dependency-security graph,
packaged activation or WinUI analyzer results.

Next aggregate ownership work still needs authorized byte-free reacquisition,
not a URL/hash treated as permission. The recipe must bind the exact document
scope, source, immutable resolution context, resolver/cache identity, consent
and security partition; normal reacquisition/preparation/preflight must verify
the prepared hash. A distinct process-wide provider snapshot admission owner
must run before cloning and before ImageBox's sixteen-permit provider semaphore.
Do not wait for reacquisition while holding the source-byte lease that it needs.
There is not yet a production retirement consumer for a backing recipe or a
qualified healthy-document eviction/restart path. No unused recipe, strict cap
or eviction policy is shipped as a substitute. Sanitizer/transient/live-graph
bounds, immutable COW publication, oversized WIC intermediates, the historical
strict zero-allocation failure origin and independent rebuild reproducibility
remain open.

The read-only allocation-origin review reconfirms the retained v46 4,992-byte
failure in `StableResidencyBandDoesNotAllocateOrRepeatLifecycleTransitions`.
Its measured window is the policy's 1,000 synchronous stable-band reconciliations,
not the control's complete viewport/indexing path. No attributable source-path
allocation or stack has been established; five exact-binary isolated passes,
the separate zero-allocation probe and later full passes do not explain the
failure. The strict zero-byte assertion stays unchanged. A failure-producing
run with managed allocation-stack evidence is needed before attributing it to
first-use/tiering or applying a code fix.

The next qualifying environment needs a normal-token Windows runner supporting
the unchanged Restricted Code policy, suspended startup and kill-on-close Job
Object, the mandatory vulnerability feed, and functional Sandbox/Edge/AOT
runners. Approved signing/Store child-process tests and the physical
architecture/device/DPI/theme/HDR matrix remain necessary. Full counterbalanced
Release benchmarking, the same-byte Edge oracle and complete 500/500 README
audit are still mandatory; neither this checkpoint nor earlier one-case
diagnostics meet them. Automation remains active and the goal is not complete.

### October 2 v49: SVG retirement and image-reflow publication ownership

The active local run implemented, independently reviewed, compiled and tested
multiple ownership fixes without waiting for another scheduled run. Exact-source
evidence is recorded in
`MarkdownRenderer/artifacts/v49-retention-checkpoint-20261002.json`. This is a
local correctness and partial-package checkpoint, not release qualification.

Implemented changes:

- A completed ordinary SVG bitmap no longer keeps its provider document in
  `ImageBox` or in its queued UI publication. The document retires through the
  existing drain-safe use owner after actual render/upload work finishes;
  disposal faults remain observable without discarding a good published bitmap.
  Tiled SVGs retain their provider document for subsequent visible tiles and
  release it on progressive suspension. Exact warm bitmap reentry avoids a
  new open/render; a different target reenters the same authorized resolution
  path. This is prompt ownership retirement, not aggregate-memory admission or
  a byte-free source backing recipe.
- Image-reflow work now borrows its snapshot and changed-index array through
  one clearable operation payload. Only the actual scheduled operation's drain
  clears these strong aliases. A bounded, non-inlined publication factory reads
  the snapshot without copying graph-bearing arguments into the async state
  machine. A one-shot atomic delivery envelope keeps only a weak snapshot
  identity and clears its action before invocation, including reentrancy and
  throwing callbacks. Compiled-assembly tests inspect the real control closure
  and state-machine shape, not just a modeled helper.
- Unload/dispose atomically closes that operation's publication owner before
  the existing cancellation/actual-task retirement barrier. A late worker
  cannot register a new delivery into a closed owner; if closure races an
  already registered callback, the held callback becomes an empty no-op.
  Snapshot/index work borrows and the CTS remain alive until their respective
  actual-work/cancellation drain. Distinct operation owners prevent an old
  worker from clearing a new owner's callback. The terminal fence uses one
  atomic slot, rather than separate closed and publication fields. No public
  API, feature flag, reflection-based production path, weaker token, security
  bypass or in-process fallback is added.

Final verification:

- The fast core suite passes **1,102/1,102**, zero failed/skipped
  (`core-v49-weak-handoff-d-20261002`). Its 501-case external-process fixture
  partition is explicitly excluded from this result. A separate unfiltered
  suite from these exact compiled inputs is serially queued
  (`core-v49-final-full-e-20261002`); queued work is not a pass. The earlier
  unfiltered `core-a` run has a preserved failed, superseded modeled-GC case
  and remains running through the external partition at this checkpoint.
- The staged renderer suite passes **1,012/1,012**, zero failed/skipped,
  including all twelve API baselines
  (`renderer-v49-document-retirement-g-20261002`). Current Core/WinUI/
  Performance and test inputs were compiled; unchanged feature runtimes and
  the unchanged v48 SVG provider were explicitly hashed. Three NU1900 warnings
  remain in this diagnostic build. This is not a normal full shipping/AOT/
  dependency-security build.
- All twelve handoff cases pass in each of twenty fresh processes
  (`retention-v49-core-repeat-b-20261002`), and all twenty image-lifetime and
  compiled-control cases pass in each of twenty fresh processes
  (`retention-v49-image-repeat-c-20261002`). Tests cover lost delivery,
  throwing/reentrant/concurrent delivery, register/close races, old/new owner
  isolation, canceled gated work, source eviction, blocked disposal, exact warm
  reuse, non-tiled versus tiled retention, and actual async payload release.
  The strict collection attempts, assertions, timeouts and production gates
  are unchanged; repetitions are not additional unique tests or a live-memory
  qualification. Earlier modeled-GC and compiler failures remain preserved.
  The final model uses bounded work/factory frames matching the production
  lifetime contract; the earlier raw-local model failure is not attributed to
  a proven production leak or waved away with additional sleeps/GC retries.
- Two new SVG regression cases fail as expected against the explicitly hashed
  prior v48 renderer (**0 passed/2 failed/0 skipped**,
  `renderer-v49-prior-retention-regression-a-20261002`): its ordinary bitmap
  publication still captures a provider document, and legacy non-tiled
  retirement times out. Both pass against the final current candidate.
  This differential establishes the changed retention contract, not browser
  fidelity, native-worker throughput or aggregate private-byte bounds.

The first two-field unload fence pushed lean managed code **205 bytes over**
the unchanged cap (`packages-anycpu-v49-c-document-retirement-20261002`).
Replacing it with the single atomic terminal slot preserves the safety contract
and removes 512 managed bytes. Final fresh five-package evidence
(`packages-anycpu-v49-d-document-retirement-20261002`) measures Core+WinUI at
**531,447/537,600 compressed bytes** and **1,257,984/1,258,291 managed bytes**,
with no lean native payload. Performance is **109,520/131,072 compressed bytes**.
CRC/SHA-verified lossless archive normalization does not change entry payloads;
the five packages pass AnyCPU IL inspection. The full size check fails for
missing Svg.Resvg instead of substituting a stale package. All fourteen normal
packs still stop at HTML's mandatory NU1900, and fresh normal app/provider
builds also fail before compilation at that vulnerability-feed error. No cap,
audit policy, required fixture or release gate was raised or suppressed.

The current PR #99 read-only snapshot remains at published head
`1c00c83ddfd2e8b65e05cb57e359a3f092b37271`, with 42 failed, 15 successful,
two skipped and two canceled checks. The local candidate is unpublished;
published CI results are not evidence for these edits. The reported Core
foreground-readiness timing failure has an existing local virtual-clock fix,
not a relaxed production readiness deadline. Top-500 UIA/app-readiness
failures still need exact case evidence and live qualification; they are not
reclassified as an image-readiness deadline issue without that evidence.

Known boundaries and next work:

- Reflow still mutates shared block/native geometry; published size/revision
  metadata is not a COW geometry root. UIA point/range/visible-range/scroll and
  semantic/native/link/code-copy peers still consume the live graph. Correct
  COW must preserve exact wrap/bidi/grapheme/block-local geometry, shared
  positions and drain-safe old native-layout leases for every consumer, rather
  than approximation or a metadata-only publication claim.
- The unload/dispose fence does not yet repair a lost accepted callback while
  the control remains loaded, dispatcher-rejection state recovery, or publish
  a rebuild-time recovery state machine. Those paths need their own safe
  dirty-index/cancellation/publication ordering before implementation.
- Provider/sanitizer/transient/active-source aggregate ownership, authorized
  byte-free reacquisition, healthy-worker eviction/restart, oversized WIC
  intermediates and the historical strict 4,992-versus-zero allocation failure
  origin remain open. Passing later isolated runs does not explain that failure.
- A qualifying environment still needs a normal-token Windows runner preserving
  Restricted Code, suspended startup and kill-on-close Job isolation, the
  mandatory vulnerability feed, and functioning Sandbox/Edge/AOT runners.
  `CreateRestrictedToken` error 87, Sandbox broker access denial, Edge startup
  failure, and independent WinRT/AOT build failures are not bypassed. Approved
  signing/Store child-process and physical architecture/D3D/WARP/mixed-DPI/
  theme/High Contrast/HDR tests remain required.

Full counterbalanced Release performance, same-byte Edge comparison and the
complete **500/500** README audit remain mandatory and unqualified. Automation
remains active; this plan and the 1.0 performance goal are not complete.

### October 2 v50: image event retention and per-README CI evidence

The next local pass continued immediately. Its exact-source checkpoint is
`MarkdownRenderer/artifacts/v50-image-completion-checkpoint-20261002.json`.

`OnImageLoadCompleted` previously queued a closure that strongly captured the
control and raw event sender. It now captures only weak control/image identities
and the scalar layout-invalidated flag. The UI callback pins the resolved
control only during execution, rejects unloaded/disposed controls, drops a dead
image instead of treating it as a custom sender, and preserves the subscribed
image check before UIA status delivery and reflow. A non-image sender still
requests rebuild when layout is invalidated; paint-only completion still
invalidates canvas and selection. No new timer, public API, resource authority,
theming behavior, image policy or dispatcher fallback is introduced. This is a
retention fix, not a loaded lost-callback/dispatcher-rejection liveness fix.

The final fast core suite passes **1,102/1,102**
(`core-v49-weak-handoff-e-20261002`), explicitly excluding the external-process
fixtures. Current compiled Core/WinUI/Performance and staged renderer tests pass
**1,013/1,013**, including all twelve API baselines
(`renderer-v49-document-retirement-i-20261002`). The new compiled-control test
requires exactly two weak captures and forbids the control, raw sender, image,
event args and snapshot fields in the actual compiler-generated image event
closure. All **21** image-lifetime/compiled-control cases pass each of twenty
fresh processes (`retention-v49-image-repeat-d-20261002`), without retries,
new sleeps, threshold changes or extra GC attempts. These are managed
correctness/assembly-shape checks, not live WinUI delivery or total memory
measurement. Three NU1900 warnings remain in the staged diagnostic build.
The initial `h` bootstrap fails before compilation because Windows PowerShell's
.NET Framework lacks `Path.GetRelativePath`; its log is preserved. The successful
fresh `i` run uses the repository's required PowerShell 7 runner, not an altered
product or weakened gate.

The earlier superseded unfiltered core `a` run finished at **1,596 passed,
1 failed, 0 skipped**: all **501** external-gate fixture cases passed, while its
previously recorded modeled-GC failure remained failed. Changed source hashes
prevent it from being final-candidate evidence. Previously queued `d` and `e`
full runs refused stale compiler inputs before starting. The current exact-source
unfiltered `core-v49-final-full-f-20261002` is now actually running from fast
compiler `e`, with detailed per-case logging. It is not a queued or filtered
pass, a current full-suite pass, or a live renderer-performance verdict.

Fresh five-package evidence (`packages-anycpu-v49-e-document-retirement-20261002`)
passes the unchanged lean subset caps: **531,503/537,600 compressed bytes** and
**1,257,984/1,258,291 managed bytes**, with no lean native payload. Performance
is **109,515/131,072 compressed bytes**. Lossless normalization retains verified
entry CRC/SHA payloads; four managed DLLs across the five packages pass AnyCPU
IL inspection. The full package-size gate still fails for missing Svg.Resvg;
normal fourteen-package packing still stops at HTML's mandatory NU1900. Fresh
normal app and SVG provider builds (`v50-a`) also fail at that security-feed
error before compilation. No stale package, audit suppression, signed execution
claim or AOT qualification substitutes for those failures.

The PR's published head and checks remain unchanged: `1c00c83ddfd2e8b65e05cb57e359a3f092b37271`,
42 failed/15 successful/two skipped/two canceled. Read-only connector retrieval
now provides a concrete failed case, not just a shard summary:

- Run **36501560943**, job **109220975283**, artifact **11010221418**
  (`readme-audit-evidence-1-25`), rank **5**,
  `EbookFoundation/free-programming-books`, failed the unchanged two-second
  first-viewport readiness deadline and reports `InfrastructureFailure=false`.
- The paint callback at `2026-09-29T02:11:24.1511087Z` reports full 660x499
  coverage (329,340/329,340 pixels), no pending/loading images, a post-arm
  painted region, generation 2 and layout revision 5. The immediately following
  poll, about 28 ms later, reports waiting for paint with the same recorded
  coverage/revision/generations; no further callback or readiness publication
  arrives before timeout.
- Published-head polling clears a rejected acknowledgement but does not rearm
  its one-shot callback. The existing local `ObservePoll` ->
  `RearmForCurrentViewport` -> acknowledgement-registration fix and targeted
  tests cover that lifecycle; they were not newly implemented in this pass.
  The historical trace lacks the predicate-level identity fields needed to
  explain which `IsCurrentAcknowledgement` component became stale. That trigger
  remains unknown, and this one case does not explain every failed shard.

The result projection, exact consecutive progress rows and provenance context
are preserved in `MarkdownRenderer/artifacts/ci-v50-rank5-readiness-20261002/`.
They are explicitly a projection/excerpt, not a complete copied artifact.
This evidence attributes one published-head wait failure to probe handoff,
not to image rendering cost, and does not justify extending the deadline.
The existing local rearm fix remains unpublished and without a qualifying
native live rerun. Full counterbalanced Release, same-byte Edge and **500/500**
audit, aggregate-memory/COW/WIC work, historical strict allocation attribution,
mandatory security feed, fixed worker isolation, Sandbox/AOT and physical
device/DPI/theme/HDR release gates remain open. Automation stays active.

### October 2 v52: deterministic SVG shutdown and completed unfiltered core suite

Exact-source/binary, raw TRX and current PR evidence is recorded in
`MarkdownRenderer/artifacts/v52-svg-shutdown-checkpoint-20261002.json`.
Earlier v49/v50 checkpoints and failing pre-fix results remain preserved.

The renderer previously stopped workers while a caller-retained document could
retain its private source snapshot. It now tracks live child lifetimes through
an internal weak-key registry: healthy renderer ownership does not root
abandoned documents or source graphs. Shutdown closes registration, cancels
every captured live lifetime outside registry locks and terminates workers;
late registration releases its snapshot and returns typed `Canceled`.
Borrowed renderer ownership at control boundaries is unchanged.

`DisposeAsync` shares one completion barrier across repeated, concurrent,
reentrant and sync-then-async calls. It waits for actual admitted child renders
and background opens to leave caller/private source memory and complete cleanup;
requesting cancellation alone is not completion. Synchronous `Dispose` initiates
the same cancellation/worker teardown without waiting for active work. One
cleanup failure does not skip other captured documents; deferred cancellation
and cleanup failures reach the shared barrier. Retained completed tasks no
longer retain the caller source or gated memory manager in the tested paths.
No new public API, reflection-based production discovery, polling timer,
weakened isolation, fallback or relaxed GC/retry/deadline gate was added.

A second deterministic regression exposed valid-content quarantine when shutdown
raced worker admission after successful preflight. `ObjectDisposedException`
was caught as a worker `InvalidOperationException`; canceled admission now maps
to typed `Canceled` before that generic worker-failure path. Genuine worker
failures and timeouts keep their existing quarantine policy.

Final verification is scoped as follows:

- The unfiltered core suite `core-v49-final-full-f-20261002` passes
  **1,603/1,603**, zero failed/skipped, including all **501** external-gate
  fixtures. It completed at 06:38:00Z after 1.362 hours, using the exact
  `core-v49-weak-handoff-e-20261002` compiler inputs with their hashes unchanged
  before and after the run. External-gate fixtures validate the validator;
  they are not a live renderer-speed or release benchmark.
- Final selected source-linked SVG tests
  `svg-v48-font-contracts-o-20261002` pass **400/400**, zero failed/skipped.
  This artifact-only `net10.0` compiler uses explicit hashed existing runtime
  references, not the shipping Windows TFM/AOT/analyzer/security graph. The
  required actual-worker test is excluded here and run separately below.
- The shutdown/source-lifetime cohort in
  `svg-v48-concurrency-repeat-d-20261002` passes all **28** cases in each of
  **20 fresh processes**, with no retries, sleeps or threshold changes. Tests
  cover weak graph collection, close/admission races, retained documents,
  actual preflight/render drain, callback reentry, concurrent sync/async
  shutdown, cleanup failures and cancellation without valid-source quarantine.
- `renderer-v52-svg-shutdown-a-20261002` passes **1,013/1,013**, including all
  **12** API baselines. It reuses the hash-verified unchanged v49 renderer/core/
  performance/test runtime and stages the fresh provider. The provider production
  DLL is byte-identical between n and final o; o strengthens a test only.
  This is diagnostic staged integration, not a fresh normal full solution build.
- `resvg-v48-without-diagnostics-f-20261002` compiles the exact final source
  with diagnostic metadata types absent. This conditional compile is not AOT
  qualification. Preserved pre-fix i records two retained-source failures
  (**381 passed / 2 failed**); m records the admission/quarantine regression
  (**399 passed / 1 failed**). These failures were not removed or waived.
- Required native integration `svg-v48-required-font-worker-f-20261002`
  still records **0 passed / 1 failed / 0 skipped** before startup:
  `CreateRestrictedToken` error **87**. A normal-token Windows runner preserving
  the RestrictedCode token, suspended startup and kill-on-close Job is needed.
  The normal Release x64 provider build (`resvg-normal-build-v52-a`) still
  fails before compilation at mandatory **NU1900**; the vulnerability feed
  must be reachable. Neither policy is weakened.

The preceding v50 five-package lean result remains unchanged evidence, not a
fresh v52 provider/all-fourteen size, license or signing pass. Current published
PR #99 remains at `1c00c83ddfd2e8b65e05cb57e359a3f092b37271`: 42 failed,
15 successful, two skipped and two canceled checks. Local fixes are unpublished.

Deterministic shutdown is not aggregate source admission, byte-free backing
reacquisition, eviction/restart recovery or immutable COW publication. Host OOM
during the initial close-snapshot allocation is also not qualified: workers are
still torn down and the shared barrier faults, but child cancellation/retirement
is not guaranteed in that exceptional path. Required native fault/memory gates,
oversized WIC intermediate bounds, the historical strict allocation failure's
origin, loaded lost-callback/dispatcher-rejection liveness, functional Sandbox/
Edge/AOT, signing/Store and the physical architecture/device/DPI/theme/HDR matrix
remain open. Full counterbalanced Release, same-byte Edge and **500/500** README
performance/fidelity qualification have not passed. Automation remains active.

### October 2 v53: bounded repository-image failure evidence and fresh restore

Checkpoint: `MarkdownRenderer/artifacts/v53-image-transport-checkpoint-20261002.json`.
This closes an audit-evidence gap, not the renderer performance goal or the missing
image itself. No image policy, fallback, retry count, readiness deadline, package
security gate or public MarkdownRenderer API is changed.

The extracted live-GitHub case for **rank 214, coder/code-server**, from run
`36501560943` at published head `1c00c83ddfd2e8b65e05cb57e359a3f092b37271`,
confirms both screenshots are complete in Edge (1519x988 intrinsic, approximately
623x405 displayed), while native resolution of `./assets/screenshot-1.png` and
`./assets/screenshot-2.png` returns handled/unavailable with no final asset.
The recorded zero byte length and null URI/hash describe that missing final asset,
not bytes delivered by transport; payload validation and decode failures are not
ruled out. Its pinned README blob is `470095071afd381ad0f41e56deadc24339fb62b5`;
browser image routes use commit `96e468eaa666f86bed3367bb1cf6e1348e15612b` under
`docs/assets/`. The old evidence contains neither document context nor fetch-stage
outcomes. `SameByteCorpus` and `SameByteReplay` are absent: this is **not** an
identical-byte oracle. The extraction lacks an artifact-download manifest, so an
opaque remote artifact ID is not asserted to identify this folder.

The current nested-README resolver and code-server regression already map a
`docs/README.md` relative source to `docs/assets/`. That does not prove the old app
received that context. A speculative path rewrite would neither diagnose a CDN/API
failure nor establish a fix. The new optional sidecar provides the next evidence:

- `GitHubRepositoryImageFetchPipeline` emits one privacy-safe raw **image-service
  result**, which may include cache hits, internal retries or stale-on-failure
  cache behavior. `RawImageServiceResult/Success` means a non-null service return,
  not an HTTP request, valid decoded pixels, or successful final image rendering.
  The real `GitHubImageService` cache test proves two service successes can mean
  only one HTTP request.
- The authenticated `RepositoryFetcher` wrapper observes actual fetcher delegate
  invocations, preserves the exact return/exception and source-lease ownership,
  and follows the existing three-invocation retry bound. It is not a count of
  Contents/Blob/LFS HTTP requests. Fixed outcomes distinguish HTTP status,
  invalid payload, caller cancellation, source-byte deferral, transport failure,
  null result and other failures without serializing exceptions or bodies.
- Both stages correlate using the existing source/document/ref hashes and a
  hash of the raw-resource path. Callback state retains no source bytes, image
  URI, document context, owner/ref/path text or exception; its separate audit
  destination is captured at creation. Admission accepts bounded HTTPS/default-
  port raw-host resources without userinfo. Oversized, malformed or relative
  BaseUri inputs can omit optional tracing; this is not an exhaustive HTTP log.
- Generation changes, transport-budget reset, final generation validation,
  reservation and append share `SignalGate`. Old callbacks cannot consume a new
  epoch's quota or switch to a newly configured destination. Tests include
  concurrent old callbacks, a fully available new failure quota, destination
  capture, observer failures and lease/cancellation preservation.
- Per launch, at most **500** hashed document identities are admitted. Each has
  **32** success samples and **512** non-success records, plus at most one limit
  notice for each quota. One launch-level document-limit notice makes aggregate
  admission truncation explicit. The maximum is **273,001** fixed-schema rows
  per launch; the top-500 harness actually launches one README per app process.
  Success sampling cannot consume failure capacity. IO/setup failures remain
  non-fatal to image resolution; an absent sidecar is not proof of no failures.

`ReadmeAuditProbe` enables this separate `image-transport.ndjson` only when
`JITHUB_README_AUDIT_IMAGE_TRANSPORT_DIAGNOSTICS=1`; otherwise it explicitly clears
the child destination. It preserves the sidecar alongside existing evidence, sets
`Native.ImageTransportDiagnosticsEnabled`, and rejects an instrumented case as
release-performance evidence because hashing and synchronous append add diagnostic
work. Use it only for focused diagnostic replay with a fresh qualifying app/runner;
ordinary qualification must keep it off. No live replay is claimed here.

Verification and limits:

- Final normal Release x64 headless run `app-v53-image-transport-d-20261002`
  passes **3,556/3,556**, zero skips, with **586** evaluated compile/import/assets
  inputs unchanged before/after. Earlier failing runs `a` and `b` remain preserved;
  the four initial test-only reflection/internal-mock issues and one remaining
  stale mock call were corrected without widening production visibility or
  enabling reflection serialization.
- `app-v53-transport-repeat-b-20261002` passes **51/51** focused contracts in each
  of **20** fresh processes using an archived exact-source runtime. The preceding
  `c`/repeat-`a` evidence remains historical, before the launch-cap notice addition.
- `normal-builds-v53-image-transport-b-20261002` builds the Release automation
  harness with **zero warnings/errors**. The normal app build stops before C#/XAML
  compilation at mandatory **NU1900**. A fresh `--locked-mode` Release x64 app
  restore (`app-v53-locked-restore-a-20261002`) independently fails the current
  vulnerability-feed request, with source dependency-lock hashes unchanged.
  Security auditing is not suppressed. `app-v53-default-compile-a-20261002`
  compiles the headless project with layout diagnostics disabled and reflection
  JSON disabled, zero warnings/errors; it is not a shipping app/AOT compile.
- Independent WinUI code review prompted the cache-label, epoch, reserved failure
  capacity and truncation fixes. No fallback, cancellation or lease regression
  was found. Public API/package/worker evidence remains the preceding v52/v50
  checkpoint, not newly qualified by these app-level tests. Published PR #99 is
  unchanged: **42 failed, 15 successful, two skipped, two canceled** checks; local
  fixes remain unpublished and cannot be credited to those CI runs.

Read-only aggregate-source review confirms actual provider snapshots are still
unadmitted byte arrays, not just numeric ledger entries. A blocking provider cap
can deadlock against resolver source leases; acquiring it after the 16-open gate
also violates required ordering. No unused admission seam or eviction is shipped
as a substitute for authorized, identity-bound, byte-free reacquisition. This work
and shared geometry COW remain open, alongside loaded callback recovery, oversized
WIC bounds, the historical allocation failure, functional restricted worker/Sandbox/
Edge/AOT, signing/Store and the physical architecture/device/DPI/theme/HDR matrix.
Full counterbalanced Release, same-byte Edge and **500/500** README performance/
fidelity gates have not passed. Automation remains active.

### October 2 v54: loaded image-reflow recovery and explicit unfinished status

Checkpoint: `MarkdownRenderer/artifacts/v54-image-reflow-checkpoint-20261002.json`.
This is local correctness work, not completion of the progressive-performance
plan. Scheduled wakeups are not required for local implementation or a reason
to stop after one small change. The existing fifteen-minute heartbeat remains
active as a resume mechanism; this run continued locally through implementation,
independent review, fresh compilation, regression tests and package measurements.

- Ordinary `ImageBox` completions already arrive on UI. They now update inline
  without another dispatcher allocation/handoff. Off-thread completions retain
  only weak receiver/image identities; a rejected enqueue records the existing
  cross-thread rebuild request. Accepted-but-withheld custom off-thread events
  are not independently recovered by this image-reflow timer.
- Pending dirty blocks have a dispatch ticket and a weak, operation-scoped
  100 ms recovery timer. It exists only while image reflow is queued or active,
  stops on completion/unload, and rejects stale timer identities. Failed timer
  setup prevents starting an asynchronous image handoff without its backstop;
  ordinary rebuild handling remains the fallback. No idle background poll is
  added. The sessionless path still uses existing synchronous reflow and is
  **not** newly performance-qualified.
- The actual worker borrow drains before either normal delivery or timer
  recovery can consume the same one-shot weak publication. Closed operation
  owners reject late registration. Worker/factory methods no longer capture the
  control implicitly, and CTS disposal waits for actual task/callback drain.
  Setup/publication failure cleanup and pending rearm are covered by review;
  mutable layout geometry/COW remains a separate open limitation.
- Review caught the requested-versus-published generation regression: a failed
  replacement could leave the old document visible while silently discarding
  its image work. Dirty indices now stay with the published snapshot and are
  cleared on successful replacement. Dispatch waits for pending/building
  replacement work, then resumes against the published generation. Canceled
  reflow re-adds current subscribed-image blocks without retaining its old
  dirty array in a delayed UI callback. Recovery uses the actual prepublished
  pipeline task to clear a withheld completed-build marker and retries the
  ordinary pending drain after an enqueue rejection. A failed replacement's
  canceled CTS cannot poison this operation's independent cancellation scope.
- All fourteen `ImageBox` completion sites use two cached immutable Boolean
  event payloads. Tests cover reuse, distinct values, readonly state and
  concurrent requests. Public API, SVG isolation, source policy and safety/
  performance/package limits are unchanged.

Verification is diagnostic-only. The final run's exact counts, source inventories,
archive hashes and package totals are in the checkpoint. Earlier runs `a` through
`d` remain historical; earlier successful tests do not validate later edits.
Final `image-reflow-v54-e-20261002` passes **1,114/1,114** fast core and
**1,018/1,018** staged renderer tests, with no failures/skips, including all
twelve API baseline cases and four compiled-capture cases. All **478** captured
source hashes and **486** archived output size/hash entries pass the independent
current-source/output audit. Fresh `packages-reflow-v54-e-20261002` measures
**532,916/537,600** compressed bytes, but **1,260,544/1,258,291** managed DLL
bytes: **2,253 bytes over**, so the lean subset **fails**. Both manifests retain
their exact SHA-256 identities in the checkpoint; the caps remain unchanged.
The source inventory is not the complete evaluated shipping compiler/dependency
graph. Staged renderer tests reuse unchanged feature assemblies, including the
source-linked diagnostic SVG provider; no new production Windows-TFM/AOT/security
qualification follows from that reuse. The fast core partition excludes the 501
external-gate fixtures; the earlier v52 unfiltered result remains historical.
Payload/capture/GC and coordinator tests do not exercise an actual WinUI timer.

The first package-run summary (`packages-reflow-v54-a-20261002`) incorrectly
aggregated managed bytes as zero from dictionary entries. It is **invalid** as a
size pass. The measurement now uses typed entries, fails an empty managed sum,
and compares actual ZIP entry lengths against the unchanged cap. Subsequent
valid measurements fail the managed limit; compressed size alone is not a pass.
Only Core+WinUI are measured here, not all fourteen packages.

The fresh read-only `winapp target snapshot sandbox --json` readiness check
fails with broker access denied (`sandbox_start_failed`, `0x80131500`). No guest
is launched, no host-desktop fallback is used and no Windows feature or security
policy is changed. The WinApp API search also fails at its protected cache lock;
timer APIs used here already exist in restored project code and compile normally.
The previously confirmed mandatory NU1900, restricted-worker startup error 87,
Edge startup, AOT and independent-generation problems are not claimed fixed by
these managed runs. PR #99 remains on its published head; local work is unpublished.

Next work still includes aggregate source/transient/scene admission and authorized
byte-free reacquisition, immutable COW publication, WIC intermediate bounds,
allocation-origin investigation, lean managed size, and operational runner fixes.
**No qualified counterbalanced Release benchmark, same-byte Edge parity result
or complete 500/500 README audit exists for this candidate.** Signing/Store,
architecture/device/DPI/theme/HDR and accessibility/fidelity qualification remain
mandatory. The plan and heartbeat stay open.

### October 2 v55: lean size restored and normalization ownership tightened

Checkpoint: `MarkdownRenderer/artifacts/v55-lean-and-svg-ownership-checkpoint-20261002.json`.
The unchanged Core+WinUI package limits now pass; all broader release gates remain
open. This run continued locally through implementation, independent review,
multiple fresh builds/tests and actual archive-entry verification.

- Three pure logging wrappers now retain the underlying Debug-only contract.
  Release omits their calls/arguments; cleanup, retirement counters, exception
  handling and work-drain barriers remain active. Conditional attributes do not
  remove the private methods themselves from an untrimmed DLL. Tests cover the
  control, environment monitor and separate Performance assembly.
- Identical raster-cache publication, pending-raster cleanup and SVG publication
  metadata/generation fencing are centralized. Distinct stale document/lease
  cleanup, tile/bitmap state, cache removal and completion events stay in their
  original paths. Full UI-publication measurement scopes remain around each
  callback. The private SVG callback pair is a value tuple rather than a record
  object: no extra carrier allocation or generated equality/printing model.
  Neither consumers nor dispatcher handoffs box that tuple. No feature, public
  API, image policy or resource/safety/performance limit was removed or lowered.
- UTF-8 normalization transfers bounded segments only after writer, reader and
  input cleanup. Its consumers clear their old input fields before flattening;
  untransferred segments are disposed on failure, and cancellation is checked
  before the final allocation. Tests retain the completed producer task/output
  while proving input collection **before** flattening, and cover malformed,
  pre-canceled and post-production-canceled paths. The caller's synchronous
  dispatch-frame alias remains subject to JIT liveness; no whole-pipeline peak,
  aggregate admission or RSS bound is claimed. The original private snapshot
  and public immutable-open/replay contracts remain intact.

Final `image-reflow-v55-e-20261002` passes **1,114/1,114** fast core and
**1,023/1,023** staged renderer cases. `svg-v55-worker-contracts-b-20261002`
passes **404/404** selected managed SVG cases, including the four new ownership
cases. The separate `renderer-v55-svg-shutdown-a-20261002` integration runtime
uses that **identical fresh SVG DLL** and passes **1,023/1,023**, including all
twelve public-API baselines. There are no failures/skips in these partitions.
The four normalization cases also pass in each of **twenty** fresh processes,
without retries or threshold changes. Diagnostics-disabled source-linked SVG
compilation passes and excludes ledger metadata. The checkpoint verifies **479**
renderer source hashes, **486** archived output size/hash entries and **64** SVG
source hashes, as well as exact renderer references and actual NuGet entries.
The independent read-only audit in
`MarkdownRenderer/artifacts/v55-independent-evidence-audit-20261002.json`
confirms those inventories, all 19 entries in the two archives, 75 SVG and 71
combined-runtime assembly records, and all twenty repetition outputs, with zero
byte/hash mismatches. This is verification of captured artifacts, not an
independent compiler rebuild or release benchmark.
These inventories are not the complete evaluated shipping compiler/dependency
graph. Source-linked SVG uses `net10.0`, not the production Windows-TFM/AOT graph;
the fast core partition excludes the 501 external-validator fixtures.

Fresh `packages-reflow-v55-e-20261002` measures **531,837/537,600** compressed
bytes and **1,257,984/1,258,291** managed DLL bytes: **5,763** and **307** bytes
of headroom. Both actual archives and every entry are SHA-256 checked. This is
only the Core+WinUI pair, not a fourteen-package, security, signing or AOT pass.
Earlier package runs remain preserved: `a` through `d` fail their managed cap;
earlier successful tests do not qualify later edits. Renderer run `a` failed
before build under Windows PowerShell 5.1; `c` has a test-only assembly lookup
failure, corrected in later fresh runs without exposing production internals.

The normal Release automation harness builds with zero warnings/errors. Fresh
normal app and provider builds still stop at mandatory **NU1900**; security
auditing is not suppressed. The required isolated font-worker case still fails
before worker startup at `CreateRestrictedToken` **Win32 error 87**, with no
weaker token or in-process fallback. Sandbox/Edge/AOT/device/signing execution
remains unqualified; no host-desktop UI fallback was used. A runner with working
NuGet security-feed access and the intended isolated-worker/Sandbox/Edge launch
paths is still required. PR #99 remains at
`1c00c83ddfd2e8b65e05cb57e359a3f092b37271`: 42 failed, 15 successful, two skipped
and two canceled historical/repeated checks, not 42 distinct renderer defects.
Local changes are unpublished; those CI results do not qualify this candidate.

Independent layout review confirms COW remains substantial unfinished work:
image relayout mutates published absolute bounds, native inline layouts and table
cell geometry; UIA peers also reference those mutable boxes. Complete COW needs
a generation-owned geometry/resource root with pinned paint/interaction/UIA
readers, stable image-resource ownership separate from geometry, and atomic
publication/retirement. Shared suffixes require local coordinates plus a
generation-owned placement table, or complete owner-subtree cloning. Tables,
inline runs, safe-HTML flow groups and aliases must all be covered. A metadata
overlay or shallow box clone is not sufficient. Routing every image completion
through a full rebuild is not accepted as a performance-goal substitute.

Aggregate source/transient/scene admission and authorized byte-free reacquisition,
WIC intermediate bounds, allocation-origin investigation, functional isolated
runner, all-fourteen-package compliance, independent generation and the complete
architecture/device/DPI/theme/HDR/accessibility/fidelity matrix remain open.
**No qualified full counterbalanced Release benchmark, same-byte Edge corpus
or complete 500/500 README audit exists for this candidate.** The plan and
heartbeat remain active; scheduling is a resume mechanism, not a work-duration
limit or evidence of completion.

### October 2 v56: bounded raster-reference memo and safe highlighting reads

Checkpoint: `MarkdownRenderer/artifacts/v56-highlight-and-raster-memo-checkpoint-20261002.json`.
This run continued locally through multiple implementation, review, diagnostic,
compiler, test and actual package-entry checks; it did not wait for a scheduled
message between milestones. No performance, fidelity, safety, package-size,
license or release gate was lowered.

- Host SVG inspection memoizes only successfully admitted raster references
  whose complete XML-decoded URI is at least 1,024 characters. The key is a
  SHA-256 digest of the **complete UTF-16 URI**, including the MIME/encoding
  header and payload; at most **256** digest-only entries are retained for that
  single inspection. No payload strings or decoded arrays are retained by the
  memo. Small references and nested SVGs use the original validation path.
  Actual raster sniffing, lowered per-open budgets, deduplicated accounting,
  forbidden-reference checks and cancellation remain mandatory before insertion.
  Nested SVG metadata/depth checks cannot be bypassed by a memo hit. Tests cover
  image/filter/CSS/nested references, MIME/encoding aliases, changed late payload,
  separate lowered budgets, nested-depth rejection, allocation and the digest cap.
- Highlighting consumes mutable measured boxes only while holding the snapshot's
  nonblocking interaction lease. The internally locking helper that returned live
  mutable boxes after releasing its lock is removed. The scheduler copies code
  strings/cache keys, then releases the lease **before** invoking user highlighter
  code. One weak snapshot identity is shared per pending batch. The async task,
  Task.Run closure and UI publication no longer strongly capture a retired
  LayoutSnapshot or CodeBlockBox. Uncached-result application reacquires the lease;
  transiently busy layouts remain retryable after the in-flight key is removed.
  Language/theme/provider metadata comes from the same immutable cache key rather
  than duplicate async fields. Provider reference/revision/generation fences stay
  intact. The generated-capture test follows parent compiler display classes and
  rejects strong box/snapshot fields, including arrays/generic containers.
  The two lease tests cover unprotected access rejection and retirement waiting
  for a reader. These are prerequisites, **not immutable geometry/COW publication**.

Final `image-reflow-v56-c-20261002` passes **1,114/1,114** fast core and
**1,026/1,026** renderer cases. `svg-v56-worker-contracts-c-20261002` passes
**414/414** selected managed SVG cases, and
`renderer-v56-svg-shutdown-a-20261002` uses that exact fresh provider DLL and
passes **1,026/1,026**, including all twelve public-API baselines. Each partition
has zero failures/skips. The ten memo cases pass in each of twenty fresh
processes, without retries or threshold changes; that repetition uses the same
SVG source compiled against the earlier immutable renderer reference, not the
final integration provider PE. Diagnostics-disabled source-linked compilation
passes with current renderer references and excludes ledger metadata. Source/
output hashes, exact renderer references and actual ZIP entry bytes/SHA-256 are
checked by the checkpoint script. These are managed diagnostics, not the shipping
Windows-TFM/AOT/dependency-security compiler graph or live dispatcher lifetime QA.

The final-C generated-capture case, two layout-lease cases and unchanged stable
residency zero-allocation case also pass **20** fresh processes (**80/80** test
executions). Each completed process verifies the exact four case identities,
counters and unchanged runtime/source-provenance hashes. Earlier malformed
logger/filter attempts remain separate; the incomplete two-case sequence is not
counted as four-case/20-process evidence. This repetition supplements current
correctness only; it does not explain the preserved allocation failure.

The preserved `image-reflow-v56-b-20261002` run fails two of 1,026 cases: the new
capture test initially expected a weak identity on the immediate display class,
although Roslyn placed it on its parent; the corrected test follows the full
generated closure graph. The existing stable-residency **zero-byte** assertion
also measured **4,384** bytes. It passes alone and in later full runs, but no
allocation-origin trace explains that failure. Ordinary allocations on other
parallel test threads do not explain a synchronous thread-local measurement.
The cause and historical allocation failures remain **open**, with the zero
threshold unchanged. Earlier package run `a` measures **1,258,496** managed bytes
and fails the cap by 205 bytes; eliminating duplicate immutable highlight task
metadata restores the final `b` pair to **531,941/537,600** compressed bytes and
**1,257,984/1,258,291** managed bytes. This qualifies only the two-package subset,
not all fourteen packages, signing, Store or AOT.

`svg-v56-raster-memo-comparison-c-20261002` is a descriptive host-preflight
comparison, **not a release benchmark**: eight offline fixtures, six alternating
fresh-process pairs per fixture, forty warmups, and three batches of 32 measured
inspections per process (**96** raw process records). Loaded provider hashes,
identical source hashes and exact resource accounting are verified. Repeat-heavy
fixtures show roughly **14–63%** lower median inspection latency and **9–20%**
fewer managed allocation bytes; unique/small fixtures are roughly **5–7%** slower,
and nested SVG is essentially unchanged. Allocation is total managed allocation,
not retained RSS/peak memory; process CPU is quantized at 15.625 ms. The fresh
paired summaries report descriptive ranges/quartiles, not confidence intervals.
Source cloning before inspection, worker parsing, rasterization, upload, Win2D,
full Markdown composition and Edge are **not** measured. The candidate provider
uses the preceding renderer reference; the final integration provider has a
different PE hash/MVID and is not retrospectively claimed measured.

Comparison `a` measures an earlier prototype. Comparison `b` is **invalid as an
old-versus-final comparison** because its supposed baseline loaded that prototype,
not the pinned v55 DLL; the staging cause is not proven without the original build
log. Comparison `c` checks the actual baseline/final-memo hashes in all 96 records.
An independent fresh harness rebuild records compiler command/log/binlog and
zero warnings/errors, but its PE is **not byte-identical** to the original C host.
Both portable PDBs bind to the current Program.cs hash, and all 45 method bodies/
signatures and assembly-reference lists match. This supplements source binding;
it does **not** establish the original compiler invocation, byte reproducibility,
an independently generated release result or browser parity. The original
comparison remains unchanged. See `v56-raster-memo-harness-proof-20261002`.

The supplemental `v56-provider-runtime-identity-audit-20261002.json` verifies
**1,095** identical provider method signature/IL records and identical CLR
assembly-reference identities across the benchmark C31 and final-main-C 98 DLLs.
All **65** compiler-input records match current bytes/hashes in both manifests.
The compile-time main-renderer binary hash changed; provider PE hashes/MVIDs
remain different. The benchmark candidate directory's provider PDB is **not**
paired with C31's PE CodeView GUID and cannot establish its source binding.
The separately archived B PDB pairs with the identical C31 DLL, and the final C
PDB also pairs; both bind SvgPreflight.cs to the current checksum. This is
metadata/source binding, not fresh timing or behavior evidence for provider 98.

Fresh normal Release automation compilation succeeds. App/provider builds still
stop at mandatory **NU1900**; auditing remains enabled. The required current-SVG
font-worker test fails before startup with restricted-token **error 87**, without
a weaker-token or in-process fallback. The read-only
`winapp target snapshot sandbox --json` also fails: Sandbox's broker reports gRPC
access denied at localhost:80 (`wsb list`, 0x80131500). No guest launch/reconnect,
host-desktop UI fallback, permission/security or Windows-feature change is made.
A runner with working dependency-security feed and authorized Sandbox/isolated
worker/Edge execution is required for live qualification. PR #99 remains at
`1c00c83ddfd2e8b65e05cb57e359a3f092b37271`, with historical/repeated 42 failure,
15 success, two skipped and two canceled checks; local edits are unpublished.

Independent interop inspection finds the existing argument order, flags,
SID_AND_ATTRIBUTES layout/count and pinned SID pointer consistent with
[CreateRestrictedToken's contract](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-createrestrictedtoken).
A historical read-only V44 probe reports an already-restricted parent token
with five restricting SIDs and no requested restricted-code SID; the fixed call
fails with 87. Windows documents intersection with an already-restricted token's
list, making an empty intersection the leading explanation, **not proven errno
attribution or a fresh token snapshot**. Passing the parent's own list succeeds
in that diagnostic but is not policy-equivalent and is not a permitted fallback.
The required fixed-policy current worker case still fails.

The initial accepted-but-withheld **off-thread image-completion** handoff remains
unrecovered: its later image-reflow timer cannot start until the missing initial
callback runs. A correct next slice must record a weak, generation-fenced one-shot
pending completion before dispatch, preserve/coalesce geometry-invalidating
events, provide an independent loaded wake-up, and cancel/drop stale work without
an always-running idle timer or callbacks under locks. Agility alone is not proof
of safe timer start/stop synchronization. Aggregate provider/sanitizer/transient
and live-graph admission also remains unfinished: the source cache and private
provider copies are independent owners; ledger/count gating is not a shared byte
cap. Complete COW, WIC intermediate containment, allocation attribution, the
physical architecture/device/DPI/theme/HDR/accessibility matrix and independent
shipping package/security/AOT gates remain mandatory.
**No qualified full counterbalanced Release benchmark, same-byte Edge corpus
or complete 500/500 README audit exists for this candidate.** The plan/heartbeat
stay active; the unfiltered managed core follow-up is not counted until its
actual completed results and compiler-input scope are verified.

### October 2, 2026 — v57 geometry-reader and publication follow-up (verification pending)

The reader audit identified actual unsafe callsites against background image/lazy
layout mutation, not merely possible future COW improvements. The current changes
put UIA range, point-hit-test and child bounds queries under the nonblocking
snapshot read lease, materialize/detach values before release, and perform native
screen transforms/scrolling afterward. UIA generation/semantic-document fences
remain authoritative; busy geometry yields the existing empty/collapsed fallback
and later layout invalidation permits retry. Visible-range queries reduce bounds
directly to copied start/end metadata instead of allocating an all-span array.

Native-control/image-index plan collection now acquires one nonblocking lease
before clearing any old plans. It reads measured boxes, copied rectangles, focus
items, size, revision and scroll target in the same protected transaction, then
releases before residency/custom/native child callbacks. Busy publication keeps
the old plans and coalesces only generation/anchor/scroll-offset values through
the existing weak recovery timer. A user scroll cancels stale anchor restoration;
reentrant commits defer instead of recursively rebuilding the same lists. Pending
publication counts as pending readiness. Canceling replacement work does not erase
an already committed geometry update; actual replacement/unload/reset clears it.
Timer failure uses a publication ticket so an older failing native callback does
not erase a newer reentrantly queued commit. Sessionless anchor capture now uses
the existing nonblocking snapshot seam, not unleased mutable Bounds traversal.

Highlighter scheduling now captures the cancellation token before provider code,
fences provider identity/generation immediately after a reentrant Revision getter,
revalidates between batch items, and registers an operation lifetime before a
sessionless provider can synchronously reenter replacement/unload. The reservation
drains after actual success/fault/cancellation and also closes on registration or
synchronous invocation failure. Progressive Task.Run invocation and weak snapshot
captures remain intact. Provider-owned highlighting lists are now copied into
renderer-owned read-only storage before dispatch/cache/lease access. Sessionless
provider entry stays synchronous, but copying a synchronously completed result
uses a worker when completion is on the UI thread; the progressive worker copies
directly. Copy cancellation and collection failures follow the existing error
path. This stabilizes retained cache accounting, not total transient admission:
provider output is not capped, and concurrent mutation during its synchronous
copy cannot be made atomic through IReadOnlyList alone.

The same review found a separate lazy completion accepted-but-withheld/rejected
dispatch gap before the new busy-plan retry boundary. One-shot weak completion
recovery now fences actual worker drain, generation and cancellation and shares
the existing UI recovery timer for both request admission and publication. A
timer-create failure cancels the pending epoch and requests a rebuild. Delayed
callbacks are created in separate static factories so they cannot merge with the
worker's strong layout closure. Pure text-position converters also
needlessly call EnsureBuffer despite using only immutable InlineRun.Text lengths;
the in-assembly metadata audit supports removing those two calls, keeping all
actual buffer consumers unchanged. Neither reader admission nor metadata-only
projection completes immutable geometry/COW. Aggregate source/resource admission,
WIC intermediate containment, allocation attribution, normal shipping security/AOT
and full package qualification remain open. **No v57 Release/Edge/500-README pass
is claimed.**

The first frozen v57 diagnostic compile, `image-reflow-v57-a-20261002`, passed
**1,114/1,114** fast core cases and compiled the renderer/Performance assembly.
The staged renderer run passed **1,053/1,056**, with **three failed new tests**;
all **twelve public API baselines** passed. The failures are preserved rather than
retried in place: a custom-list test expected one access although index zero
succeeded before index one threw; its source-order test searched for the UI-thread
guard after the copy even though the guard precedes it; and the placement test
mistook a dictionary-population loop for the final detached-plan cleanup. The
test corrections preserve the intended checks. This first build is not the final
candidate: subsequent review found mutable image/block geometry still read by
visible realization and native property callbacks able to unload/dispose midway
through placement or attachment. The follow-up uses copied plan geometry, element
ownership checks between native property writes, generation-fenced indexed plan
adoption, and detached cleanup outside the layout lease. Visible realization uses
committed image-index bounds and copied embed rectangles, fences native callbacks
against snapshot/index replacement, and caps reentrant processing at two inline
passes before a weak low-priority continuation. Teardown owns detached plan arrays
and invalidates queued continuation tickets. Source-wiring tests
do not execute a real XAML property callback or qualify live UIA behavior.

The separate `image-reflow-v57-b-20261002` attempt is also preserved: **1,114/1,115**
fast core and **1,057/1,059** staged renderer cases passed, with all twelve API
baselines passing. The new bounds test incorrectly treated a sorted index slot
as a persistent original ordinal, and the teardown source check expected a
declaration/assignment rather than the guarded in-try assignment. The existing
SVG stage-trace listener counted unrelated concurrent work IDs. Corrections
rewrite every bounds slot before Commit, check the actual teardown ordering, and
filter exact stage assertions by the test's unique work ID using a thread-safe
recorder. No event count, metric assertion, or performance limit is relaxed.
B's source inventory also changed during compilation (the index test); its
`sourceHashesStillMatchAfterCompilation` is false. It is not exact-source
qualification. **v57C** passed **1,115/1,115** fast core cases and compiled the
renderer, but its renderer-test compile failed the mandatory xUnit2031 analyzer:
the correlated event check used `Assert.Single(events.Where(...))` rather than
the equivalent predicate overload. That failure is preserved; the predicate
overload retains the exact matching-event count assertion.

The frozen **v57D** follow-up passes **1,115/1,115** fast core and
**1,059/1,059** staged renderer/API cases, with all twelve public API baselines.
All **490** captured inputs remain unchanged across compilation. The independent
geometry/lifetime repetition passes **35 cases in each of 20 fresh processes**
(**700** results), recording actual VSTest-launcher PID/start-time pairs, exact
case identities, per-run TRX/log hashes and unchanged source/runtime manifests.
These source-wiring and managed lease tests are not live XAML/UIA, device or
browser-speed qualification.

The selected source-linked SVG **v57A** run is preserved at **412/414**, with two
existing policy-listener tests observing unrelated concurrent pool events.
The **v57B** test-only correction filters each listener by its unique synthetic
numeric primary-worker identity before queueing. Both tests retain the exact
phase/decision/metric/privacy assertions and check that no own-event leftovers
remain; parallelism is not disabled. Fresh selected SVG tests pass **414/414**
with zero compile warnings/errors. The separate current-provider integration
also passes **1,059/1,059** renderer cases and all twelve API baselines. Strict
fresh-process policy/concurrency repetitions are in progress, not yet claimed.

Fresh default diagnostics-disabled **Core+WinUI** packaging fails the unchanged
managed cap: **1,271,296/1,258,291** bytes (**13,005 over**), although the compressed
pair passes at **537,300/537,600** bytes. All package entry bytes are preserved by
the existing lossless archive normalization. The regression remains open; no
threshold, API baseline, lifetime fence or package payload is removed to manufacture
a pass. An IL-size comparison is diagnostic attribution only, not semantic
equivalence, independent reproducibility or full fourteen-package qualification.

A separate immutable **v57C unfiltered core replay** completed **1,616/1,616**
results in **90 minutes 15.329 seconds**, including all **501** external-evidence
validator fixtures. Its 92 archived runtime files and reconstructed 211 C# inputs
plus project file matched the C compiler provenance at receipt capture. At that
time the broader historical source inventory had 13 later changes outside those
core inputs (477/490 matched). Subsequent H edits also changed core inputs; this
is historical C evidence, not qualification of the current H sources.
The Csc invocation was not archived, so this is a reconstructed project definition,
not an independently captured consumed-compiler-input list. The watcher observed
exit but failed to capture its numeric code; passing stdout/TRX is not substituted
for that missing process proof. The strict checkpoint validator still requires an
actual numeric zero. The immutable receipt is
`image-reflow-v57-c-core-unfiltered-replay-20261002/completed-provenance.json`,
SHA-256 `91332e19dc4d60f7d783c5d327f27743d202d9d0df47d2d031a72ca251c0e365`.

The image completion review also distinguishes an unreachable defensive second
hop from current production behavior: sealed ImageBox guarantees LoadCompleted
on its UI dispatcher, and every current producer uses that publication path.
v54 recovery starts after that event and cannot rescue a hypothetical off-thread
completion whose accepted callback is never delivered. No future-producer
guarantee is claimed. The upstream ImageBox accepted-but-lost publication remains
a separate fail-closed retirement/live-dispatcher qualification concern; this
review does not turn it into a fixed or browser-qualified case.

The archived immutable **v56B unfiltered core replay completed 1,615/1,615**,
including all **501** synthetic external-evidence validator fixtures, with zero
failures/skips. Its TRX, assembly/PDB SHA-256 and the **212-entry** source-definition
audit (211 authored C# inputs plus the project file) are recorded under
`image-reflow-v56-b-core-unfiltered-replay-20261002`. This is prior-source evidence,
separate from v57 verification; those validator fixtures are not 500 rendered
READMEs. PR #99 was rechecked during this run and still publishes
`1c00c83ddfd2e8b65e05cb57e359a3f092b37271` with failed 500-result consolidation;
the local changes are not published-head CI qualification.

### October 2 v58: static UIA callbacks, exact cache-key semantics, and fresh repetitions

All nine leased UIA geometry call sites now pass explicit value state to static
callbacks. This removes per-query capture objects while retaining the same
nonblocking snapshot lease, four document/generation fences, exception propagation,
post-release identity check, and detached geometry before screen transforms or
scroll callbacks. Source-wiring tests are not live UIA or physical-DPI qualification.

Two internal cache keys now use ordinary immutable structs rather than unused
record-generated formatting, deconstruction and cloning machinery. Four tests
compare every equality component against test-only original-record contracts,
including ordinal nullable strings, provider revisions, policy partitions and
value-equal document-source records. Public records and API baselines are unchanged.
The preserved **v57E** renderer-test compile failed CS0191 because one existing
test used a `with` initializer on an internal cache key; its equivalent explicit
constructor preserves the revision-change assertion.

Fresh **v58A** passes **1,115/1,115** fast core and **1,063/1,064** renderer tests.
Its sole failure is the new source-wiring assertion expecting `static (_, state)`
at the hit-test site, which correctly uses `static (snapshot, state)`. The exact
static-callback assertion is corrected; A is preserved, not overwritten. Frozen
**v58B** passes **1,115/1,115** fast core and **1,064/1,064** renderer tests,
including all twelve API baseline result identities. All captured compilation
inputs remain stable. Selected source-linked **v58A SVG** passes **414/414** with
zero compile warnings/errors; current-provider integration passes **1,064/1,064**.

The current geometry/lifetime repetition passes **40 cases in each of 20 fresh
processes (800 results)**. The first v58 repetition invocation supplied a scalar
class-list argument instead of a PowerShell string array and failed prerequisite
validation before launching a repeated process; its stage is preserved separately.
The fresh managed SVG policy/concurrency repetition intersects the unchanged
captured managed-eligibility filter with seven requested classes, verifies exactly
**123** baseline identities via nonexecuting discovery, and passes **20 fresh
processes (2,460 results)**. Per-run identities, counters, runtime/source hashes
and process start identities are retained. No native fixture or performance
limit is waived by these diagnostic partitions.

The preserved broader **v57 SVG repetition A** selected three required real
worker-start probes outside that managed partition. All three fail before startup
at restricted-token creation, Win32 error **87**; the remaining 123 managed cases
pass. A corrected v57B invocation did not execute because a build-wrapper inventory
hash changed. The fresh v58 source-linked compilation recaptures that script;
the three actual native failures remain open, not superseded by managed results.

Default diagnostics-disabled **v57 package B** saved **3,072 managed bytes** after
the two internal key changes, measuring **1,268,224/1,258,291** managed and
**536,491/537,600** compressed bytes. Static UIA callbacks reduce allocation but
increase generic metadata: **v58 package A** measures **1,269,760/1,258,291** managed
and **537,024/537,600** compressed bytes. The managed cap is still **11,469 bytes
over**. Conditional diagnostics-only records are absent from this default build
and cannot repair that shipping-size deficit. No public API, lifetime fence,
interop root, feature payload or threshold is removed to manufacture compliance.

The immutable v58B checkpoint is under
`MarkdownRenderer/artifacts/image-reflow-v58-checkpoint-b-20261002`; all live
Release, browser-relative, same-byte Edge, 500/500 README, signing/AOT and physical
device/theme qualifications remain false. The full unfiltered v57C pure-core
replay is complete with the process-exit capture limitation recorded above.
Its 501 external-evidence fixtures are not rendered READMEs.

A fresh feed connectivity check fails through the configured local proxy at
`127.0.0.1`; the mandatory NU1900 security gate remains failed. Live isolated
worker qualification also still requires a Windows process context capable of
creating the specified restricted token, and the Sandbox broker must be available
without its localhost authorization failure. These runner prerequisites do not
close the remaining aggregate source/live-graph ownership, WIC intermediate-memory,
copy-on-write publication, package-size, or browser-performance implementation gaps.

The separate artifacts-only allocation attribution harness links the unchanged
image-residency policy and reproduces the stable 40-target, 100-warmup, 1,000-call
measurement shape under .NET **10.0.12**. It reports **0 bytes** for the exact
source and **0 bytes** in every phase of a separately disclosed instrumented copy.
This does not reproduce, explain or negate the historical **4,384/4,992-byte**
xUnit failures, and the original zero-byte assertion remains unchanged. No
process-scoped allocation tracing tool is installed/cached; sampled allocation
ticks would not reliably attribute a few kilobytes. No system-wide recording is
started or allocation failure dismissed as noise.

The completed SVG CPU-admission slice borrows the existing opt-in session's fair,
non-exclusive preparation slot for synchronous provider sanitization plus host
preflight/hash work. It links image/session cancellation for the whole operation and
releases the slot before keyed/provider/worker waits. Null-session behavior and
raw-source ownership remain unchanged. It limits simultaneous CPU/transient
preparation overlap, not aggregate provider snapshots, caches or live graphs.

Six new direct-ImageBox result cases cover one-slot serialization, slot release
before blocked provider open, queued image retirement, active/queued session
shutdown, blocked provider-open cancellation, and rejection/throw recovery.
The preserved **v58C** test compile fails CS0649 for an unused fake-provider field;
the fake now exposes its constant generation without that field. **v58D** passes
**1,068/1,069** renderer cases and **v58E** passes **1,069/1,070**, retaining the same
real session-shutdown failure: queued cancellation emitted a generation-retry
notification. Linking the complete operation fixes provider-open cancellation,
but nested admission can observe parent shutdown before the outer linked callback
runs. The final catch filters inspect the captured parent session token too.
No retry/publication assertion is relaxed, and genuine provider-generation
cancellation remains retryable when the session and image are alive.

Frozen **v58F** passes **1,115/1,115** fast core and **1,070/1,070** renderer cases,
including twelve API baselines, with all 491 captured compilation inputs stable.
Fresh selected **v58B SVG** passes **414/414**, and its separate current-provider
integration passes **1,070/1,070**. The combined geometry/lifetime/ImageBox suite
passes **75 exact cases in each of twenty fresh launcher processes (1,500 results)**.
The preceding v58C repetition invocation used a nonexistent class name and failed
prerequisite validation before any repeated process; its directory remains
explicitly unperformed. Fresh managed SVG policy/concurrency repetitions also pass
**123 exact cases in twenty fresh processes (2,460 results)** against the current
SVG source-linked runtime. Their explicit managed-eligibility partition remains
distinct from required native-worker and release qualification.

Fresh default **v58 package B** measures **537,299/537,600** compressed bytes and
**1,270,272/1,258,291** managed bytes: the managed gate still fails by **11,981 bytes**.
The package attempt is a lean-pair diagnostic, not the fourteen-package/security,
signing, AOT, independent reproducibility or browser release gate. All cap values
and payload/CRC/SHA preservation requirements remain unchanged.

The sealed F checkpoint is
`image-reflow-v58-checkpoint-f-20261002/checkpoint.json`, SHA-256
`8c050bdf1ad6e048c9f9fbcdf81bed6963a6c8895b639c82448e9949a5a6d22b`.
It does not bind the archived C full replay or claim the missing process proof.
The next source generation will rerun unfiltered core tests with an owned child
process and directly captured exit code; the original incomplete receipt stays
preserved.

The source-ownership audit identifies a remaining liveness constraint: caller raw
reservations, host sanitized arrays/caches, authoritative-preflight private copies
and retained tiled-provider sources have distinct owners. Adding a second blocking
reservation after raw admission can deadlock when the raw budget is full. Holding
all tiled-document byte leases can also indefinitely block later valid opens in
legacy controls that never retire those documents. A transient-only provider gate
would be a narrower improvement, not an aggregate retained-source cap; a complete
design needs eviction/reacquisition and actual temporary-allocation accounting.
No heuristic multiplier is promoted to a hard memory guarantee.

The next slice enforces the same immutable XML depth (**128**), element
(**100,000**) and cumulative attribute (**800,000**) ceilings as each parser node
is yielded, before the static-snapshot DOM materializes that node. This is a
single synchronous parser pass, with cancellation checked before and after each
read; namespace/attribute navigation and inert DTD settings remain unchanged.
It limits DOM structural amplification, not all parser, source, native or process
memory. Already-over-depth raw sources are now rejected before sanitization can
flatten or discard their branches. Three synthetic 20,000-depth iterative-transform
tests remain through an explicitly preloaded-DOM algorithm seam, alongside a
public raw-source rejection fixture; production calls that seam only after the
budgeted load. Five additional tests cover exact structural boundaries,
post-read cancellation and unchanged XML/static-sanitizer behavior.

The cancellation review also repairs the provider's sixteen-slot admission wait
to use the linked image/session token. A direct test saturates all slots with
unrelated blocked opens and proves shutdown drains the queued image without
waiting for those unrelated images. The common image publication handoff now
drops accepted queued callbacks after the borrowed session is disposed, with
exactly-once resource cleanup. This does not rescue silently lost dispatcher
callbacks or replace their existing fail-closed retirement barrier.

The preserved **v58G** fast-core compile fails two CS8764 nullable-return
overrides; its renderer/API partition still passes **1,072/1,072**. Corrected
**v58H** passes **1,115/1,115** fast core and **1,072/1,072** renderer/API, including
all twelve public API baselines, with all 491 captured inputs stable. Selected
**v58C SVG** passes **419/420**: the sole new assertion assumed an absent DTD
internal subset was null, whereas XmlDocument exposes an empty string. It is
replaced with an exact comparison against the unchanged unwrapped parser path
plus an explicit empty-subset assertion. Fresh **v58D SVG** passes **420/420**
with zero compile warnings/errors, and current-provider integration passes
**1,072/1,072**. Geometry/lifetime/ImageBox repetitions pass **77 exact cases in
twenty fresh processes (1,540 results)**; SVG policy/concurrency repetitions pass
**123 exact cases in twenty fresh processes (2,460 results)**. All remain managed
correctness diagnostics, not native-worker, Release, browser or README gates.

Fresh default **v58 package C** measures **537,467/537,600** compressed bytes and
**1,270,784/1,258,291** managed bytes; the unchanged managed cap fails by
**12,493 bytes**. The sealed H checkpoint is
`image-reflow-v58-checkpoint-h-20261002/checkpoint.json`; no archived C replay or
missing exit-code proof is implicitly attached (SHA-256
`4d6af63b0e4cfd9cabb60e68c289a91654ffc889cfddff27f8ac793853cd0770`). The full unfiltered H replay
started at **14:49:27 UTC** with a retained process object for actual numeric exit
capture, and completed at **16:22:56 UTC**, **1,616/1,616 passed**, zero failed,
skipped, pending or aborted, actual owned process exit **0**. All **501** synthetic
external-evidence fixtures passed; they are not 501 README renders. CIM process
lookup is denied, and the original parser wrongly marked VSTest's standard
`ResultSummary outcome="Completed"` as not-all-passed. The original receipt is
unchanged. A fresh strict supplemental binding revalidates every raw row,
required counter, result identity, source/runtime hash and the captured live
testhost **94040 → 93492 → owned launcher 84312** ancestry. It accepts `Completed`
only with all rows/counters and actual exit passing; its **33** synthetic positive
and negative cases pass. See
[VSTest's completed-run summary behavior](https://github.com/microsoft/vstest/blob/main/src/Microsoft.TestPlatform.Extensions.TrxLogger/TrxLogger.cs).
The accepted binding is `toolhelp-binding-v58h-derived-f-20261002/derived-binding.json`
(SHA-256 `25f811e9419d770834919a2015b49f41b013c8ea7e01a06841e9d38e158b95e8`).
Root independently revalidated all **491** compiler inputs and **486** outputs at
`image-reflow-v58-full-core-seal-h-20261002/seal.json` (SHA-256
`a567a4fe47c93318cda1cff463cee05adacf9895d73d07c31b7d91267fdecd2c`).
Every release/Edge/top-500 flag remains false. The source freeze is now released;
later changes cannot inherit H's current-source claim.

Rank 214 now has independently recovered pinned repository bytes from authorized
GitHub connector reads. The **3,353-byte** `docs/README.md` matches Git blob
`470095071afd381ad0f41e56deadc24339fb62b5`; both PNGs match their pinned blobs and
are **1519x988**, at **116,203** and **273,808** bytes. The local verifier and
SHA-256 identities are preserved in
`rank214-pinned-bytes-v59-20261002/verified-payloads.json`. The corrected audit in
`rank214-diagnosis-v60-corrected-20261002/rank214-corrected-audit.json` preserves
the original audit and explicitly retracts its transport/decode inference.
The focused current resolver suite passes **18/18**, with an actual exit code
zero. The exact-pinned two-reference replay now passes **44 assertions**, using
the current compiled document-source factory and resolver, and exits zero. Both
authored `./assets/screenshot-*.png` references resolve to the pinned
`docs/assets/` paths. The full current headless application suite also passes
**3,552/3,552**, with zero failures or skips and actual exit zero. The protected H
compiler inventory and core runtime remain unchanged. Evidence is under
`rank214-resolver-runtime-replay-v60-20261002`. The identity audit caught a
hand-transcribed TRX hash in the full-suite receipt. The original is preserved;
the corrected receipt and file-by-file audit are under
`rank214-receipt-correction-v61-20261002`. The actual TRX SHA-256 is
`bd30a1d3bb4fed9e1bae87afc426053b37a31ad0c54386357a7b8f097c4ab8b6`,
with 3,552 passed result rows and unique execution IDs. The resolver receipt and
its pinned payload, factory and harness input identities also verify. No complete
headless-app Csc manifest is claimed. These tests do not fetch or render the
images or establish the bytes/context used by the historical app. This is not a
missing-image fix or a same-byte Edge/top-500 pass.

The preview workflow's checkout, .NET setup and artifact actions now use their
published Node 24 action majors (checkout v7, setup-dotnet v6 and upload-artifact
v7), closing the recorded deprecated-runtime usage locally. The workflow has not
run with these unpublished edits; its existing CI failures and release
qualifications are not declared fixed by this configuration change.

### October 2 v59: complete license-expression selection and worker parser limits

The native license verifier previously accepted a selected identifier merely
because it appeared somewhere in the declared expression. That could accept
`MIT` alone for `(MIT OR Apache-2.0) AND Unicode-3.0`, omitting a required
conjunct. The shared generator/verifier helper now evaluates the complete
bounded AND/OR expression, with AND precedence and parentheses, and fails closed
for unsupported syntax and unknown identifiers. The selected conjunction still
has to use the unchanged repository-approved license identifiers. The existing
known-but-unselected `0BSD` alternative is recognized explicitly; the mutable
dependency inventory cannot grant arbitrary additional known identifiers.
No license policy, dependency pin, SBOM or notice was changed.

All **25** license-expression regression cases and the offline **76-package**
locked Rust inventory verification pass with owned numeric process exits zero.
The preview workflow invokes the regression script before inventory verification.
Evidence is in `svg-spdx-selection-v59-20261002/evidence.json`, SHA-256
`f6146bc1596f6dabe8222aee796dd69b221ea8b9bced3f8ab4cbcfb893bb6fd3`.
This is scoped local supply-chain verification, not complete package/release
compliance or an independent legal assessment.

The authoritative worker parser also now enforces **200,001 XML nodes** while
parsing both outer and nested SVGs, before the full tree can materialize. The
bound accommodates the existing 100,000 ordinary-element envelope with text
nodes and the document root; comments and processing instructions also consume
the node budget. Exceeding it returns a typed resource-limit failure. Existing
source, structural, depth, image and process limits remain unchanged; this is not
an aggregate heap guarantee. Exact-boundary and comment/PI-storm regressions pass
in the native suite (**45 passed**, zero failed, one pre-existing manual benchmark
ignored); Clippy and formatting checks pass.

Fresh offline release builds for x86, x64 and ARM64 succeed and their PE machines
are correct, but separate output-directory repeats produce different SHA-256
hashes at identical lengths for **all three architectures**. The binaries and
prior-runtime backups are preserved under
`MarkdownRenderer.Svg.Resvg/artifacts/worker-xml-node-limit-20261002-a`. Checked-in
workers are being updated only after the raw-identity differences were examined.
Exactly 20 bytes per pair differ, confined to COFF/debug timestamps and part of
the CodeView identity; `.text` and all other payload bytes are identical. The
unchanged repository comparator passes all three A/B pairs with its existing
documented PE-only normalization. No build-script or comparator change was
needed. This is local normalized-payload repeatability, not raw byte identity or
an independent CI result. The prior workers and both new build sets remain
recoverable. Guarded installation of all three unsigned workers is complete;
the installed binaries match build A and the five pinned source/build identities
remain stable. `worker-refresh-provenance.json` records the comparisons, logs,
actual exits and recovery paths. A single fresh real-worker font-rotation probe
using the unchanged current managed D partition fails at required restricted-token
creation with **Win32 error 87**, test exit one. The original D stage is unchanged;
only the intended worker differs in its fresh copy. No weaker token, retry or
in-process fallback was used. No signing, isolated-worker, Edge or release gate is
declared passed.

PR #99 was rechecked at its unchanged published head
`1c00c83ddfd2e8b65e05cb57e359a3f092b37271`. Its preview job stopped at the old
wall-clock `ForegroundReadinessWaitsForAQualifiedSample` fixture timeout, after
999/1,000 core tests passed. The current deterministic virtual-clock regression
is already part of H's passing local core partition; the unpublished fix has not
been verified by a fresh PR job. The published top-500 consolidation remains
**378 passed / 122 failed**, with **zero same-byte cases**. None of these local
diagnostics converts that historical audit into a 500/500 or browser-parity pass.

An artifact-only warmed probe now identifies avoidable allocation in the current
SVG tile queue: with four already-pending tiles and no provider open/render,
`QueueSvgTileRange` allocates **304 bytes per call** and
`QueueVisibleSvgTiles` allocates **728 bytes per call**, across 50,000 calls after
1,000 warmups. The latter creates dictionary snapshots and enters task-closure
scopes even when no new tile is admitted. The validated result is
`svg-tile-warm-allocation-v59-probe/baseline-h-warm-viewport-c.json`; an earlier
misnamed stale range-only invocation is preserved and excluded. This is a direct
managed queue-path diagnostic, not paint, UI timing or a qualified scroll/Edge
benchmark. The next candidate removes those warm-path snapshots/closures and
pins tile work to an immutable descriptor installed with accepted metadata:
document, exact dimensions, cache prefix, and generation cannot be mixed by a
replacement Open. Stale success and failure callbacks check that descriptor;
residency/retirement clear it. Fresh **v58I** passes **1,115/1,115** fast core and
**1,085/1,085** renderer/API (including thirteen new tile cases), all four compiler
and test exits zero and all **492** source/**486** output identities stable.
The comparable fresh I probe measures **zero bytes** for both 50,000-call warmed
loops with zero provider calls/new work; its runtime SHA-256 is
`c224a174f8dc93c32365c8440fbde4a49a4c2576f69d368fbeab2f0d4afb5787`, and its compiler
receipt SHA-256 is `8605e5a4c95512cc3b2747271512596eb96f94afbb08ed1a7652b91ebec76d2e`.
The I artifacts are `image-reflow-v58-i-20261002` and
`svg-tile-warm-allocation-v59-candidate/candidate-i-warm-viewport-a.json`.
The wider early-adoption/previous-good-document lifetime race is fixed in v59E
by candidate staging, lease transfer to delivered/dropped publication, and a
generation-serialized metadata/document/plan commit. A second fence rejects late
failures from viewport-evicted/replaced tile requests. Fresh E tests and its own
allocation probe verify those later changes; they do not inherit I's result.

The optimized PowerShell gate candidate is promoted with unchanged thresholds,
sample/pair validation, process isolation and watchdogs. **1,589** source/candidate
comparisons pass, including long mixed signed-zero/tie vectors and malformed
numeric inputs; the immutable I selected replay now passes **501/501**. Helper speedups
are synthetic gate-runner diagnostics, not renderer latency or browser parity.

Fresh non-mutating runner checks still fail: `winapp target snapshot sandbox
--json` exits one with broker access denied at `localhost:80` (`wsb list`,
`0x80131500`), and the configured NuGet proxy at `127.0.0.1` cannot reach
`api.nuget.org:443`. These remain explicit requirements for authorized Sandbox
execution and the mandatory dependency-security/release graph, not waivers.

The raster-memory review confirms the remaining contract gap: a WIC transform
bounds returned SoftwareBitmap dimensions, not codec-owned intermediates. Native
transform support is optional and codec-specific; the sniffed format is not proof
of the installed decoder's capabilities. Current process admission saturates
large estimates to an exclusive weight rather than enforcing a single decoder's
peak memory. A selected-decoder capability plan would improve routing, but a hard
workspace bound requires isolated decoding under an enforced process limit and
actual fault/peak qualification. The earlier ~131 MiB WebP observation remains a
sampled test-probe private-byte increase, not an attributed production peak. No
dimension cutoff, concurrency reduction or transformed-output heuristic closes
that gate. Microsoft documents this codec-specific fallback in
[IWICBitmapSourceTransform](https://learn.microsoft.com/en-us/windows/win32/wic/-wic-imp-iwicbitmapsourcetransform)
and [GetClosestSize](https://learn.microsoft.com/en-us/windows/win32/api/wincodec/nf-wincodec-iwicbitmapsourcetransform-getclosestsize).

### October 2 v59E atomic SVG publication, cache-key bounds, and fresh evidence

Candidate provider documents are reserved through actual native work and the
delivered/dropped UI handoff. Metadata, document ownership, and the immutable
tile descriptor commit together under the generation lock; a queued or rejected
replacement does not retire the previous accepted document. The old accepted
plan remains eligible for bounded visible/overscan refill while a newer request
is pending. Tile success and failure must own the exact in-flight key/CTS pair,
so an evicted or reassigned request cannot overwrite a tile or poison a newer
candidate. Provider generation getters run outside the tile lock. Actual native
uses drain before retired handles dispose, including cancellation and shared
handle rejection. Full-bitmap commits retire parsed documents rather than
retaining them unnecessarily.

Shared-content and process-cache followers now stage all metadata and keep the
captured request epoch and resolved key; they cannot advance past a newer
request or expose candidate dimensions/title before publication. An authored
null title is not replaced by an old image title. Known-source intrinsic-size
changes request image reflow; unchanged geometry remains paint-only. Delivered,
dropped, stale, suspended, disposed, duplicate/shared-handle, viewport-eviction,
and old-plan/new-candidate cases have direct managed regressions.

Weighted caches charge retained UTF-16 key bytes as well as payloads and
pre-evict before insertion to avoid a `long.MaxValue` accounting overflow.
The bitmap-cache key is a readonly value type, removing the lookup key object.
Count-bound negative caches refuse to retain keys above 4,096 characters and
bound memoized diagnostic descriptions. This limits failure memoization only:
it does not reject valid images, change image admission, or weaken authorization.
Warm key lookups retain strict zero-allocation assertions after warming the
exact measured helper. D's preserved run exposed three older payload-only
accounting assertions and first-use helper allocation; E's corrected tests keep
the cache caps and zero-allocation assertion unchanged.

`image-reflow-v59-e-20261002` passes **1,121/1,121** fast core and
**1,126/1,126** renderer/API tests. The synthetic external-process partition is
excluded from E; unchanged feature runtime dependencies are explicitly reused
from v52. All four actual compiler/test exits are zero, and the independent
seal rechecks **494** sources and **486** outputs, raw passing TRX rows and all
failure/unfinished counters. Compiler receipt SHA-256:
`2b8912e71aebbd6399db635c956438792ca725c9104d251e458211f033f2818b`.
Renderer SHA-256:
`c81a4e26dc2772d9a1b370bafaf114f68886ae1eac40caea6d4bc868f5f409c5`.
The fresh `svg-tile-warm-allocation-v59-a-20261002/candidate-e-warm-viewport-a.json`
probe uses that exact renderer: **zero allocated bytes** in both 50,000-call
loops, four prefilled pending tiles, zero new work/provider calls. It does not
exercise drawing, GPU publication, worker execution, or scroll-frame timing.
The independent seal is `image-reflow-v59-checkpoint-e-20261002/seal.json`,
SHA-256 `ac4eabb5b6278db0092b982c6659e3d205a86ade6c84805ae5f2747b196f0fa8`.
Failed intermediate J/A/B/C/D attempts remain preserved and are not counted as
passing checkpoints.

Fresh diagnostics-disabled default packing records both compiler exits zero,
stable inputs, lossless archive-entry hashes, and the unchanged caps:
**1,274,368/1,258,291** managed bytes and **539,223/537,600** compressed bytes.
Both size gates fail. `packages-reflow-v59-a-20261002/partial-size-evidence.json`
is a two-package measurement only, not an all-fourteen-package, dependency-
security, AOT, signing, or release pass. The current PR audit remains
**378 passed / 122 failed**, **zero same-byte cases**, at published head
`1c00c83ddfd2e8b65e05cb57e359a3f092b37271`. Local fixes remain unpublished
because this worktree's Git metadata is read-only. Sandbox broker access,
mandatory vulnerability-feed access, restricted worker-token startup, and
Edge startup still prevent the qualifying live reruns; none is waived.

### October 2 v59G extreme viewport arithmetic and final scoped verification

Visible physical coordinates now clamp in double precision before integer
conversion, and right/bottom overscan addition uses widened arithmetic. Both
maximum-integer-width and maximum-integer-height regressions preserve the exact
two pending edge-tile owners without a provider call. Running the new G tests
against a copied immutable E renderer fails both exact-owner assertions: its
overflow path evicts and requeues the last tile. The artifact runner initially
predicted count loss only; that failed receipt remains unchanged. A separate
strict binding verifies the actual owner-replacement failures, at the exact
compiler-bound assertion line, with VSTest exit one and exactly two failed rows.
This is regression sensitivity evidence, not a passing old renderer or release
performance result. `svg-extreme-viewport-v59-old-e-negative-20261002/ownership-failure-binding.json`
has SHA-256 `732f49df48afa64e941a358b16ae5013666be9de1ceedd6691b9cbd924b77215`.
F's two fixture-setup failures are preserved; the corrected tests cancel any
accidentally queued work through its normal lifetime token without modifying
the process-wide admission semaphore.

Fresh G passes **1,121/1,121** fast core and **1,128/1,128** renderer/API tests;
all four compiler/test exits are zero, and **494** source/**486** output identities
remain stable at the seal. Compiler receipt SHA-256:
`bb9643d5f3f8b42a4409b65275af5ed9a8231ebb12b8405a622cb8ba667a41f6`.
Its renderer SHA-256 is
`a5937ebef95555c9c77b0d20990fc67c800488f6a213740c5026ca5baf7bb8d2`, MVID
`9ca43820-f72f-4372-bd9a-c580ab501f73`.
The separate 20-process repetition verifies **53** tile/publication/cache-key
cases each time (**1,060** results), exact identities, actual zero exits, every
failure/unfinished counter present and zero, and unchanged source/runtime bytes.
The fresh G warm probe again reports **zero allocated bytes** in both
50,000-call pending-tile loops with zero new work/provider calls. Drawing,
worker, GPU, UI/frame and Edge timings are not measured by that probe.
The independent G seal is `image-reflow-v59-checkpoint-g-20261002/seal.json`,
SHA-256 `47865c430e869a5af4a76e2177866cf1ec04151f6e38f373a7b61b13e4dcc4bd`.

The optimized shipping gate's selected fixture run completes **501/501 Passed**
against immutable I runtime bytes. Retained supplemental Process objects report
both launcher and child exit zero at **18:06:16 UTC**. The strict B receipt binds
all **92** runtime files, exact canonical fixture identities and TRX counters,
the TRX time window, launcher→child→testhost PID/parent/start/module identities,
and **17** evidence files hashed before reads and after validation. Reconstructed
project/source identities and **three** later workspace changes are recorded;
there is no archived Csc invocation or current-G qualification. The initial
PowerShell wrapper lost its durable launch/log record; live attachment does
not retroactively repair it. The earlier receipt remains preserved and is
explicitly superseded by B, SHA-256
`9c1540c76a6abc2340f208698d00908fc9d31ea407d519030e9f4796cfab37eb`.
This is neither an unfiltered G core replay, a full counterbalanced Release
benchmark, a same-byte Edge corpus, nor a 500-README rendering audit.

Fresh default package B still fails both unchanged caps:
**1,274,368/1,258,291** managed bytes and **539,137/537,600** compressed bytes.
An independent metadata/IL review found no quantified low-risk removable block
large enough to close the managed deficit. Public record contracts and required
WinRT/AOT support cannot be stripped to satisfy it. The production control
supplies a dispatcher and serializes publication callbacks; null-dispatcher
internal/headless concurrent publication remains outside this live-host proof.
Sandbox broker access, the mandatory NuGet audit feed, restricted-token worker
startup, Edge startup, read-only Git metadata, aggregate-memory/COW/codec bounds,
and the physical release matrix remain open. PR #99 is rechecked at the same
published head with **42 failed / 15 successful / 2 skipped / 2 canceled** checks;
no local result is substituted for those checks or the **378/122**, zero-same-byte
historical README audit.

### October 2 v60: raster policy, preparation admission and UI-reader lifetime follow-up

This is an active local diagnostic checkpoint, not release qualification. The
immutable v59G seal and its size failure remain historical evidence. No previous
receipt, failed run, gate, public API baseline or security restriction is replaced
or relaxed by this follow-up.

Implemented raster and preparation corrections:

- Source-sized cache entries cannot bypass a consumer's display-sized decode
  choice or lower pixel ceiling, including eager data-URI constructors. A
  compatible source-sized consumer still reuses eligible small entries without
  another CPU preparation. Cache validation uses immutable recorded axes rather
  than native metadata calls under an LRU lock.
- A detached `RasterImageSizingRequest` fixes the sizing inputs before async
  waits and uses them for raw dimensions, authoritative EXIF orientation, cache
  validation and preview choice. Cache families distinguish pixel ceilings and
  authored sizing; variants also bind viewport/DPR inputs so pre-EXIF raw-axis
  clamping cannot alias two different oriented outputs. Preview candidates need
  valid oriented intrinsic metadata, positive integral dimensions, a valid
  invariant request suffix, the same policy family and a strictly smaller output.
- Uniform downscaling now reconciles the one-pixel short-edge minimum with the
  actual pixel cap. Extremely thin/tall images therefore cannot exceed the cap
  after rounding. Zero/nonpositive authored dimensions follow layout's
  unspecified-dimension behavior; oversized authored width/height are constrained
  to the cell's displayed rectangle before DPR conversion rather than decoded at
  an invisible larger size. Source axes and hard output ceilings remain final
  limits. This is output sizing, **not** a bound on native WIC working memory.
- SVG preflight/static-source preparation and raster decode/upload share
  `MarkdownImagePreparationAdmission` across controls, sessions and legacy/null
  sessions. The fair queue retains cancellation and exact lease cleanup. SVG
  scheduling weight is three times source length; the lease ends before provider
  open/worker waits, so hung provider opens cannot consume all host preparation
  slots. Session CPU admission precedes process admission consistently. These
  are estimated scheduling weights, **not** aggregate retained source/resource
  ownership, codec peak memory or a process RSS guarantee.

UI-reader/command corrections under combined verification:

- Visible-image readiness uses an immutable image-plan membership publication and
  a nonblocking exact snapshot/generation/revision read lease. Busy layout is
  conservatively not ready, never a synchronous wait on the UI thread.
- Block scrolling captures scalar target/owner geometry under the same lease,
  retries busy/unmeasured targets, requests the owning lazy band, rejects stale
  generations and keeps the height-ratio fallback if a replacement snapshot no
  longer has the exact anchor identity. Reset no longer traverses retained boxes
  merely to clear an unread diagnostic hover property.
- Focus and horizontal overflow use leased geometry capture and detached UI
  publication, with bounded pending keyboard intent, reentrant queue-clear/head
  fences and lifecycle cleanup. This does not assert that all older hosted/pointer
  focus callbacks are detached or that the mutable layout tree has become
  copy-on-write. Pointer press, double tap, holding and right-tap selection now
  carry their captured snapshot/generation/revision through focus, release and
  selection callbacks; changed/unloaded/disposed snapshots stop further hit
  testing, selection mutation and pointer capture. Source-contract tests cover
  this wiring, not a live WinUI callback/mixed-DPI qualification. `Focus()` itself
  still executes under the pre-existing outer dispatch interaction lease.
- Context menus capture URL/range/code/table data and an owned image bitmap lease
  rather than retaining live layout boxes. Execution clones its bitmap lease
  before a host command or first await, so menu/navigation cleanup cannot dispose
  the pending copy's image. Table formatting is deferred to background work only
  for the default Copy Table operation; cell-text snapshot capture still scales
  with table content and is not claimed as a bounded UI-frame operation. Pointer
  menus must publish after the **outer** interaction lease, not merely after a
  nested hit-test lease, for both immediate and deferred gestures. The originally
  interpreted layout revision is carried through target capture and revalidated
  immediately before `ShowAt`, rather than accepting a newer same-document
  revision. Stale/reentrant branches release detached payloads and clear flyout
  state as well as rejecting the menu.

Evidence obtained before the final combined source freeze:

- `raster-cache-policy-v60-baseline-b-20261002/evidence.json` records five assertion
  failures and one compatible-cache pass against immutable G. Candidate e passes
  all six matching cases with actual compiler/test exits zero. The source fixture
  SHA at that point was
  `8ab95abaf1ad26e2898075f66de44aec2d27d4c7618550c59d0a000362e30635`.
- Actual WIC EXIF JPEG fixtures pass all fifteen cases against candidate e; G
  reproduces seven assertion failures with eight orientation cases passing.
  Receipts are `exif-raster-policy-v60-{baseline,candidate}-a-20261002/evidence.json`.
  The candidate e binaries predate subsequent authored-sizing/focus/menu edits;
  those scoped successes cannot qualify the final v60 source by themselves.
- Forty-eight pure sizing cases pass in
  `raster-sizing-v60-d-20261002/raster-sizing-v60-d.trx` (actual test exit zero),
  including eight required scales from 100% through 400% plus valid sub-100%
  scales, authored containment, EXIF axes, invalid/default inputs, invariant keys
  and thin/tall caps. Live device/DPI qualification is still open.
- The interim wide v60-a run passes 1,146 fast core cases and 1,166 of 1,167
  renderer/API cases, but its source hash check is **false** and its test exit is
  **one**, so it is deliberately unsealed. The remaining warm-constructor
  residency fixture assumed a source-sized bitmap was eligible for a display-
  sized consumer. It now uses the compatible source-sized policy while retaining
  the original synchronous-release assertions. A preparation-weight fixture's
  earlier free-capacity arithmetic mistake is corrected, with its failed
  nine-case run preserved. Compile failures and the PowerShell 5
  `Path.GetRelativePath` runner failure also remain preserved as failures.
- Portable README capture/replay and source-alias contracts pass 42/42 in a fresh
  owned Node process with stable sources, using the same-process test-runner mode
  explicitly recorded in `readme-portable-contracts-v60-b-20261002/evidence.json`.
  The earlier process-isolated attempt fails before tests with `spawn EPERM` and
  remains preserved. These mocked/local-server tests launch no browser/native UI
  and do not constitute a rendered README audit or Edge corpus qualification.

At the interim checkpoint, required next verification was a fresh frozen-source wide checkpoint, exact
current-candidate fresh-process repetitions, a matching warm tile-allocation
probe, diagnostics-disabled package measurement and strict evidence seal. A new
full-core runner must bind current reconstructed Compile inputs, exact runtime
bytes, owned launcher PID/start/numeric exit and the unfiltered TRX; the 501
synthetic gate fixtures are not 500 rendered repositories. Prior runner capture
limitations are retained, not retroactively repaired.

The earlier frozen-source checkpoint **v60-c** passes **1,172/1,172** fast core
tests and **1,203/1,203** renderer/API tests, with all four actual build/test
exits zero and the compiler source-hash check true. Its strict managed receipt
is `MarkdownRenderer/artifacts/managed-checkpoint-v60-c-sealed-20261002/managed-evidence-seal.json`
(SHA-256 `7d803d63feba061e49d4afa81900e8bdf1b28685d270c23b951a204657768e97`).
It verifies distinct adapter test IDs/execution IDs for the twelve API baseline
cases: xUnit truncates the two grammar-pack argument display labels to identical
text, so display text alone cannot represent their identities. The failed
v60-b run remains preserved (stable sources, four test-fixture/source-contract
errors, test exit one); the subsequent corrections changed tests only.

All **150** scoped renderer cases pass in **20** fresh owned VSTest launcher
processes (**3,000** passing results), with exact identities and source/runtime
hash stability. The matching immutable renderer's warm pending-tile queue and
visible-queue probes each record **zero** current-thread allocated bytes over
**50,000** calls after **1,000** warmups; neither probe opens a provider, renders,
draws, measures frames or establishes browser-relative performance. Default
diagnostics-disabled lean packaging measures **546,514 / 537,600** compressed
bytes and **1,295,360 / 1,258,291** managed bytes. Both size gates **fail** (8,914
and 37,069 bytes over respectively); they remain false in the seal. Both package
build exits are zero, but the size runner's actual exit is one. Mandatory NuGet
vulnerability-feed warnings remain present, not suppressed.

The reproducible PE/IL-only package audit is
`package-il-audit-v60-b-20261002/package-il-audit.json` (tool exit zero). It binds
the exact two packages, their managed entries and the failed size receipt;
assemblies are parsed, not loaded/executed. It reports 481,224 method-IL bytes
and only 841 theoretical duplicate-copy bytes among exact identical bodies
**at least 16 bytes long**. Smaller bodies are explicitly excluded, and this is
not an achievable C# refactoring/PE/ZIP saving. The original unlabelled audit is
preserved, not overwritten. No demonstrated small deduplication closes the
37,069 managed / 8,914 compressed-byte gaps; generated WinRT/AOT support and
public record behavior must not be removed to make size checks green. A measured
architectural size reduction remains required, along with the full fourteen-
package/security/AOT/signing gate.

The strict full-core replay runner preserves its failed preflight stages:
`full-core-replay-v60-c-20261002` launches no discovery/tests because empty
comparison output lost its array shape under StrictMode. The subsequent
`full-core-replay-v60-d-20261002` verifies the 215 project/source rows and 92
runtime files and discovers exactly **1,673** current cases, including **501**
synthetic gate-validator fixtures, but does not launch tests: void-task copy
results polluted the helper's process-receipt output. These are runner failures,
not executed test successes. The helper repairs were verified against matching
source/runtime inventories and a single owned `dotnet --version` receipt. A new
unfiltered execution under `full-core-replay-v60-e-20261002` completed with
**1,673 passed, zero failed/skipped** and the owned launcher exit **zero**
(PID 91548, start 20:18:57 UTC, exit 21:19:52 UTC). All 215 reconstructed
project/source rows and 92 runtime files match the compiler checkpoint and are
unchanged across execution. However, its strict receipt runner exits **one**:
two data-URI theory names containing a globe emoji are printed literally by
discovery and escaped as surrogate pairs by TRX. The original receipt remains
failed; its TRX SHA-256 is
`80ec93cffc038e99d75772c6accee6366a1afe1c38c3418d1861b830139321f8`.
Separate **F2 reconciliation passes** at
`full-core-replay-v60-f2-20261002/reconciliation-receipt.json` (SHA-256
`7161cedd3f97ca8d5c5604c38a0bb32260e9e05b9226eb96093069c1dbfc8a57`).
It retains raw evidence, records only the explicit two-name bijection, verifies
all 1,673 distinct adapter/execution IDs and matching definitions, and passes
seven alias/collision/double-escape self-tests. Its fresh **six-case** C-runtime
replay exits zero (owned launcher PID 10272) with matching stable test IDs/raw
TRX names and stable 215 source/project / 92 runtime identities. This attests
the existing E full execution, not another 1,673-case rerun. First F remains
preserved and superseded: all seven self-tests passed individually, but its
aggregate predicate incorrectly expected six. F2 corrects that predicate and
fails closed; no arbitrary mismatch is ignored or E failure overwritten. The
501 synthetic gate-validator cases remain distinct from the mandatory 500
rendered README audit. The source freeze ended only after F2 completed binding;
subsequent implementation requires fresh candidate evidence.

The fresh source-linked managed SVG-provider diagnostic compiles successfully
but does **not** reach test discovery. Preserved attempt
`managed-svg-provider-v60-c-h-20261002/managed-provider-audit.json`: restore and
build exits are zero, while the VSTest 18.7 host reports a missing conventional
`testhost.runtimeconfig.json` and produces no discovered identities. The runner
rejects that empty discovery despite its launcher exit zero. Attempts d–h and
their startup/handshake failures remain preserved. This is compilation-only
evidence, not a 420-case pass, worker execution, AOT or release qualification.
The subsequent fresh `managed-svg-provider-v60-c-i-20261002/managed-provider-audit.json`
also compiles with zero errors/warnings and generates the test assembly's own
runtime configuration. Nevertheless, VSTest 18.7 times out connecting to
testhost after 90.56 seconds and discovers zero cases. The launcher fails
closed before test execution; the reported testhost process is no longer
running. Its 69 source inputs and 388 runtime files are unchanged. This probe
links the current provider source to historical C renderer references, not the
new GIF candidate; no SDK/testhost configuration graft or security bypass is
used, and that attempt does not verify the 420-case provider partition.

A fresh dependency-manifest diagnostic identifies the actual missing assembly:
the artifact project omitted its pinned `Microsoft.NET.Test.Sdk` package, so
the testhost dependency graph could not resolve `Microsoft.TestPlatform.CoreUtilities`.
Fresh J uses the already-cached normal Test SDK 17.13 graph and discovers all
420 cases, then fails closed before execution because two historical display
names contain literal Unicode escape text instead of the current emoji.
K preserves J and accepts only those **two exact whole-name aliases**, with
raw names, UTF-16 code units, matching source rows, bijection, near-miss and
tamper checks; it performs no generic Unicode or escape normalization.
`managed-svg-provider-v60-e-k-20261002/managed-provider-audit.json` (SHA-256
`2736bf83bcff763a33b153c48bce4257266cf83866614b7dda817d3a1d9f86a0`)
records restore/build/discovery/test exits zero and **420/420** passing results,
unique identities, all required failure/skip counters zero, owned process IDs/
start times and stable current provider sources, cached SDK assets and **388**
immutable E reference-runtime files. This is a fresh artifact-only managed,
no-worker partition, not the complete shipping Windows-TFM provider suite,
native isolation, AOT, UI, browser or release qualification.

The aggregate ownership audit narrows the next implementation gap: SVG
preparation weights end before provider waits, and the provider semaphore caps
concurrent opens/renders rather than all accepted document bytes/trees. Visible
and overscan images are still treated as one active residency band. The existing
diagnostics-only `SvgProviderSnapshotLedger` already observes scoped/process
logical live/peak source lengths, and source snapshots already retire after the
document's admitted-render drain. The next coherent milestone must build on
those lifetimes with real retained-source admission and pressure-aware overscan
retirement backed by proven, authorized, identity-bound byte-free backing and
reacquisition; another telemetry counter alone would not close the gap.
Source-cache bytes, provider-private snapshots,
worker parsed/decoded resources and GPU leases must remain separate owners;
estimating any one of them is not an aggregate memory ceiling.

The read-only ownership and COW reviews are preserved in
`MarkdownRenderer/artifacts/svg-retained-source-admission-audit-20261002.md` and
`MarkdownRenderer/artifacts/layout-cow-audit-v60-20261002/review.md`. They trace
the existing drained provider-source lifetime, worker-tree aliases and mutable
layout readers rather than claiming that admission estimates or `IReadOnlyList`
make the retained graph bounded/immutable. The targeted hosted-element focus
lookup is corrected in the E candidate below. Other pointer activation/capture-
release callbacks and the complete immutable layout generation remain open.
Those reviews are source audits, not executed release evidence.

An additional six-pixel GIF reproduction found a real budget/availability defect
while the v60-c source was frozen for the full suite. Windows reports and decodes
the **first frame rectangle**, whereas `TryReadGif` currently reports the logical
screen dimensions. A valid 4×4 screen with a 2×3 first frame is rejected by the
display-sized decoder metadata fence. A malformed 1×1 screen with the same
2×3 descriptor bypasses a one-pixel consumer ceiling on the direct-load path.
The actual WIC metadata/SoftwareBitmap receipt is
`raster-frame-metadata-v60-b-20261002/run-evidence/evidence.json`; the matching
immutable v60-c **ImageBox/Win2D** pipeline reproduces all four expected
baseline outcomes in `gif-imagebox-v60-baseline-a-20261002/run-evidence/evidence.json`.
Both artifact build/probe exits are zero and their recorded input/runtime hashes
are stable. They use only six decoded pixels, no host UI or browser; they do not
qualify large-image codec working memory. The fix must preserve the logical
canvas as intrinsic/display geometry, validate WIC against separately admitted
first-frame axes, and compose the frame at its scaled logical offset onto a
bounded transparent output canvas. Merely substituting frame axes would make
valid partial images available but distort their aspect and omit padding.
Independently bound the logical screen and every frame rectangle, account
aggregate decoded work, preserve bounded static previews, and test valid
offset/partial frames plus malformed/truncated later descriptors. It is not
implemented at the immutable v60-c checkpoint. Following F2's completed source
binding, the new candidate now validates the logical screen and every frame,
retains separate first-frame metadata, composes partial/offset frames onto a
transparent bounded canvas, and independently checks actual uploaded output
axes/pixels before caching/publication. Default source-sized consumers remain
source-sized; canvas correction alone does not opt them into viewport/DPR
decoding. Composed cache families are explicitly partitioned, and transient
admission includes the additional canvas allocation through cleanup. New pure
and native GIF regressions pass in the fresh combined E compiler checkpoint
below; fresh repeated-process verification also passes, while the full
unfiltered replay remains in progress.
The isolated prototypes below are not substituted for that production gate.

The fresh headless composition prototype
`gif-canvas-composition-v60-b-20261002/run-evidence/evidence.json` has build/probe
exits zero with stable recorded inputs. Against the frozen v60-c Win2D
references, a real 2×3 WIC frame at `(1,1)` composes onto the 4×4 logical canvas
with exact frame-pixel placement and transparent padding; 2×2 and 1×1 targets
also have exact physical dimensions. Fractional destination edges are retained
rather than expanding the drawn frame to rounded integer coverage. This is an
isolated prototype, not ImageBox production verification, an Edge comparison,
or large-image working-memory evidence. Its failed predecessor `v60-a` is
preserved: an incorrect test-fixture descriptor offset produced the wrong frame
axes, so that build/probe had exits zero/one. WinApp's project-scoped API lookup
also failed on its outside-workspace cache lock; the restored references and
actual compiler verified the composition API instead. No host UI fallback was
used.

The additional read-only architecture reviews are
`MarkdownRenderer/artifacts/svg-host-park-vs-source-backing-audit-20261002.md` and
`MarkdownRenderer/artifacts/hosted-focus-realization-plan-audit-20261002.md`.
Control-owned SVG handles can be drained/disposed and reopened through the
existing authorized resolver without requiring a new public source-backing
interface; pressure parking still needs retained-pixel demand detection and
finite provider admission. Hosted focus can use detached current realization
plan identities rather than an unleased O(focusables × document boxes)
recursive lookup. Pressure parking and finite retained-source admission remain
unimplemented. The hosted-focus proposal is implemented in E, but these audits
do not prove the aggregate-owner or immutable-COW milestone complete.

The new frozen-source combined **v60-e** checkpoint is
`MarkdownRenderer/artifacts/image-reflow-v60-e-20261002/compiler-provenance.json`.
It passes **1,191/1,191** fast core and **1,224/1,224** renderer/API cases, with
all four compiler/test exits zero, no failed/skipped cases, and unchanged source
hashes. The new coverage includes 19 pure GIF-budget cases, 18 real WIC/Win2D
GIF ImageBox cases and three hosted-focus source contracts. The native cases
cover offset padding, transparency, fractional one-pixel composition, lower
consumer caps, cache order, default source-sized behavior and simulated scales
from 75% through 400%. They are not live UI/Edge, large-codec memory or physical
device qualification. The preceding D run remains failed: one fixture compile
error and two pixel-crop assertions. Only test helpers changed to correct local
variable shadowing and crop the actual image destination after its normal
six-DIP content margin; no production layout margin was removed.

A separate forced-software replay of those exact production GIF assertions
passes **18/18** at `gif-headless-warp-v60-e-d-20261002/run-receipt.json`.
It mechanically transforms only the two test/helper device acquisitions to
`CanvasDevice.GetSharedDevice(forceSoftwareRenderer: true)` and references the
immutable E assemblies. Source-derived display names, TestDefinitions, unique
test/execution IDs and exact per-method counts match; restore/build/test owned
launchers exit zero and bound source/runtime hashes remain unchanged. A/B
setup failures and C's passing raw TRX with a failed fact-name receipt parser
are preserved separately. D corrects that parser without filtering any case.
Offline cache-only restore is explicitly not dependency-security qualification.
This is headless WARP-path coverage, not physical DPI, UI/UIA, Edge, hardware/
HDR/theme composition or the complete release matrix.

Hosted focus plans now carry detached scalar identities captured under the
existing snapshot lease. Lookup scans current realization plans rather than
recursively walking live boxes once per focusable item. Publication stamps are
invalidated before plan changes and published only after callback-capable
retirement completes and snapshot/generation/revision/epoch still match. Native
descendant, hosted-control and parent fallback `Focus` calls are followed by
generation and focused-item fences before focus-ring/UIA publication. Pending
focus resumes only for its captured snapshot generation. This closes the
targeted lookup/publication race, not the outer pointer dispatch lease or full
copy-on-write layout milestone; its source-contract tests are not live
reentrant XAML/UIA evidence.

The strict E managed seal is
`MarkdownRenderer/artifacts/managed-checkpoint-v60-e-sealed-20261002/managed-evidence-seal.json`
(SHA-256 `57b11aa22ea17f8c076173c76372f01c7ae7146c919627a100128d3c37b9dcb2`).
All **179** selected renderer cases pass in **20** fresh owned launcher
processes (**3,580** results), with exact identities, numeric exits and
unchanged 504 source / 388 runtime files. The original 150-case selection is
retained, with the whole GIF-canvas and realization-plan classes added; no
failed case is filtered away. Two shell invocation attempts failed before
starting the repetition runner or any tests and remain separate no-start
receipts. The matching warm pending-tile and visible-queue probes each allocate
**zero** current-thread bytes over **50,000** calls after **1,000** warmups,
with four already-pending keys and no provider calls. Their actual local
compiled program/project and before/after inventories are bound by the seal.
The warm launcher receipts retain process IDs and numeric exits but not OS
start times; no full-release process identity is inferred from them. A prior
artifact runner `$PID` variable collision also remains a no-child failure.

Fresh diagnostics-disabled lean packages measure **548,731 / 537,600**
compressed bytes and **1,300,992 / 1,258,291** managed bytes. Both size gates
**fail**, by 11,131 and 42,701 bytes; both package builds exit zero while the
size runner exits one. This current failure replaces no earlier result and
remains false in the seal. NU1900 security-feed warnings are still present.
The current-package metadata/IL inspection at
`package-il-audit-v60-d-20261002/execution-receipt.json` exits zero without
loading/executing either assembly. Exact duplicate IL bodies of at least sixteen
bytes total only 949 bytes across the pair before refactoring overhead; this is
not a measured saving and cannot explain away the 42,701-byte managed overage.
The largest handwritten default-theme materializer is 5,385 IL bytes, while
the generated WinRT vtable code is required AOT/ABI plumbing, not a deletion
target. Further size work must preserve features, APIs and all unchanged caps.

The new E unfiltered replay completes at
`MarkdownRenderer/artifacts/full-core-candidate-v60-e-20261002/candidate-replay-receipt.json`
(SHA-256 `b08cddd92a62aba3eb4bc9ce7147b64211feabac36fc4f986ab307899d5bffea`).
Same-runtime discovery and the strict TRX agree on exactly **1,692** distinct
passing cases (1,191 fast cases plus 501 synthetic gate-validator cases), with
all required nonpassing counters zero. The retained dotnet launcher PID 88008
started at 22:07:50.9086521 UTC and exited at 23:05:43.5714056 UTC with captured
numeric exit **0**; no testhost process identity is inferred. Compiler receipt,
source/project, runtime, tool and runner/module hashes remain unchanged before
and after execution. The only display-name aliases are the two explicitly
F2-pinned Unicode cases; raw discovery/TRX names are preserved. This is fresh E
managed diagnostic evidence, not the prior 1,673-case C execution, and the
source freeze is now released. The synthetic partition represents no live
repository README. All browser, README, security, full-release performance and
physical-device gates remain open.

Additional fresh native-source checks pass at
`MarkdownRenderer/artifacts/resvg-rust-all-cases-v60-a-20261002/run-evidence.json`
(SHA-256 `484674d54f161528cd2d21345a3a369435e3a1aa1ad34c1c496fbc9a46044066`).
Locked/offline Release discovery and execution agree on **46** Rust cases,
all passing with zero ignored/filtered cases, including the pre-existing
manual font-fallback pixel-equality microbenchmark. Formatting and strict
Clippy also exit zero; owned cargo launcher identities and unchanged source
hashes are recorded. The preceding default run passes 45 cases with that
explicit manual case ignored and is preserved separately. The eight-iteration
microbenchmark's identical pixels are not Edge fidelity or a counterbalanced
latency/CPU result; neither run launches the isolated worker or qualifies its
restricted token, IPC, job limits, AOT, architecture or browser matrix.

An artifact-only retained-snapshot admission precursor passes **28** checks in
`svg-snapshot-admission-prototype-v60-a-20261002/run-evidence.json`
(SHA-256 `16129033332c48e5df6306911b617c31e22d1d465dea51b2d820a8f402b4aa47`).
It exercises exact virtual byte caps/headroom, shrink/Dispose races, immediate
saturation before a simulated clone, outside-lock notification and the actual
provider `SvgDocumentLifetime` render-drain barrier. Inputs/runtime are stable
and the owned prototype launcher exits zero. This is deliberately **not**
integrated into provider Open or ImageBox: real normalization/clone ownership,
tiled-host pressure parking, authorized re-demand, native memory and progress
remain required together. An admission-only shipping patch would falsely turn
capacity pressure into unavailable valid content and is not accepted.

The refined provider/host integration design is
`MarkdownRenderer/artifacts/svg-provider-admission-and-host-pressure-park-design-20261002.md`.
It targets only accepted **tiled** SVG documents: whole-raster documents already
retire after publication. A narrow internal engine-neutral capability can
distinguish transient capacity pressure without changing the public renderer
API or caching a content failure. Finite process snapshot admission must reserve
before the provider's first private clone and transfer ownership to the existing
drained source lifetime. The host must park exact eligible accepted plans while
retaining identity-bound pixels/metadata, then reopen uncovered visible tiles
through the existing authorized resolver. Completed visible coverage must also
be parkable so many visible images cannot deadlock the source budget. Exact-plan
CTS ownership, stale-generation fences, bounded capacity notifications and
full-pipeline visible/overscan promotion are required together. Currently initial
SVG open/whole-render requests default to Visible even for lookahead loads;
per-tile requests alone preserve the intended priority. This design and the
primitive are not shipping implementation or aggregate-memory qualification.
The follow-up source review also finds that the existing sixteen-slot provider
semaphore and preceding keyed-open waits can retain caller bytes in unbounded
async waiters. The coherent fix must bound/replace that source-bearing host
queue too; a provider snapshot cap behind it is insufficient. Deferred retries
must retain only bounded weak identity/demand metadata and reacquire through
the authorized source path. Queue promotion must update visible counts,
detached-font re-entry and fairness, not just read a new priority at dequeue.
Snapshot weight shrinks only after every provider transform, and capacity
delivery must be an asynchronous signal outside every ownership/worker lock.

PR #99 was refreshed read-only at the same published head
`1c00c83ddfd2e8b65e05cb57e359a3f092b37271`: 42 failing, 15 successful, two skipped
and two canceled checks. The published top-500 result remains 378 passing/122
failing with no qualified same-byte corpus. Local changes cannot be published
from this task's read-only Git metadata. Required Sandbox broker authorization,
worker-compatible Restricted Code token, functioning Edge startup, reachable
mandatory NuGet security feed and physical/AOT/signing runner matrix remain
unavailable. Aggregate source/transient/live-graph ownership with authorized
byte-free reacquisition, full immutable COW publication and hard native codec
workspace containment also remain implementation work. The original full
counterbalanced Release benchmark and all 500 README/Edge gates remain required;
the automation is active and no overall completion is claimed.

The broader offline README-helper attempt at
`readme-offline-audit-v60-e-20261002/evidence.json` is **failed**: 66/68 pass,
two fail with `spawn EPERM` (generated-profile cleanup and the emoji-generator
child-process test), launcher exit one, exact TAP identities and stable source/
tool hashes. Its explicit nine-file partition omits the mixed live/offline
Edge replay file; this is not a passing full Node suite or a waived browser
gate. The preceding C recursive-suite attempt included that real headless
browser case and was stopped; its incomplete raw TAP, exact owned Node process
termination and lack of qualification are preserved. No host browser result is
substituted for authorized Sandbox/native/Edge release evidence, and no child
spawn denial is hidden by filtering the two failed cases from E.

### October 2, 2026 — v60-f runtime-limit cache recovery and audit-helper partitioning

`ImageBox` previously treated every provider `ResourceLimitExceeded` as a
permanent content verdict after preparation. Both the worker pool and pipe
transaction queue use that reason for temporary saturation; rendering can also
reject target-dependent raster/intermediate sizes. The failure key contains
content/security/provider generation, not queue state or target dimensions.
The general runtime catch now memoizes only `UnsupportedContent`; explicit
source-preparation/preflight resource rejections remain memoized. Three fresh
regressions verify same-source, same-renderer, same-generation recovery after
one-shot Open and Render limits, and preserve deterministic preflight rejection
memoization. This prevents poisoning later boxes; it does **not** implement
automatic pressure retry, source parking or the broader admission design.

The fresh F compiler receipt is
`MarkdownRenderer/artifacts/image-reflow-v60-f-20261002/compiler-provenance.json`
(SHA-256 `e2fea4a1847ab63abce7422559edc295c9794951303df80301b5a51394d6352c`).
All four compiler/test exits are zero, all 504 source identities are stable,
and the diagnostics pass **1,191/1,191** fast core and **1,227/1,227**
renderer/API cases, including the three new recovery tests. The copied
unchanged feature assemblies remain explicitly reused, not a fresh shipping
dependency build. F's pure-core test DLL is byte-identical to E's, but this
checkpoint does not claim a new unfiltered F execution or promote E's separate
501 synthetic fixtures into live README evidence.
The strict F seal is
`managed-checkpoint-v60-f-run2-20261002/seal.json`
(SHA-256 `484efb5ce9a1a2563b7a3abcc822b696116e40495169429569cd73bbea520bba`).
It binds exact result/definition/execution identities and all twelve distinct
public-API adapter test IDs, with 504 current source inputs and the exact 486
output partition (92 core runtime, 388 renderer runtime, six log/TRX files).
The first seal runner's fail-closed partition-count error is preserved. No E
warm probe, repetitions, package result or full-core execution is imported into
F's current-source verdict.

The fresh lean-only F package evidence is
`packages-reflow-v60-c-20261002/partial-size-evidence.json`
(SHA-256 `e27e8dd170173e61740e7a0d44751f5133bd0d5a484ab57532b91cc8675d51c1`).
Both pack commands exit zero and source hashes remain stable, but the unchanged
caps **fail**: **548,718/537,600** compressed bytes and
**1,301,504/1,258,291** managed bytes. The package runner exits one; NU1900
security-feed warnings remain visible. This supersedes E's lean size
measurement, not the full fourteen-package/security/AOT/signing gates.

Audit helper process-free unit contracts now use an injected command executor
and the emoji generator's exported validation logic. Real scoped PowerShell
cleanup and Node CLI tests remain in two explicit `*.integration.test.mjs`
files, both required by the top-500 workflow alongside the existing Edge
partition. The offline partition cannot qualify those launches or browser
rendering. The earlier failed E and stopped C receipts remain unchanged; this
separation is not a waiver or a claim that process denial has been repaired.
The fresh nine-file offline F receipt is
`readme-offline-audit-v60-f-20261002/evidence.json`
(SHA-256 `bbb81939eaad1e5883e36536d7e9ca35e9a587bef429071147ba22c174ba0d46`).
It passes **69/69** exact source-derived TAP cases, with no fail/cancel/skip/todo,
stable runner/Node/eng input hashes, pinned Node 22.22.0 and retained owned
version/test process identities and numeric exit zero. Single-process diagnostic
mode is explicit; no browser/UI or process-integration body was launched.
The two process-launch integration files and mixed Edge replay file remain
listed as required and unqualified. Unsupported capability diagnostics are
empty in this run. Earlier process-isolated EPERM failures remain preserved.

The later G offline receipt is
`readme-offline-audit-v60-g-20261002/evidence.json`
(SHA-256 `506082202638fc1f7245895ca796c21a23d02a0d1e17566b73e282823a6c8493`).
It binds the then-current argument-parser source and passes the same **69/69** exact
offline identities, with all required nonpassing counts zero, stable source/
tool hashes, and both owned Node processes' PID/start/end/numeric exit zero.
The cleanup helper no longer identifies ownership from a raw command-line
substring or whitespace regex: it parses Windows arguments, requires one exact
`--user-data-dir` value, rejects ambiguous/malformed input, frees every non-null
native argument allocation, and aborts on parser/type initialization errors.
The pure matcher passes eighteen synthetic Windows command-line cases without
querying or stopping any process. This direct matcher diagnostic does not
qualify the Node-to-PowerShell integration launch, which remains blocked by
`EPERM`, or the required browser/500-case release partitions. F remains
historical evidence rather than being overwritten by G. Additional independent
review subsequently found ownership-unknown, process-identity, case-sensitive
path and caller-cleanup gaps; G does not qualify the later helper changes.

The supplemental historical-E binder is
`MarkdownRenderer/artifacts/supplementary-v60-evidence-binder-20261002.json`
(SHA-256 `df8965430181c0be0825380c13d721105ae156b9575282d470cc1f85b93939a2`).
It independently cross-checks E's **1,692/1,692** unfiltered core replay and
managed seal, the separate **420/420** managed/no-worker provider diagnostic,
the transformed **18/18** headless WARP GIF tests, and **46/46** locked/offline
Rust cases. It preserves exact raw case identities and the two pinned display-
encoding aliases, the failed E package caps, and exactly two compiler-source
changes since E. Provider K overlaps E in only two source files and reuses E's
Core/WinUI outputs; the WARP harness deliberately requests software rendering.
These are separate diagnostic compile/execution partitions, not one shipping
graph. The binder does not import the later F seal or qualify NativeAOT,
restricted worker execution, security, same-byte Edge, top-500 or release size.

The later audit-helper review closes additional ownership and report-publication
gaps. Windows argument ownership is now tri-state: a canonical, exact single
profile argument is an owner; an unrelated argument is not; ambiguous aliases,
duplicate switches, equivalent/uncertain paths and malformed input fail closed.
Live process inventory omissions and missing command lines cannot masquerade as
successful cleanup. A stale CIM row is ignored only after its PID is absent from
the fresh inventory. Termination uses a held process handle and verified creation
time, not a reusable numeric PID. The emoji generator checks Windows file
identity rather than folding path case, preserving case-sensitive directories.

All three Edge callers now complete scoped cleanup before publishing a passing
terminal report. Graceful browser close is bounded; cleanup failures preserve
the profile and explicitly non-qualifying render evidence, fail the run, and
still attempt server closure. Reports use fresh, non-overwriting destinations.
Seven process-free cleanup contracts cover stop/remove/server failures, output
ordering, teardown propagation and invalid profile scope. The workflow retains
the real process-launch integrations and same-byte Edge partition as mandatory;
the pure contracts do not replace them.

The strict source-derived ownership proof is
`readme-profile-ownership-proof-v60-n-20261002/receipt.json`
(SHA-256 `e5272fef2bc462d1c85c7c0cbd39cc323681ddf426bdc67da430e9463b1ee280`).
It passes **69/69** synthetic pure cases: 27 argument matcher, 14 tri-state
ownership, 15 process-identity and 13 process-inventory cases. It hashes the
actual helper and integration-test source, proof template, wrapper and tools
before and after, with no drift. The extractor's owned Node process has a
captured numeric exit zero; the proof receipt explicitly records its own runner
exit as expected, not independently observed. The outer tool invocation returned
zero. No live process inventory, termination, browser or UI operation ran.
Earlier failed proof/runner preflights are preserved. This proof is not the Node
process boundary, README rendering, performance or release qualification.

The fresh, direct ten-file offline Node runner produces
`readme-offline-audit-v60-n-20261002/evidence.json`
(SHA-256 `45d6d8031f06a243b94390f9b3bdab3966f97f41aa875c47a1b5704e49b6a417`).
It passes **77/77** exact source-ordered TAP identities with zero failed,
canceled, skipped or TODO results, pinned Node 22.22.0, and captured PID,
UTC start/end, numeric exit zero and no timeout for both owned Node processes.
All 50 audit inputs and both tool/runner identities are stable before/after;
an independent recheck also matches the current inputs and raw TAP rows.
Unsupported-capability diagnostics are empty. Single-process isolation is
explicit. The two real child-process integration files and mixed Edge replay
file remain required and unqualified; their earlier `EPERM` failures are not
waived. Neither 77 unit tests nor 69 pure ownership probes are README cases.

Before the v61 edits, an independent recheck of the F seal found **zero drift across all 504
compiler inputs and 486 outputs**, including the 480 immutable runtime files.
At that recheck, only audit tooling and documentation had changed after F. PR #99 remains open at
published head `1c00c83ddfd2e8b65e05cb57e359a3f092b37271`, with 42 failed,
15 successful, two skipped and two canceled checks in the latest retrieval.
The local fixes are not thereby published or CI-qualified. The measured lean
size failures, aggregate ownership/parking and COW implementation gaps, and the
required authorized Sandbox/worker, reachable vulnerability feed, functioning
Edge oracle and full release/device/architecture runs remain open.

The next coherent SVG scheduling change must carry a monotonic demand ticket
through resolver activation, open and initial rasterization, then promote queued
work and tile work when it becomes visible. Current lookahead visibility is lost
after `StartLoad`, so open and whole-image render requests default to Visible.
Changing only those initial enums would leave overscan-to-visible queue promotion
and source-bearing semaphore waits unresolved. The provider admission/host
pressure-parking design above identifies the coupled bounded, source-free
re-demand path. The subsequent v61 implementation is integrating that coupled
path; it has not yet been compiler/test qualified and cannot close the milestone.

### October 3 v69: readiness publication outcomes and full managed app verification

The app previously discarded the readiness writer's returned `Task<bool>`.
An IO failure, canceled generation, superseded pending request or asynchronous
fault therefore could appear only as a later readiness timeout. The writer now
records fixed typed terminal outcomes (`published`, `write-failed`, `canceled`,
`suppressed`, `faulted`). Classification is captured at the actual decision:
a callback returning false remains a write failure even if cancellation arrives
at that moment, and an active write failure does not erase a later pending
same-generation success. Existing boolean/null return contracts are retained.

The bridge snapshots matching process/host/generation admission metadata from
the real same-byte source admission. It never substitutes a requested harness
digest for rendered-byte proof. Records contain bounded metadata/hashes, not
README payloads, exception strings, viewer references or dispatcher callbacks.
Serialization remains source-generated and statically discoverable.

Terminal outcomes have a separate gate, queue, task and derived
`.publication.ndjson` file. At most 32 pending records are retained; serialized
records are limited to 4 KiB. Neither that queue nor its file is awaited by the
ready-progress drain or used by the readiness validator/deadline. The harness
reads bounded, allow-listed summaries only for failure diagnostics, clears
derived files per attempt, and attempts preservation on success and failure.
That preservation is best-effort, so a diagnostic-path/copy error cannot turn
a valid signal into a failed audit or mask the original audit failure. Required
evidence snapshots still fail strictly. Their per-attempt `finally` now removes
only the generated temporary snapshot path; non-IO publication failures retain
their original exception and IO failures retain the existing retry/deadline.

Verification evidence:

- Fresh v69-E ordinary Release/x64 app tests: **3,567 results / 3,567 unique
  execution IDs, all passed, zero skipped**. Twelve new outcome Facts plus
  three new snapshot-failure cases all pass. Five existing source-contract
  scenarios were updated to verify the actual current routing, stale-generation
  and revision guards, lease ownership through awaited image copying, recovery
  timer/fallback, strict/best-effort evidence separation and post-cleanup replay
  publication. No performance/fidelity/resource ceiling was changed.
- Receipt: `artifacts/readiness-publication-v69-e-20261003/diagnostic-receipt.json`
  (15,506 bytes; SHA-256
  `f435be5e100a6c4bb99d81057e67872033ce6f3c7bebd80af9e4076c63dba750`).
  Nineteen explicitly named production/test/source-contract/project/assets/
  root-props inputs match before/after. This is not a full compiler/runtime
  dependency closure or held-source execution proof. Cached `--no-restore`
  assets retain mandatory audit settings but do not refresh vulnerability audit.
- The actual v69-C automation harness build exits zero, zero warnings/errors;
  it compiles the modified audit probe and snapshot path. Observation:
  `artifacts/readiness-publication-v69-automation-c-20261003/build-observation.json`.
  It is an ordinary managed build, not a native app/UI/AOT/release gate.
- Earlier attempts remain failed, not relabeled: A placed test output under
  nested renderer props and reported 2,375/2,832 passes; B corrected the root
  output but reported 3,558/3,563 passes with five stale assertions; C reported
  3,565/3,567 passes with two deeper stale assertions and an analyzer warning;
  D reported 3,566/3,567 passes with one remaining caller-owned image-lease
  assertion. C's post-run test-only repair occurred before its after-hash
  snapshot, so its receipt explicitly does not claim all 18 inputs stable.
  D's 18 inputs and E's 19 inputs match; E is a separate fresh run.

PR #99 remains open at published head
`1c00c83ddfd2e8b65e05cb57e359a3f092b37271` in the October 3 19:59 UTC read.
The six returned published-head runs are unchanged: preview validation and
top-500 audit failed, product performance was canceled, and the other three
passed. The published NativeAOT success does not qualify these local sources.
Local work has not been published from this read-only Git session. The current
PR observation is `artifacts/readiness-publication-v69-pr99-20261003/evidence.json`.
These new diagnostics have not been exercised on the failing rank-5 case and
do not establish its root cause or turn its missing evidence into a pass.

The aggregate residency milestone remains a real implementation gap. The
unique-entry bitmap ledger observes pixel weight through clones, eviction and
disposal; it does not deny allocation. A coherent admission transaction must
reserve output before native creation, transfer ownership into the unique
entry and retain the charge through final successful disposal (including
uncertain failed disposal). It must cover raster, SVG and tiles, release all
CPU/decode slots before a cancellable residency wait, and not classify valid
capacity-deferred content as unavailable. Waiting while retaining the previous
good bitmap can self-deadlock a full pool during replacement; transition
headroom or a bounded visible-tile/downsample strategy must be designed and
verified as part of that transaction, not added as an unconsumed ledger helper.
Dropping bytes and re-resolving cannot assume repeatability for arbitrary custom
resolvers. Renderer-owned logical pixel weight is not physical GPU/RSS or codec
scratch memory; the original measured efficiency and whole-pipeline gates stay
mandatory. No broader memory-bound claim is made by this checkpoint.

Read-only replacement-path review confirms that the current cached-raster
preview installs only when `_bitmap` is null; it is not a full-pool transition
mechanism. A candidate design must partition the unchanged logical residency
capacity into steady ownership and non-borrowable visible-transition headroom,
trim cache-only references, and generate a bounded same-content transition
preview or candidate tile before swapping/releasing the previous entry. Every
old/preview/candidate/fallback tile and COW/cache alias remains charged until
its actual last lease ends. Overscan cannot consume the transition lane ahead
of visible replacements. SVG can use the existing 1,024-physical-pixel tile
path when a monolithic target does not fit the steady share; large ordinary
rasters still need a coherent tileable or bounded progressive representation.
A preview-only/placeholder shortcut is not a fidelity pass, and subtracting
weight merely at cache eviction or `ReplaceBitmap` would be incorrect. This is
design evidence for the open transaction, not an implemented/qualified cap or
a new public API decision.

The two actual lean package-size misses, full aggregate ownership/COW work,
oversized codec/tile memory qualification, clean counterbalanced Release,
same-byte Edge and live 500-case audit, plus architecture/device/theme/DPI/AOT/
packaging gates remain open. Required execution needs a normal-token Windows
runner/session with working WinApp Sandbox broker, Edge and restricted-worker
startup, reachable audited/locked NuGet feeds, and normal Git publication.
The restricted agent session's broker authorization, worker-token/child-process,
audit-feed, task-host and Git-write failures are not product passes and are not
bypassed with host UI or in-process SVG. The heartbeat remains active; no 1.0
performance or release completion is claimed.

### October 3 v69-A renderer: bounded fallback residency and post-change app verification

The active local run continued beyond readiness diagnostics into the SVG tile
residency gap. `ImageBox.QueueVisibleSvgTiles` previously pruned the accepted
current grid, but retained its resize/theme fallback dictionary without viewport
or replacement-coverage pruning. This could retain old native tile leases
after they had stopped contributing to the displayed image.

The installed path now checks fallback geometry on the old physical-pixel grid,
clips the viewport to the destination, and keeps a 1,024-old-pixel overscan
band. Exact boundary contact remains retained, consistent with painting.
In-band tiles remain until every canonical current-grid tile covering their
entire mapped rectangle is published. Integer floor/ceiling uses safe Int64
products, including Int32.MaxValue squared; the required-key count is checked
against the resident dictionary before any grid walk. Invalid dimensions,
keys, rectangles or empty overlap fail safe by retaining the tile. This check
does not inspect lease/native values and allocates no managed objects.

Retirement lists reserve their complete capacity before the first lease removal
or queued-work state claim. Busy/terminal-failure tile owners are not canceled
by viewport pruning. Removed leases and queued cancellation requests are drained
outside the tile lock in a `finally` after the viewport gate is released,
including exceptional exits before visible-demand promotion. Native release
failure remains a retirement failure; it is not counted as freed bitmap weight.
The WinUI review influenced these ownership/exception-safety changes; no XAML,
public API or theming behavior was changed.

Thirteen new `SvgFallbackTileResidencyTests` Facts cover horizontal/vertical
overscan boundaries, off-band retirement, exact changed-DPI canonical coverage,
noncanonical keys, one-pixel partial final edges, Int32 extreme projection,
finite translated huge DIP coordinates, invalid geometry, bounded huge-grid
rejection, allocation-free warm checks and mutation/cleanup ordering. A real
no-native `QueueVisibleSvgTiles` test verifies off-band and covered removals,
uncovered in-band retention, then retirement after wider-view replacement
coverage; empty bitmap leases and preloaded current keys prevent provider/device
calls. Both its warmed queue and the pure helper measure exactly zero bytes.

Root executed the fresh helper
`MarkdownRenderer/artifacts/Invoke-SvgCoupledV69A-20261003.ps1` to observed exit
zero. All **1,242 selected Core, 1,458 renderer, 477 no-worker provider and 12
API baseline** cases pass, with zero failures/skips and the exact 1,258-case
renderer baseline plus 200 individually enumerated additions preserved. All
717 selected source identities and tools remain stable; 484 outputs and 376
exact staged-copy bindings are recorded. Seven owned parent phases and four
owned provider phases exit zero and drain. Normal renderer builds retain three
NU1900 warnings because the NuGet vulnerability service could not be reached;
the separate cached/source-linked no-worker provider continues to explicitly
disable audit. This is not a fresh license/security/AOT/package qualification.

The compiler receipt is
`MarkdownRenderer/artifacts/svg-coupled-v69-a-20261003/compiler-provenance.json`
(1,677,189 bytes, SHA-256
`b831122dbc5c9c6df63cfd83e73768b0893fc4b31535f0238a2584dfb67193b3`).
Root then actually executed the fresh seal helper to exit zero; its strict
source/output/reference/tool, restored-assets/package-root/253-SDK-file,
process-drain and positive/negative/reversed-row comparisons pass. The seal
also binds all thirteen new Fact names to results, source additions and the
25,025-byte test file SHA
`2256554aa5623a11d2e6e3bad9ccac58d9b56053d9349a9f2691ecb1384e3d44`.
The new seal is
`MarkdownRenderer/artifacts/svg-coupled-v69-a-seal-20261003/seal.json`
(1,569,659 bytes, SHA-256
`c17d1bc3cbe7ed3d98f2f83e5f791e2a6a9d63dcfd96aebf37386fe50e7593c0`).
It is a point-in-time selected-input diagnostic, not a held full compiler/SDK
closure or reproducible clean release build. Prior G4 and failed checkpoints
remain untouched. In particular, the historical 1,743-case unfiltered Core
run/reuse evidence is not newly rebound to this changed ImageBox source.

After the SVG change, root reran the full ordinary Release/x64 app test suite
using audit=true/all, no restore, isolated output/obj and cached app assets.
Attempt F passes **3,567/3,567** (zero failures/skips, 3,567 unique execution IDs),
including the same fifteen new readiness/snapshot behavior cases. All 21
explicitly named input hashes remain unchanged before/after; this does not
claim a held full app/compiler/runtime closure. Its receipt is
`artifacts/readiness-publication-v69-f-20261003/diagnostic-receipt.json`
(16,410 bytes, SHA-256
`f54bf6c198f915e1b6f6e44d699a030f02261cf0d6c86af661a3b0037b3e0c80`).
The distinct earlier E pass and A/B/C/D failures are preserved.

The fallback fix does not add the still-missing aggregate pre-allocation
reservation/preview-transition/raster-tiling architecture. It also does not
repair the last observed failed package-size gate or establish native resident
memory, device/DPI/theme behavior, browser-relative CPU/latency/fidelity, a
qualified clean counterbalanced release benchmark, or actual 500/500 same-byte
README evidence. The precise external qualification need remains a normal-token
Windows runner/session with working Sandbox broker, Edge/restricted workers,
audited NuGet access and writable clean Git provenance, plus the required
architecture/device/theme matrix. This restricted agent session's prior broker,
restricted-token, child-process, AOT task-host and network failures are not
grounds to elevate, disable audit, bypass isolation or waive release gates.
Local changes remain uncommitted/unpublished because Git is read-only here;
the heartbeat stays active and the overall performance goal remains incomplete.

### October 3 v70-C renderer: bounded same-root multi-raster publication

This local continuation extends the narrow completed-raster COW path beyond
one text run and one image. An admitted eager Body paragraph may contain up to
64 distinct completed raster placements and 256 ordinary text/image runs.
Descriptor capture checks exact run indexes, offsets, alias/style membership,
image order, block identity and duplicate ImageBox references before taking
clone leases. Unsupported runs, unresolved/active images, SVGs, changed
context/DPI/flow, embeds, deferred/vector scenes, multiple roots and lazy
layout keep the existing fallback route. No public option or API is added.

Every admitted image receives its own fresh ImageBox, cancellation/lifetime
state and explicit immutable-bitmap lease. Fresh text runs preserve link,
source and accessibility metadata without sharing mutable native text layout.
An all-or-nothing construction owner retires earlier clones if a later image
cannot be cloned; adoption occurs only after the private root owns that image.
Native measurement, arrangement, semantic nodes and image/focus indexes run
after the old snapshot lock is released. The existing exact immutable
text/grapheme projection reuse and logical generation/fingerprint fences stay
intact. The image index stores image ordinals, independently of source block
indexes or its sorted viewport slots.

UI publication now checks every weak source-image identity in order, the exact
subscription set and every captured completed-raster lease before changing
subscriptions or swapping the snapshot. Matching counts alone cannot publish
a reordered/replaced source plan. The warmed membership check measures zero
allocations. This bounds image/run descriptor work, not text length, arbitrary
alias-string length, DirectWrite cost, total retained GPU bytes or callbacks'
wall-clock latency. It does not complete general immutable COW.

The WinUI review also identified a test-fixture teardown race: transferred
source snapshots retired asynchronously, but device cleanup could precede
their native-resource drain. Fixtures now keep the transferred snapshot and
join its actual retirement before disposing the device. Existing unsupported
shape cases now use a genuinely unsupported CodeInlineRun instead of treating
another ordinary TextRun as unsupported. Those are test correctness changes,
not waived behaviors or product crash-isolation evidence.

The 24 new executed cases cover independent leases/native layouts, preserved
Unicode/link/semantic metadata, exact membership/lease replacement rejection,
image-ordinal indexing, late unsupported-image cleanup, duplicate/invalid
descriptor rejection, admission ceilings and exact maximum acceptance,
pre-cancellation and ownership drain. Eight cases simulate 100%, 125%, 150%,
175%, 200%, 225%, 300% and 400% rasterization scales with alternating LTR/RTL.
They do not establish physical monitor/device/theme/HDR behavior. Cancellation
cases use pre-canceled tokens, not an injected mid-clone cancellation race;
the publication case is source-wiring inspection, not live dispatcher/UI proof.

The first fresh v70-A attempt preserves a new-test compilation failure from a
missing Images namespace import. The subsequent v70-B attempt preserves a
provider harness preflight failure because its staged input set omitted the
required README.md. Neither receipt is a passing checkpoint or has been
rewritten. After fixing those exact issues, root executed fresh v70-C to exit
zero: **1,242 selected Core / 1,482 renderer / 477 no-worker provider**, zero
failures/skips, all **12** API baselines and the exact historical **1,258**
renderer identities plus **224** individually enumerated additions. All seven
parent and four provider phases have complete observed exit/output drains.
Three NU1900 warnings remain in the normal renderer build; the source-linked
no-worker provider remains explicitly audit-disabled and diagnostic-only.
No fresh security/license/AOT/native-worker/package qualification is inferred.

The actual compiler receipt is
`MarkdownRenderer/artifacts/svg-coupled-v70-c-20261003/compiler-provenance.json`
(1,690,125 bytes, SHA-256
`2d70bd9a77359f94e4bde19c8ce6951231d10eb412b5086b34cd8cde31889774`).
Root reviewed and executed the independent C seal helper to exit zero. It pins
the compiler/provider receipts, all 720 selected source and 484 output rows,
376 staged-copy identities, restored assets/package roots/253 SDK files and
all 24 exact new executed/source-bound names, retaining strict comparer
positive/negative/reordered-row tests and every prior caveat. The seal is
`MarkdownRenderer/artifacts/svg-coupled-v70-c-seal-20261003/seal.json`
(1,601,247 bytes, SHA-256
`c6d25066fbf02560d07b15eb88b6a44f5815d2d14d8e94fb300656f34955e857`).
This is a point-in-time selected-input diagnostic, not a held full compiler/SDK
closure. The historical 1,743-case unfiltered Core run is not newly rebound to
this changed renderer generation.

The separate actual ordinary Release/x64 app suite passes **3,567/3,567**, zero
failures/skips and 3,567 unique execution IDs. Its updated source contract binds
the bounded multi-image admission; it does not execute a native app. All 31
explicitly named relevant source/project/cached-assets inputs match before and
after. The receipt is
`artifacts/multi-raster-v70-b-20261003/diagnostic-receipt.json`
(16,053 bytes, SHA-256
`2855c7b98429ad72583061a8cc72a81c6d358194c9d8084499185b022333d0f4`).
Normal audit=true/all with existing no-restore assets is not a new security
audit; tool-session completion is not a held full app/compiler/runtime closure.

Read-only package attribution found at most 909 method-body IL bytes across
exact duplicate bodies in the existing lean binaries. That theoretical bound
cannot close the observed **45,725 compressed / 128,717 managed-byte** size
misses. Removing public XML documentation would violate the existing gate and
would still not close both misses. No package optimization or passing size
claim was fabricated from this inspection.

The canonical qualification entrypoint already exists:
`.github/workflows/markdown-renderer-physical-release.yml`. After these local
changes are reviewed, committed and pushed from a normal writable checkout,
an authenticated Actions-write operator can run:

```powershell
gh workflow run markdown-renderer-physical-release.yml --ref codex/markdown-renderer-1-0-finalize
```

It requires `MARKDOWN_RENDERER_RELEASE_REFERENCE_SHA` to resolve to the approved
full schema-12 baseline SHA, a strict ancestor of the candidate, and an online
self-hosted `Windows`, `X64`, `markdown-renderer-physical` runner in the active
signed-in Windows 11 build 26100+ console session with the foreground
coordinator. The runner label is an operator assertion of hardware identity,
not independent bare-metal attestation. The workflow invokes a fresh same-run
NativeAOT same-byte top-500 child, then joins only exact 500/500 passing/capture
counts and browser ratios with the strict source-bound R1,C1,C2,R2 verdict.
A separate top-500 dispatch is not needed; local diagnostic runs are not
substitutes for the canonical clean-source/Actions provenance. Dispatching now
would test the old pushed HEAD `1c00c83ddfd2e8b65e05cb57e359a3f092b37271`, not
these dirty changes. No workflow was dispatched or infrastructure bypassed.

Aggregate source/native/scene admission, full COW, oversized codec/tile memory,
the failed package-size ceilings and the complete live browser/device/AOT
matrix remain genuine implementation/qualification gaps. The restricted local
token/network/Git limitations remain unchanged; publication plus the eligible
normal-token runner/reference configuration are precise external prerequisites,
not grounds to lower any acceptance gate. The heartbeat remains active.

### October 3 v71-C renderer: bounded multi-root COW and streaming semantic text

The completed-raster private placement path now accepts multiple eager Body
and Heading1–6 roots, including text-only roots and ordinary links. All roots
must pass allocation-free shape/environment checks before any clone lease is
acquired. Aggregate admission is 64 roots, 256 runs, 64 distinct raster images,
64 Ki UTF-16 layout units and 128 Ki prior semantic text units. Exact-limit and
one-over cases execute; over-budget/unsupported documents retain the normal
fallback, not a content rejection. Lists, tables, code, stateful disclosures,
embeds, vector/active images and lazy layouts remain outside this fast path.

Every mutable root/run/layout/semantic node is fresh. Raw and collapsed margins,
heading metadata, link/source aliases and text-only atomic-object state are
preserved. Source shape, metadata and bitmap-lease cloning are performed under
the old snapshot gate; native measurement, arrangement, semantic construction
and image indexing are outside it. Source/candidate ownership retires
independently, with cleanup attempted for all partially built roots and actual
native drains before device disposal. Ordered source-image/lease publication
fences cover every root, and image indexing uses image ordinals rather than
block indexes. These finite input bounds do not prove bounded native layout
time or bound arbitrary source metadata/native allocations.

The semantic builder now compares fragments against the captured immutable
projection while building fresh nodes/spans. Matching text does not allocate a
duplicate StringBuilder buffer; deferred terminal newlines can be trimmed
without materializing the matching prefix. A mismatch materializes the exact
verified prefix and actual suffix. Comparison, fallback string copies and
deferred-newline copies check cancellation in chunks of at most 4,096 units.
UTF-16 fragment boundaries, late mismatch, longer/shorter output, newline
trimming, shrink/reappend and empty projections are covered. The warmed
one-Mi-unit exact-match helper test measures zero duplicate text-storage
allocation; it does not claim zero allocation for the whole semantic builder.
Exact SourceMap identity remains necessary for projection reuse.

Eager and lazy layout builders scope their effective pass cancellation through
semantic construction and restore the outer scope. A missing explicit token
retains ambient/context cancellation rather than masking it with None.
Cancellation after the last measure cannot publish a semantic snapshot, and
owned roots are retired. Thread-static scopes are disposed synchronously
before fixture awaits. No public API/option changes are required.

Verification preserves the actual failures. V71-A passed selected Core but
failed renderer compilation on duplicate contentWidth locals. V71-B then
passed the 477-row provider partition and 1,509/1,510 renderer cases; its new
aggregate-cap fixture accidentally constructed 258 runs instead of 257. The
corrected V71-C is a fresh run, not a rewritten B result. It passes **1,242
selected Core / 1,512 renderer / 477 no-worker provider**, zero failures/skips,
all **12** API baselines and exactly the historical **1,258** renderer
identities plus **254** enumerated additions. Thirty additional executed Facts
cover the current multi-root/text/cancellation changes. Seven parent and four
provider processes exit zero with complete drains. Normal renderer builds
retain three NU1900 warnings; the provider remains an explicitly audit-disabled
offline diagnostic. The current compiler receipt is
`MarkdownRenderer/artifacts/svg-coupled-v71-c-20261003/compiler-provenance.json`
(1,705,539 bytes, SHA-256
`983a8dd10e44fed8ab29fd1fb1ac73bcdfce93ee4ad8acd1669ad54bf946726f`).

The first C seal failed closed before creating its stage because a source
expectation still named the old single-root RasterPlacementImagePlan identity.
The failed helper/verdict remain unchanged. Root corrected the separate C2
helper to the actual compiler/current-source identity and exact 30 new result
names (the existing ownership-test class also has historical Facts). The C2
seal executes to exit zero and rehashes 724 selected sources, 484 outputs, 376
staged-copy bindings, provider assets/SDK/reference/tool identities, exact
result names and comparer self-tests. It explicitly retains A/B failure
evidence. The current seal is
`MarkdownRenderer/artifacts/svg-coupled-v71-c2-seal-20261003/seal.json`
(1,635,730 bytes, SHA-256
`200e9f159524826a0d6f726b42ae84e409150fce267784abc6a151421272427f`).
This is point-in-time selected-input diagnostic evidence, not held full compiler
closure or fresh unfiltered 1,743-case Core coverage.

The separate ordinary Release/x64 app suite passes **3,567/3,567** executions
with 40 named relevant inputs unchanged before/after. Its TRX has 3,567 unique
execution IDs and 3,552 definitions: three existing MemberData methods each
have six separately executed page cases. Parsing checks every execution and
definition, exact permitted page labels, and zero failure/skip counters; it
does not deduplicate invocations to manufacture a count. Initial ad-hoc parser
assumptions were corrected, not a test failure or acceptance change. Receipt:
`artifacts/multi-root-v71-c-20261003/diagnostic-receipt.json`
(24,348 bytes, SHA-256
`bd9cb74864cbe049cb6e3266eb9f98a3ff1ef416755fbb1e2c3c151b6f517756`).
The linked-source managed app tests do not execute the native app, and existing
no-restore audited assets are not a new security audit.

Aggregate memory remains a local implementation gap: cache accounting starts
after physical allocation, and live leases survive LRU eviction. A real cap
must reserve before raster/full-SVG/tiled-SVG birth, transfer one charge to the
unique physical owner, and retain it through all aliases until successful
native disposal. A denied producer must release decode/provider/source slots
before source-free retry; waiting while holding them would deadlock. Retaining
old-good images during replacement also requires explicit transition headroom
and bounded visible sizing, not a cache-only cap or temporary overrun. General
COW, codec scratch memory and failed package ceilings remain open.

The canonical source-bound physical release workflow and precise publication,
reference-SHA and interactive Win11 runner prerequisites are documented in the
preceding checkpoint. Current fixes remain dirty/unpublished; dispatching the
old PR head cannot qualify them. No Edge-relative timing/fidelity, actual full
top-500, physical DPI/device/theme/HDR, native worker, full package/security or
NativeAOT release qualification is claimed. The heartbeat remains active.

### October 3 v72-A: effective cancellation throughout lazy realization

Post-v71 review found that BuildLazyCore selected an effective cancellation
token but passed the raw argument into EnableLazyLayout. The nested viewport
measure scope could therefore push None over an inherited ambient token. The
initial pre-canceled tests did not cover cancellation triggered during measure.
Eager/lazy block construction, eager checkpoints, the final construction/measure
checks and initial realization now receive the effective token. Direct internal
EnsureMeasuredBand likewise preserves a cancelable explicit token, otherwise
inherits the active pass token, and passes it through every measure checkpoint
and native scene residency preparation. Public API shapes are unchanged.

Three new executed Facts cover eager measure, initial lazy realization, and
direct lazy realization of initially unmeasured roots. Cancellation triggered
by the first measure stops the second; exact token identity and outer
ThreadStatic restoration are checked. Every new fixture ends its synchronous
scope before awaits and uses nested finally cleanup so context drains still
run if snapshot retirement fails. The independent COW review found no further
ownership/fence defect in the bounded multi-root path; its supported and
fallback shapes remain as described above.

Root executed fresh V72-A to exit zero: **1,242 selected Core / 1,515 renderer /
477 no-worker provider**, zero failures/skips, all **12** API baselines, the
historical **1,258** exact renderer identities and **257** enumerated additions.
All seven parent and four provider processes exit zero with complete drains.
The independent seal rehashes 724 selected sources, 484 outputs and 376 staged
copies. It binds the prior V71-C2 seal, all three changed cancellation source
identities, and 714 unchanged selected non-artifact inputs. The 33 exact
multi-root/semantic/ownership Facts include the three new cancellation cases;
historical ownership Facts are not mistakenly counted as new. Retained v71-A/B
and first-C-seal failures remain unchanged. Compiler receipt:
`MarkdownRenderer/artifacts/svg-coupled-v72-a-20261003/compiler-provenance.json`
(1,706,948 bytes, SHA-256
`edabadf3571a7fe7b8df37996adfcee1fcdc1efa50cd7d02cdbbdcf8faf1c111`).
Current seal:
`MarkdownRenderer/artifacts/svg-coupled-v72-a-seal-20261003/seal.json`
(1,640,634 bytes, SHA-256
`646f5d2d32a9469e85a4245efa8a726062cb7aab395f08cba2173ab24a35ef46`).
Three normal NU1900 warnings and the explicitly audit-disabled no-worker
provider diagnostic caveat remain. This is not a full compiler closure,
unfiltered Core rerun or native worker/browser qualification.

The separate ordinary Release/x64 app execution passes **3,567/3,567**, zero
failures/skips and forty named inputs unchanged before/after and at verification.
A strict DTD-disabled TRX verifier checks 3,567 unique execution IDs, all 3,552
definitions executed, each definition's execution ID bound to an actual result,
and the exact six page cases for each of the three existing shared MemberData
definitions. Receipt:
`artifacts/cancellation-v72-a-20261003/diagnostic-receipt.json`
(8,656 bytes, SHA-256
`05412aa836ae0e5c22108cefd7758c586ff785dff3594584f7a77de7defb9e71`).
Its linked-source/stub tests do not launch the native app; no-restore audited
assets do not freshly qualify security. All original aggregate memory, general
COW, oversized codec/tile memory, package-size, same-byte Edge/top-500,
counterbalanced timing and physical device/architecture gates remain open.

### October 3 v72-C: current default-Release package footprint

The former v68-I footprint is historical; the v71 diagnostic compiler DLL
lengths were only projections, not fresh default-Release package evidence.
Root now executed a fresh source-bound pack of Core and WinUI with the exact
V72-A passing managed seal pinned by length and SHA-256. This uses ordinary
Release/x64 selection, AnyCPU library targets, isolated intermediate/output
roots, diagnostics explicitly disabled, retained public XML documentation and
NuGet audit enabled for all packages. Cached no-restore inputs are recorded;
NU1900 vulnerability-service failures are retained and prevent fresh security
qualification. No production source, feature, API, documentation requirement
or size ceiling was removed to obtain this observation.

Preparation review corrected literal-dollar regular-expression escaping,
comparison of the deliberately different before/after observation labels, and
case-sensitive Boolean acceptance. The snapshot comparison excludes only the
validated phase label; all reported properties/import identities still match.
Read-only function probes check the actual 537,600 / 1,258,291-byte caps,
changed-property detection, phase/duplicate rejection and strict Boolean
acceptance. Executed attempt A then fails before child builds because its
PowerShell path query returns multiple applications. Fresh B pins the actual
running interpreter and builds both packages, but the documentation assertion
mistakes a single unwrapped dictionary's field count for entry cardinality.
Both XML files are actually present. Fresh C preserves a real array for that
check and completes all before/after checks. A/B helpers and failure receipts
remain unchanged; neither is a completed footprint qualification.

Root observed fresh C's outer **exit 1** for the unchanged size gate. All seven
owned child phases exit zero with complete cleanup and stdout/stderr drains.
The exact current packages total **588,498 compressed bytes**, exceeding
**537,600 by 50,898**, and **1,400,832 managed bytes**, exceeding **1,258,291 by
142,541**. Core is 173,568 managed bytes and WinUI is 1,227,264. Both required
public XML documents remain present; native entries are zero. The 227 source
and configuration rows stay stable before/after; the 215 Core/WinUI project
rows exactly match the seal. Seven external pack inputs also appear in its
724-row compiler source scope. All 484 linked compiler output identities stay
unchanged, as do cached reference/assets, reported imports, tools and helpers.

An independent read-only review rehashes the source/tool/compiler/log identities
and both archives. All 19 archive entries match their names, uncompressed and
compressed lengths and SHA-256; the totals and negative gate verdict match.
Footprint receipt:
`MarkdownRenderer/artifacts/packages-reflow-v72-c-20261003/footprint-evidence.json`
(572,437 bytes, SHA-256
`06cc58e01b4ea99c35bb66c0a71d04084e9559709d12d25736f852d793f6075f`).
Executed helper:
`MarkdownRenderer/artifacts/Measure-LeanSourceV72C-20261003.ps1`
(56,159 bytes, SHA-256
`ddbec221dd764e4d45b785003b34f455453d32644750da4a7d77d8d9c113fefc`).
Root's observed A/B/C tool completions are retained in
`MarkdownRenderer/artifacts/packages-reflow-v72-c-20261003/root-observed-execution.json`.
The MSBuild-reported import set is not a complete SDK/compiler/reference-pack
closure, local archive sidecars do not establish authenticity, and this
two-package diagnostic does not execute the full package/license/AOT gate.
Every release, worker, Edge, top-500, memory and UI/device qualification remains
false. Package reduction is still a real local implementation milestone;
removing documentation or increasing the ceilings is not an accepted fix.

## Summary

Close the measurable gap across parsing, layout, images, SVG, math, Mermaid,
highlighting, and scrolling—not just image downloads. Image prefetch now starts
after parsing and alongside layout, ordinary rasters use display-sized decode,
and geometry-changing image reflow is prepared off the UI thread. Remaining
gaps include qualifying source-byte admission under live memory storms,
oversized raster tiling, aggregate graph ownership beyond bounded completed-scene
cache retention, integrated
live qualification of inline-Math deferral, copy-on-write layout publication, and qualified browser-relative
performance/fidelity results for the progressive pipeline.

The new pipeline is opt-in for library consumers and enabled by JitHub. Browser
comparisons will separate GitHub’s server/CDN advantage from work both renderers
perform on the client. No slow case will be dismissed merely because the overall
top-500 median looks good.

## API and ownership

- Add `MarkdownRenderer.Performance.MarkdownPerformanceSession`, constructed
  with immutable `MarkdownPerformanceOptions.Progressive`. Expose a
  `PerformanceSession` property on the renderer controls and
  `WithPerformanceSession(session)` on the builder. Controls borrow the session;
  the host disposes it.
- The options independently select progressive whole-document source prefetch,
  display-sized raster decoding, ahead-of-viewport scene preparation, and
  cached-preview-then-upgrade behavior. They also set lookahead and lowerable
  concurrency, memory, and pixel ceilings. A null session preserves existing
  behavior, including the existing explicit `IMarkdownImagePrefetcher`
  integration.
- Use a two-viewport lookahead and one-viewport trailing band. Default maximums
  are 16 image fetches with four slots reserved for visible work, two CPU
  preparations, 64 MiB source retention, 64 MiB device-bitmap retention, and
  64 MiB in-flight source bytes on x64/ARM64; halve those limits on x86. The
  existing hard image/SVG safety ceilings remain hard ceilings—options cannot
  raise them. Energy Saver and metered connections pause speculative work, not
  policy-permitted visible work.
- Expose `Trim()` and a privacy-safe `GetSnapshot()` (queue depth, cache
  bytes/hits, stage timings, cancellations; no URLs or image bytes). A session
  belongs to one security/account partition. JitHub owns one shared session in
  `JitHubMarkdownRuntime`, passes it to both viewport modes, and recreates it on
  account change.
- Keep the public `MarkdownEngine.ParseAsync` eager and compatible. The opt-in
  control path uses an internal lightweight presentation work plan so expensive
  built-in Math/Mermaid scenes can be prepared later; its generation-scoped lease
  keeps engine-owned extension services alive until work is canceled or
  completed. No reflection or provider discovery is added, preserving NativeAOT.

## Pipeline and rendering changes

- Produce one admitted resource index while parsing Markdown and safe HTML;
  remove the silent 2,048-source prefetch cutoff. Begin safe source requests
  immediately after parsing, concurrently with layout, rather than after first
  render. Initially prioritize likely first-screen resources, then use exact
  layout visibility. Queue order is visible, nearby, then the rest of the
  document under idle capacity; deep jumps preempt stale work. Downloads,
  decodes, and scenes are single-flight and fair across active documents.
- Route preparation and visible `ImageBox` loads through the same resource store
  so prefetched bytes and prepared bitmaps are actually reused. Cache keys
  include resolver/context identity before resolution and the host’s partitioned
  asset key afterward; assets without a safe shared key remain document-local.
  Preserve all existing authorization, external-image, offline, and nested-SVG
  restrictions. Budget eviction never turns a valid image into “unavailable.”
- Decode static raster images to
  `ceil(display DIPs × XamlRoot.RasterizationScale)`, bounded by source
  dimensions and the safety ceiling, rather than routinely uploading the
  original pixel dimensions. Use Windows’ transformed decode with explicit EXIF
  orientation and color handling; test actual codec memory behavior rather than
  assuming every codec scales during decode. [Microsoft documents the
  transformed decode API](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.bitmapdecoder)
  and [its scale/crop ordering](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.bitmaptransform).
  For very tall or oversized content, prepare visible tiles or a bounded static
  preview. Reuse an already cached lower-resolution image immediately, then
  replace it at screen resolution without changing layout. Retain original
  source bytes under the host’s source-cache policy, not as a full-resolution
  GPU bitmap.
- Keep SVG parsing/rasterization in the existing isolated resvg worker. Prepare
  near-viewport SVG documents and exact-DPI rasters without creating offscreen
  UI elements; retain the prior good bitmap during DPI, theme, resize, or device
  transitions. Deduplicate by content, partition, device, physical size, and
  only relevant theme inputs. Dispose CPU pixels after GPU upload.
- Defer offscreen Math/Mermaid compilation and code highlighting through the
  same priority scheduler. Visible content gets priority; a rapid jump can
  temporarily show readable source/alt content, never a false failure, until
  its scene is ready. Keep diagnostics, selection, UIA, and exact source
  fallback intact. Hosted controls and task checkboxes are never speculatively
  instantiated.
- Reserve image geometry from authored dimensions or asynchronously read
  intrinsic headers. If geometry genuinely changes, build a copy-on-write
  dirty-block layout off the UI thread and atomically publish one coalesced
  revision while preserving the scroll anchor. Bitmap upgrades with unchanged
  geometry are paint-only. Make lazy-layout lookahead adaptive to viewport and
  scroll velocity, without realizing the whole document or invalidating
  document tiles for resource completion.

## Verification and release gates

- Instrument parse, extension work, initial layout, first paint, fetch
  queue/network, decode/preflight/worker, GPU upload, dirty-block reflow, actual
  scroll work, and audit-harness waiting. Reclassify the known `awesome-mac`,
  `gin`, and `minio` outliers from traces before attributing them to images or
  accepting a fix.
- Qualify the same-byte first-viewport comparison at a matched source-available
  boundary. Native records a monotonic tick immediately before assigning the
  exact decoded Markdown string; its raw README SHA-256/byte size, separate
  decoded-string SHA-256/UTF-8 size, process, host, and renderer generation
  must bind to the matching render-complete and viewport-paint-ready evidence.
  Edge verifies raw bytes, decodes with matching BOM semantics, hashes the
  decoded string, then starts its monotonic client-render clock immediately
  before parsing. The first-render ratio uses only these source-admitted
  intervals; host-ready-to-render and total app-experience timing remain
  diagnostics. Missing or mismatched identity/generation evidence fails closed.
- September 28 local source-admission contract verification: the focused x64
  Release native-readiness and audit-source-contract tests pass 65/65 from an
  isolated artifacts directory. The PowerShell top-500 merge/mutation suite
  passes, including source-admitted ratio recomputation and the unchanged 110%
  sentinel. These validate measurement identity, diagnostics, and gate
  enforcement only; they are not a renderer-speed result or a top-500 audit
  pass. The focused real-Edge Node suite passes 15/15; the long-running Release
  Core external-process fixture passes 485/485 after 1h30. The unchanged strict
  package-size gate passes for the freshly packed default Core+WinUI pair:
  1,257,984/1,258,291 managed bytes (307 bytes headroom) and
  531,108/537,600 compressed bytes. Detailed CanvasTextLayout/LayoutBuilder
  diagnostics are compile-time opt-in via `EnableMarkdownLayoutDiagnostics`
  and absent from the default shipping build; the cap and source-bound first-
  render p95 gate remain unchanged and unclaimed. The same focused tests also
  require a 100%-position sample after an extent change to be confirmed by a
  later stable image-ready pass. Rank 140's overlap-correction cause remains
  unproven; correction exhaustion now preserves only numeric viewport geometry
  and last-scroll diagnostics, while the hard traversal failure remains.
- Replay pinned Markdown and identical local asset bytes in Edge and JitHub to
  measure client rendering fairly; report live network/CDN time separately. The
  top-500 live audit must remain 500/500 with no renderer exception or valid
  asset marked unavailable. Same-byte first-viewport and full-traversal p95
  must be within 110% of Edge, with each previously slow outlier resolved or
  demonstrated to be external delivery rather than native work. Preserve the
  stricter existing per-SVG fidelity and latency gates.
- Extend the 100 KiB/1 MiB/10 MiB and 120 Hz harnesses with 1,800-image storms,
  giant-but-displayed-small rasters, deep jumps, narrow tables, many
  Math/Mermaid blocks, mixed HTML, rapid navigation, memory pressure,
  cancellation, and device loss. Require scroll-frame p95 below 8.33 ms,
  maximum below 16.67 ms, UI publication at most 2 ms, no synchronous image
  relayout on the UI thread, and no offscreen UI realization. Cache hits must
  avoid worker requests.
- Run cold/warm, x86/x64/ARM64, hardware/WARP, mixed-DPI and 100–400% scale,
  Light/Dark/High Contrast, Energy Saver, metered/offline, both viewport modes,
  and NativeAOT/trimming tests. Check sustained CPU and incremental memory
  against the same-byte Edge workload, cache ceilings, handle growth,
  accessibility, visual fidelity, and source-policy behavior. Make the
  interactive performance runner a required release gate rather than treating
  a queued run as a pass.

## Assumptions

“Progressive whole document” means spare capacity may prepare distant sources,
but it does not promise retaining every one of thousands of images or scenes
simultaneously. Visible work always preempts it. The cached-preview choice
applies only when a lower-resolution result already exists; the renderer will
not perform an extra decode solely to manufacture a preview. JitHub’s
configured image policy remains authoritative, and GitHub’s server-side
rendering/CDN advantages are measured separately rather than used as blanket
waivers for native stalls.
