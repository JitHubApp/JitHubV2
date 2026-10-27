# MarkdownRenderer correctness closure (October 25, 2026)

This document closes the correctness goal adopted October 12, 2026 (see
[the correctness plan](correctness-plan.md)). It records what each phase
proved, on which candidate, and what is still open.

The goal is correctness within the documented feature set. Performance,
browser parity, the Rust rewrite and the distribution checklist (API
approval, AOT for every package combination, signing, NuGet publishing) are
out of scope and remain open; see [Current gaps and roadmap](current-gaps-and-roadmap.md).

## Outcome

| Phase | Result | Open items |
| --- | --- | --- |
| 1 Core document correctness | **Closed.** Every frozen case group passes live or through the current-source test suites. Every known Phase 1 defect (P1-001 to P1-042) is fixed and regression-covered. The full batch passes at actual 100%, 150% and 200% display scaling. | Four live tooling limits, listed below. |
| 2 Existing feature correctness | **30 of 32 matrix rows pass**; the other 2 are hardware-gated. Live rows also pass at 200% on the final candidate. | P2-MERMAID-01 and P2-SVG-05 need ARM64 hardware. |
| 3 Real-world audit | **Complete.** All 500 frozen READMEs were evaluated, and every renderer defect found (P3-001 to P3-023) is fixed. | None in scope. The audit candidate predates four narrow fixes (see Candidates). |

## Candidates

Product code is the renderer libraries and the JitHub app. Commits that change
only the harness, Sample or docs are not product changes.

| Candidate | Product change | Qualified by |
| --- | --- | --- |
| `9ff81fe0` | P3-023 carried image failures | Phase 3 P3-023 reruns (app19). Phase 2 matrix run 6 (`c482610e`, same product code). Phase 1 clipboard closure (`cc7fda21`, 100%). Phase 2 system High Contrast (`af14395a`). |
| `73bfe432`, `a40b8655` | P1-038 range clipping, V1-005 handle grid, P1-039 label trimming | 150% DPI gate (full Phase 1 batch, 51/0/11) |
| `50d14a39`, `7be71973` | P1-040 handle right-click, P1-041 unowned handle release | 200% DPI gate (full Phase 1 batch, 55/0/10); Phase 2 live rows at 200%; closure suites on `385a552d`; P2-RASTER-02 desktop WebP check |
| `0159d16b`, `31342c1e` | P1-042 focus reveal and focus-entry suppression | Final full Phase 1 batch at 192 DPI (56/0/9, run `20261026T070246Z-ed213cac9d224e7299aa2cfe53d79960`); cold-start probes 5/5; GitHub.Tests 3,324/3,324 |

The final product revision is `31342c1e`; later commits are CI and docs.
On October 26 the three Mermaid native DLLs were rebuilt from the same source
with path remapping, so they no longer embed the build machine's paths. The
Mermaid and GitHub test suites pass on the rebuilt DLLs; the live runs above
used the earlier build.
After the Phase 3 audit candidate, the product changed only in those six
commits. They cover selection-handle placement and routing, range rectangles
across wraps, the inline failure label, and keyboard focus entry and reveal. The 500 READMEs were not re-audited
after them; the 150% and 200% batches and the full suites cover them.

The last full Phase 1 batch at 100% ran on the October 21–22 qualifying
candidate (Q2–Q5, P1-032 closure). At 100%, the later candidate `cc7fda21`
qualified the clipboard and selection closure cases (8/8).

## Phase 1 evidence

- **Live batches.**
  - 100%: Q2–Q5, October 21–22. Q3–Q5 have no FAIL. The P1-032 stress run was clean.
  - 150%: run `20261025T085026Z-93b2199dbfc1477a898fb2edf2285f2e` (51/0/11). Every probe ran at 144 DPI.
  - 200%: run `20261025T194830Z-8459e7f3d6be459e9517dccabc4c0d52` (55/0/10). Every probe ran at 192 DPI, and all 112 probes of the qualifying run report it.
  - Final candidate (`31342c1e`) at 192 DPI: 56 pass, 0 fail, 9 standing UNVERIFIED (run `20261026T070246Z-ed213cac9d224e7299aa2cfe53d79960`).
  - The user set display scaling on the host; no runner changed a display setting.
- **Themes.**
  - Light, Dark, forced High Contrast and 200% text scale run in every batch.
  - Actual Windows High Contrast passes in the follow-up runner (October 21) and in Phase 2 (run `20261025T012400Z-1e90a5bece2c43a3a29516fd4e92ecd9`).
- **Follow-up and touch runners (October 22).** These pass in both viewport modes:
  - C10 handle crossing and edge autoscroll;
  - C11 point-range queries;
  - C14 unload/reuse and dispose during touch.
- **Current-source suites** (`385a552d`, Release/x64):

  | Suite | Passed |
  | --- | --- |
  | Official CommonMark 0.31.2 + GFM 0.29 corpus (C01) | 1,322 / 1,322, 0 exceptions |
  | MarkdownRenderer.Tests | 2,047 |
  | MarkdownRenderer.GitHub.Tests | 3,321 |
  | Svg.Resvg.Tests | 753 |
  | Mermaid.Tests | 259 |
  | Math.Tests | 80 |
  | TextMate.Tests | 74 |
  | Html.Tests | 10 |
  | Conformance harness | 9 |

  Every suite has 0 failures and 0 skips. Its image-policy tests attach C07, and the feature projects attach C15.
- **Screenshots** were reviewed for every scale: no clipping, overlap, missing content or theme defect remains.

### Defects found by the DPI gates

