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

Schema-10 release evidence uses three excluded, raw 100-sample warmup trials and
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

Schema 10 records consistency-scaled median absolute deviation for every
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

Release evidence is schema 10 and binds the complete private runtime output with
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
[deployment-bound quick smoke](../../MarkdownRenderer/artifacts/performance/performance-quick-schema10-deployment-bound-r3-20260910.json)
passes the structural, cache, runtime, monotonic-timestamp, and topology contract
but is explicitly non-gating.
The retained schema-8
[baseline](../../MarkdownRenderer/artifacts/performance/performance-baseline-optimized-r7-schema8-20260910.json),
[candidate A](../../MarkdownRenderer/artifacts/performance/performance-candidate-a-optimized-r7-schema8-20260910.json),
and [candidate B](../../MarkdownRenderer/artifacts/performance/performance-candidate-b-optimized-r7-schema8-20260910.json)
are methodology evidence. The later retained schema-9
[baseline](../../MarkdownRenderer/artifacts/performance/performance-baseline-optimized-r8-schema9-20260910.json),
[candidate A](../../MarkdownRenderer/artifacts/performance/performance-candidate-a-optimized-r8-schema9-20260910.json),
and [candidate B](../../MarkdownRenderer/artifacts/performance/performance-candidate-b-optimized-r8-schema9-20260910.json)
proved that order-invariant Hodges-Lehmann/MAD checks could accept a settling
trend; candidate B failed the warm 10 MiB comparison. All are preserved as
methodology evidence, not schema-10 release evidence.

A first full schema-10
[absolute baseline](../../MarkdownRenderer/artifacts/performance/performance-baseline-schema10-deployment-bound-r1-20260910.json)
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
