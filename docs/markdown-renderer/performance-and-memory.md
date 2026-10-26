# Performance and memory

Immutable engines and documents separate parse cost from view realization. Reuse
a `MarkdownEngine` and assign a previously parsed `MarkdownDocument` when the
same content appears in more than one view.
Dispose every ordinary engine at the end of that reuse scope. Reusing a builder
or frozen extension set is safe: owned feature services are created independently
for each engine rather than leased from a one-shot shared instance.

## Parse behavior

`MarkdownEngine.ParseAsync` is cancellation-aware, deduplicates concurrent work
for the same source, and retains completed documents within its configured byte
budget. Set `WithParseCacheBudgetBytes(0)` to disable completed-document caching
while preserving in-flight deduplication.
The completed-cache path remembers the most recently resolved source identity,
so repeated assignment of the same immutable string does not re-hash the whole
source. That identity is weak and its document pointer is cleared with the
bounded cache entry, so the shortcut cannot retain a second large source or
escape eviction/disposal semantics. Equal-but-distinct strings still use ordinal
content-keyed deduplication. An engine with completed caching disabled skips the
identity probe entirely, avoiding a redundant serialized gate acquisition on
cache-disabled and concurrent miss workloads.

Unique cache misses also pass through `MarkdownParseLimits`: maximum source
length, concurrent parse count, outstanding parse count, and total outstanding
UTF-16 source bytes are all bounded. Configure stricter ceilings with
`WithParseLimits`. A request that exceeds the source ceiling faults with
`ArgumentOutOfRangeException`; a new unique request arriving when admission is
full faults with `InvalidOperationException` and can be retried after existing
work completes. A request for an already admitted source still joins that work.

Markdig's syntax-tree construction is synchronous and cannot be interrupted in
the middle of its parse. Cancellation remains prompt for queued work and async
extension processing; hard source and admission limits bound the non-preemptible
phase.

### Release-run foreground and visibility qualification

Release evidence is eligible only when the benchmark window remains the
foreground, visible, non-minimized, non-cloaked, within the monitor work area,
and unobstructed for the full measured run. The harness polls these conditions
every 250 ms and records foreground, desktop-switch, and relevant window
visibility events. WinEvent hooks run on a dedicated message-pump thread, not
the WinUI thread, so window enumeration and DWM queries cannot become measured
UI work. Missing hooks, capture failures, a sampling gap over one second, or
any observed focus/visibility loss rejects the run; latency and refresh-rate
thresholds are unchanged, and no trial is retried. Window normalization and
runtime warmup happen before this interval; report writing and renderer cleanup
happen after it. Serialized evidence contains only bounded sample-source labels,
monotonic timings, counters, and visibility booleans—never HWND values, window
titles, executable paths, or pixels.

Schema-12 release evidence uses three excluded, raw 100-sample warmup trials and
six ordered 100-sample measured trials for each first-viewport scenario, five
ordered 2,400-frame warm-scroll trials, and five ordered
40-sample cancellation trials. Each cancellation trial starts with a full
managed collection and one input-distinct, unrecorded warmup supersession. The
warmup materializes scenario-specific JIT, event, cancellation, and teardown
paths without warming an evidence input's document cache. Every warmup and each
recorded trial p95 must independently remain within the 16 ms cancellation
budget with no stale commit. Recorded source seeds are distinct across all five
trials.

With the nearest-rank calculation, each 100-sample first-viewport trial p95
retains five observations above the selected order statistic, each 40-sample
cancellation trial p95 retains two, and each 2,400-frame scroll-trial p99 retains
24. The 600 measured viewport samples per condition, 12,000 scroll frames, and
200 cancellation samples
are also pooled for their unchanged absolute gates. Cross-revision comparison
uses the one-sample Hodges-Lehmann location of the trial percentiles. First
viewport uses the median of 21 Walsh averages from six measured trials; the
five-trial metrics use 15. Trials cannot be omitted, trimmed, adaptively stopped,
or retried selectively.

