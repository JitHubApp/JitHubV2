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
  from the successful-attempt browser comparison. This does not waive a
  second timeout or the unresolved rank-401 native SVG failure. The full
  current-head 500-case audit must pass again.
- Measured: the full x64 repeated-construction release benchmark is not yet
  qualified. Both reference and candidate failed its stationarity contract;
  the candidate's full run dropped to about 121 observed Hz on a configured
  240 Hz display and exceeded the 1 MiB first-viewport budget. A controlled
  12,000-frame scroll run with fewer first-viewport constructions sustained
  about 240 Hz at 4.46 ms frame p95, while a 20-presentation diagnostic
  allocated about 28-35 MiB per 1 MiB presentation. Reduce construction and
  native-object/GC pressure, then rerun the frozen full counterbalanced gate.
- Open: actual source-byte in-flight admission (a host resolver currently owns
  its download buffer), global scene queue fairness across documents,
  and copy-on-write layout publication with a measured ≤2 ms UI commit.
- Open: oversized raster tiling and session-owned SVG/document/GPU preparation
  caches.
- Open: defer Math/Mermaid scenes and ahead-of-viewport highlighting without
  changing public eager `ParseAsync` semantics or losing diagnostics/UIA.
- Open: expanded deterministic corpus and live top-500/Edge same-byte release
  gates, device/theme/DPI and x86/ARM64 NativeAOT runtime matrices. The live
  audit requires the read-only GitHub audit credentials specified by its runner.

## Summary

Close the measurable gap across parsing, layout, images, SVG, math, Mermaid,
highlighting, and scrolling—not just image downloads. The current code already
has background work and caches, but image prefetch starts only after render
completion, ordinary raster images can be decoded at full source resolution,
image completion can synchronously relayout blocks on the UI thread, and
Math/Mermaid compilation is eager even for content far below the viewport.

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
