# MarkdownRenderer correctness closure (October 25, 2026)

This document closes the correctness goal adopted October 12, 2026 (see
[the correctness plan](correctness-plan.md)). It records what each phase
proved, on which candidate, and what is still open. It supersedes the
[October 17 handoff](project-handoff-2026-10-17.md) as the current status.

The goal is correctness within the documented feature set. Performance,
browser parity, the Rust rewrite and the distribution checklist (API
approval, AOT for every package combination, signing, NuGet publishing) are
out of scope and remain open; see [Current gaps and roadmap](current-gaps-and-roadmap.md).

## Outcome

| Phase | Result | Open items |
| --- | --- | --- |
| 1 Core document correctness | **Closed.** Every frozen case group passes live or through an attached current-source receipt. Every known Phase 1 defect (P1-001 to P1-041) is fixed and regression-covered. The full batch passes at actual 100%, 150% and 200% display scaling. | P1-042, a one-off scroll observation that never recurred (suspected only). Four live tooling limits, listed below. |
| 2 Existing feature correctness | **30 of 32 matrix rows pass**; the other 2 are hardware-gated. Live rows also pass at 200% on the final candidate. | P2-MERMAID-01 and P2-SVG-05 need ARM64 hardware. |
| 3 Real-world audit | **Complete.** All 500 frozen READMEs were evaluated, and every renderer defect found (P3-001 to P3-023) is fixed. | None in scope. The audit candidate predates four narrow fixes (see Candidates). |

## Candidates

Product code is the renderer libraries and the JitHub app. Commits that change
only the harness, Sample or docs are not product changes.

| Candidate | Product change | Qualified by |
| --- | --- | --- |
| `9ff81fe0` | P3-023 carried image failures | Phase 3 P3-023 reruns (app19). Phase 2 matrix run 6 (`c482610e`, same product code). Phase 1 clipboard closure (`cc7fda21`, 100%). Phase 2 system High Contrast (`af14395a`). |
| `73bfe432`, `a40b8655` | P1-038 range clipping, V1-005 handle grid, P1-039 label trimming | 150% DPI gate (full Phase 1 batch, 51/0/11) |
| `50d14a39`, `7be71973` | P1-040 handle right-click, P1-041 unowned handle release | 200% DPI gate (full Phase 1 batch, 55/0/10); Phase 2 live rows at 200%; closure suites on `385a552d` |

The final product revision is `7be71973`; later commits are evidence and docs.
After the Phase 3 audit candidate, the product changed only in those four
commits. They cover selection-handle placement and routing, range rectangles
across wraps, and the inline failure label. The 500 READMEs were not re-audited
after them; the 150% and 200% batches and the full suites cover them.

The last full Phase 1 batch at 100% ran on the October 21–22 qualifying
candidate (Q2–Q5, P1-032 closure). At 100%, the later candidate `cc7fda21`
qualified the clipboard and selection closure cases (8/8).

## Phase 1 evidence

- **Live batches.**
  - 100%: Q2–Q5, October 21–22. Q3–Q5 have no FAIL. The P1-032 stress run was clean.
  - 150%: `dpi-150-gate.json`. Every probe ran at 144 DPI.
  - 200%: `dpi-200-gate.json`. Every probe ran at 192 DPI, and all 112 probes of the qualifying run report it.
  - The user set display scaling on the host; no runner changed a display setting.
- **Themes.**
  - Light, Dark, forced High Contrast and 200% text scale run in every batch.
  - Actual Windows High Contrast passes in the follow-up runner (October 21) and in Phase 2 (`phase2-system-high-contrast-af14395a.json`).
- **Follow-up and touch runners (October 22).** These pass in both viewport modes:
  - C10 handle crossing and edge autoscroll;
  - C11 point-range queries;
  - C14 unload/reuse and dispose during touch.
- **Current-source receipts** (`closure-suites-385a552d.json`, Release/x64):

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

Each fix has a regression test that failed before it and passes after; the
receipts are in `evidence/2026-10-25/`. P1-041 also has a live reproduction:
the failure needs a touch point 3 px inside the handle, and passes 13 px above it.

### Live tooling limits (not product defects)

