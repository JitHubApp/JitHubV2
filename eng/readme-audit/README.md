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
  CPU/working-set evidence, so evidence capture is not mistaken for rendering.

Run an optimized smoke audit (Release is the default and is required for
browser-relative performance evidence):

```powershell
.\eng\Invoke-TopReadmeAudit.ps1 -Count 5
```

Run or resume the complete release corpus:

```powershell
.\eng\Invoke-TopReadmeAudit.ps1 -Count 500 -Resume
```

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

The scheduled/manual workflow runs ten isolated 50-repository shards and then
requires a consolidated, duplicate-free set of ranks 1–500. Missing shards or
case files fail the final job; a partial run cannot be reported as a top-500
pass.

JitHub's audit build remains framework-dependent, matching the production
deployment model. Each hosted runner installs the exact x64 Windows App Runtime
1.8.10 release used by the app through Microsoft's silent installer, after
verifying its pinned SHA-256. Native launch failures preserve isolated startup
phase/error logs and abort the shard immediately because they occur before any
repository-specific work and cannot produce meaningful per-case comparisons.