Schema 12 retains schema 11's consistency-scaled median absolute deviation for every
comparison population. First-viewport eligibility additionally uses the ordered
Theil-Sen slope over each condition's actual global trial ordinals, projects it
across the measured ordinal span, and compares the last two warmup-trial centers
with the first two measured-trial centers. Its allowance is the larger of 5% of
the Hodges-Lehmann location and the scenario's frozen noise floor. Every measured
trial p95 and each of the four warmup-boundary endpoints must also remain within
that allowance of the robust location; pooled percentiles and robust estimators
therefore cannot hide one catastrophic trial. A failed dispersion, trend,
boundary, or residual-envelope check makes the evidence ineligible and the
comparison inconclusive; it is never relabeled as a product regression.
Evidence also becomes inconclusive when robust dispersion exceeds 25% for scroll UI
work, cancellation, or renderer-owned allocation; 35% for scroll frame time; or
20% for source lookup. A stable comparison passes when its absolute increase is
no greater than the larger of the frozen relative allowance and metric-specific
noise floor. The relative limits remain 5% for latency and 2% for allocation.
The frozen absolute floors are:

| Comparison metric | Noise floor |
| --- | ---: |
| 100 KiB first viewport, cache-disabled / cache-hit | 5 ms / 3 ms |
| 1 MiB first viewport, cache-disabled / cache-hit | 5 ms / 5 ms |
| 10 MiB first viewport, cache-disabled / cache-hit | 3 ms / 1 ms |
| Scroll UI-thread work p95 / p99 | 0.05 ms / 0.10 ms |
| Scroll frame-time p95 | 2 ms |
| Source lookup | 30 ns |
| Cancellation | 0.05 ms |
| Renderer-owned allocation | 64 bytes |

These floors apply only to relative regression decisions. All first-viewport,
scroll, allocation, lookup, cancellation, lifecycle, and retained-memory
absolute gates remain mandatory and cannot be relaxed by the hybrid allowance.
Before every scroll trial, the harness observes the viewport at offset zero,
waits for lazy layout to finish, and drains retired snapshots. It repeats the
pending-work and drain sequence after forced GC and immediately before capture;
a timeout fails the declared trial instead of substituting another run.

Every first-viewport warmup and measured trial has the same non-adaptive shape:
a fresh engine, an optional cache-hit prime and identity-hit verification, a full
managed collection, one unrecorded settling presentation, and then 100 recorded
presentations. The renderer is constructed with the real source, avoiding an
interleaved empty-document parse. Cache-disabled trials must retain zero completed
parses and hash once per settling/recorded presentation; cache-hit trials must
retain exactly one completed parse and one source hash throughout. Those counters,
raw warmup and measured samples, timestamps, settling duration, collection deltas,
and process allocation deltas are serialized and independently checked. Evidence
also pairs every recorded first viewport with its generation/source-matched
UI-publication duration. The in-process and external gates independently
recompute each measured trial and scenario maximum and require it to be at most
2 ms; quick mode validates the same structure without applying that budget.
timestamps come from one UTC anchor plus a monotonic clock, every trial has a
positive duration inside the report interval, and collection deltas must satisfy
the physically possible `Gen0 >= Gen1 >= Gen2` hierarchy.

The six conditions run in a frozen six-by-six Williams design: each condition
occupies every period once and every directed first-order carryover appears once.
The entire first-viewport phase runs on the lowest processor allowed to the UI
thread at `Normal` priority, with affinity/group/processor evidence serialized and
required to name an active native Windows processor and match across candidate
and reference. This prevents hybrid-core
scheduling and fixed condition order from masquerading as a build effect.

Each scroll trial serializes the stopwatch frequency and raw UI-thread-work
ticks, frame-interval ticks, and renderer-owned allocation samples. The harness
and external gate independently recompute the trial p95/p99 values, pooled
absolute values, Hodges-Lehmann estimates, and robust dispersion rather than
trusting summary fields alone.

The 100,000-entry source-map check retains 20,000 individually timed lookups for
the unchanged 25 microsecond absolute p95. Its cross-revision estimator separately
uses five fixed trials of 128 observations, where every observation contains
4,096 lookups, after eight fixed full-sequence warmup passes. During this
microbenchmark only, the UI thread is pinned to the lowest processor allowed by
the process and raised to `Highest`; the original affinity and priority are
restored afterward. The affinity mask, processor group/number, priority, clock
frequency, raw elapsed ticks, and normalized samples are serialized. Candidate
and reference affinity metadata must match, and the external gate recomputes
every trial p95, the Hodges-Lehmann estimate, and robust dispersion. Both measured
lookup scopes must allocate zero managed bytes.

