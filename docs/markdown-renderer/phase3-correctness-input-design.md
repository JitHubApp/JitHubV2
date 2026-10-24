# Phase 3 browser-free correctness input: design

Status: proposed, October 20, 2026. Nothing here is implemented yet. This
design unblocks the [Phase 3 real-world audit](correctness-plan.md#phase-3-real-world-correctness-audit)
without weakening any existing integrity check.

## Why the current path cannot close Phase 3

The in-app probe (`JitHub.WinUI.Automation/ReadmeCorrectnessCorpusProbe.cs`)
passes a case only when all 11 required checks are `Passed`. Three can never
pass today, regardless of input:

| Check | Current behavior |
| --- | --- |
| `contentCompleteness` | Hard-coded `NotEvaluated`: no independent Markdown-to-visible-content oracle. |
| `unsupportedSyntaxClassification` | Hard-coded `NotEvaluated`: no per-document diagnostic inventory. |
| `resourceCoverage` | `Failed` on unavailable/loading images, otherwise `NotEvaluated`: no demand oracle. |

Separately, `verifiedCorpusInput` accepts only schema-2 same-byte folders that
come from the old Edge parity capture and require `browserRender.status ==
"passed"`. Only ranks 1, 59, 60, 203 and 318 have one. The app never renders
`rendered.html`; in corpus mode it renders the pinned README bytes, and the
browser capture only gates integrity and decides which images were stored.

So two things are required: a correctness-only input format, and the three
oracles. Neither alone closes Phase 3.

## Schema 3: `correctness-input-v1`

One folder per rank, found directly as `<root>/<rank:D3>-<sanitized fullName>/manifest.json`
(replacing the schema-2 grandparent-of-manifest rule). Required fields:

- `schemaVersion: 3`, `kind: "correctness-input-v1"`, `complete: true`.
- `repository`: `fullName`, `commitSha` (lowercase), `readmePath`,
  `readmeGitBlobSha1`, `rank`, `repositoryId`, and the frozen manifest SHA-256
  `615c5672…`. Binding to the frozen manifest replaces folder-name inference.
- `readme`: `file: "readme.md"`, `byteSize`, `sha256`; `readme.md` remains
  byte-identical to the Git blob and is checked by both probe and resolver.
- `assets[]`: unchanged schema-2 shape and limits: `urlSha256`, `sha256`,
  `byteSize` (≤ 64 MiB), `mimeType`; at most 100,000 entries and 256 MiB of
  unique bytes, stored as `assets/<sha256>`. `urlSha256` keys must use the
  resolver's existing `AddUrlHash` normalization; round-trip tests must cover
  URLs whose encoding differs between producers.
- `imageDemand[]`: one entry per authored image occurrence (Markdown and HTML
  `<img>`), with its UTF-16 source span, `sourceSha256` (SHA-256 of the
  authored `src`, matching the headless audit and the app's
  `image-resolution.ndjson`), `origin`, the candidate URL hashes tried, and
  either the stored `assetSha256` or a terminal state: `upstream-unavailable`,
  `http-<status>`, `not-an-image`, `data-uri` or `unsupported-scheme`.
- `acquisition`: fetcher identity/version, creation time, host policy and
  request/response receipt hashes.

Dropped from schema 2: `browserRender`, `rendered.html`, `imageRoutes` and
decorative-exclusion evidence, which all require a browser. A failed empty-alt
image therefore surfaces as an ordinary resolver miss, classified by its
`imageDemand` terminal state rather than hidden.

## Host-side acquisition

A new host script builds each rank's folder from the already verified source
store (`Invoke-MarkdownRendererTop500SourceAcquisition.ps1` output):

1. Enumerate image occurrences from an extended headless audit (below).
2. Derive candidate URLs with the app's own rules
   (`GitHubMarkdownImageUrlResolver`, `MarkdownRemoteImagePolicy`): relative
   paths against the README directory at the pinned commit, raw/blob GitHub
   URLs, the `../assets` branch convention, Camo, and allowed third-party hosts.
3. Fetch each candidate once with the app's size limits, recording status,
   resolved URL and content hash. Never substitute a different commit or file.
4. Write the folder and a run receipt. Upstream failures stay recorded
   failures; they are never stored as passes.

## Oracles

- **Content completeness:** extend the headless audit to emit, per document,
  ordered block inventories with source spans: headings (level and text hash),
  paragraph text-run hashes, links, Markdown and HTML images, code blocks
  (excluding Mermaid rendered as scenes), tables, task items and disclosures.
  The probe compares counts and identities against UIA (`Header`, `Table`,
  `MarkdownImage*`, `MarkdownCodeBlock`, `MarkdownDiagram`, links'
  `RawDestinationSha256`) and checks block text as an ordered subsequence of
  the exact UIA document text it already captures for selection.
- **Unsupported syntax:** headless `diagnostics[]` (code, severity, span) plus
  HTML-block spans become the per-document inventory. Unsupported syntax is
  then classified (`Unsupported`), not passed and not counted as a defect.
- **Resource coverage:** join `imageDemand[]` with the app's
  `image-resolution.ndjson` (`SourceSha256`, `HasAsset`, `Reason`): every
  demanded image must reach a final state; a stored asset that does not render
  is `Failed`; a recorded upstream failure is `UpstreamUnavailable`.

## Code changes

- `ReadmeCorrectnessCorpusProbe.cs`: schema-3 branch in
  `VerifyCorrectnessCorpusCandidate`, direct rank-folder lookup, the three
  oracle evaluators, and new accounting self-tests (the existing
  `clean-resource-count-without-demand-oracle-remains-not-evaluated` case
  changes meaning once the oracle exists).
- `MarkdownSameByteAuditImageResolver.cs`: accept schema 3 without
  `browserRender`, skip decorative exclusions for it, keep `GetSourceUrlHashes`
  unchanged. Add v3 tests beside `MarkdownSameByteAuditImageResolverTests`.
- `MarkdownRenderer.ReadmeCorrectnessAudit`: inventory extension and self-tests.
- A Sandbox guest runner modeled on `Invoke-MarkdownCorrectnessUiTests.ps1`
  that sets `JITHUB_README_CORRECTNESS_SANDBOX=1` and the clipboard-isolation
  marker, runs shards of 25 and keeps PASS/FAIL/UNVERIFIED accounting.
- Unchanged: `LaunchOptions` (it validates the frozen manifest, not the
  corpus) and the `JITHUB_README_AUDIT_SAME_BYTE_CORPUS` environment path, on
  which source-identity evidence and `routeAwayAndBack` depend.

## Order of work

1. Headless inventory extension and its self-tests.
2. Host-side acquisition producing schema 3 for all 500 ranks.
3. Resolver and probe schema-3 support with tests.
4. The three oracles and accounting self-tests.
5. Guest runner, then the full audit in shards and per-rank triage.
