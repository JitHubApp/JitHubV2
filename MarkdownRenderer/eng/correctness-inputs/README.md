# Correctness inputs

The MarkdownRenderer correctness scripts read these files. They are inputs, not
run results. Run outputs go to Git-ignored folders under `artifacts/`.

| File | Used by | What it is |
| --- | --- | --- |
| `top-repositories.json` | `Invoke-MarkdownRendererTop500SourceAcquisition.ps1`, `Invoke-MarkdownRendererCorrectnessHtmlAcquisition.ps1`, `eng/Invoke-TopReadmeSandboxCorrectnessAudit.ps1` and their script tests | The frozen September 28, 2026 list of 500 GitHub repositories for the Phase 3 README audit: repository, commit, README path, blob SHA and size for each. It holds metadata only, not README content. |
| `phase2-matrix.json` | `Invoke-MarkdownPhase2OfflineEvidence.ps1` | The 32 frozen Phase 2 feature cases, the offline test filters each case runs, and the outcome recorded for each case with its run ID. |

The scripts, `MarkdownRenderer.ReadmeCorrectnessAudit` and the WinUI corpus probe
check that `top-repositories.json` has the raw SHA-256
`615c56723fb16d17744af2549615a4de32b1fc299a1759727cc86b70edacac72`. The
`.gitattributes` in this folder stops Git from changing its line endings. Do not
edit or reformat it; a different membership needs a new manifest and new pinned
hashes.

See the [correctness plan](../../../docs/markdown-renderer/correctness-plan.md)
for the cases and the [correctness closure](../../../docs/markdown-renderer/correctness-closure-2026-10-25.md)
for the outcomes.