Quick mode serializes its own frozen requirements: one trial-shaped single-sample
warmup and one measured trial in the first Williams row for each of six
first-viewport conditions, one 30-frame scroll trial, eight lifecycle
iterations, and one cancellation trial containing one recorded supersession.
It structurally validates those results plus all three retained-memory scenarios,
raw scroll evidence, both raw source-lookup sample populations and their tick
normalization, and provider/render failures. It does not apply release latency,
memory, refresh-rate, or relative-regression budgets, but any missing, malformed,
or failed scenario returns a nonzero exit code.

Release evidence is schema 12 and binds the complete private runtime output with
the canonical `runtime-output-manifest-v1` digest. The executable, runtimeconfig,
harness, renderer, and core assemblies retain separate SHA-256 entries for direct
review, and a candidate also binds the exact reference-report bytes. Candidate and
reference must match the configured refresh rate, while
each report independently qualifies its observed rate as at least the greater
of 115 Hz and 95% of the configured rate. Observed-rate jitter between otherwise
qualified reports is not treated as a machine-identity change.

The harness runtimeconfig explicitly disables tiered compilation, tiered PGO,
concurrent GC, and ReadyToRun. Any `DOTNET_` or `COMPlus_` environment override
makes the report ineligible; the runtimeconfig itself is part of the artifact hash
set. The retained schema-10
deployment-bound quick smoke (local run artifact `performance-quick-schema10-deployment-bound-r3-20260910.json`)
passes the structural, cache, runtime, monotonic-timestamp, and topology contract
but is explicitly non-gating.
The retained schema-8
baseline (local run artifact `performance-baseline-optimized-r7-schema8-20260910.json`),
candidate A (local run artifact `performance-candidate-a-optimized-r7-schema8-20260910.json`),
and candidate B (local run artifact `performance-candidate-b-optimized-r7-schema8-20260910.json`)
are methodology evidence. The later retained schema-9
baseline (local run artifact `performance-baseline-optimized-r8-schema9-20260910.json`),
candidate A (local run artifact `performance-candidate-a-optimized-r8-schema9-20260910.json`),
and candidate B (local run artifact `performance-candidate-b-optimized-r8-schema9-20260910.json`)
proved that order-invariant Hodges-Lehmann/MAD checks could accept a settling
trend; candidate B failed the warm 10 MiB comparison. All are preserved as
methodology evidence, not schema-10 release evidence.

A first full schema-10
absolute baseline (local run artifact `performance-baseline-schema10-deployment-bound-r1-20260910.json`)
is retained with a valid deployment manifest and frozen runtime evidence, but it
failed viewport stationarity, the 1 MiB cache-disabled absolute budget, warm-scroll
cadence/stall, and refresh qualification (240 Hz configured, 117.72 Hz observed).
It is failed diagnostic evidence and cannot serve as a passing reference. A passing
same-machine baseline/candidate and true cross-revision claim still require
separately built reference and candidate revisions on the same qualified machine.
The external counterbalanced runner uses
the fixed `R1, C1, C2, R2` sequence. Its sole relative verdict is the ABBA contrast
`((C1 + C2) - (R1 + R2)) / 2`, which cancels additive linear drift; pair deltas,
role spans, and the inferred drift remain diagnostics rather than hidden gates.
Four runs cannot distinguish a treatment effect from symmetric nonlinear drift,
so representative release evidence and review are still required.

Every output path must be new, cannot alias the reference under Windows path
semantics, and is opened with `CreateNew`, so no normal, fatal, or startup path can
overwrite prior evidence. Output and reference reports must remain outside the
measured deployment directory. Reference-report SHA-256 authenticates the
reference's stored artifact metadata; the current report's five critical artifacts
and complete runtime-output manifest are recomputed from disk. Equal manifest
identities must agree on all five explicit hashes. Different manifest identities
may legitimately share those hashes when another managed, native, WinRT, XBF, PRI,
dependency, or host file changed.

## View behavior

The native views build and paint around the effective viewport, cancel superseded
work, lazy-load images, and virtualize hosted WinUI elements. Use
`MarkdownDocumentView` when a page already owns scrolling so layout and
realization observe the page's viewport instead of creating a nested one.

