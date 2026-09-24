# Opt-in progressive performance pipeline for MarkdownRenderer

Status: in progress. The opt-in image-source, bounded raster decode, directional
lookahead, cached raster previews, background image reflow, and JitHub session
wiring are implemented. The scene scheduler, copy-on-write layout publication,
expanded corpus, and browser-relative release gates below remain work to
complete before claiming this plan or the 1.0 performance goal is met.

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
  runs after this change; hosted validation remains pending.
- Measured: the full x64 repeated-construction release benchmark is not yet
  qualified. Both reference and candidate failed its stationarity contract;
  the candidate's full run dropped to about 121 observed Hz on a configured
  240 Hz display and exceeded the 1 MiB first-viewport budget. A controlled
  12,000-frame scroll run with fewer first-viewport constructions sustained
  about 240 Hz at 4.46 ms frame p95, while a 20-presentation diagnostic
  allocated about 28-35 MiB per 1 MiB presentation. Reduce construction and
  native-object/GC pressure, then rerun the frozen full counterbalanced gate.
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
- Open: actual source-byte in-flight admission (a host resolver currently owns
  its download buffer). It needs priority-aware byte admission across the
  renderer session and JitHub's HTTP/authenticated fetches, not a per-request
  semaphore: the authenticated Git LFS fallback calls the same image service
  recursively, so holding a whole-request permit can deadlock. Global scene
  queue fairness across documents and copy-on-write layout publication with a
  measured ≤2 ms UI commit also remain open.
- Open: oversized raster tiling and session-owned SVG/document/GPU preparation
  caches.
- Open: defer Math/Mermaid scenes and ahead-of-viewport highlighting without
  changing public eager `ParseAsync` semantics or losing diagnostics/UIA.
- Open: expanded deterministic corpus and live top-500/Edge same-byte release
  gates, device/theme/DPI and x86/ARM64 NativeAOT runtime matrices. The live
  audit requires the read-only GitHub audit credentials specified by its runner.

## Summary

Close the measurable gap across parsing, layout, images, SVG, math, Mermaid,
highlighting, and scrolling—not just image downloads. Image prefetch now starts
after parsing and alongside layout, ordinary rasters use display-sized decode,
and geometry-changing image reflow is prepared off the UI thread. Remaining
gaps include in-flight source-byte admission, oversized raster tiling, bounded
scene preparation beyond code highlighting, copy-on-write layout publication,
and eager Math/Mermaid compilation far below the viewport.

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