| ID | Found at | Fix |
| --- | --- | --- |
| V1-005 | 150% | Handles snap 1/16 px past the root pixel grid, with one physical pixel of slack (`73bfe432`). |
| P1-038 | 150% | Range rectangles clip whitespace hanging past a wrap (`73bfe432`). |
| P1-039 | 150% | Compact image-failure labels trim with an ellipsis (`a40b8655`). |
| P1-040 | 200% | A mouse right-click on a selection handle opens the selection menu (`50d14a39`). |
| P1-041 | 200% | A double-tap's release that lands on a newly shown handle still ends its touch contact. Before, all later touch was ignored (`7be71973`). |

Each fix has a regression test that failed before it and passes after. P1-041 also has a live reproduction:
the failure needs a touch point 3 px inside the handle, and passes 13 px above it.

### Live tooling limits (not product defects)

- **P1-031 / P1-006.** Injected `POINTER_FLAG_CANCELED` packets reach WinUI as ordinary releases. Native cancellation is covered by offline classifier tests only.
- **C14 capture loss.** WinUI raises no in-contact `PointerCaptureLost` when code releases a touch capture. The renderer's cleanup after a real capture move is verified.
- **T1-021.** A no-frame stationary hold cannot be delivered. A repeated-position hold covers edge autoscroll.
- **Clipboard isolation.** The Sandbox shares its clipboard with the host. Copy assertions read the shared clipboard, so isolation cannot be attested.
- **V1-004.** This investigation only counts inside its 1.5-second envelope. It passed at 100% (r3) and at 200% (focused run). In the 200% qualifying batch, guest setup took 1,566 ms, so it was recorded as UNVERIFIED.

### P1-042 (fixed October 26)

In the first 200% batch, a UIA scroll to the bottom of the Mermaid page stopped
at 1.35%. Warm reruns passed, but a cold-start probe reproduced it 5 of 5
times: each scroll ended at 1.35%, or reached 100% and snapped back to 4.42%.

Temporary logging found the cause:
1. A UIA client sets focus on the document before scrolling it.
2. `FocusDocumentFromAutomation` suppresses first-item promotion for that focus, but cleared the flag on the dispatcher.
3. WinUI raises `GotFocus` after that, so the first focusable item was promoted and its reveal scrolled back over the client's scroll.

The fixes:
- `31342c1e` captures the suppression in the synchronous `GettingFocus` event.
- `0159d16b` keeps a delayed focus-reveal retry from overriding a newer scroll request.

Each fix has red/green regression tests. Afterwards C15 passes on fresh launches and 5 of 5 cold-start scrolls reach 100% and stay there.

## Phase 2 evidence

- **Matrix.** The Phase 2 matrix (`MarkdownRenderer/eng/correctness-inputs/phase2-matrix.json`) has 30 PASS and 2 UNVERIFIED rows. Run 6 qualified candidate `c482610e`; the 2 open rows are gated by ARM64 hardware.
- **P2-RASTER-02 (October 26).** The animated WebP needs the WebP Image Extension. Windows Sandbox cannot install it: registering the Store-signed package fails with 0x80073CFF, because Store packages cannot be sideloaded. With the user's approval, the check ran on their desktop, which has the extension, on product revision `7be71973`. The center showed the first canvas's blue and the corner stayed transparent at 0 and 1,500 ms.
- **x86 and x64.** NativeAOT smokes cover the worker on both (at `4ba0144a`).
- **CI and the WebP codec.** GitHub's Windows Server runners have no WebP Image Extension. The 11 GitHub.Tests cases that decode WebP through WIC are tagged `Requires=WicWebpDecoder`; CI lists them in a warning as not run, never as passed. They pass where the codec is installed, including the closure suites.
- **Plain-text copy.** Mermaid plain-text copy is qualified live (run `20261025T020257Z-ecf4136288fc456ea43ddeeb71d1253b`).
- **System High Contrast.** P2-MERMAID-03 and P2-HIGHLIGHT-02 pass under actual Windows High Contrast.
- **200%.** The live rows pass at 200% on the final candidate.
  - Three rows first failed only because content sat outside a 432-DIP-tall window.
  - The harness now brings that content into view first; no oracle changed.

## Phase 3 evidence

- **Full audit.** The app18 run used candidate `8ceae293` and the frozen manifest `615c5672…`. It evaluated 500 of 500 READMEs:

  | Outcome | READMEs |
  | --- | --- |
  | Pass | 368 |
  | Classified (upstream unavailable, unsupported syntax or documented limit) | 129 |
  | Environment or documented limit (T3-008) | 3 |

  The three T3-008 cases are a 44.6-megapixel PNG, AVIF without the AV1 codec, and animated WebP without the WebP codec.
- **P3-023.** The four ranks with images still pending were rerun on `9ff81fe0` (app19). All four are classified.
- **Clipboard.** It is shared with the host, as recorded with the run and accepted by the operator.

## Run records

Run outputs are not committed. Raw run directories under
`MarkdownRenderer/artifacts/` are Git-ignored; this document names each run by
its ID and candidate revision.

The two correctness inputs are committed in `MarkdownRenderer/eng/correctness-inputs/`:

| File | Purpose |
| --- | --- |
| `top-repositories.json` | Frozen 500-README manifest for the Phase 3 audit. The scripts require its raw SHA-256 to be `615c56723fb16d17744af2549615a4de32b1fc299a1759727cc86b70edacac72`. |
| `phase2-matrix.json` | The 32 frozen Phase 2 cases, their offline test filters and their recorded outcomes. |