Applications should avoid repeatedly assigning equivalent source, performing
blocking work in host services, or returning heavyweight hosted elements for
large repeated sets. Hosted-element creation is asynchronous and
cancellation-aware; `Recycle` should release app-owned handlers and state.

The opt-in `MarkdownRenderer.Performance` pack supplies
`MarkdownPerformanceSession`. It starts admitted image-source requests
after parsing while layout proceeds, shares source bytes within one security
partition, and keeps unkeyed assets local to a document. Its source LRU is
bounded to 64 MiB on x64/ARM64 and 32 MiB on x86 by default. Visible requests
have reserved fetch capacity and can supersede a queued speculative request.
Source-cache bookkeeping has a separate conservative 4 MiB x64/ARM64 or 2 MiB
x86 ceiling, including retained keys, MIME/URI metadata and document context.
Tiny sources cannot bypass that ceiling; zero source budget disables retention
even for empty assets. Oversized metadata is simply not cached: authoritative
resolution and rendering are unchanged. `SourceCacheBytes` reports source payload
bytes, not this separate bookkeeping estimate. Trim and final unkeyed-owner
release replace empty backing collections so high-water capacity is reclaimable.
Shared cache keys preserve resolver reference identity through a weak-key identity
table; cached sources do not keep a retired host resolver or its plugin state alive.
The session also bounds concurrent static-raster and SVG host preparation,
including provider sanitization and host preflight/hash work. SVG CPU slots end
before provider/worker waits; they limit simultaneous preparation, not aggregate
provider snapshots or sanitizer DOM memory. Session retirement cancels the SVG
operation through its actual drain without treating cancellation as unavailable
content or a provider-generation retry. Static rasters decode to the
displayed physical size (at most 8.4 million output pixels on x64/ARM64 or
4.2 million on x86 by default), and records aggregate privacy-safe counters. JitHub
enables this session for its Markdown views. This does not yet constitute the
complete browser-relative performance gate, which remains a separate 1.0
release gate.

Lease-aware resolvers can implement `IMarkdownLeasedImageResolver` from the optional
Performance pack. Reserve source capacity before materialization and transfer it
with `MarkdownImageResolutionLease`; keep a retained handle for each simultaneous
consumer and dispose every handle deterministically. The renderer carries these
handles through shared fetches, decode admission and actual source use. Fetch
slots end at resolution, not at decode completion. Completed fan-out tasks store
only a completion signal, so they do not pin released arrays. Cache hits wait for
admission using scalar length/generation metadata, then verify the entry before
exposing its bytes. A source lease also participates in session shutdown until
its final disposal.

Resolvers can additionally use the scoped `IMarkdownImageSourceWorkTracker`
capability to register source-free completion tasks before starting nested or
hedged transport work. Shutdown waits for the actual producer and its
cancellation callbacks, even if the canceled caller has already returned and no
source bytes were reserved. Registration after the resolve scope closes is
rejected. These tasks must not carry asset results or source-bearing exceptions.

The built-in local and data-URI readers use the same source admission when a
performance session is enabled, including raw SVG data URIs that cannot be parsed
by `Uri`. Reads and decoding run off the UI thread. Native `DataReader` input and
managed output overlap are charged conservatively. SVG Open is awaited to actual
completion before returning the caller's source reservation, even if a provider
ignores cancellation; the provider retains its own hard transaction deadline.
Late resolver successes and their cleanup completion are also tracked during
retirement. Ordinary raw `ResolveAsync` consumers and nonparticipating legacy
resolvers remain compatible but do not receive this pre-materialization guarantee.
JitHub's offline same-byte resolver reserves the exact validated file length
before reading/hash verification, and its lifecycle/audit wrappers preserve
leased capability only when the inner resolver supports it. JitHub's production
GitHub adapter transfers source owners through HTTP/memory/disk, stale/refresh,
authenticated repository, LFS and hedge routes. It advertises the leased
capability only when its configured image service supports it, and reuses one
facade per provider to preserve shared-flight/cache identity across views. Large
unknown-length downloads release their spool scratch buffers before returning
the initial reservation and waiting for the final exact-size grant. The production
authenticated client streams raw Contents image responses through this same
reader; it avoids JSON/base64 UTF-16 intermediates without changing the general
GitHub client contract or authentication/ref/policy semantics. Sources that cannot
fit the request's reservation ceiling stop after one proof byte rather than being
fully downloaded/spooled. Speculative sources defer for a later visible request.
The spill file disables FileStream's private managed write buffer so it cannot
retain unaccounted encoded bytes during a final-admission wait. Fragmented reads
are batched in the already-admitted scratch before disk writes; there is no
second batching buffer. Scratch and partial-output references are also cleared in retained completed or
faulted reader tasks before their reservations end. Small chunked responses keep
a conservative scratch-plus-result reservation; JSON-only custom clients remain
compatible and outside this raw-source guarantee. Error-response body parsing is
bounded to 64 KiB independently of source admission.
These source-linked seams pass managed tests; the real app's fresh compilation
and release qualification remain blocked by the mandatory dependency-security
check. Live SVG provider/sanitizer copies and broader aggregate memory remain
separate open work, not proof of a total process-memory cap.