- **P1-031 / P1-006.** Injected `POINTER_FLAG_CANCELED` packets reach WinUI as ordinary releases. Native cancellation is covered by offline classifier tests only.
- **C14 capture loss.** WinUI raises no in-contact `PointerCaptureLost` when code releases a touch capture. The renderer's cleanup after a real capture move is verified.
- **T1-021.** A no-frame stationary hold cannot be delivered. A repeated-position hold covers edge autoscroll.
- **Clipboard isolation.** The Sandbox shares its clipboard with the host. Copy assertions read the shared clipboard, so isolation cannot be attested.
- **V1-004.** This investigation only counts inside its 1.5-second envelope. It passed at 100% (r3) and at 200% (focused run). In the 200% qualifying batch, guest setup took 1,566 ms, so it was recorded as UNVERIFIED.

### P1-042 (open, suspected)

Once, in the first 200% batch, a UIA scroll to the bottom of the Mermaid page
stopped at 1.35%. It did not recur in four later C15 runs or in 36 in-guest
probe iterations. A layout commit restoring its scroll anchor over a scroll
request that has not applied yet is one possible cause, but no evidence
confirms it. Reopen it with a reproduction before changing code.

## Phase 2 evidence

- **Matrix.** `phase2-matrix.json` has 30 PASS and 2 UNVERIFIED rows. Run 6 qualified candidate `c482610e`; the 2 open rows are gated by ARM64 hardware.
- **P2-RASTER-02 (October 26).** The animated WebP needs the WebP Image Extension. Windows Sandbox cannot install it: registering the Store-signed package fails with 0x80073CFF, because Store packages cannot be sideloaded. With the user's approval, the check ran on their desktop, which has the extension, on product revision `7be71973`. The center showed the first canvas's blue and the corner stayed transparent at 0 and 1,500 ms (`evidence/2026-10-26/p2-raster-02-desktop-webp.json`).
- **x86 and x64.** NativeAOT smokes cover the worker on both (`phase2-aot-smokes-x86-x64.json`).
- **Plain-text copy.** Mermaid plain-text copy is qualified live (`phase2-mermaid-plain-copy-8141e49c.json`).
- **System High Contrast.** P2-MERMAID-03 and P2-HIGHLIGHT-02 pass under actual Windows High Contrast.
- **200%.** The live rows pass at 200% on the final candidate.
  - Three rows first failed only because content sat outside a 432-DIP-tall window.
  - The harness now brings that content into view first; no oracle changed (`dpi-200-gate.json`).

## Phase 3 evidence

- **Full audit.** The run (`phase3-top500-run-app18.json`) used candidate `8ceae293` and the frozen manifest `615c5672…`. It evaluated 500 of 500 READMEs:

  | Outcome | READMEs |
  | --- | --- |
  | Pass | 368 |
  | Classified (upstream unavailable, unsupported syntax or documented limit) | 129 |
  | Environment or documented limit (T3-008) | 3 |

  The three T3-008 cases are a 44.6-megapixel PNG, AVIF without the AV1 codec, and animated WebP without the WebP codec.
- **P3-023.** The four ranks with images still pending were rerun on `9ff81fe0` (`phase3-p3023-reruns-app19.json`). All four are classified.
- **Clipboard.** It is shared with the host, as recorded in the receipt and accepted by the operator.

## Evidence hashes

SHA-256 of the committed receipts, as of the latest commit that changes this document.
Raw run directories under `MarkdownRenderer/artifacts/` are Git-ignored; each
receipt names its run IDs.

