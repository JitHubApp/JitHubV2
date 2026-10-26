# MarkdownRenderer checkpoint evidence

This archive preserves selected historical receipts from the October 17
checkpoint of the [correctness plan](../../correctness-plan.md). The receipts
record passing and failing work; **none declares a completed correctness phase**.
They are not newly executed tests of the documentation commit.

## Included records

| File | What it establishes |
| --- | --- |
| [R6 managed lifetime investigation](p1-032-managed-lifetime-investigation-r6.json) | Three failing-before lifetime source contracts, 20 focused passes, 3,195 passing tests in both Debug and Release, clean JIT sample build, and the limited 2-pass/60-unverified owned-viewport resize replay. P1-032 stays open. |
| [Original option crash replay](p1-032-original-options-crash.json) | The retained R3 payload crashes in the approved Sandbox; 38 pass/5 fail/19 unverified, double-free detection stack, timeline and prior dump-setting restoration. It does not establish the first invalid release or crash cause. No dump is included. |
| [Broad R3 sample results](sandbox-broad-r3-results.json) | The earlier 47-pass/7-fail/8-unverified batch, individual case outcomes, capture hashes and failure/qualification boundaries. Later repairs do not retroactively pass this run. |
| [Mermaid component verification](mermaid-component-verification.json) | Recorded source/native/public/AOT component results, RID distinctions, rejected candidates and a prior 500-input headless result. This is not complete live feature or current whole-app certification. |
| [Top 500 source integrity](top500-source-integrity.json) | All 500 frozen source objects verified, totaling 18,023,117 bytes, and the same-commit Next.js symlink proof. Source bodies are not embedded. |
| [Correctness input preflight](readme-correctness-input-preflight.json) | All 500 input classifications: five compatible complete captures, 495 missing audit inputs, zero integrity failures. No app rendering or interaction is evaluated. |
| [Frozen repository manifest](top-repositories.json) | Exact original September 28 membership and repository/commit/README/blob/size identities for all 500 cases. This is public repository metadata, not captured README content or assets. |
| [Archive index](archive-index.json) | Original locations/hashes and archived-file lengths/hashes for integrity checks. |

The six receipt files are wrappers. Their `receipt` property contains the
historical data; `originalRelativePath`, `originalByteLength` and
`originalSha256` describe the unmodified original file. Local account/workspace
prefixes and computer names are replaced with explicit placeholders, and JSON
is serialized with LF endings. Outcomes, hashes, source identities and failure
qualifications are preserved. Historical `committedOrPushed=false` or no-side-
effect statements describe the original run, not this later archive operation.

The archived receipt hash therefore differs from the original receipt hash.
Do not feed these sanitized wrappers to a runner expecting an original result
schema, or treat a redacted local path as a usable runtime path. Old guest PIDs,
HWNDs and epochs are historical identity evidence, never current UI targets.

The frozen manifest is different: it is copied **byte for byte**, without
redaction or JSON reserialization. Its SHA-256 remains
`615c56723fb16d17744af2549615a4de32b1fc299a1759727cc86b70edacac72`.
The local `.gitattributes` prevents checkout line-ending conversion from breaking
that pinned identity. Do not reformat it or substitute a freshly ranked list.

## Integrity verification

From the repository root, verify the included files without starting any app:

```powershell
$checkpointEvidence = 'docs/markdown-renderer/evidence/2026-10-17'
$checkpointIndex = Get-Content "$checkpointEvidence/archive-index.json" -Raw | ConvertFrom-Json
foreach ($entry in $checkpointIndex.files) {
    $path = Join-Path $checkpointEvidence $entry.name
    $item = Get-Item -LiteralPath $path
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($item.Length -ne $entry.bytes -or $hash -cne $entry.sha256) {
        throw "Checkpoint evidence mismatch: $($entry.name)"
    }
}
```

These checks validate the archive, not the external TRX, screenshots, dump or
original process runs referenced within it. A hash without the corresponding
raw file is an identity record, not a recoverable copy of that file.

## Source acquisition and headless reproduction

The frozen manifest is a usable input to the existing acquisition runner. These
commands are continuation instructions; this documentation checkpoint performs
no downloads, builds or audit reruns.

```powershell
# Choose fresh output directories. This downloads pinned README source bodies.
pwsh -NoProfile -File MarkdownRenderer/eng/Invoke-MarkdownRendererTop500SourceAcquisition.ps1 `
    -CorpusManifestPath docs/markdown-renderer/evidence/2026-10-17/top-repositories.json `
    -OutputDirectory artifacts/readme-top500/restored-frozen-sources

# Tests runner accounting only; it is not page rendering.
dotnet run --project MarkdownRenderer/MarkdownRenderer.ReadmeCorrectnessAudit/MarkdownRenderer.ReadmeCorrectnessAudit.csproj `
    -c Release -p:Platform=x64 -- --self-test

# Parse the verified source store. The final output directory must not exist.
dotnet run --project MarkdownRenderer/MarkdownRenderer.ReadmeCorrectnessAudit/MarkdownRenderer.ReadmeCorrectnessAudit.csproj `
    -c Release -p:Platform=x64 -- `
    docs/markdown-renderer/evidence/2026-10-17/top-repositories.json `
    artifacts/readme-top500/restored-frozen-sources `
    artifacts/readme-top500/restored-headless-results
```

HTTP availability can change. Preserve real failures and the frozen identities;
do not silently accept a different commit, README or repository. Headless parsing
does not load images or exercise layout, copy, selection or navigation. A
nonzero result containing the known unsupported TeX diagnostics is not a clean
complete audit and must not be reported as one.

## Local data not in this checkpoint

The repository ignores generated `artifacts`, build outputs and test-result
directories. Source commits plus this compact archive do **not** preserve:

- Full TRX/binlogs, original raw receipts and matching test/app output trees.
- PNG/video captures or the complete browser/native page-and-asset corpora.
- The 500 acquired README bodies, HTTP captures and source-object caches.
- Temporary private native source exports, symbol caches and experimental builds.
- The retained full process-memory crash dump or prepared unused debugger tools.

The R6 original artifact root is named
`MarkdownRenderer-Correctness-R6-69832a42ceca41d98c97fac98c9408d4` under the
original machine's local temporary directory. The original-flags replay root is
`MarkdownRenderer-SandboxCrash-R3-OriginalFlags-20261017-71e828ab/full-sequence`.
The sanitized receipts retain other original artifact-root suffixes and file
hashes. Repository-local raw data remains under the original `artifacts/...`
locations listed by the archive index and the detailed correctness plan.

Before wiping or replacing storage, retain any required raw evidence in a
separate approved backup and verify its hashes. The crash dump can contain
process memory and must remain private; it is deliberately excluded from Git.
Native source patch manifests/locks and selected-RID shipping binaries are
already versioned separately in the implementation backup commit. Rebuilds and
reacquisition can reproduce new runs, but cannot reconstruct the original crash
memory or screenshots once those local files are lost.