An `ImageBox` retains SVG source identity weakly plus scalar metadata; its raw
source arrays belong to the separately weighted source cache or admitted active
work. Queued UI publication uses a weak/scalar carrier and does not keep an
uncharged raw array alive. If source entries are evicted, a new raster variant
re-enters the authorized admitted resolver/read path, while an exact GPU bitmap
hit remains worker-free. Metadata reuse requires the current security partition.
The host-ownership regression uses a provider that does not retain source
copies and therefore does not qualify the real SVG provider's separate memory.
Resvg now separately clears a retired document's provider-owned source snapshot
only after its admitted renders and cancellation callbacks drain, and releases
the cleanup delegate even on failure. Its required caller snapshot is copied
off non-pool callers, and validated inert UTF-8-prolog DOCTYPE removal mutates
only that private copy. Validated top-level non-UTF-8 XML takes a bounded streaming
UTF-8 normalization path; encoding/standalone declarations and inert metadata are
preserved. UTF-8 input bypasses that serialization. Segment capacity is bounded by
the lowered source-byte cap, but the exact output array and input snapshot coexist
transiently and are not one aggregate cap. Failed/canceled normalization and
inspection tasks clear their snapshot and stream references. Nested SVG admission
checks the actual root/prolog without a 512-byte heuristic; nested non-UTF-8 SVG
remains unsupported, with recursive body security validation owned by the worker.
Embedded percent/base64 payloads decode from spans into
one exact array after checking the lowerable byte limit; complete UTF-16 runs
preserve supplementary Unicode. The worker's signature-first raster resolver
covers nested SVG and filter images, removing the host's redundant MIME-repair
DOM pass. Parser-based DTD/stylesheet checks preserve harmless comment/CDATA
markers, and allocation-free metadata scans avoid full-source lowercase copies.
Unicode attribute names no longer trigger UTF-8 slicing panics.
Isolated managed and native pixel tests cover these guarantees, and all three
worker architectures compile. The fresh provider end-to-end probes still fail
before restricted worker startup; those tests do not qualify isolation, the
shipping Windows-TFM/AOT build, Win2D composition or browser-relative performance.
Live-document source copies, static-snapshot sanitizer intermediates and decoded/
mapped transient ownership still need independent aggregate-memory qualification.
Completed open/render/inspection and mapping/admission tasks now clear source
aliases at their actual terminal barrier. Retaining those tasks does not retain
retired private source bytes; active renders still keep their source intact.
Failed post-OPEN document construction schedules best-effort binding cleanup.
A queue-full CLOSE does not invalidate a healthy worker: any remaining binding
is bounded by native binding/cache eviction or restart, not guaranteed closed
immediately. Detached cleanup faults are observed.

With `EnableMarkdownLayoutDiagnostics=true`, the resvg provider publishes
`MarkdownRenderer.Svg.Resvg.SourceSnapshots` event 1 (`SnapshotChanged`). Its six
numeric fields are scope ID, live bytes, peak bytes, live snapshots, peak
snapshots and sequence. Scope 0 aggregates participating renderer scopes;
out-of-order callbacks must be interpreted using each scope's sequence. These
are logical lengths of designated provider snapshots (including about-to-clone
tickets), not allocated memory, RSS or a quota. Normalization overlap, caller/
cache arrays, sanitizer DOM/output, decoded resources, mappings, native trees
and GPU memory are excluded. Tickets follow failure/cancellation and actual
document/render retirement; callbacks run outside state locks. The ledger is
compiled out when diagnostics are disabled and does not change admission or
queue policy. Weighted aggregate ownership and backing/reacquisition remain open.

