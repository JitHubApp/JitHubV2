# Top repository README audit

This release audit compares JitHub's real repository README surface with the corresponding `article.markdown-body` rendered by Microsoft Edge on GitHub.

The corpus is generated from the current GitHub repository search ordered by
stars, then pinned by repository identity, default-branch commit SHA, README
path, and README blob SHA. Edge opens GitHub's view of that commit and JitHub
requests the same immutable ref. A run creates isolated evidence for every
repository:

- complete browser and native vertical tile sets;
- browser DOM and native UI Automation text/structure inventories;
- browser and native image/media completion evidence;
- JitHub render exceptions, process exit state, and resource timing;
- normalized text coverage, semantic counts for headings, links, images/media,
  tables, code blocks, task checkboxes, and disclosures, styled-viewport SSIM,
  and same-machine Edge-relative timing ratios;
- pre-screenshot Edge CPU/layout/heap metrics and isolated JitHub process
  CPU/working-set evidence, so evidence capture is not mistaken for rendering;
- privacy-safe, aggregate Markdown preparation counters at first render and
  after the final native tile (source fetches, cache hits/bytes, raster and
  scene preparation, failures, and cancellations). These diagnose outliers;
  each resolved image also records its resolver start and elapsed time. These
  do not prove same-byte parity or replace the release benchmark.
- if an isolated resvg worker misses a deadline, a separate privacy-safe
  record names the phase (process startup, font catalog, open, render, or
  maintenance), configured deadline, worker-process CPU milliseconds during
  the transaction (-1 if unavailable), transport phase (request write, pipe
  flush, or response read), request-write duration, and whether the worker
  had exited. For `open`, it also records the last bounded worker progress
  phase (mapping, hash, XML, security, font gate, SVG options initialization,
  options construction, resolver setup, theme transformation, `usvg` tree
  construction, tree, or document attachment).
  Timeout-only working set and private commit are reported in KiB, with a
  cumulative process page-fault count; -1 means unavailable. Normal rendering
  never queries these process-memory counters. The record contains no SVG
  bytes or URLs.
- visible-image waits of at least 500 ms record their tile index, duration,
  and whether they reached the 20-second deadline. Only on a deadline does a
  one-time UIA diagnostic count still-loading image peers; zero distinguishes
  a stale aggregate status, while `-1` means that diagnostic walk failed.

Run an optimized smoke audit (Release is the default and is required for
browser-relative performance evidence):

```powershell
.\eng\Invoke-TopReadmeAudit.ps1 -Count 5
```

Run or resume the complete release corpus:

```powershell
.\eng\Invoke-TopReadmeAudit.ps1 -Count 500 -Resume
```

To collect a fresh same-byte fixture for a focused rank (this does not qualify
the release performance gate), use `-CaptureSameByteCorpus` without resume or
browser-evidence reuse:

```powershell
.\eng\Invoke-TopReadmeAudit.ps1 -StartRank 1 -Count 1 -CaptureSameByteCorpus
```

For a qualified full same-byte release audit, open **Actions → Top 500 README
browser parity → Run workflow**, select the renderer revision, and enable
`same_byte_release`. Keep `diagnostic_rank` at `0`. This opt-in dispatch runs the
same immutable 500-repository manifest through all twenty 25-repository shards,
captures fresh Edge response bytes for every available pinned README, and makes
the final merge require same-byte evidence for each such case. It also requires
the complete rank set, clean native exits, zero unavailable or still-loading
native images, aggregate first-render and full-page p95 no greater than 110% of
Edge, and individually checks the previously slow ranks 60 and 203 against that
same 110% ceiling. A focused rank replay, a mixed set of results with and
without same-byte capture, or a partial 500-case result cannot pass this release
path.
For each rendered case, the merge recomputes the first-render and full-page
ratios from native and offline Edge timings and rejects missing, non-finite,
non-positive, or inconsistent stored ratios.