| Receipt | SHA-256 |
| --- | --- |
| `evidence/phase2-matrix.json` | `784e77590a601786e9745214520452bf1313f54c5aa02cc874771fe3a939ecad` |
| `evidence/2026-10-22/phase2-offline-evidence.json` | `2acd4fb1d2a56101217487daea8725ab859cd43f90717617efc292114fdc2db2` |
| `evidence/2026-10-22/phase3-top500-run-app7.json` | `52c2f1ec353a66d58ca2a87cacc17c90d0c0a6878382ebc850600ea7e0dd092a` |
| `evidence/2026-10-22/touch-runners.json` | `b0698f8a91a5b521c367cac63c9163236907426e09d973646b38a9ab0b8acb54` |
| `evidence/2026-10-23/phase3-reruns-app16-app17.json` | `155cdf3a87384368e09633d9518d672cffb7bec396160cf856237c87c80c9181` |
| `evidence/2026-10-23/phase3-top500-run-app14.json` | `e54d641697aad7a6932c9a5bd9df912b1314ec117904d10cd9ac9d1439601dbd` |
| `evidence/2026-10-24/phase1-clipboard-selection-cc7fda21.json` | `53231c3fab5308a9b5aca3bc97d91f41b02c6863411c2beef0cc4ef6b6d3bc90` |
| `evidence/2026-10-24/phase2-aot-smokes-x86-x64.json` | `3f307ef46db7dfb6c36d0a679afc979e18046a06b9b75523fe71414dc7b2f1b8` |
| `evidence/2026-10-24/phase2-live-run6-c482610e.json` | `6ea0832ed1825d2e12454a37eeb875c2e2325cb969c5796e9224a9fa2af8ea5c` |
| `evidence/2026-10-24/phase2-mermaid-plain-copy-8141e49c.json` | `1f8fffeac382e03bda31e5b248738d365da66945281dd006cbf08b37e4c19512` |
| `evidence/2026-10-24/phase2-offline-evidence-c482610e.json` | `fb3516283498bd2e4b634eeba0a9893c6d18303c670c49a4753802d3d6e38232` |
| `evidence/2026-10-24/phase2-system-high-contrast-af14395a.json` | `fdfee5c650e0d112e9915b67d02780d398551be716e335592faeac7c65dc2c76` |
| `evidence/2026-10-24/phase3-p3023-reruns-app19.json` | `3ef553797e68e5a686d16c5aa1cbdb932b2c3d8666169665b09d265e33aa6776` |
| `evidence/2026-10-24/phase3-top500-run-app18.json` | `d711247b94b8cf505238dd637fa4ca5877034d73bdf949b1737992370c9a864b` |
| `evidence/2026-10-25/closure-suites-385a552d.json` | `e1125910e1403a21bfe0de0d57c27285980758c1bfe39e4e2dde9df95ffd3995` |
| `evidence/2026-10-25/dpi-150-gate.json` | `b110ede49aa63bca4eed7c0baa963d88fd69e15d0e916dfbef92ce217ee1b651` |
| `evidence/2026-10-25/dpi-200-gate.json` | `cc4c35e8e4041026d456de9b8eeb6ecedb7ec2c025f7f3a85f5df0ab7ac3cc43` |
| `evidence/2026-10-25/p1-040-green.txt` | `6f529d95bbce8befb2262db5fc1360843af57fa9087ed4f6dcad41df826e2b56` |
| `evidence/2026-10-25/p1-040-red.txt` | `af50745d7c1ea973eb217226ce7c94cb3e1a40d32876f3e4db0c3c0432456b68` |
| `evidence/2026-10-25/p1-041-green.txt` | `eed8ed8fcc63c784ed005c1d50423e4ee68b14f545930298633459c07af1318c` |
| `evidence/2026-10-25/p1-041-live-before.txt` | `88e3715da78a10a2c39b140b03f60c40aac37a7b4e4f11dbce6f4820b0bffa82` |
| `evidence/2026-10-25/p1-041-red.txt` | `fef3b650e3ba7b01c593450e90a3d21f46bc3e7fc4e00044dcdedad65ac0123e` |
| `evidence/2026-10-26/p2-raster-02-desktop-webp.json` | `4de120c610f6e85105899d16e16b32f65c9b22a8cb42880017a6dfdbaf246f0f` |
| `evidence/2026-10-17/top-repositories.json` | `615c56723fb16d17744af2549615a4de32b1fc299a1759727cc86b70edacac72` |
| `evidence/2026-10-17/top500-source-integrity.json` | `88f067fdd565ea988ac5b496f864b9f35997b5c889303da04527c6d7a224a8b1` |

Hashes are of the blobs stored in Git (`git cat-file blob <commit>:<path> | sha256sum`).