The host's repository/network download methods also clear their local byte-array
references before releasing untransferred admission, including malformed remote
SVG rejection. The retained-failed-fetch regression verifies collectible read
buffers and zero remaining admission; it does not bound all active source copies.

JitHub's separate host image-source LRU also clears its retained entries on
Windows app-memory-pressure notifications. The callback is cache-only and
thread-safe: it neither mutates borrowed source arrays nor does UI or network
work. Its static event uses a weak relay, with rollback and idempotent teardown;
platforms without that notification retain the ordinary bounded LRU behavior.
This is independent of the library session's `Trim()` and does not release
arrays still pinned by live boxes, resolver results or queued decode work. The
host cache, session cache and active consumers are not one aggregate memory cap.

Ordinary-raster cache misses also share process-wide codec safety admission,
including controls without a progressive session and assets without cache keys.
The default scheduling budget is 128 MiB with at most two active decodes on
x64/ARM64, or 64 MiB with one on x86. A conservative weight includes encoded
bytes, source pixels and possible output/upload buffers. A larger valid image
is admitted alone at the whole scheduling budget, not rejected for exceeding
that estimate. Exact bitmap-cache hits bypass admission. Per-session CPU limits
can be stricter; document queues rotate without bypassing a waiting full-budget
request with later small requests.

Cancellation removes queued requests, but an active codec permit is retained
until non-cancelable native awaits and output cleanup actually finish. Session
shutdown cancels keyed decode waits before CPU admission and drains registered
raster work through cleanup and permit release. Final UI publication is covered
by the image owner's separate retirement barrier. These are scheduling and
lifetime guarantees, not measurements of native codec peak memory or a bound
on encoded arrays retained by pending requests. Single-codec intermediate memory,
no-session source-buffer admission, and the full image-storm memory gate remain
open.

Bitmap and source-cache budgets are cache-only. The independent
`MarkdownPerformanceOptions.ReleaseOffscreenImageResources` option releases a
box's GPU bitmap and tile leases, SVG document and raw source reference after it
leaves the directional layout band and its admitted work drains. This is off by
default on independently constructed options and enabled by `Progressive`, which
JitHub uses. It does not cancel whole-document source prefetch or evict the shared
source cache. Without the option, boxes retain their ordinary resources until
snapshot retirement.

Band reconciliation uses the existing ordered image index; the full image plan
is supplied only after a plan rebuild. Stable overlapping members retain their
resources. Cancellation closes the departing box's admission before requesting
asynchronous cancellation, and old native operations, late results and queued
UI publications remain in its drain. Dropped publications release their owned
results. Resource detachment is excluded from paint by the existing snapshot
read lease, and re-entry waits for the prior residency to finish before using the
existing exact bitmap cache and authorized resolver/session source path. A warm
constructor lease with no admitted work can be released synchronously. Intrinsic
dimensions, source identity, alt/title/description, selection geometry and a
previously loaded accessibility state survive eviction; eviction is not an
unavailable-image result or a reason to rebuild document layout. Tracking sets
drop released members and compact oversized sparse backing storage after large
band contractions rather than retaining a document-sized seen set.

Adopted SVG documents have independent use leases for their load pipeline,
queued tile work and actual native rendering. Cancellation of an await does not
end a non-cooperative provider's native use. Retiring one document rejects new
uses and waits for its own users, not unrelated work on replacement documents;
native disposal runs off the UI thread inside the image retirement barrier.
Weak-key retirement state rejects stale captured handles without retaining every
previous document tree for the lifetime of the box.

These are residency and ownership guarantees, not a hard cap on simultaneously
visible resources or measurements of codec intermediates, native driver cleanup,
total current-document memory or UI-frame cost. The full image-storm memory,
physical-device/DPI and browser-relative gates remain open.

Built-in raster and SVG data URIs are decoded on the preparation worker rather
than sent to a WinRT URI stream reader. The shared decoder checks the bounded
header and exact decoded byte count before allocating output, uses a direct
base64 span for ordinary payloads, and streams percent-escaped base64 without a
whole-payload string copy. Binary percent escapes preserve their original bytes;
literal Unicode uses strict UTF-8. Cancellation and the existing raster/SVG
input ceilings still apply. This does not admit nested network/file SVG access.