Capture pins README source bytes to the manifest's Git blob SHA and records
completed visible Edge image responses as content-addressed files. If a raw
GitHub URL returns a symlink blob instead of the file GitHub renders, capture
retries only through the authenticated GitHub README Contents endpoint at the
same pinned commit. The response is streamed under a 24-MiB JSON/base64 cap,
decoded under the normal 16-MiB README cap, and accepted only when both its API
SHA/size metadata and a locally recomputed Git blob SHA/size match the manifest.
The read-only audit token is inherited from `JITHUB_README_AUDIT_GITHUB_TOKEN`,
sent only as an authorization header, and never written to corpus or report
files. Any mismatch or unavailable fallback fails closed. URL lookup values are SHA-256
keys only; the manifest does not persist source URLs or request headers. The
fresh JitHub process gets an audit-only resolver over that case's corpus. A
missing URL, manifest, or mismatched file is blocked; this path has no live
image-network fallback. GitHub Camo images are matched using the rendered
`src`/`currentSrc` and the authored `data-canonical-src` URL, so the same Edge
response can be replayed for the original Markdown URL without saving either
URL in clear text. Capture rejects more than 15,000 visible image elements or
URL lookup aliases, tracks at most 100,000 network requests and 16 MiB of URL
metadata, and bounds each image to 64 MiB and each README's unique image payloads
to 256 MiB. Schema version 2 pins the raw README SHA-256 and captures a
32-MiB-bounded static `article.markdown-body` snapshot with computed presentation
styles and indexed image routes. `rendered.html` is private, capture-only input:
it may contain authored links or embedded data-image bytes, so it must never be
uploaded as a workflow artifact. The manifest stores only its size/hash and
content-addressed route metadata. GitHub's captured image MIME is canonicalized
from bounded payload signatures; unknown payloads fail closed. READMEs shown by
GitHub as source have an explicit `browserRender.status: "not-applicable"`
result; they still require a clean native source audit and are not assigned
fabricated renderer timings.

The old captured-HTML Edge replay is retained as `sameByteHtmlReplay` diagnostic
evidence only; it does not qualify the release performance gate. For a rendered
README, after native traversal the audit starts a separate headless Edge replay
from the exact captured raw Markdown bytes. It parses client-side with the
vendored [Marked GFM parser](https://marked.js.org/) 18.0.5 (MIT; see
`vendor/marked-18.0.5.LICENSE`), whose exact bundle bytes are SHA-256 pinned in
code and verified again by the loopback server and browser SRI. The source-bound
runner accepts the measured native Markdown host viewport and device scale
factor, verifies Edge's actual `innerWidth`, `innerHeight`, and DPR, and applies
the same light/dark theme. It admits Markdown image elements only when their
URL maps unambiguously to a captured URL hash and payload. A bounded indexed
`data:image` URI is also accepted only when its decoded MIME and SHA-256 match
the corresponding payload in the hash-verified captured GitHub HTML; replay
uses a temporary Blob URL rather than broadening the page's CSP. Uncaptured
images and network dependencies fail the case. Request interception denies all
non-loopback requests, and CSP plus the closed-miss server fail any incomplete
asset replay.

