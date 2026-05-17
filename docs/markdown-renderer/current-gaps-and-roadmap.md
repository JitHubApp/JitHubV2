# Roadmap

This document summarizes release-readiness work and deferred feature tracks.

## Maturity snapshot

| Area | Status | Release note |
| --- | --- | --- |
| Rendering | 1.0-ready except excluded areas | Raw HTML and LaTeX/math are intentionally tracked separately. |
| Selection | 1.0-ready | Manual clipboard smoke across target apps remains. |
| Theming | Implementation complete | Manual contrast-theme smoke and consumer presets remain as release validation. |
| Accessibility | Implementation complete | Manual Narrator, contrast-theme, and language smoke remain. |
| Performance | 1.0-ready | Manual long-document/device-reset stress remains. |
| Packaging/API | Complete | Release validation and NuGet publishing remain. |

## Release-blocking items

- Run the manual 1.0 smoke matrix: Narrator, every built-in Windows contrast
  theme, one custom contrast theme, RTL/system-language behavior, monitor or
  graphics-device reset, and x86/x64/ARM64 sample launch.
- Inspect Release packages for metadata, XML docs, README/icon/license, symbols,
  and all ThorVG native assets.

## Before 1.0

- Publish signed NuGet packages after release validation.
- Keep raw HTML and LaTeX/math documented as out of scope for this release.

## v1.1 candidates

- Math/LaTeX support.
- Safe raw HTML policy and renderer.
- Optional richer diagram package built on the embed API.
- Targeted recycling for additional proven pure managed helper allocations.

## Deferred feature tracks

- Raw HTML rendering needs a sanitizer and native rendering policy before it can
  be enabled safely.
- LaTeX/math rendering is intentionally deferred.

## Release validation debt

- Manual release smoke is still required because UIA, contrast themes,
  language/RTL behavior, and graphics-device reset cannot be trusted from unit
  tests alone.

## Source of truth for work tracking

The repository also contains `MarkdownRenderer\TODO.md` for checklist-style
engineering work. When adding new maturity work, update this roadmap and that
backlog together.