The progressive session also has a cross-document completed-scene LRU. Set
`MarkdownPerformanceOptions.SceneCacheBudgetBytes` to lower its default
64 MiB x64/ARM64 or 32 MiB x86 conservative graph-weight budget; zero disables
strong cache retention, not scene rendering. Weights include immutable content,
commands, paths, text, styles, and semantic/link metadata and are computed on the
preparation worker, never while scrolling. `SceneCacheEstimatedBytes`,
`SceneCacheHits`, and `SceneCacheEvictions` report cache-only, privacy-safe
statistics. These estimates are not measurements of native or process memory.

An unconsumed visible publication keeps a temporary layout handoff even when it
cannot fit the cache. Once consumed, a candidate layout owns its scene content.
Leaving the viewport attempts to transfer an unconsumed handoff into bounded
cache retention. If it cannot fit or the budget is zero, the strong handoff is
released; later visibility may require worker re-preparation.
`MaxConcurrentScenePreparations` covers both worker preparation and prepared
results awaiting dispatcher acknowledgement across documents. Cancellation
clears a queued result's payload; a callback that has already started keeps its
slot until it finishes, and retirement waits for that acknowledgement. Worker
preparation timing stops before the dispatcher wait. This limits queued prepared
graphs, not per-marker admission waiters or results transferred to layout owners.
Scene-free reconstruction metadata has a separate aggregate LRU ceiling of
4 MiB x64/ARM64 or 2 MiB x86, including when strong graph retention is disabled;
the per-result reconstruction copy is limited to 1 MiB. Plan descriptors only
hold that metadata weakly after publication. Resident metadata can reuse a
still-live block or inline scene after graph-cache eviction; content-tree
reconstruction runs on the preparation worker, not during UI layout or scrolling.
Eviction or an
oversized metadata copy is a normal re-preparation opportunity, never a permanent
unavailable-content result. `Trim()` drops source, strong scene, and reconstruction
metadata cache retention, but does not retire active layouts or cancel visible
handoffs. Metadata-cache bytes are separate from `SceneCacheEstimatedBytes`.

This bounds session-cache retention, not the entire live scene graph. Current
and retired layout owners, engine feature caches, and pending handoffs are
separate owners. Full active/off-band graph detachment and aggregate pinned-memory
qualification remain required by the progressive plan; a cache-only byte counter
must not be presented as proof of those release gates.

Terminal snapshot retirement releases completed Math/Mermaid command graphs
from their block/inline drawing owners after paint/interaction readers drain,
including when native cleanup fails. Retained semantic records, source ranges
and worker-captured text styles do not own those command graphs. Ordinary
offscreen native-resource eviction is reversible and keeps the graph; this
does not establish an aggregate current-document graph limit. Inline/native
UIA geometry still needs the separate immutable-publication work. The optional
session now permits the scoped single-leaf block detachment described below;
ordinary eviction and inline/multi-leaf graphs retain their existing behavior.

Block-vector hit testing, selection eligibility, text endpoint lookup and link
lookup use retained semantic/link records and a read-only borrow of the existing
scalar depth array. They do not access the drawing's command graph, including
after terminal retirement. Depth and link lookup stay O(1) per candidate within
the existing semantic scan; no ancestry walk, second depth array or per-hit
allocation is added. This is a semantic-lifetime prerequisite, not active-band
command-graph detachment or copy-on-write native text geometry.

Block-vector accessibility-tree construction, keyboard-focus plans, intrinsic
measurement and semantic text styles now use scene-independent metadata too.
The drawing remains the sole command-graph owner in the box; retaining a
declarative `MarkdownContent` is no longer a second ownership path. Typography
and authored/semantic paint are frozen during worker construction, not rebuilt
by command scans for each accessibility span or lookup. One command pass indexes
text/background paint and infers a missing reference font size; inherited
background resolution is iterative and memoized for arbitrary parent ordering.
An explicitly sized scene without semantic items skips that indexing scan.
The retained style-reference array replaces the two command-index arrays
(8 bytes per semantic on x64, 4 on x86 versus the former 8 on either); each
indexed text semantic also retains an `ElementStyle` object. This is bounded
projection metadata, not a claim of zero construction allocation or an aggregate
graph-memory cap. Current managed bounds still supply positioning; exact native
text/UIA geometry needs separate immutable-publication work.