The schema-3 `sameByteReplay` report binds the README Git blob/SHA-256, parser
name/version/license/SHA-256, viewport/theme/actual Edge dimensions, the
complete CSS-pixel `renderedExtent`, exact asset-URL-map digest,
expected/verified image counts, blocked/missing request counts, an HMAC-keyed
visible-text token multiset, and screenshot tiles that continuously cover that
full extent. Before using that replay for the native comparison, the audit
re-HMACs the captured GitHub article's visible text with the same per-case
ephemeral key and checks token-multiset coverage in both directions. Each
direction must meet the existing 98.5% text floor. The sanitized case result
retains only the two aggregate coverages, token counts, and the digest-key
fingerprint; the separate source-replay evidence contains HMAC digests rather
than plaintext tokens. Neither file persists the key or raw visible prose.
Native JitHub-versus-source coverage remains a separate comparison using the
same 98.5% floor. Failure reports retain a bounded category/status and static
message, never an exception stack, source URL, or checkout path. Its
`firstViewportPaintMs`,
`firstViewportImagesReadyMs`, and `fullTraversalMs` clocks begin after the
manifest/parser are loaded and stop before screenshot capture. The image-ready
boundary requires successful decode of every visible pinned image; traversal
uses the same viewport step/visible-image readiness semantics as the native
audit. This measures client parsing/layout against the same pinned README and
image bytes as JitHub, without GitHub navigation, CDN, or server-side rendering
latency in the denominator. Live GitHub timings and `sameByteHtmlReplay` remain
separate diagnostics. The same-byte merge independently requires finite
`comparison.textTokenCoverage >= 0.985` and
`comparison.visualStructureScore >= 0.95` for every rendered case; a `passed`
status alone cannot satisfy these gates. GitHub-versus-source structure is a
separate gate over heading, table, task-checkbox, and authored-details counts.
Each domain uses `CountFidelity` with equal weights normalized to a total of
one, and its aggregate must also meet 0.95. It is not averaged with the
native-versus-source score. The merged release recomputes this aggregate from
the captured GitHub and source counts, then checks the persisted score. The
summary reports GitHub/source and native/source scores in separate columns.
For source-bound full-page timing, `fullTraversalMs` remains the raw end-to-end
replay clock. The release comparison uses `chargedTraversalMs`, which is the
same clock minus `auditOnlyFrameWaitMs`: measured requestAnimationFrame wait
intervals from successful post-target stability confirmations and the final two
terminal-proof frames only. The first target-matching movement sample, first
paint, Markdown parse/insert, every image decode wait, and the terminal paint
after image readiness remain charged. The replay persists the raw clock, the
charged clock, and excluded-frame duration/count; the merge validates their
arithmetic and recomputes the existing performance ratio from the charged
clock. The existing 1.10 performance ceiling and 0.90–1.10 extent gate are
unchanged.
The browser boundary is an operational analogue, not a claim of compositor
frame equivalence: native timing charges `ChangeView` through its non-intermediate
`ViewChanged` event, while UIA settle-probe/transport overhead is removed and
image-ready work remains charged. Edge charges each movement through its first
target-matching sample and keeps image-decode work, without asserting that an
rAF callback maps to a particular native paint event.
Workflow shard artifacts retain
manifests, reports, and raster screenshots/tiles for 14 days. Those images are
intentional visual evidence for public READMEs and can visibly show their text
and images. The artifact excludes the raw `rendered.html`, `readme.md`, and
`assets/**` corpus payload files; that exclusion does not mean the raster
evidence is free of visible README content.

The standalone runner can also be used against an existing case corpus after
the native viewport is known:

```powershell
node .\eng\readme-audit\same-byte-edge-source.mjs `
  --corpus=<case-corpus-dir> --out=<output-dir> `
  --width=<native-markdown-width-css-px> --height=<native-markdown-height-css-px> `
  --device-scale-factor=<native-dpi-over-96> --color-scheme=light
