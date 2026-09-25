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
- Open: oversized raster tiling and session-owned SVG/document/GPU preparation
  caches.
- Open: defer Math/Mermaid scenes and ahead-of-viewport highlighting without
  changing public eager `ParseAsync` semantics or losing diagnostics/UIA.
- Open: expanded deterministic corpus and live top-500/Edge same-byte release
  gates, device/theme/DPI and x86/ARM64 NativeAOT runtime matrices. The live
  audit requires the read-only GitHub audit credentials specified by its runner.
- The separate product live-performance jobs for PR #99 remain queued on the
  `self-hosted, Windows, X64, jithub-interactive` runner label set after their
  contract jobs passed. The current GitHub credential receives HTTP 403 from
  the repository runner-status API, so availability cannot be verified from
  this task. An online, unlocked interactive runner with those labels is
  required; queued jobs are not a performance pass.

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