Semantic-tree attachment also memoizes omitted/decorative ancestor paths. It
supports forward parent references, stops at the nearest exposed node and keeps
the original semantic-index sibling order. Temporary integer arrays are bounded
by the admitted semantic count and released after construction; no parent map
is retained by the document. Empty atomic scenes skip them. Cancellation is
observed in long traversal/unwind paths and before attaching a child. This
removes a quadratic hidden-ancestor walk without changing exposed hierarchy.

Materialized block and inline owners carry an internal readonly marker/scene-slot
binding, not the result fragment or another command-graph reference. The binding
is distinct from the unresolved placeholder marker; keeping it does not schedule
attached ready content again after cache trimming. Inline measurement freezes
width, height, baseline and reference-font size during construction and uses the
same scaling/clamping formula, while terminal disposal still rejects measurement.
Inline and multi-leaf bindings remain reversible-residency prerequisites; the
single-leaf block path described below now implements actual detach/refill.

The synchronous construction ledger also owns successfully created inline vector
runs while they are staged outside a parent. Registration precedes staging-list
insertion; adoption follows successful parent insertion. It shares the existing
reference-identity ledger with block roots, with no second retained set. Blocks
have their own virtual disposal contract, while inline vectors are disposable;
cleanup preserves both contracts and observes block async retirement barriers.
Reachable snapshot runs are removed during transfer, and a second constructor's
cancellation/failure retires previously staged native resources even when another
cleanup throws. The ledger is released with the build, not stored in the snapshot.

Immutable scene resource estimates are memoized behind weak scene keys, so
leaving/re-entering a residency band does not repeat a command-graph walk.
Only the scalar diagnostic weight is retained; this adds no strong scene owner
and is not a native-memory bound. Residency mutation still runs under the
snapshot's existing lock. Text-range image lookup and top-level enclosing-node
lookup use ordered semantic indexes; nested vector label ranges retain their
original ordering and containment behavior. These reduce unrelated-document
scans without changing geometry publication or claiming COW safety.

### Scoped block command-graph residency

The optional Performance session can detach a materialized block containing
exactly one vector leaf at ordinal zero. Admission requires its original source
range and a 32-byte full immutable projection identity, not just a marker or
scene dimensions. Inline and multi-leaf graphs stay attached. This restriction
preserves selection, links, authored paint, typography and semantic identity
across refill, including custom async providers.

Re-entry restores a cached tree or rerenders after eviction, then validates the
full projection before adopting a staged native drawing. It does not rebuild
the whole document or use the initial scene-publication callback. Provider work,
hashing and native drawing preparation are off-lock; capture and final adoption
use the existing snapshot lease. Previous good pins survive staging; separate
transient pins and actual-use producer retirement protect cancellation and
superseded bands. A native drawing adopted by the base box remains box-owned.

Detached visible content uses bounded source/name text prepared by the optional
package, never allocated in paint. Failure yields MDP0002 and localized
unavailable status, preserving geometry and semantics. A failed owner is settled
for scheduling but not render-ready. Null/throw/cancellation during fallback
replacement preserve the prior visual and dispose rejected candidates. UIA
status uses one validated visible probe and targeted owner-to-peer lookup; busy
notification leases retry after paint and drain even without listeners.

This slice does not bound total live command-graph bytes, source/provider copies
or native codec peaks. Active-band weights remain estimates. The bounded fallback
creation/commit still occurs under snapshot exclusion; copy-on-write text/geometry
publication and complete inline/multi-leaf graph residency remain separate work.
Managed regressions and source wiring do not establish live UIA, compositor-frame,
physical DPI/theme, browser-relative or top-500 release qualification.

## Optional payloads

The resvg worker and TextMate grammar packs are not costs of the lean package.
Select only the SVG capability and grammar resources the application needs. The
provider bounds its parsed-resource and GPU caches by architecture, deduplicates
in-flight work, and discards CPU rasters after upload. Math and Mermaid also
retain independent source, scene, time, and working-memory budgets.

## Remaining validation

Before 1.0, verify cancellation races, cache budgets, long documents, enormous
single blocks, selection, image storms, graphics-device reset, high DPI, x86/x64/
ARM64 payload resolution, memory recovery, and ancestor-owned viewport behavior.