```

Every capture uses a fresh corpus directory and cannot resume a completed case.
The manual image-source replay server remains available with
`node eng/readme-audit/same-byte-replay-server.mjs --corpus=<case-corpus-dir>`;
it binds only to IPv4 loopback and returns 404 for an uncaptured source.
Run the local contracts, including a real headless Edge loopback replay when
Edge is installed, with:

```powershell
node --test .\eng\readme-audit\*.test.mjs
```

The manifest contains URL/content hashes, byte counts, and MIME types; excluding
the source payloads preserves the capture identity and URL-to-content mapping
without uploading large or potentially sensitive content. The capture/replay
tests are deterministic and offline; the manual 500-case run is the
qualification gate.

For a focused raster-preparation investigation, add `-RasterDiagnostics` to
the audit invocation. This opt-in evidence writes only a preparation ID,
source byte count/dimensions, and durations for preparation admission,
stream write, decoder creation, WIC pixel decode, Win2D upload/direct load,
and bitmap-cache publication to
`native/raster-preparation.ndjson`. It records no image bytes or URLs. The
extra synchronous event/file work deliberately stays **off** in ordinary
top-500 and release benchmark runs; diagnostic timings are not qualifying
performance results.

To reproduce an intermittent hosted-runner image stall, manually dispatch
`Top 500 README browser parity` on the renderer branch with a nonzero
`diagnostic_rank`. Supply `diagnostic_corpus_run_id` from a top-500 audit
containing the pinned corpus and `diagnostic_repeats` from 1–5. A dispatch
with rank zero still runs the entire 500-case release audit. The focused job
downloads and validates that run's exact immutable README manifest. It uses
the same pinned Windows App Runtime as the normal audit, builds Release once,
and uploads all attempts'
diagnostics even if a replay fails. Enable raster stage tracing only for raster
stalls; SVG worker timeout stages are recorded without it. This preserves the
original rank/README bytes, though externally served image bytes must still
be compared by the recorded source hashes; the workflow does not freeze CDN
responses. The focused reruns are strictly diagnostic, never a qualifying
Edge timing or 500/500 result.
If a failure occurs only after earlier cases in a shard, set
`diagnostic_prior_cases` to 1–4 to replay those immediately preceding ranks
before the target rank in every attempt. The default is zero, preserving the
single-rank diagnostic; this context option does not skip or waive any case.

Prerequisites are an interactive unlocked Windows desktop, Microsoft Edge, Node.js 22 or newer, and an authenticated GitHub CLI session. The runner passes the current GitHub token only to isolated audit child processes and never persists it in evidence.

Native first-render timing is the Markdown host-ready to render-complete
lifecycle interval. Full-page timing adds lazy realization while traversing the
document. Repository navigation, API loading, UI Automation stability probes,
screenshots, and the already-at-bottom confirmation are recorded separately
and are not charged to the renderer. Cold-start and app-ready-to-content timing
remain in each case result for user-experience diagnosis.

The run fails when JitHub throws, exits abnormally, leaves an image loading,
reports an unavailable image, represents fewer distinct atomic image/media
items than Edge,
falls below 98.5% browser text-token coverage, or falls below 95% full-page
structural fidelity. Each native viewport is compared with the corresponding
width-normalized Edge viewport; its SSIM remains a diagnostic because JitHub
intentionally uses native Fluent typography and styling rather than GitHub's
CSS. Runs of at least 20 repositories also gate first-render and full-page p95
timing at 110% of Edge on the same machine.

If Edge cannot begin a navigation because its host reports exactly
`net::ERR_NO_BUFFER_SPACE`, the oracle stops that attempt, waits one second,
and makes one fresh navigation with a new CPU/layout baseline. Its total wall
time and retry count remain in the case evidence. A second failure, any other
navigation error, or a failed/partial README after the retry still fails the
case; this is not a fidelity or performance waiver.

When GitHub presents an extensionless README candidate as source rather than a
rendered `article.markdown-body`, JitHub must make the same choice. Such a case
still gates process stability, content presence, and unavailable resources, but
is excluded from rich-render fidelity and timing percentiles.

A top-ranked repository with no README remains in the corpus; it is not silently
replaced by a lower-ranked repository. The audit requires both GitHub and JitHub
to expose no rendered README for that immutable commit, while still enforcing
clean startup, navigation, and shutdown.

Manifest API calls use the workflow token normally. If a public repository's
organization IP allow list rejects that token, the generator falls back only
that request to GitHub's unauthenticated public API. Other authorization errors
remain fatal, and only an explicit README 404 is treated as an absent README.
If GitHub rate-limits public metadata or its ephemeral Actions token is
temporarily unavailable to the GitHub CLI, CI may use the checked-in immutable
`pinned-top500.json` snapshot only after validating its
schema, age (14 days or newer), exact rank coverage, uniqueness, commit/blob
identities, and trusted GitHub URLs. No repository is skipped or substituted;
stale or malformed fallback data fails the corpus job.

The scheduled/manual workflow runs twenty isolated 25-repository shards and then
requires a consolidated, duplicate-free set of ranks 1–500. Missing shards or
case files fail the final job; a partial run cannot be reported as a top-500
pass. Smaller shards bound each hosted runner's lifetime and narrow the
diagnostic range if a runner disappears before its evidence upload.

JitHub's audit build remains framework-dependent, matching the production
deployment model. Each hosted runner installs the exact x64 Windows App Runtime
1.8.10 release used by the app through Microsoft's silent installer, after
verifying its pinned SHA-256. Native launch failures preserve isolated startup
phase/error logs and abort the shard immediately because they occur before any
repository-specific work and cannot produce meaningful per-case comparisons.

`Test-WinAppManifestOverrideSandbox.ps1` verifies that WinApp CLI `run
--manifest` honors a supplied manifest without launching an application. Its
default mode is plan-only and read-only. An explicit `-Execute -InputFolder
<built loose AppX folder>` creates two random package identity names in a
private temp copy (the auto-detected manifest gets A; the override gets B),
verifies both names are absent on host and Sandbox, then runs folder mode with
`--no-launch --on sandbox`. It passes only if B is registered, then unregisters
only newly observed A/B candidates and verifies both are absent again. It
never reads or passes GitHub credentials, touches the host app identity,
launches a UI, changes system-wide WER settings, or claims an audit result. If
state is ambiguous or cleanup cannot be proven, it fails closed and preserves
the exact temp evidence path for review. Coordinate execution with any
build/pack or Sandbox UI audit; this test mutates only the existing Sandbox
for the duration of package registration.

#### Token-free Windows Sandbox renderer smoke (documented, not run)

The lifecycle fixture path can also render a local Markdown file without any
GitHub credential: `--markdown-lifecycle-fixture` enables the bridge and
`--markdown-corpus=<path>` reads a `.md`/`.markdown` file up to 4 MiB. In a
Sandbox run, the file must first be copied into the guest with
`winapp target push sandbox`; this exercises the Markdown pipeline but does
not replay captured image payloads or bypass normal remote-image policy.

The WinApp-managed Sandbox is persistent, not a fresh VM per command, and this
machine's existing Sandbox already has app deployments. The manifest proof
above does not establish cleanup of pushed guest files or app-data roots, so
this smoke procedure remains non-executable until those paths are proven. Never
use the production package identity, `--clean`, or an existing guest
destination. Establish cleanup of both that exact deployment and the unique
guest file before running it.
Never use the production package identity, `--clean`, or an existing guest
destination. Once that isolation/cleanup review is complete, the intended flow
is:

```powershell
# Choose a fresh nonce. Before pushing, use read-only target inspection to
# prove this relative destination does not already exist; never overwrite it.
$nonce = [guid]::NewGuid().ToString('N')
$guestCorpus = "readme-smoke-$nonce\README.md"
winapp target push sandbox .\README.md $guestCorpus

# Use a separately verified temporary manifest/layout; do not use the
# repository's production Package.appxmanifest identity directly.
# Replace <resolved-guest-path> only after verifying the target's managed work
# root and the exact path used by target push. Do not assume a fixed C:\WinApp
# location from this example.
winapp run .\JitHub.WinUI\JitHub.WinUI.csproj -c Release --arch x64 --no-build `
  --manifest <unique-temporary-manifest> --output-appx-directory <unique-layout> `
  --with-alias --debug-output --symbols --unregister-on-exit `
  --args "--page=repo-code --repo=codecrafters-io/build-your-own-x --branch=<pinned-commit> --markdown-lifecycle-fixture --markdown-lifecycle-host=MarkdownHost_RepositoryReadme_RepoCodeReadme --markdown-corpus=<resolved-guest-path>" `
  --on sandbox

# UIA commands must also specify --on sandbox and target only the newly
# verified guest app PID/window. Do not automate by app name if ambiguous.
```

This local-corpus route is a renderer/shutdown smoke only: the repo shell may
still query public metadata, and linked images continue through the ordinary
image policy. Sandbox app-data cleanup and managed-work-root path are not yet
established, so the commands above are a reviewed outline, not an executable
procedure or an invitation to run against the current persistent Sandbox. Do
not replace the placeholder or execute any command until cleanup and path
semantics have been verified on a disposable target.
