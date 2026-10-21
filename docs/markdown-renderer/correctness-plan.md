# MarkdownRenderer correctness goal and phase plan

Active objective adopted October 12, 2026, from the user-approved
`goal-objective.md` attachment. The existing feature set is frozen. This plan
supersedes the performance and browser-parity program as the active work goal;
the old plans and failed results remain historical evidence.

For the current concise handoff, architecture/repair summary, progress,
challenges, methodology and continuation order, read the
[October 17 project handoff](project-handoff-2026-10-17.md). Selected sanitized
receipts and the byte-preserved frozen 500-repository manifest are versioned in
the [portable evidence archive](evidence/2026-10-17/README.md). Original
`artifacts/...` paths below preserve historical locations; most raw artifacts
remain Git-ignored and need a separate backup before local storage is discarded.

## Goal

Make the existing MarkdownRenderer reliably correct within its documented
feature set. Supported Markdown must parse correctly, render the intended
content, and support accurate text selection and copying. Loading, navigation,
cancellation, and document replacement must leave the control in a correct state
without crashes, hangs, stale content, or broken interactions.

The renderer does not need to match a browser's appearance, performance, memory
usage, or implementation. Native presentation choices are acceptable when they
preserve content and meaning. Performance optimization, additional features,
and a Rust migration are separate future projects.

Complete means no known functional defects remain within the phase's agreed
scope, with passing regression tests and live interaction validation. It does
not mean proving that no undiscovered bug can exist. Supported syntax is defined
by [Supported markdown](supported-markdown.md), and selection/copy by
[Selection and clipboard](selection-and-clipboard.md). Existing safety limits,
host image policy, and borrowed-service ownership remain functional contracts.

## Phase 1 Core document correctness

Deliver a dependable native Markdown reader using the existing implementation.
Scope includes CommonMark/GFM, supported HTML text formatting, image sizing and
table containment, source positions, selection and clipboard, both viewport
ownership modes, resize/scroll, navigation/cancellation/disposal, and regression
smoke coverage for every optional feature. Mouse, keyboard, and touch selection
must work across formatting and blocks, including Unicode, combining characters,
emoji, RTL, and embedded objects at the document's end.

Prioritize crashes, hangs, lost or incorrect content, wrong layout/hit geometry,
and broken selection/copy before cosmetic refinements. The frozen cases below
define the phase; add a case only when a genuine defect needs a regression test.
An optional provider's integration bug belongs here even when exhaustive syntax
certification belongs in Phase 2.

### Frozen fixture and interaction set

The checked-in sample sources and existing test fixtures at source checkpoint
`17f24924d2bf2f239a86ec91caa141a22e319646` are the starting corpus. Changes to a
fixture must preserve its purpose and expected meaning, rather than remove a
failure. Individual new regression tests must name the defect they prove.

| Case | Required behavior | Fixed fixture or suite |
| --- | --- | --- |
| C01 Parsing | CommonMark 0.31.2 and GFM 0.29 parse according to their documented syntax; no exceptions or waived examples. | Existing pinned offline conformance corpora and `ConformanceHarnessTests`. |
| C02 Core blocks and formatting | All intended text and block types appear, preserve order, and wrap without clipping/overlap. | `Typography`, `Lists`, `Tables`, `Selection` sample documents; native renderer regressions. |
| C03 Lists and tasks | Nested list structure and native task checkbox state/activation remain correct. | `Lists`, `TouchSelection`; existing tight-list and task-list tests. |
| C04 Code and links | Fenced/inline code remains legible/selectable; code copy and link activation remain independent controls/actions. | `Code`, `KeyboardNav`, `Selection`. |
| C05 HTML flow and scripts | Supported inline/cross-block formatting preserves content, wrap, and run positioning, including ordinary superscript/subscript phrases. | `Html`; `SafeHtmlRenderingTests`, `RelativeScriptFontSizeTests`, new script geometry regression cases. |
| C06 Image layout | Raster/SVG image aspect ratio, intrinsic hints, inline placement, and table containment remain correct at narrow widths. | `Images`, `LazyImages`; table/image relayout and inline placement suites. |
| C07 Image failures and lifecycle | Genuine failures receive documented fallback; valid admitted local images do not become unavailable; replacement/cancellation never publishes old images. | Existing source-lease, publication, failure, and retirement tests; `LazyImages` navigation. |
| C08 Source and copy | Half-open UTF-16 source spans agree with semantic selection; normal Copy yields rendered text and rich HTML; Copy Markdown preserves the selected source syntax. | Source-map/boundary/clipboard suites; `Selection`. |
| C09 Mouse and keyboard selection | Selection crosses lines, formatting, lists, and tables; release preserves it; Ctrl+A/C and Escape behave correctly. | `Selection`, `Tables`, `KeyboardNav`; existing live sample runner scenarios. |
| C10 Touch selection | Pan remains scrolling; hold/double-tap selects a word; handles, menus, crossing, continuous edge-scroll, and cancellation work. | `TouchSelection`; touch session, endpoint, and edge-scroll unit suites plus live gestures. |
| C11 Unicode and RTL | Selection never splits a grapheme; logical text/source order and visual endpoint/hit geometry remain coherent. | `TouchSelection`, `Rtl`; boundary/bidi regression cases. |
| C12 Embedded selection | Images, math, and diagrams retain visible content during selection; the final object is selectable within its bounds. | `Images`, `Math`, `Mermaid`; existing vector/image selection regressions and focused live checks. |
| C13 Viewport parity | Owned and ancestor scrolling preserve selection/hit geometry through scroll, resize, and relayout. | Both sample viewport modes; selection-adornment, scroll-anchor, and layout snapshot suites. |
| C14 State reset | Navigation, replacement, selection disable, capture loss, unload, and disposal leave no stale capture, timer, selection handles, or flyout. | `TouchSelection` navigation; deferred input, touch, snapshot retirement and disposal suites. |
| C15 Optional feature smoke | Math, Mermaid, SVG, highlighting, alerts, footnotes, Markdown Extra, and hosted controls keep their currently supported basic behavior and defined invalid-input fallback. | `Math`, `Mermaid`, `SvgStress`, `Code`, `GitHubAlerts`, `Footnotes`, `MarkdownExtra`, `Embeds`; feature pack tests. |

Native layout, rendering, hit testing, selection, and UI Automation must consume
the same geometry. Source-string contracts or self-consistency checks alone are
not proof of a correct position; use independently calculated or measured expected
geometry when testing placement.

### Initial defect and validation list

| ID | Priority and scope | Initial evidence | State |
| --- | --- | --- | --- |
| P1-001 | Incorrect HTML superscript/subscript phrase positioning. | October 12 checkpoint: OpenType `sups/subs` glyph substitution is incomplete for ordinary phrases; no uniform baseline geometry. | Fixed: 92 independent native checks, full native suite, and live raised/lowered phrase geometry and screenshot pass. Actual DPI matrix remains under C13. |
| P1-002 | Touch word selection skips standalone emoji or selects a neighboring word. | `FindWordBoundaries` excludes symbols and searches for a neighboring word; an emoji-only paragraph returns an empty range. | Fixed with symbol/keycap grapheme selection; focused tests pass. Live word selection remains required under V1-002. |
| P1-003 | Canceling a later ordinary touch contact clears an already committed selection. | `BeginContact` retains the `Selected` state, and pointer cancellation can then attribute the prior range to the new contact. | Fixed with per-contact prior-selection state and flyout cleanup; focused tests pass. Live control behavior remains required under V1-002. |
| P1-004 | A new nearby touch tap can be consumed as a previous hold gesture's duplicate event. | Executed production-equivalent policy characterization: pointer 17 holds at (20,40), then pointer 18 taps at (52,40) after 500 ms. Both are suppressed because the predicate checks only time/distance and a new contact never retires it. | Contact-epoch fix passes focused policy tests; deferred gesture ordering and live link activation still require validation. |
| P1-005 | Dropping a deferred terminal touch event can leave a stale primary owner and reject later touch contacts. | Production path: an accepted Press is processed, its Release is deferred for unavailable geometry, and a failed band-realization request drops the queue. `ResetDeferredPointerInput` clears the queued Release but never ends the touch-session or tap-policy owner; a new pointer is then rejected. | Fixed with epoch-checked terminal retirement; focused/core tests pass. Live control validation remains required. |
| P1-006 | A dropped deferred cancellation diverges from normal cancellation. | Independent review: the terminal-input retirement path calls `EndContact` for Cancel as well as Release, preserving selection created by that canceled contact. Normal cancellation clears that range while preserving a range committed before the contact. Further caller review confirms `OnUnloaded` resets the gesture epoch before queued-terminal retirement can match it. | Shared cancellation and unload ordering are corrected. Unload stages the delayed-recognizer barrier without discarding ownership, retires the exact queued terminal, then resets the epoch. The failing source-order characterization and subsequent 65-case focus, 1,471-case correctness slice, and 2,988-case native receipts are retained below. Normal unload still preserves committed selection. Actual WinUI unload with a queued Cancel remains live-unverified; policy/source-order coverage is not substituted for it. |
| P1-007 | Source/document replacement can retain a stale touch-session owner. | Independent review: replacement resets the gesture policy, but does not retire the active touch-selection session; a delayed hold can act on replacement content and a new contact can be rejected. | Fixed with transient/session reset and a recognized-gesture provenance guard surviving rebuild resets until a fresh accepted contact. Focused/core tests pass; live replacement remains required. |
| P1-008 | SVG admission conflicts with the documented unsupported-content fallback. | Fresh Sandbox screenshot `svg-final-object-selected.png` paints the script-bearing red rectangle and animated black circle although the checked-in fixture and image documentation require atomic typed placeholders. Existing static-snapshot tests instead expect the active elements to be stripped before admission. | Fixed: original-content admission regressions and full SVG suite pass; matching live pipeline produces typed accessible atomic placeholders, visually reviewed. Active code execution was not observed. |
| P1-009 | Standalone HTML script source disappears instead of appearing as inert literal text. | Fresh Sandbox C05 cannot find `unsafe-script-sentinel`. The frozen HTML sample and documentation promise visible inert source; the parser retains raw markup but the block renderer suppresses the element and returns an empty root instead of literal fallback. Existing nested-element tests do not cover this standalone path. | Fixed: 128 focused HTML cases and full native suite pass. Matching live UIA exposes the entire exact literal `<script>unsafe-script-sentinel</script>` once, including tags; frame/form sentinels are absent. Independent ordinal review is recorded in the final repair's `html-exact-literal-review.json`. |
| P1-010 | Direct SVG provider admission does not repeat forbidden-content checks inside nested SVG data resources. | Independent review finds that `PrepareSource` rejects nested scripts/events but direct `OpenAsync` reaches a nested metadata scan lacking the top-level forbidden-element and event-handler checks. The provider contract requires independent authoritative admission. | Fixed: direct nested rejection before worker attachment and allowed static nested open/render pass in the complete 717-case provider suite. |
| P1-011 | Relayout can retire an ordinary touch contact without suppressing its delayed recognized gesture. | Independent review finds that generic transient reset clears the accepted contact but does not set the replacement provenance latch; a delayed ID-less hold/tap can then dispatch against rebuilt geometry. | Core fix implemented and regression-covered by the 73-case touch focus and full native suite: reset preserves retired-contact provenance until a fresh accepted contact. Live relayout/navigation remains under V1-002/C14. |
| P1-012 | Lazy-image sample cannot demonstrate its advertised behavior. | The sample never supplies a network image resolver. Its four HTTPS fixtures therefore receive the documented `RemoteContentBlocked` fallback, while its instructions promise HTTP requests/cache behavior that cannot occur. This also explains the remote avatar placeholder; no upstream failure is inferred. | Fixed: four admitted deterministic local SVG fixtures preserve viewport-demand loading/scroll anchoring, explicitly make no HTTP requests, and direct remote-policy demonstrations to the appropriate pages. All four matching live fixtures render and their captures are reviewed in the final repair; library image policy is unchanged. |
| P1-013 | Ancestor-scroll UIA text rectangles do not match painted text. | Physical 100% first repair `manual-20261013T003632Z-*` captures stable geometry: the visible word `family` is at the viewport top, but its ancestor-mode UIA rectangle is at Y=492 instead of owned Y=175. Double-tap at the reported center (580,502) correctly selects the visibly different table word `selects`. All four preparation/result PNGs are reviewed; this is not a timing inference. | Corrected candidate passes the full 2,978-case native suite and actual 100% owned/ancestor selection at the same painted word rectangle. Actual 150%/200% qualification remains open; the first converter's double-origin failure is retained below. |
| P1-014 | UIA `RangeFromPoint` throws on valid TouchSelection content. | The physical 100% runs record `0x8000000B` (`E_BOUNDS`) at the stable visible `family` target in both viewport modes. Exact-fixture native reproduction identifies clipped Mermaid labels returning `Rect.Empty`; placement translation reconstructs its negative-infinite dimensions and throws. | Regression-verified: preserve absent geometry in both placement directions and in the peer's document-to-screen conversion, without suppressing exceptions or inventing visible bounds. Exact callback, clipped-range and placement regressions pass; the complete native suite passes 2,980/2,980 with zero skips and the diagnostic-free sample builds cleanly. Live COM point-query validation of the rebuilt payload remains required. |
| P1-015 | Sample theme toggle does not represent its startup appearance. | Rebuilt physical sample starts with `ThemeStatus=theme:dark` but the Dark theme toggle is unchecked. Its initial unchecked state does not raise the Unchecked handler, so asking it to remain off does not select Light. Final physical repair `manual-20261013T005103Z-*` stops in setup before touch; the exact-window read-only status query confirms Dark. | Fixed: initialize the existing toggle from the application's startup theme before registering handlers. Focused contracts pass, and fresh physical `manual-20261013T005726Z-*` reaches touch after explicit Light setup and successfully restores the original appearance. Its unrelated coordinate failure does not make that batch a pass. |
| P1-016 | A digit-based keycap emoji joins adjacent words during selection and word movement. | Independent literal UTF-16 range tests on `left1️⃣right`, Arabic, combining text and digit neighbors reproduce 11 failures out of 17 cases in `keycap-adjacent-characterization.trx`. Selecting the keycap directly is atomic, but word expansion classifies its initial digit as a word character and crosses it; moving forward skips the next word. | Fixed in word expansion/navigation with ASCII keycap-base eligibility. Literal regressions, 113 focused cases and eight native formatted-run/LTR/RTL cases pass. Fresh r3 also passes all 12 real-input cases: owned/ancestor × left word/keycap/right word × double-tap/long-press, with exact UIA/clipboard ranges and handle cleanup. This qualifies actual 100% only; the separate high-DPI matrix remains open. |
| P1-017 | A nested disclosure in a list attaches to the wrong parent when the list itself is inside another disclosure. | `github-details-list-indent-wrapped-characterization.trx` reproduces the 2-column list-content case: ordinary Markdig accepts a nested heading and the GitHub profile nests a top-level list disclosure, but an active outer disclosure makes the child disclosure a sibling of the list item. A separate sibling-opening regression reproduces an implicitly closed list disclosure retaining a descendant count of 1. | Fixed: non-consuming continuation rewinds to Markdig's `ColumnBeforeIndent`, and opening uses only `GetCurrentContainerOpened()` ancestors. The final details regressions and 3,070-case Release suite pass. No depth-limit change is needed. The passing 500-input engine receipt does not establish correct nested-list layout; the separate Debug lease-count validation failure is retained below. |
| P1-018 | An unclosed fenced-code body can leave its disclosure's source span short of the final literal closing-looking line. | `github-details-eof-fence-span-beforefix2.trx` reproduces all four LF/CRLF × standalone/formatted closer cases. The literal code stays present, but the enclosing span stops at the fence opening. | Fixed with a span update on code-content continuation, without consuming the literal line or changing indentation. All 14 details regressions and the final 3,070-case Release suite pass. The separate Debug lease-count validation failure is not attributed to this repair without diagnosis. |
| P1-019 | Supplemental images in disclosure summaries map to the wrong authored source. | `github-summary-image-source-beforefix.trx` reproduces four final-layout failures with same-line/multiline summaries and LF/CRLF. The image is visible, but its span starts 11–18 UTF-16 units before the authored image tag. The fragment parser receives stripped/trimmed summary text while the renderer uses one constant source offset. | Fixed with an explicit UTF-16 piecewise projection through tag stripping, indentation, CR/LF/CRLF normalization and nested HTML. All 36 focused cases and both final 3,070-case Debug/Release suites pass, including repeated images and exact Copy Markdown source slices. Source-map tests do not replace live clipboard/selection validation. The earlier Debug lease-count fixture failure is diagnosed and corrected under T1-007; its red receipt is retained. |
| P1-020 | Rich code copy loses indentation, tabs, repeated inline spaces, and nested code styling inside links. | Retained pre-fix TRX reproduces four whitespace failures and three linked-style-map failures. | Fixed with escaped logical block preformatting and whitelisted inline/style-map serialization. All 16 new cases and complete 3,094-case Debug/Release suites pass. Live target-app formatted paste remains required. |
| P1-021 | Selection/copy drops authored final code blank lines and HTML hard breaks. | Code displays `alpha\n`, but the committed semantic text is `alpha`; generated and authored terminal LF are trimmed together. | Fixed by retaining span-backed content and trimming only synthetic separators. Eight new regressions plus strengthened historical break assertions pass in both full 3,094-case suites. Exact LF/CRLF source mapping is preserved; live selection/copy remains required. |
| P1-022 | Rich selection copy flattens tables and discards row/column/header relationships. | `table-copy-beforefix.trx` reproduces 2/2 failures for GFM and supported HTML tables. Native semantic tables have two rows, two columns and four exact cell labels, but CF_HTML has no table structure. | Managed repair verified: all 12 table regressions and both complete 3,162-case Debug/Release suites pass. Partial ranges preserve columns without leaking unselected content; native reparse retains the grid and nested preformatted content does not emit an empty sibling. Live formatted copy/paste remains required; the freshly authorized Sandbox batch is in progress. |
| P1-023 | Large code selections map to the wrong authored UTF-16 source positions. | Four native-pipeline interior-sentinel regressions fail after the bounded heuristic projection declines large text. LF fenced 20 KiB expects `[10248,10249)`, gets `[10246,10247)`; CRLF expects `[10249,10250)`, gets `[10247,10248)`; LF fenced 80 KiB expects `[40968,40969)`, gets `[40966,40967)`; indented 20 KiB expects `[10244,10245)`, gets `[10242,10244)`. | Managed repair verified: exact parser-derived code/HTML-pre projections, prepared-source line/private-summary restoration, atomic authored CRLF spans, and shared extension-owned code layout. All 22 large-code, 8 HTML-pre, 4 summary-projection and 18 pure exact-segment cases pass, as do both complete 3,162-case native suites. Draft failures and the diagnostic-only mixed-binary receipt remain retained; live clipboard/boundary qualification remains open. |
| P1-024 | Native code chunks split emoji/combining/ZWJ graphemes between independent text layouts. | Actual native layout regression places each grapheme across the 32,768-unit chunk boundary. All three cases fail: that split is not a boundary in the independent `StringInfo.ParseCombiningCharacters` oracle; the emoji case can separate surrogate halves. The same receipt passes all 12 table-copy cases. | Managed repair verified: all 3 native grapheme, 10 boundary-helper cases and both complete 3,162-case Debug/Release suites pass. Preserve complete graphemes and authored lines, exact source offsets and concatenated text. No new live-input or actual-DPI qualification is inferred. |
| P1-025 | Length-based code chunks can manufacture visual rows and truncate the horizontal extent of one authored NoWrap line. | Retained native `r3`/`r4` diagnostic receipts reproduce both frozen cases: one authored 44,768-unit NoWrap line becomes two rows, and two authored long lines become five. A subsequent explicitly mixed-binary diagnostic confirms that line-preserving chunk boundaries fix the first case but leave a duplicate empty native row at the inter-chunk LF seam in the second. | Managed repair verified: all 7 native row-count/baseline/final-LF/repeated-measure cases, 28 existing visual-cache cases and both complete 3,162-case suites pass. Preserve whole authored lines and suppress only a verified nonfinal duplicate zero-character row, retaining raw metrics, source LF and a real final empty row. No public admission ceiling is raised; live-input qualification remains open. |
| P1-026 | GFM table compatibility restoration can replace an authored private-use Unicode character with a dash. | `gfm-masked-pua-beforefix-r1.trx` reproduces one failure and one passing unchanged-source control: a mismatched table separator activates restoration, and the authored U+E000 in another paragraph is lost. Source review confirms restoration replaces every U+E000 in every literal inline, not just recorded edits. | Fixed with verified recorded-mask restoration before AST remapping, preserving authored U+E000 alongside genuine masks and earlier inserted pipes. All 3 frozen cases, the 1,532-case correctness slice, 1,322 official examples and both full 3,162-case native suites pass. The backing-reference-only failed draft is retained; no colliding replacement marker or weakened Unicode oracle is used. |
| P1-027 | Supported HTML table captions disappear from the rendered document. | Fresh Sandbox `Html.png` shows `<caption>Native HTML table</caption>` in the source but no caption above the rendered table. Both supported-element lists admit `caption`, while `BuildTable` only collected table rows. | Fixed: three focused native regressions cover ordered caption content, formatting/source spans, copy, caption-only content and nested-table isolation; complete Debug/Release suites pass. Rebuilt r3 C05 explicitly requires and exposes the caption, and its HTML capture is visually reviewed. The original failed capture remains retained; actual high-DPI coverage remains a separate gate. |
| P1-028 | Selecting narrow block math/diagrams paints an opaque viewport-wide band and weakens transparent-content readability. | Fresh Sandbox C12 captures in both viewport modes show a solid blue band across the entire right pane; the actual final Math peer is only 54×50 pixels. `VectorSceneBox.GetSelectionRects` returns full arranged `Bounds`, while the old overlay filled those rectangles opaquely and repainted the narrower scene at 0.82 opacity. | Fixed for the reproduced normal-theme states: native pixel/geometry, capacity-fallback and High Contrast contracts pass, and r3's final Math/Mermaid captures in both viewport modes show readable content-bounded translucent selection, not an opaque viewport band. Both kinds also preserve complete independently authored source on the clipboard. Paint-only geometry preserves endpoint/hit behavior; actual system High Contrast and high DPI remain separate unqualified matrix rows. The old screenshots are not retroactively passed. |
| P1-029 | Same-document width relayout preserves selected text but loses touch selection handles. | Fresh Sandbox C14 retains exact `hold` selection and current UIA geometry after restoring width, but command 1358 returns `handles:[]`. A restyle resets transient input to Idle; the later same-document commit preserved the range without restoring handle presentation. | Component repair passes 50 focused state/provenance/control-wiring cases, the 1,541-case logic slice and complete native suites. r3's narrow state preserves exact selection and two 44-DIP handles; actual control and snapshot widths both equal 345 DIPs. The later wide restore suffers P1-032's native crash, so complete resize survival/handle restoration remains unqualified. The narrow capture does not independently establish a clipping defect. Restoration is exact-generation/range/same-document bound and preserves no contact/capture/gesture provenance. |
| P1-030 | Final-object selection/copy and a short-document handle drag do not satisfy their exact live oracles. | Fresh r2 selects only preceding text/newline for final SVG; Math omits source on copy. r3 fixes Math/Mermaid copy but still omits image alt and loses selection on a delivered handle press/drag. | Post-r3 component repairs now pass both complete 3,188-case suites: block image/embed/hosted after-endpoints round-trip to semantic end, preceding-text-only ranges exclude the next atomic rectangle, and the app-root outside-click hook exempts only its own two handle subtrees after clearing other renderers. Three semantic regressions and one root-order source contract reproduce the old failures first. Existing inline/vector boundary contracts remain unchanged. Exact final Math/Mermaid source copy is live-qualified at 100%; rebuilt image/handle live proof is still pending. T1-014 separately records the SVG gesture-target defect. |
| P1-031 | Native device cancellation flags are ignored when the packet is routed as a move or release. | The input dispatcher classified packets solely by routed event. The referenced Windows App SDK exposes `PointerPoint.Properties.IsCanceled`; r2's injected canceled-up packet was observed as a routed Release, so its native flag was not recorded and cancellation was not qualified. | Source repair classifies a flagged packet as Cancel/HandleCancel before both live processing and deferred enqueue; all 14 classifier cases and complete native suites pass. Terminal r3 command 1236 fails cancellation-delivery qualification: contact 75/1 has one press, one move, one raw/ordinary release, zero native-canceled packets, `lastNativeCanceled=false`, zero cancels and no active contact. This sequence does not prove mishandling of a genuinely flagged packet. Preserve the passive observer and the distinction; ordinary Release must not be relabeled Cancel. Live cancellation remains open. October 21 batches 1, 3 and 4 repeat r3 exactly: the injected `POINTER_FLAG_CANCELED|UP` packet reaches WinUI as one ordinary routed `Released` with `IsCanceled=false` and no native-canceled packet. The harness now records that exact pattern as an UNVERIFIED tooling limit; any other delivery mismatch still fails. Product cancellation remains covered by the offline classifier tests only. |
| P1-032 | Critical: the sample suffers native heap corruption around selected-content relayout/replacement. | Original r3 C14 exits during width restoration (`0xc0000374`). The later original-option R3 replay also crashes after command 1310 source replacement, before C14; its retained full dump detects double-free during Win2D `CanvasImageSource` destruction in XAML image-peer cleanup. | Open, highest priority. The detection stack is known; the first invalid release and root cause are not. At the user's request, investigation switches to ordinary managed source review, regression tests and Sandbox public-action replays, with no new debugger attachment, dumps or low-level process instrumentation. The R6 candidate closes drawing sessions before committing selection state, retains source owners across XAML attachment/detachment, and rejects stale publication. Its three new source-order checks fail before the change and pass afterward; these are not a native-crash reproduction or behavioral fault-injection proof. Current live qualification remains separate below. The October 19 structural candidate (`3d4343d`) removes the churn that feeds the detection path: one grow-only adorner surface per Image, no surface release or adorner detach on snapshot commits, deferred (never nested) Source mutation, exception-safe unload release, device-lost notification and a layout-device paint gate. Five new contracts fail 5/5 before the change; behavioral reentrancy/failure tests and both complete 3,217-case suites pass. P1-032 stays open until the original-option Sandbox replays pass; see the October 19–20 continuation below. **October 21 root cause:** the structural candidate still crashed (`0xc0000374`) in batch 2. With the user's approval, a guest-only page-heap batch captured a full dump. The fault is a read of freed memory that page heap attributes to a Win2D `CanvasImageSource`; its free stack is the .NET `ComWrappers` finalizer. The faulting access is XAML's `UIAffinityReleaseQueue` releasing a finalized element whose child reference still reaches that object's aggregated inner `SurfaceImageSource`. XAML's reference to the inner peer does not keep Win2D's outer object alive, so a XAML image source aggregated around a Win2D outer depends on GC order. The fix removes that presentation: the adorner draws into a `CompositionDrawingSurface` (via `CanvasComposition`) on a `SpriteVisual` hosted under an inert `Border`, and retired surfaces are disposed deterministically. Four new contracts fail 4/4 before the change and three new disposal tests fail to compile; both complete suites pass 3,226/3,226. Live qualification is in progress. |
| P1-033 | A wrapped image caption can make the after-image selection boundary unreachable from visible artwork. | Frozen `ImageBoxVisibleArtworkSelectionEndpointTests` fails **4/4 before production edits** for LTR/RTL and 1×/2× text scale: an independent 96×96 artwork rectangle sits in a block taller than 192 DIPs, and its lower half incorrectly maps to offset 0 rather than 1. | Component repair verified: split the actual painted `AutomationRenderDestination` when the point is in artwork; retain the old full-block fallback elsewhere and leave ordinary hit testing start-only. Focused green passes 4/4; final source passes both complete **3,192/3,192** Debug/Release suites, with no old cases removed. Post-artwork and top/bottom margin fallback checks pass; the post-artwork point is not claimed to be a caption glyph. Exact red/green/final source and receipt hashes are in `sandbox-r5-image-selection-code-verification.json`. Live selection and actual-DPI proof remain pending; this does not resolve P1-032. |
| P1-034 | A touch long-press inside a committed selection replaces it with the word under the finger. | Original-option batch 1 (`20261021T052930Z-0310…`, candidate `3d4343d`) fails `touch-double-tap-handle-drag-context-copy-and-cancel`: long-pressing inside the drag-extended range collapses it to its first word. `ProcessHolding` went straight to word selection, unlike tap and `ContextRequested`, which already keep a selection that contains the point. | Component repair: a hold inside the active selection re-selects the touch selection, suppresses the tap and returns before word selection, using the same `IsPointInsideSelection` query as tap/context. `TouchHoldInsideSelectionContractTests` fails 2/2 before the edit and passes after; the touch/selection filter passes 202/202. Live C10 proof is pending on the next batch. |
| P1-035 | A selected block image paints an opaque selection band across its whole arranged block. | Batch 1 and batch 4 `svg-last-object-selected-*` captures are identical. A 96×96 SVG under a short caption shows a solid selection band across the full 1,359-DIP block, including the empty width and the caption line. Math and Mermaid show content-bounded translucent selection since P1-028. `ImageBox.GetSelectionRects` returns `Bounds`, and the adorner filled those rectangles opaquely. | Component repair: `ImageBox` provides paint-only geometry through the shared `ISelectionBackgroundGeometrySource` contract, with a translucent tint over the painted artwork and an opaque band only behind the caption text. Full bounds remain the interaction geometry. The pre-existing composition-independent captures show this is not a P1-032 regression. `ImageBoxSelectionBackgroundTests` fail 3/3 before the change, with one opaque 600×129 background, and pass after it. All 216 image and vector selection tests pass. Live capture review is pending. |
| P1-036 | A touch handle resolved to an end role lands one grapheme past the hit-tested caret. | Follow-up run `followup-20261021T180939Z-52ec…` drags the `then` start handle past the end of `drag` in both viewports and selects `" drag "`. The independent UIA expectation is `" drag"`. `HitTestSelectionEndpoint` returns a caret boundary, but `TouchSelectionEndpointPolicy.Resolve` treated it as the index of the grapheme under the finger. On a boundary, `FindBoundaries` returns the following grapheme, so the end role took its end. This affects any end-handle drag that lands on a boundary, as well as a crossed start handle. | Component repair: a caret already on a grapheme boundary is the endpoint for either role, and only a mid-grapheme caret snaps to the side its role owns. Two new policy tests fail 2/2 before the change and pass after. The emoji/combining-sequence and RTL policy tests are unchanged. One older test that encoded the character-index reading (`alpha beta`, caret 8 → 9) now expects 8 and documents why. Core tests pass 1,541/1,541. Live requalification is pending. |
| T1-001 | Stale image-relayout recovery source assertion. | Fresh core baseline fails because it expects removed `RequestRebuild()` syntax; the real catch path already requests `RequestRebuild(RebuildReason.Full)`. | Corrected and focused rerun passes. No production behavior changed. |
| T1-002 | Reserved sample test URLs invoke the external shell and contaminate touch testing. | Final Sandbox command 0779 activates a dynamic `https://example.invalid/suppression/...` link; the next inventory shows a foreground OpenWith dialog. Eleven subsequent touch commands are refused because the app is not foreground. The sample previously intercepted only two exact fixture paths. | Corrected sample-only HTTP(S) routing for the exact reserved host; 11 URI-policy tests pass and Release/x64 builds cleanly. Ordinary links and renderer behavior are unchanged. Fresh live qualification remains required. |
| T1-003 | Test runner mistakes valid UIA/JSON behavior for selection defects. | Final repair treats a Single/empty/rectangle-free caret as selected content, shifts a PS7 UTC DateTime by seven hours during process identity comparison, reads a missing success status field, and expects one Escape to close both a flyout and selection. | Offline fixes and regressions pass under PowerShell 7 and Windows PowerShell 5.1. Disabled None still requires zero ranges/handles/menu; exact content and gesture bounds remain enforced. The original failed live results are preserved. |
| T1-004 | A second project-mode probe launch loses the original sample; discovery must also reject PID reuse. | The final batch records original PID 7004 before its second project-mode launch and no original afterward. The cause of termination is not proven. Independent review then finds that comparing only the newly launched PID/path/hash, without its exact start time, could certify a replacement instance. | The opt-in second launch now starts the exact existing unpackaged guest executable without project redeployment, verifies the original process before/after, and compares the discovered new process with the launch receipt before hashing/input or cleanup. Functional offline guards pass under PS7/PS5.1, including a 100-ns start-time change. Original-app survival still needs a fresh live batch. |
| T1-005 | The final-SVG selection test can pass without proving that the SVG itself is selected. | The original check only requires growth in selected-text length and any overlapping rectangle; neighboring text can satisfy it. | The harness now requires independently authored exact image-alt text, one semantic range, exact Copy-as-Markdown source, loaded image identity and bounds, in both viewport modes. Offline guards pass; new live execution remains required. |
| T1-006 | The new deferred-touch test has mismatched host/guest result and cleanup evidence. | Review finds the host expects a stdout wrapper although `target exec` forwards direct child JSON; raw Identity emits `exePath`, not the canonical `executablePath`/`processName` fields. The guest reports `pumpStopped=false` after ordinary Stop clears its reference, and `AutoResetEvent.Set()` leaks a Boolean into the terminal metrics return. The host also attempted normal close without affirmative worker cleanup. | Bounded integration fixes are regression-covered offline: exact direct-child result parsing, strict raw Identity validation and path-verified normalization, worker-join proof retained across ordinary completion, one terminal metrics receipt, and withheld close/remaining cases when cleanup is uncertain. The host's 58 and driver's 63 offline cases pass under both PowerShell 7 and 5.1. This new driver has not run live; P1-005/P1-006 remain unqualified. |
| T1-007 | The completed-SVG table relayout test checks temporary bitmap leases before their asynchronous retirement barrier. | Earlier Debug receipt `markdown-details-projection-p1-019-debug-final.trx` records expected 3 / actual 4 at line 690, while the existing context drain runs only in `finally` afterward. Prepared clones acquire a lease, and image retirement explicitly force-yields before releasing it. The neighboring table tests already assert exact counts after the drain. | Corrected after independent ownership/retirement review: the same exact equality runs after the existing drain. Expected counts, timeout, production behavior and cleanup remain unchanged. All 36 table-image tests and the full 3,070-case suites in both Debug and Release pass with zero skips. No production leak was demonstrated. |
| T1-008 | Custom-artifacts UI validation can silently launch and verify the wrong output tree. | The first newly authorized Sandbox run records `buildArtifactsPath=null` and matching host/guest hashes under the older E: defaults despite the caller supplying the tested C: output. Script-level assignment clears the bound parameter before it is inspected; extracted-helper checks miss the script-scope alias. | Fixed initialization preserves the bound value. A regression executing the real parameter block and pre-function initialization as an actual temporary `.ps1` fails before the fix and passes afterward in PS7/PS5.1. The stale-output run is canceled and not qualified; its exact sample is normally closed. The completed r2 rerun independently matches all eight C: candidate assemblies and exact guest process identity; its 39 pass / 14 fail / 10 unverified results remain a failed batch, not an integrated correctness pass. |
| T1-009 | Empty-handle projection prevents Unicode touch cases from reaching input. | Fresh Sandbox command 0720 returns valid `{handles:[]}`; P1-016 exits before its first gesture. Executing the exact old AST assignment reproduces a strict-mode exception in PS7 and PS5.1. The combining-text setup uses the same pattern. | Fixed: explicit zero/one/two arrays are preserved and missing/null/scalar states rejected in PS7/PS5.1. r3 reaches and passes all 12 keycap interactions plus Arabic/combining text; these were harness failures, not renderer defects. Actual high DPI remains open. |
| T1-010 | Selected-link menu check reads an absent enabled-state property. | The `raw-name` guest mode returns a name, not `isEnabled`; r2 stops before Copy Link. | Fixed: the existing process-wide `focus-state` reader exposes `isEnabled` without changing focus. r3 confirms native Copy Link is enabled, copies the independently expected exact URL and does not activate it. The corresponding menu capture is reviewed. |
| T1-011 | Authored LF fixtures are compared byte-for-byte with native TextBox CR source. | Mermaid's r2 clipboard contains all exact source characters but CR separators; captured editor values independently confirm native CR storage, as documented in the sample. | Fixed: expected source is predicted from independently authored fixtures using only native CR line-ending normalization, never copied from the selected range/editor. LF/CRLF/CR and non-line-ending-change controls pass offline; r3 passes exact complete final Math and Mermaid clipboard checks in both viewport modes. Math's original missing-source failure was a separate production repair, not waived. |
| T1-012 | Selection-disable probe child exits before an exact window identity is discovered. | r2 launches guest PID 4740 from the original executable, then no exact probe title is found. A later read-only inventory finds only the original PID 1916. No unidentified child receives cleanup input. Batch 1 repeats it with PID 4064 while the original app survives. | Root cause found; harness, not renderer. Two causes stack: the framework-dependent sample runs on WinApp's private runtime (`C:\WinApp\dotnet\x64`), which a direct launch does not inherit, so it showed the .NET runtime prompt instead of the app; the launcher now passes `DOTNET_ROOT` derived from the original process's `hostfxr.dll`. Even with the runtime, the child is terminated when the launcher script returns (T1-017). Live requalification is pending on the next batch. |
| T1-013 | Ancestor point-range follow-up silently uses owned-mode scroll setup. | The host accepts `ScrollSemantic -Viewport ancestor` but omits `-ViewportMode` for that guest mode; the guest then uses its default `owned` when selecting the scroll owner before/after `ScrollIntoView`. | Corrected before its first live run. A functional mock executes the actual host forwarding function for both modes, and all 66 follow-up offline cases pass in PS7/PS5.1. This does not qualify either live point-query case; native input and guest settings are unchanged by the fix/checks. |
| T1-014 | Final-SVG drag targets broad layout bounds rather than independently known visible artwork. | Fresh r3's fixed 96×96 SVG exposes a 1,359×129 ImageBox peer that includes full available width and caption. Its target (2,528,418) is inside that peer but outside the artwork at the leading edge. | Corrected offline, live pending. The controlled standalone Markdown image inherits Body margin `(0,0,0,8)` and 19.6-DIP minimum line height through `BuildParagraphOrImage`; its artwork starts at the peer top. Final helper checks validate both contact and effective caret inside the independently derived 96×96 artwork at simulated scales, and guard effective promotion/theme/sample inputs. Review rejects an intermediate constructor-default 6-DIP offset before UI use. Both shells pass 61 target checks plus existing harness checks; these are not physical-DPI or live-input qualification. The completed r3 runner/evidence remain unchanged. |
| T1-015 | Three timing-bounded tests time out on a cold, freshly provisioned machine. | The first M0 Release run after the October 17 machine wipe fails `RetiringSession_CancelsAByteWaitEvenWhenResolverPassesNoToken`, `UiaTextStyleReportsTheEffectiveNodeForegroundAndBackdrop(highContrast: False, …)` and `MermaidCatalogFontControlsMeasurementAndPaintAndLabelFitsNode(Consolas, Segoe UI)`, each at 9 s, together near the 22-second mark (3,192/3,195). All three pass in isolation and the immediate warm full rerun passes 3,195/3,195; Debug also passes. | Open test-reliability item, not a renderer defect. The likely trigger is first-use Mermaid native/font-catalog initialization contending with parallel tests; this is not yet proven. Diagnose before changing any timeout, and keep the failed `github-tests-release.trx` receipt. |
| T1-016 | A redirected guest launch keeps `winapp target exec` waiting for as long as the GUI child lives. | `Start-Process -RedirectStandardOutput/-RedirectStandardError` creates the child with handle inheritance, so it also inherits the launcher's stdout/stderr pipes and `target exec` never sees EOF. | Fixed in the harness: the selection-disable launch now runs through a detached `cmd.exe /d /s /c` wrapper created by `Win32_Process.Create` (T1-017), which owns the run-scoped redirects and never holds the exec pipes. Offline launch-diagnostics and UI-harness contracts pass. |
| T1-017 | Guest launchers start the probe inside the `target exec` job, which terminates it when the launcher returns. | Deferred-touch run `deferred-touch-20261021T070013Z-c7fa…` receives a valid LAUNCHED receipt for child PID 8792, but no later window inventory contains it. A manual guest launch that stayed alive for 15 s produced the window, so a minimal pair isolated the cause: `Start-Process` children of a returning `target exec` script are gone 5–8 s later, while a `Win32_Process.Create` child of the same script survives. Created with an explicit environment block holding only `DOTNET_ROOT`, the sample also dies, because that block replaces the inherited one. With the full block it shows `MarkdownRenderer deferred-touch probe` and stays alive. | Fixed in all three runners (selection-disable, touch-lifecycle and deferred-touch): the probe is started with `Win32_Process.Create`, `ShowWindow=1` and the launcher's full environment block. Selection-disable uses a hidden `cmd.exe` wrapper so its stdout/stderr capture is kept. Receipts now record `launchMethod='guest-Win32_Process-Create'` and the host validators require it; every offline contract asserts that no `Start-Process -FilePath` launch remains. A live guest check confirms the wrapper starts one sample child with its window and both redirect files. Runner requalification is pending. |
| T1-018 | The selection-disable probe expects a touch double-tap to open the selection flyout. | Batch 4 is the first run in which the probe launches (T1-017). In both viewports, `wait-for MarkdownContextCopyMarkdown` times out after a double-tap that selected exactly `touchenabledword` with two handles. The C10 case defines the product behavior: a double-tap selects with handles, and a tap inside the committed selection opens the flyout. | Fixed in the harness: both flyout checks now tap inside the selection first, and an offline contract requires that order. Live requalification is pending. |
| T1-019 | Follow-up handle cases drive handles while the long-press flyout is still open. | Follow-up runs with one sample window still fail handle crossing (selection stays `then`) and stationary edge (no routed contact). The handle swipe reports two windows for the app PID, because releasing a long-press opens the selection flyout and its light-dismiss layer consumes the next outside touch. C10 dismisses the flyout with Escape before dragging handles. | Fixed in the harness. A shared helper waits for the flyout, dismisses it and re-asserts the exact selection before every handle gesture. The guest `Range` read no longer calls `ScrollIntoView(alignToTop)` on an already visible target; that had placed the target on the viewport edge, inside the handle-drag autoscroll zone. Touch targets are taken only from a top-scrolled layout that is unchanged across consecutive reads, and every hold is checked for unexpected scrolling. With these fixes, handle crossing reaches the product and exposed P1-036. |
| T1-020 | `FilterPublicXmlDocumentationTests` depends on the parent terminal's color settings. | On this machine its child `pwsh` renders redirected errors with ANSI color escapes, so the expected plain-text message does not match (1 of 1,541 core tests fails). It passes with `NO_COLOR=1`. | Fixed: the test sets `NO_COLOR=1` for its child process. No assertion changed. |
| V1-001 | Wrapped inline image geometric coverage. | Existing multiline case checks snapshot agreement, but not independently correct placement on later lines. | Closed: independent line-height/baseline oracle passes for narrow widths, LTR/RTL, text scales 1/1.5/2, and varied image heights. No production defect found. |
| V1-002 | Current-source end-to-end validation. | Last live sample/README binary predates the current checkpoint. | Open; freshly build and run the frozen cases against the revised source. |
| V1-003 | Complete current parsing and feature smoke receipts. | Historical passing tests do not prove newer worktree changes. | R6 passes **3,195/3,195 in both Debug and Release, zero failed/skipped**, retaining all 3,192 R5 identities and adding three lifetime source-order contracts. Its Release/x64 JIT sample builds cleanly with matching tested renderer bytes. The ordinary focused Sandbox replay is **2 PASS / 0 FAIL / 60 UNVERIFIED**, owned viewport and actual 100% DPI only, not complete feature/lifecycle or whole-app AOT qualification. R5's separate unchanged-filter logic slice passes 1,541/1,541. Earlier R3 optional receipts and its **47 PASS / 7 FAIL / 8 UNVERIFIED** broad live run remain historical and failed overall. Earlier provider/AOT and 500-input headless evidence does not certify full current pages/interactions. Crash resolution, actual high DPI and remaining live lifecycle gates stay open. See the portable R6 receipt and final managed-investigation section below. |
| V1-004 | Live check for P1-004's near-origin link activation. | Policy characterization confirms suppression of a new nearby contact; live command timing still needs measurement. | Terminal r3 command 1250 records a bounded PASS: distinct routed contacts 76/77, 10-DIP separation, a 1,379.3346-ms release-to-tap interval, preserved selection after Escape, dismissed menu and successful exact independent-link activation. Both actual routed contacts match their independently computed root coordinates within 0.020 DIP and terminate without active-contact residue. This qualifies that sequence at actual 100% only; it does not pass the failed overall batch. Fresh r2 remains UNVERIFIED because its 2,669.3-ms interval was outside the 1.5-second reproduction envelope. |
| V1-005 | Actual 150%/200% handle layout and hit-geometry qualification. | Physical 150% receipt `manual-20261013T012212Z-a3ac427b663f4d70bc0ccf9cec4e6e2d` reports 144 DPI and a 65×66-pixel UIA start-handle rectangle, i.e. 43⅓×44 DIPs. Production Width/Height/MinWidth/MinHeight are all 44 DIPs; 44×1.5 is exactly 66 pixels, so ordinary rounding is not a demonstrated cause. | Open. Pair the same handle's in-process ActualWidth, transforms, overlay/clip, and automation bounds at real 144/192 DPI before changing its size. The observer captures those values, but that rebuilt observer has not been qualified at 150%. Host scaling changes and physical input remain paused; guest viewer zoom is not DPI proof. |
| V1-006 | Explicit live reset coverage during active touch. | The main batch covers navigation/replacement and opt-in selection disable, but does not induce and observe capture loss, renderer unload, or disposal during an active touch/handle interaction. | Open. An opt-in actual-input probe now builds and has eight passing source-wiring guards; its three lifecycle cases in both viewport modes remain unexecuted. The aggregate still records C14 UNVERIFIED, not live proof from offline retirement coverage. |

#### October 14: P1-020 formatted code-copy whitespace

Before implementation, `code-copy-beforefix2.trx` reproduces four failures
out of five exact-content cases. Fenced, indented, and supported HTML `<pre>`
code retain indentation/tabs in the committed semantic document, but their
CF_HTML fragment loses both when rendered by the existing supported HTML
receiver. An inline-code control changes `left  right` into `left right`.
The former writer emits ordinary `<code>` and `<br />`, which supplies no
whitespace-preserving context. The HTML/CSS contract requires preformatted
block content and an explicit whitespace-preserving inline-code declaration;
receivers may support different CSS subsets, so the inline declaration also
needs a structural/exact-text contract, not an assumption that this renderer
supports arbitrary clipboard CSS. The current fragment and failed TRX remain
retained under `C:/Users/neroc/AppData/Local/Temp/MarkdownRenderer-CodeCopy-Correctness-20261014/`.
The repair serializes each committed logical code block as escaped
`<pre><code>`, including virtualized chunks, and emits the static inline-code
`white-space: pre-wrap` declaration. Linked code labels retain their validated
inline style paths, clipped to the selected range; unsafe destinations remain
inert. No source HTML, authored CSS, or new allowed link scheme is copied.
The whitespace contracts follow [HTML preformatted text](https://html.spec.whatwg.org/multipage/grouping-content.html#the-pre-element)
and [CSS white-space](https://www.w3.org/TR/css-text-3/#white-space-property).
Status: production repair verified in both complete 3,094-case Debug/Release
suites; live clipboard qualification pending. The 16 new cases include exact partial ranges,
linked/nested formatting, separate blocks, and a 270-line chunked block. Inline
CSS is verified structurally and by exact text, not by claiming support for that
CSS in the renderer's intentionally bounded safe-HTML receiver.

#### October 14: P1-021 final authored code lines disappear from selection

Before implementation, `terminal-code-beforefix.trx` fails its exact-content
assertion: fenced source with one final authored blank code line produces
`CodeBlockBox.CodeText == "alpha\n"` and two logical display lines, but the
document's semantic text is only `"alpha"`. The semantic builder trims every
terminal LF without distinguishing real run content from its generated block
separator. Its existing range-clamping then shortens the published node/span
ranges, making selection/CF_HTML disagree with the code-copy button's unchanged
payload; no out-of-range publication is inferred from this reproduction.
The frozen C08/C11 document-content and range contract requires preserving
authored content, not merely making these inconsistent results agree.
The repair retains all span-backed authored text and removes only synthetic
terminal separators. Both reverse scans retain cancellation checkpoints. Eight
new cases include empty code, decorative trailing images, multiple blank lines,
terminal HTML hard breaks, and LF/CRLF source ranges. Source-copy assertions
compare the exact original UTF-16 string, not a byte-level claim.
Status: production repair verified in both complete 3,094-case Debug/Release
suites; live selection/copy qualification pending.
The first complete run retained its four failing historical HTML-break cases:
those assertions required removal of authored terminal breaks. Their test names,
fixtures, range bounds, geometry, and source-slice checks are preserved; the
content oracle now requires the exact authored LF sequence and exact one-unit
break spans. This corrects a demonstrated data-loss expectation, not a waiver or
removal of a failing test. The red receipt is retained.

Record newly reproduced functional defects here before implementation. Do not
turn historical SSIM, timing, or package-size failures into correctness defects
without evidence of wrong content or behavior.

#### October 14: P1-022 table copy loses its structure

Before implementation, the GFM and safe-HTML 2×2 cases in
`MarkdownRenderedHtmlTableCopyTests` verify native row/column counts and exact
cell text, then fail because the generated rich-copy fragment contains no
`table`. The former inline-only path converts the committed row-major cells into
one flat sequence. This loses the table relationships promised by the existing
selection/clipboard contract; this is not a request to match browser styling.
The failed receipt is retained at
`C:/Users/neroc/AppData/Local/Temp/MarkdownRenderer-TableCopy-Correctness-20261014/table-copy-beforefix.trx`.
The repair must use committed semantic rows/cells, not reparse authored Markdown
or HTML; encode cell text and reuse safe inline formatting, retain column
positions for partial selections without including unselected authored text,
and avoid duplicating table descendants or swallowing neighboring blocks.

#### October 14: P1-023 large code source drift

`LargeCodeBlockSourceMappingTests` places a unique `M` halfway through real
20 KiB and 80 KiB code bodies. Its independent expected span comes directly from
the authored source's body start plus that midpoint. Actual parsed layout and
`MarkdownSourceMap.TryMapRange` return the four wrong spans recorded above.
The decisive assertion is interior to the body, not at a fence or chunk boundary;
the documented source-delimiter inclusion policy is not the oracle. The initial
edge-first and revised interior-first failures were reported in the tool
transcript; a later file audit could not locate separately persisted original
TRX files. Do not cite the retained implementation-draft `r3`/`r4` TRXs as
pre-product baseline receipts. The original wrong spans remain recorded above,
and the exact authored-offset assertions remain in the regression tests.
The bounded repair must map authored code units/newline normalization/indentation
and chunk offsets exactly, retain immutable source ownership and existing safety
ceilings, and preserve intended delimiter inclusion for complete selections.
No source-text search heuristic or unbounded edit-distance table may replace
the current proportional fallback on this code path.

The coherent implementation-draft `integrated-code-table-source-characterization-r8.trx`
passes 148/150 cases, with two source-origin failures retained before the next
repair. A GFM-0.29 table compatibility insertion leaves a following code line's
parser slice backed by the prepared string although the AST span is already
mapped to the original string; the exact interior selection expects `[100,101)`
but returns an empty span. A trimmed disclosure-summary CRLF selection expects
both authored units `[106,108)` but maps only LF `[107,108)`. Restore parser-owned
line origins through the compatibility map and preserve atomic normalized-newline
coverage through the summary projection. These failures are not table-copy or
geometry passes and are not waived by the 148 passing cases.

The next coherent `r9` passes 152/153 cases: the prepared-table code, empty-line,
HTML-pre, and table-looking fenced-code cases now pass, but the same CRLF summary
assertion still returns `[107,108)` instead of `[106,108)`. The producer repair
alone is therefore insufficient; this red receipt remains retained. Read-only
composition review raises a possible private summary-origin gap after a prepared
table, but `prepared-table-summary-beforefix-r10.trx` does **not** reproduce the
predicted interior-character drift: both new prefixed cases map the interior `M`
exactly. Its 22 cases pass 20, with only the prefixed and unprefixed CRLF separator
checks failing. A proposed private-summary remapping contract is therefore on
hold until the actual preparation/projection chain proves a need. Diagnose the
CRLF failure at each runtime stage; do not infer a constant shift, add a
speculative contract, or change the exact authored-byte expectation.

The isolated `r12` stage diagnostic explains that apparent contradiction. It
uses the unchanged `r10` product DLLs plus an instrumented test assembly
(`BuildProjectReferences=false`), not a fresh candidate build. For an unprefixed
CRLF, the summary correctly maps `[96,98)`, but the final code entry uses the
legacy unit-map aligner and returns only `[97,98)`. With the prepared table, the
private summary itself maps LF `[162,163)` instead of `[161,162)`, and CRLF
`[170,172)` instead of `[169,171)`. The final legacy aligner happens to hide the
LF/interior-character shift. Its four diagnostic cases assert only structural
run validity, not correct offsets, and are not counted as correctness passes.
These measured intermediate values now justify repairing private summary
origins and retaining the exact code projection through the child-layout path.

#### October 14: P1-024 Unicode code-chunk splitting

`CodeBlockChunkGraphemeTests` builds actual native code layouts for emoji,
combining-accent text and a family ZWJ sequence deliberately crossing the current
32,768 UTF-16-unit boundary. Exact concatenated code text remains correct, but
the independently shaped chunks end/start inside the authored grapheme. The
failing `code-grapheme-table-characterization.trx` retains all three failures
and all twelve passing table-copy cases. The repair must not merely recombine
the semantic string after shaping has already split the cluster. Preserve
authored UTF-16 text, exact source mapping, and bounded construction work.

#### October 14: P1-025 long NoWrap code-line geometry

The frozen `CodeBlockNoWrapChunkGeometryTests` fixtures use a single 44,768-unit
line and two lines of 40,768/36,768 units. They explicitly select NoWrap and
require one native visual row per authored line, exact unchanged text and full
horizontal access. `CodeBlockBox.Measure` currently stacks independently shaped
chunks, while character-count chunking can cut one logical line. This is a
separate geometry defect from splitting Unicode graphemes. The retained native
`code-projection-characterization-r3.trx` and `r4.trx` reproduce both cases
before the geometry repair. The line-preserving planner is then checked in
`MarkdownRenderer-NoWrap-Geometry-Diagnostic-20261014-r1/results/NoWrap-Geometry.trx`:
the single-line case passes, while the two-line case still has three rows instead
of two. That diagnostic deliberately uses a newly built renderer with the prior
test assembly, so it is explicitly not a coherent release receipt. The remaining
row is DirectWrite's zero-character trailing row on a nonfinal LF-terminated
chunk. The repair must retain all source text and raw native metrics, trim only
that duplicate placement, preserve a real final empty row, and remain stable
across repeated measure/arrange. Coherent current-source integration and full
suites are still required. The private 32,768-unit chunk target must not be
confused with the public document-source admission ceiling.

The bounded P1-001 implementation is a glyph-run renderer forwarding
`CanvasTextLayout.DrawToTextRenderer` output while shifting script-run baselines.
It must preserve normal shaping, wrapping, color glyphs, decorations, and adjacent
glyph overhangs, with paint, caret, selection, and hit geometry agreeing. This is
regression-validated and live-checked at 96 DPI; per-region drawing clips were
rejected because they risk losing visible glyph pixels.

### Completion criteria

1. Every required fixture and interaction case passes.
2. Every known Phase 1 defect is fixed and regression-covered.
3. Existing optional-feature smoke tests remain passing.
4. The actual app passes focused reading and selection checks at narrow layouts
   and 100%, 150%, and 200% scaling. Check Light, Dark, and High Contrast for
   visible content/selection and correct interaction geometry.
5. Remaining specialized-feature defects are explicitly documented for Phase 2.

Close Phase 1 and record results when these criteria pass. Phase 1 completion is
not a claim that Phases 2 and 3 or distribution qualification are complete.

## Phase 2 Complete existing feature correctness

Validate all currently documented capabilities of HTML, math, Mermaid,
SVG/raster images, highlighting, and hosted controls. Freeze a feature/case
matrix covering supported syntax, configuration, accessibility semantics,
selection, copy, failures, and lifecycle transitions before starting this phase.
Valid supported content must render correctly; invalid, unsupported, or genuinely
unavailable input must receive its documented fallback. A fallback cannot hide a
defect in claimed support.

Known specialized investigations carried forward from the checkpoint:

- Animated WebP first-frame composition and large static WebP resource handling.
- x86 isolated-worker startup returning `0xC0000022`; ARM64 execution lacks fresh
  physical evidence. Supported architectures must not silently lose image support.
- Classify existing SVG comparison failures by supported-content correctness;
  cross-engine pixel/antialias differences alone are not defects.
- Classify the rank-7 image event by exact source occurrence and final state;
  a genuinely unavailable upstream decorative image is distinct from a native
  decoder, routing, or fallback error.
- Reconcile the documented DTD prohibition with the existing explicit acceptance
  of an inert standard SVG 1.1 external DOCTYPE. The Phase 1 script/animation
  admission fix does not silently change that separate specialized contract.

### Frozen Phase 2 feature-and-case matrix

Frozen before Phase 2 behavior changes on October 14, 2026. Phase 1 remains
open: preparing or executing offline Phase 2 cases does not waive its pending
live interaction/DPI checks or claim that it is closed. The previous whole-goal
block was too broad: live-test authorization does not prevent offline work on
the specialized defects already named above.

These 32 named cases reuse the checked-in feature samples and test fixtures at
the Phase 1 source checkpoint plus the recorded Phase 1 repairs. The support
contract is [Supported markdown](supported-markdown.md), the optional package
READMEs, [Images/SVG](images-svg-and-assets.md), [Accessibility](accessibility.md),
[Selection/copy](selection-and-clipboard.md), and
[Theming](theming-and-customization.md). No new syntax, provider, decoder library,
animation playback, browser parity, performance target or public API is added.
Add a regression fixture only for a demonstrated defect in this existing set.

| Case | Required existing behavior | Fixed fixtures / authoritative evidence |
| --- | --- | --- |
| P2-CONFIG-01 | Explicit CommonMark/GFM/GitHub profiles and optional HTML/math/Mermaid/SVG/TextMate registration enable exactly their documented syntax; missing providers retain the documented fallback. | Existing builder/profile/engine integration tests and the corresponding feature sample pages. |
| P2-CONFIG-02 | Built configuration is immutable; engines/providers follow their existing owned/borrowed lifetime and can be reused by independent views without cross-document state. | Existing engine configuration, service ownership, provider reuse and control replacement/disposal tests. |
| P2-HTML-01 | Supported inline tags, block scopes and cross-block formatting preserve ordered visible text, style and UTF-16 source positions. | `Html`, `SafeHtmlParserTests`, `SafeHtmlContractsTests`, `SafeHtmlRenderingTests`; actual native view. |
| P2-HTML-02 | Malformed, forbidden or over-budget HTML has its documented inert/literal/omitted fallback; links and images cannot bypass host-mediated I/O policy. | Existing SafeHtml fuzz/security/budget/URI cases, including the standalone script sentinel. |
| P2-HTML-03 | HTML links/images/text retain UIA roles, logical text and exact selected source; rendered/plain/HTML/Markdown copy preserve their respective contracts. | HTML source-map and clipboard suites; live focus, selection and copy around the fixed HTML sample. |
| P2-HTML-04 | Cancellation, relayout and source/engine replacement do not publish stale HTML content or retain native resources. | Existing layout publication/retirement tests and live replacement in both viewport modes. |
| P2-MATH-01 | Documented dollar-delimited inline/block math renders all valid fixed Math/PopularReadme fixtures; backslash delimiters remain literal. | `Math`, `MathContractsTests`, `MathEngineIntegrationTests`, `MathDelimiterScannerCallbackTests`, `PopularReadmeSyntaxTests`; native scene geometry/paint. |
| P2-MATH-02 | Invalid/unsupported TeX, disabled processing, recursion/size limits, timeout and cancellation preserve exact authored source and the mapped diagnostic. | Existing formula fuzz, recursion, diagnostic and processor limit tests. |
| P2-MATH-03 | Structural accessibility and linked/atomic math semantics agree with selection/copy; selection does not obscure the scene and a final formula is selectable within its bounds. | Math accessibility/source-map/vector selection tests; live inline/block/final-object checks in both viewport modes. |
| P2-MATH-04 | Theme/text-size changes and document/processor replacement discard stale scenes without disposing borrowed services. | Existing scene cache/publication/cancellation/lifetime tests; actual view transition checks. |
| P2-MERMAID-01 | The fixed Mermaid sample and PopularReadme syntax render their documented native scene/label/link semantics through the selected-RID engine. | `Mermaid`, native/engine integration, scene decoder/adapter and PopularReadme suites; actual scenes. |
| P2-MERMAID-02 | Invalid syntax, unsupported ELK layout, malformed MMIR, limits, timeout and cancellation retain exact code plus the mapped diagnostic. | Existing contract, diagnostic mapping, provenance, fuzz/scene validation cases. |
| P2-MERMAID-03 | Authored/semantic colors and Windows contrast roles remain legible; accessible nodes/links, atomic selection and rendered/Markdown copy retain content, including the final diagram. | Semantic paint/typography/accessibility/selection suites; fixed Mermaid page in Light/Dark/system contrast, both viewport modes. |
| P2-MERMAID-04 | Scene caching, theme change, source/engine replacement and disposal cannot publish an obsolete scene or transfer service ownership. | Existing scene-cache, pipeline revision and lifetime tests; live navigation/replacement. |
| P2-SVG-01 | Existing deterministic fixtures preserve geometry/transforms/viewBox/aspect, CSS/presentation rules, gradients/patterns/markers/clip/mask/filter and text/Unicode/RTL content. | Checked-in static SVG corpus and worker/provider pixel tests; missing regions are defects, antialias/pixel parity with a browser is not a gate. |
| P2-SVG-02 | Root and nested SVG consistently reject DTD/entities, scripts/events/animation/foreignObject and external I/O; admitted data images obey deduplicated resource limits. | Host/provider/nested admission, unsupported-content and resource-budget suites, including the DTD characterization below. |
| P2-SVG-03 | Only the used currentColor/color-scheme inputs affect theme resolution; authored artwork remains intact; sizing/DPI/device changes replace the correct generation. | Existing theme dependency, exact physical sizing, cache/publication/device tests and fixed `SvgStress` samples. |
| P2-SVG-04 | Alt/decorative/title/desc semantics, atomic selection and source copy remain correct; a last-document SVG is selectable without obscuring it. | Vector accessibility, image/source-map and final-SVG tests; actual view/UIA/copy in both viewport modes. |
| P2-SVG-05 | Each supported x86/x64/ARM64 worker actually starts and renders under unchanged isolation; crash/hang/malformed responses remain typed fallbacks with cleanup/recovery for unrelated content. | Exact-RID host/worker integration and fault receipts, process/handle retirement evidence; compilation or a substituted RID is not runtime proof. |
| P2-RASTER-01 | Existing PNG/JPEG/GIF/static-WebP fixtures preserve admitted size, orientation, alpha/color and display-sized preview pixels within the fixed resource envelope. | Existing image budget, decode plan, isolated WebP streaming, WIC and final ImageBox pixel suites. |
| P2-RASTER-02 | Static presentation of an animated image is the correctly composed first canvas, not a raw offset subframe or later animation frame. No playback is introduced. | Existing GIF canvas goldens; a bounded animated-WebP first-canvas characterization with independent expected pixels before any repair. |
| P2-RASTER-03 | Resolver/base URI/security partition/consent and loading/failure policy stay correct; intrinsic/CSS/table sizing preserves aspect ratio and remains shrinkable. | Existing image resolver, base-URI, sizing/table, failure and resource tests; fixed Images/LazyImages narrow-view cases. |
| P2-RASTER-04 | Image alt/decorative semantics and selection/copy remain atomic and visible, including linked and final-document images. | Existing image accessibility, inline placement and clipboard suites; actual view/UIA/copy. |
| P2-RASTER-05 | Cancellation, decode/provider/device failure, source replacement and disposal do not publish stale pixels or leak borrowed/owned resources. | Existing source lease, activation/reset, publication, residency and retirement tests; actual navigation. |
| P2-HIGHLIGHT-01 | Existing built-in/custom grammar sources and language aliases produce the documented token runs without changing code text. | `Code`, `TextMateGrammarPackTests`, `TextMateCodeBlockSyntaxHighlighterTests` and existing grammar fixtures. |
| P2-HIGHLIGHT-02 | Unknown language, absent provider, invalid result, timeout and cancellation retain plain readable code; configuration and High Contrast color behavior follow the existing contract. | Existing provider/grammar/options/result validation and contrast tests; actual Code page. |
| P2-HIGHLIGHT-03 | Highlighting does not alter semantic text, source positions, UIA ranges or plain/HTML/Markdown copy. | Existing code block geometry/source/clipboard suites; actual selection and code-copy button. |
| P2-HIGHLIGHT-04 | Incremental highlighting, theme/grammar/source replacement and snapshot retirement never apply stale spans or retain disposed layouts. | Existing code highlight snapshot/cache/operation lifetime suites; navigation/replacement. |
| P2-HOSTED-01 | The documented block-level factory produces native controls with the existing fallback for unsupported inline hosting. | Fixed `Embeds` sample and hosted factory/layout tests; no new inline adapter. |
| P2-HOSTED-02 | Null/throwing/attach-failed factories and canceled realization use their documented native fallback without corrupting document flow. | Existing hosted element lifecycle/attach-failure tests and fixed fallback sample cases. |
| P2-HOSTED-03 | Hosted controls retain normal XAML UIA/focus and direct interaction; surrounding selection/copy maps to the correct source without duplicate text. | Hosted focus buffer, semantic child/range and clipboard suites; live focus/touch/selection in both viewport modes. |
| P2-HOSTED-04 | Virtualization/recycling, factory/document replacement, unload and disposal retire exactly the owned controls/resources and leave no stale capture/focus. | Existing hosted lifecycle/retirement suites plus actual view transition checks. |

Every row starts **UNVERIFIED for Phase 2** until its referenced current-candidate
receipts and required actual-view evidence are inspected. Phase 1 receipts may
be reused where they prove exactly the same requirement; an offline/source
contract is never promoted to a live pass. Failed or unknown cases remain open;
there are no fixture waivers or feature downgrades to obtain a green matrix.

### Phase 2 defect and investigation register

| ID | Evidence / scope | State |
| --- | --- | --- |
| P2-001 | Root SVG accepts and strips arbitrary external DOCTYPEs while the written contract forbids DTDs and nested SVG already rejects them. `SvgAdmissionMetadataTests` even accepts a loopback system identifier. No external retrieval or entity execution was observed; this is an admission-contract inconsistency. | Fixed in host/provider admission, regression-covered before worker attachment. Root/direct/nested paths reject DTDs; legal comment/CDATA/text markers and non-DTD encoding normalization remain valid. Focused host 59/59, provider 24/24, full provider 720/720, native 2,989/2,989 and core correctness slice 1,475/1,475 pass. Fresh live validation remains separate. |
| P2-002 | The x86 worker exits `0xC0000022` before argument parsing under the production restricted token; x64 substitution and removing restricting SIDs are not valid fixes. | Fixed on the reproducing Windows 11/x86 path. A protected initial-thread-only trusted startup context admits WOW64 initialization; the worker drops it and verifies its RC-only, enabled low-integrity primary before IPC/input/threads. Final matching x86 and x64 NativeAOT hosts pass all raster, 100-cycle resource, isolation, restart and shutdown checks; provider 725/725 and native renderer 2,989/2,989 pass. All three workers rebuild reproducibly and use only audited headless import contracts. ARM64 execution, minimum-OS and packaged/Store qualification remain open under P2-SVG-05; compilation is not substituted for those checks. |
| P2-003 | HTML URL attributes are entity-decoded in `ParseAttributes`, then again in `TryNormalizeUrl`. For an authored `href="https://example.test/?q=&amp;amp;v=1"`, the second pass changes the intended once-decoded `?q=&amp;v=1` destination to `?q=&v=1`. Image `src`, picture `srcset` and video `poster` share this path. | Fixed at the shared decoded-attribute validation boundary. Independent element/tag and public-engine/native layout regressions pass, including linked-image source identity and CF_HTML re-encoding. Existing scheme/control-character/host-I/O restrictions remain intact. Core correctness slice 1,511/1,511 and final full native renderer 3,034/3,034 pass with zero skips. This is not live clipboard/browser qualification. |
| P2-004 | Two distinct Mermaid flowchart nodes with the same Arabic/Latin/emoji label both map to the first source occurrence. The selected-RID engine produces two correctly named node semantics, but their Markdown UTF-16 spans are `[52+15, 52+15]` instead of the independently authored `[52+15, 82+15]`. | Fixed and regression-covered for flowchart node identity with final-model-owned spans, exact preprocessing remap and canonical SVG identity, without label/order/generated-ID guessing. Both public parse paths pass five core fixtures, renderer checks pass 26+1, and final source patch 1 passes 29/29 product native tests. All three static-CRT RID builds finish successfully. The matching final x64 managed receipt has 124 passing cases and five separately retained P2-008 failures; this is not an all-green feature-suite or ARM64 runtime claim. |
| P2-005 | Already-decoded HTML image alternative and disclosure-summary text is decoded again by block rendering. The actual engine/native layout pipeline produces `&` instead of authored `&amp;`, and `R&D` instead of `R&amp;D`. | Fixed with an explicit decoded-text whitespace/render boundary, including both generic HTML and GitHub-specific disclosure summaries. Nine initial generic failures and three separate GitHub-summary failures are retained; final exact-content, source/copy, inline controls and whitespace tests pass in the 1,511-case core slice and final 3,034-case full native suite. No script execution was observed or inferred from literal-looking text. |
| P2-006 | The public GitHub engine does not install the Markdown-bearing `<details>` block parser that its legacy presentation pipeline installs. A multiline disclosure with heading, emphasis and a list becomes an opaque `HtmlBlock` instead of the intended Markdown container. | Fixed with one shared static parser contribution for engine and control builders, plus the existing registry path. The three initial engine failures and four separately reproduced control-builder failures are retained. Eleven public parser/composition/strict-profile tests pass in the final 3,034-case native suite; CommonMark/GFM literal overrides remain literal and deriving an engine does not mutate the original. Live disclosure interaction and profile-only pre-existing view-engine combinations are not newly qualified by these headless cases. |
| P2-007 | `KeepTextContent` suppresses script source under a root unknown element but emits it under supported/nested HTML containers. | Fixed by limiting the existing inert literal-script exception to `RenderLiteral`. The actual-engine characterization retains three initial failures and two passing controls. All five new policy cases and the existing P1-009 literal/source/link-isolation cases pass in the final 3,034-case native suite. Default literal rendering remains selectable inert text; `KeepTextContent` consistently suppresses the script tag and raw body. No code execution was observed. |
| P2-008 | Documented Mermaid `#quot;` entity syntax renders the literal `&quot;` instead of a double-quote character in native SVG labels. | Fixed and regression-verified on the three-patch x64 payload for ordinary and default-Markdown shape-data labels, edges, self-loops, clusters and swimlane labels. The 178-case public-pipeline receipt has 172 passes and six separately retained P2-010/P2-012 failures; all quote/numeric hash/emoji/combining exact-text and independent literal-geometry cases pass. The initial five text and three geometry failures remain retained. [Official syntax](https://mermaid.js.org/syntax/flowchart.html#entity-codes-to-escape-characters) and [upstream issue #7015](https://github.com/mermaid-js/mermaid/issues/7015) define the entity behavior, not browser-bug compatibility. This is not live or all-family feature qualification; no expected-output weakening is used. |
| P2-009 | The managed Mermaid adapter strips recognized formatting tags after native entity decoding, so authored literal `a#lt;b#gt;text#lt;/b#gt;b` becomes `atextb` instead of visible `a<b>text</b>b`. | Flowchart component fixed and regression-verified in the three-patch public pipeline, including mixed raw/encoded wrappers, default-Markdown bold adjacency, encoded line breaks, double-encoded references and literal SVG title metadata. October 14's expanded Sequence regression reproduces the same classification error upstream of the adapter: `left#lt;b#gt;text#lt;/b#gt;right` loses literal wrappers, while the raw-formatting control passes. That family extension remains open with P2-010. Classification must precede final decoding/measurement; the private final-text marker, raw XML escaping and managed pass-through prevent a second interpretation. No FFI/MMIR record or public API extension is introduced. Live rendering/clipboard qualification remains separate. |
| P2-010 | Authored Unicode matching Mermaid's private placeholder delimiters is incorrectly interpreted as preprocessing metadata. | Fixed across Flowchart, Sequence, State, Mindmap and Class using exact parser-known source origins and owner-specific projections; no sentinel guessing, content search or expected-output weakening. Final x64 public coverage is **259/259 PASS, 0 FAIL, 0 SKIP** in Debug and Release, including authored marker literals mixed with generated `#quot;`, an authored `#amp;quot;` control remaining literal `&quot;`, encoded `<b>` and literal `\n` through the resvg-safe path. The final product-native suite is **54/54, 0 ignored**. All three static-CRT RID builds are complete and their PE machines verified; only x64 was executed, while x86/ARM64 are build/PE-verified only. The registered **117-case source gate passes 117/117, 0 FAIL, with all 77 prior package/test identities retained**. No live clipboard/DPI/end-to-end qualification is claimed. Earlier red receipts remain retained as history. |
| P2-011 | Mermaid atomic semantic/copy text collapses equal labels from distinct nodes and appends their per-word shaping fragments. | Public-engine Arabic/emoji and ASCII characterizations fail before the repair despite two distinct correctly named vector semantics. The final single command-order projection passes 23 focused adapter/public-engine cases in `mermaid-semantic-copy-order-final.trx`: emit an explicit owner name once at its first actual text command; retain distinct repeated owners, root/unowned occurrences and unnamed runs in order; exclude shape-only/root metadata from body copy while preserving accessibility and empty-body fallback. Sequence actor labels remain twice each for their two painted locations, not four semantic aliases or one globally deduplicated label. Broad current-payload and live clipboard qualification remain separate. |
| P2-012 | Entity-derived ordinary spaces remain inside Markdown word tokens and can produce incorrect line breaks. | Fixed and regression-verified: patch 4 normalizes decoded ASCII whitespace into word boundaries only after Markdown classification, preserving style and separator provenance. Both previously failing independently authored plain/bold literal-geometry comparisons pass on the rebuilt x64 payload. Isolated upstream regressions pass 29/29, product native tests 41/41, and all three RID builds complete. The full public suite remains red only for the separate P2-010 defect; live/all-family qualification is not inferred. |
| P2-014 | Final2 shared Markdown projection leaks an internal generated atom from swimlane Markdown edge labels. | Component fixed and regression-verified on patch-set `15775d7c...`: an explicit plain-text projection keeps ownership independent from Markdown classification through measurement, wrapping, cache preparation and SVG emission. The original normal/self-loop failures now pass, as do all 13 public Flowchart/Swimlane routes in both Debug and Release. Full 215-case public suites are 211 PASS / 4 FAIL, with only the separate non-flowchart P2-010 failures. Product-native 43/43, source 77/77 (all original 70 identities retained), preparation 24/24, and all three static-CRT RID builds pass. x86/ARM64 are compile/PE-verified only; no live DPI/clipboard qualification is claimed. The failed final2 receipts and unchanged assertions remain retained. |
| P2-015 | Class relationship captions copy one newline per SVG shaping fragment instead of one logical label. | Fixed and public-regression-verified on the final x64 candidate: explicit canonical edge identity shares one named semantic owner across a relationship path and its primary caption, preserving the complete logical caption rather than per-word fragments. The complete product-native suite passes **54/54 with 0 ignored**, and both public Mermaid suites pass **259/259** in Debug and Release. Equal captions remain distinct owners, and path plus caption do not count as separate edges. All three static-CRT RID builds are complete and their PE machines verified; only x64 was executed, while x86/ARM64 are build/PE-verified only. The registered **117-case source gate passes 117/117, 0 FAIL, with all 77 prior package/test identities retained**. Live clipboard qualification remains outstanding. |
| P2-016 | Default-hidden Sequence actor link menus are painted and copied as document body. | Fixed and public-regression-verified on the final x64 candidate: hidden menu subtrees are excluded from paint/copy/link emission while authored menu content remains in the model. The complete product-native suite passes **54/54 with 0 ignored**, and both public Mermaid suites pass **259/259** in Debug and Release. All three static-CRT RID builds are complete and their PE machines verified; only x64 was executed, while x86/ARM64 are build/PE-verified only. The registered **117-case source gate passes 117/117, 0 FAIL, with all 77 prior package/test identities retained**. Live end-to-end qualification remains outstanding. |
| P2-017 | Mermaid's root accessible description is discarded by native scene compilation. | Fixed and public-regression-verified on the final x64 candidate: root `<desc>` maps as literal XML metadata into the existing description field, without becoming painted or copied body text. The complete product-native suite passes **54/54 with 0 ignored**, and both public Mermaid suites pass **259/259** in Debug and Release. All three static-CRT RID builds are complete and their PE machines verified; only x64 was executed, while x86/ARM64 are build/PE-verified only. The registered **117-case source gate passes 117/117, 0 FAIL, with all 77 prior package/test identities retained**. Live accessibility qualification remains outstanding. |
| P2-I01 | Animated-WebP budget admission counts frames but has no first-frame composition metadata; the existing static-plan rejection is not an ImageBox pixel oracle. | Bounded investigation closed with a new passing final-ImageBox 3×1 offset/alpha first-canvas regression, both without and with a registered static provider (which is never queried or invoked). No production rendering defect was reproduced and no decoder behavior was changed. This is not qualification of every animation variant or configuration. |
| P2-I02 | Large opaque/lossless WebP previews already have worker coverage, while lossy-alpha VP8 must charge its full-frame working ledger despite header admission. | Bounded provider characterization complete, no production defect demonstrated. The pinned valid 256×256 VP8+ALPH fixture decodes visibly at the unchanged architecture limit, preserves alpha 127 and premultiplication, and returns typed ResourceLimitExceeded at a genuinely insufficient 200 KiB limit; unrelated valid content still decodes. Portable focused test 1/1 and full provider 721/721 pass before the subsequent x86 startup change. This is not final ImageBox or arbitrary-large-image qualification. |
| P2-013 | Final SVG Markdown text incorrectly decodes unrelated authored HTML-entity spellings. | Component fix regression-verified on the matching corrected-Swimlane x64 payload in both Debug and Release: the unchanged public Markdown-string and shape-data cases preserve literal `&nbsp;literal&nbsp;`, match the independently escaped source's viewport/node geometry and pass exact paint/semantic-copy checks. The individually discovered source gate also preserves the original SVG/Markdown expectations. Untyped helpers retain their distinct semantics. The complete feature suite still fails the separate non-flowchart P2-010 cases; live rendering/clipboard qualification is separate. |
| P2-I04 | The private origin-repair candidate decides its non-Markdown HTML fast path before decoding generated entities. | Component review finding repaired in the registered fifth patch. The source gate executes the actual non-Markdown generated-icon fast-path regression and authored-private-frame control, with all 77 cases passing. HTML emission decides the icon path after expansion of its owned entities. Full private renderer 1,203/1,203 and flowchart SVG 83/83 also pass. This closes the bounded candidate review finding, not live/all-family HTML qualification. |
| P2-I05 | The private origin-repair candidate carries explicit ownership into HTML emission but not all HTML-label measurement paths. | Component review finding repaired with explicit source mode through HtmlLike measurement, wrapping and emission. Independently authored final-text metrics and the typed-label regressions pass in the registered 77-case gate and full 1,203-case private renderer suite. The matching x64 public suite retains its flowchart geometry cases. Live view/theme qualification remains separate; no new feature or font matching target is introduced. |
| P2-I06 | The private Markdown finalization policy conflates HTML entity interpretation with ownership of generated atoms. | Component review finding repaired by separate untyped, typed SVG and typed HTML finalization policies. Existing untyped semantics and authored private frames stay intact; typed SVG preserves unrelated authored HTML entity spelling; typed HTML applies its existing entity semantics after classification and before owned-atom expansion. Direct final-word/wrapped PUA and HtmlLike metrics/emission regressions pass in the 77-case source gate and full renderer suite. P2-010's four non-flowchart family routes remain open and are not covered by this component closure. |
| T2-001 | Source-preparation validation compares culture-sorted manifest paths with Git's ordinal status order. | Fixed: the first five-patch manifest reproduced rejection of valid ` M crates/merman-core/src/diagrams/flowchart.rs` because culture sorting puts `flowchart_grammar.lalrpop` first. Manifest paths, changed paths and status entries now use `StringComparer.Ordinal`, retaining exact path/count/status, mode/hash and tampering checks. All 24 preparation checks pass on the preliminary five-patch identity; repeat against the final reviewed native candidate before qualification. |
| T2-002 | The private origin candidate drops three existing four-patch whitespace regression identities/assertions. | Repaired before final candidate registration: the three original mixed-whitespace/style/SVG test identities are present and pass alongside the new provenance cases. Root compares the full prior 70-case identity set with the 77-case gate and confirms every prior case is retained. `merman-swimlane-source-gate.json` records the actual discovered names, passing count and `Previous70TestIdentitiesRetained=true`. The failed preliminary candidate is retained as historical evidence, not accepted by reducing the gate. |

Complete the phase when every required matrix case passes and no known in-scope
functional defects remain. Do not change documented support merely to hide a
failure.

## Phase 3 Real world correctness audit

Audit a frozen snapshot of the top-500 repository README corpus. Preserve the
snapshot identity and evaluate all 500 documents for content completeness,
supported-feature behavior, usable layout, selection/copy, and exception-free
navigation. Classify upstream resource failures and unsupported syntax separately
from renderer defects. Browser screenshots can identify missing content, but
pixel similarity and comparative performance are not acceptance criteria.

Complete the phase when all 500 documents have been evaluated and every identified
renderer correctness defect is resolved. The historical 500-case run and later
10-case rerun are retained evidence, not proof of this candidate's correctness.

### Frozen correctness input: September 28 snapshot

Use the existing immutable 500-entry manifest
`artifacts/readme-top500/corpus-36360809497/top-repositories.json`, generated
September 28, 2026 00:06:56.9839348 UTC. Its raw SHA-256 is
`615c56723fb16d17744af2549615a4de32b1fc299a1759727cc86b70edacac72`.
Read-only validation confirms contiguous unique ranks 1–500, unique repository
IDs, full commit/blob SHAs, safe nonempty README paths, positive declared byte
counts and exact commit/path-bound GitHub blob and raw URLs for every entry.
The absent September 29 historical manifest is not a prerequisite: that older
audit is historical evidence, not the required membership for this new frozen
correctness audit.

All **500/500** frozen README bodies are now acquired and independently verified
against the manifest's declared byte size and Git blob SHA-1, totaling
**18,023,117 bytes**. The acquisition runner preserves content-addressed bodies
under `artifacts/readme-top500/source-acquisition-615c56723fb1/`; a cached resume
reuses all 500 with no body downloads. Rank 66's raw URL initially returns a Git
symlink target path rather than the manifest's README body. A same-repository,
same-commit Git-tree proof resolves that path, and the accepted target bytes must
still match the original frozen manifest's blob SHA and size. The manifest is
not rewritten to accept a different body.

The first complete current-engine **headless** pass attempts all 500 and
successfully parses 499. Rank 184 (`bregman-arie/devops-exercises`) throws a
Markdig depth-limit exception; rank 340 (`jingyaogong/minimind`) reports two
unsupported-command math fallbacks. All 19 Mermaid fences produce scenes,
and source equality/source-map bounds pass for the successful parses. These
checks do not instantiate a control, lay out or paint a page, load images,
select/copy text, or exercise navigation. Actual content, layout, selection/copy
and navigation evaluation remain required for all 500.

After the disclosure parser repair, the fresh schema-2 receipt
`headless-parse-615c56723fb1-details-fix-20261014T121424937` successfully parses
**500/500**, with **zero parse exceptions, source mismatches, source-map bound
failures or engine-disposal exceptions**. There are 499 diagnostic-free documents
and one document with the two separately classified U3-001 math diagnostics.
All 19 Mermaid fences produce scenes. Independent receipt review verifies all
500 unique ordered ranks and repository/commit/blob/size identities against the
original frozen manifest. The exit code remains 1 for recorded diagnostics; it
is not weakened to claim a clean complete audit. The summary embeds hashes of
the actual runner/runtime assemblies and native Mermaid DLL used. This remains
an engine-only result, not a full-page rendering or selection pass.

| ID | Correctness finding | State |
| --- | --- | --- |
| P3-001 | Rank 184's many sibling disclosures can become incorrectly nested because prefixed closing lines such as `</b></details>` are not recognized. The public engine then reaches Markdig's nesting guard. | Parser repair passes the fresh actual 500-input engine rerun, including rank 184, with no exceptions. Block-owned descendant tracking lets the innermost disclosure consume its close; code-block literals remain content. Focused sibling/nested/code/EOF tests and final 3,070-case Debug/Release renderer suites pass. The current-candidate 500-input run reuses one engine and disposes without an exception. No depth ceiling increase, blanket exception suppression or whole-document literal fallback is used. Live disclosure layout/interaction remains separate. |
| U3-001 | Rank 340 uses `\underbrace` and `\texttt`, neither implemented by the frozen CSharpMath command table. | Classified as unsupported syntax, not a rendered-content success. Four new public-processor/engine tests preserve exact atomic literal source and MATH100 diagnostics; the full math suite passes 80/80. No new command support or documented-feature waiver is introduced. |

October 14 evidence audit: the retained historical ordinary-web audit did run
all **500** documents, in 20 contiguous 25-case shards covering ranks 1–500;
independently summing its JSON receipts gives **378 pass / 122 fail** under the
old criteria. Its snapshot is September 29, 2026 01:57:20 UTC, query
`stars:>1 sort:stars order:desc`; it is not the later same-byte snapshot or the
current candidate. The later same-byte local rerun covers only ranks 1–10,
**9 pass / 1 fail**. Neither set supplies the required complete current
selection/copy/navigation audit, and historical performance or pixel-difference
failures must not be relabeled renderer correctness defects.

The rank-149 Sherlock and rank-214 code-server unavailable-image records are
investigation leads, not established URI-routing bugs: they lack the actual
document path/ref, resolved raw URI and terminal fetch outcome. Current resolver
logic and existing pinned nested-document regressions produce the expected
`docs/images/...` and `docs/assets/...` identities. A future rerun must capture
those fields before changing routing. No speculative URL rewrite is applied.

## Evidence and execution

Work locally and continuously through the highest-impact open correctness issues;
scheduled continuation remains canceled. Do not push without authorization.
Retain raw test and UI evidence under `MarkdownRenderer/artifacts/correctness/`
with a fresh run directory. Record the source revision plus any uncommitted diff,
build identity, target/guest identity, fixture identity, expected/actual outcome,
and unverified cases. Never call an unexecuted or indirect case a pass.

Current phase: **Phase 1 in progress**. No phase is complete at adoption.

### Current validation receipts

Local evidence directory: `MarkdownRenderer/artifacts/correctness/phase1-20261012/`.

- `conformance.json`: 1,322/1,322 pinned CommonMark/GFM cases pass, zero exceptions
  or failures. Checks parser output and immutable source/semantic invariants,
  not live native drawing.
- `selection-focus.trx`: 59/59 pure logic/source-recovery cases pass, zero skips.
  Includes the new emoji/keycap, ordinary variation-selector word, prior-selection
  cancellation, and first-gesture cancellation regressions.
- `selection-focus-final.trx`: the same 59/59 checks pass after the final contact
  state refinements, zero skips. Live control behavior is still unverified.
- `wrapped-image-geometry.trx`: 11/11 native WARP geometry cases pass, zero skips.
  Logical text-scale cases are not a substitute for actual display-DPI checks.
- `script-baseline-focus.trx`: the first compiled shared-script-geometry
  candidate passes 72/77 native cases, with five failures and zero skips.
  The failures are independent raster checks for adjacent mixed script runs,
  all-script wrapped lines, and emoji-neighbor pixels. They remain under
  investigation; passing geometry alone is not proof of correct painting.
- `script-baseline-diagnostics.trx`: 73/78 native checks pass, five fail, zero
  skips. Expanded diagnostics confirm that the custom glyph renderer loses
  color-emoji palettes beside scripts. The adjacent-script expected origin and
  wrapped-line pixel snapping are being diagnosed separately. The new `fi`
  cluster characterization passes on this host; none of these results closes
  P1-001.
- `script-baseline-color-experiment.trx`: 76/80 native checks pass, four fail,
  zero skips. Corrected independent baseline/pixel-origin oracles now pass;
  the real color-emoji regression remains. Reconstructing a fragment layout
  does not preserve the original color/shaping contract and is rejected as
  a production solution. Native original-face/glyph color drawing is under
  bounded investigation; P1-001 remains open.
- `renderer-baseline.trx`: 2,949/2,949 native renderer regression cases pass, zero
  skips. This is a baseline before the pending script-positioning change, not a
  final all-fixes receipt.
- `math-smoke.trx`, `mermaid-smoke.trx`, and `html-smoke.trx`: respectively
  76/76, 115/115, and 10/10 cases pass, zero skips.
- `conformance-selftests.trx`: 9/9 harness/source-contract cases pass, zero skips.
- `textmate-smoke.trx` and `svg-smoke.trx`: 61/61 and 704/704 feature checks pass,
  respectively, zero skips.
- `touch-suppression-characterization.trx`: 2/2 checks reproduce the existing
  P1-004 policy behavior and deadline expiry. A characterization pass proves the
  defect, not its fix.
- `selection-contact-epochs.trx`: 73/73 focused cases pass, zero skips, including
  contact-owned suppression, reused pointer IDs, secondary-contact rejection,
  cancellation, and one-shot synthetic fallback. A subsequent deferred-input
  ordering refinement is still under review; this is not a final live receipt.
- `core-correctness-candidate.trx`: 1,375/1,375 core cases pass, zero skips,
  using the same fresh focused-test binary. This run excludes
  `PerformanceExternalGateFixtureTests`, `PerformanceReportValidatorFixtureTests`,
  and `PerformanceReleaseWorkflowContractTests`; it is not a full-project receipt.
- `selection-deferred-terminal.trx`: 83/83 focused cases pass, zero skips, after
  the bounded deferred-terminal ownership fix. Independent review subsequently
  found P1-006/P1-007; this receipt does not certify those pending refinements.
- `core-deferred-correctness.trx`: 1,379/1,379 current core correctness cases pass,
  zero skips, with the same historical-performance exclusions above. This is
  still not a full-project receipt or a live interaction result.
- `selection-replacement-final.trx`: 68/68 focused touch policy/session,
  deferred-queue, and Unicode boundary checks pass, zero skips, after
  P1-006/P1-007 and the rebuild-persistent provenance guard.
- `core-replacement-final.trx`: 1,382/1,382 core correctness cases pass, zero
  skips, from that fresh build. Historical performance fixture/workflow
  exclusions are unchanged; this is not a full-project or live UI receipt.
- `core-full-final.trx`: the normal full core project now passes 1,883/1,883
  tests, zero failures or skips (52 minutes 22 seconds). This binary includes
  the final touch/contact fixes and T1-001 correction. It predates the new
  sample-only contact-observation source guard, which separately passes 1/1;
  neither receipt is live UI or native script-paint proof.
- `script-baseline-native-color-verified.trx`: the original-font-face native
  color renderer compiles and passes 13/17 focused cases, zero skips. Four
  shifted-color raster comparisons fail in LTR/RTL on both the current
  Context7 path and forced Context4/Factory4 compatibility path. Color palettes
  are now present, but native-layout pixel positioning still requires diagnosis.
  No comparison threshold was relaxed; P1-001 remains open.
- `renderer-native-color-checkpoint.trx`: the broader native suite passes
  2,962/2,966 cases, zero skips, with only those same four color-positioning
  failures. This is the pre-snap-edit checkpoint, not a final pass.
- `script-baseline-final-snap.trx`: a subsequent snap-after-offset candidate
  passes 74/80 focused script/font cases, zero skips. The four color comparisons
  still fail and two wrapped-script raster checks now differ by one row.
  The candidate is not accepted; a bounded callback-origin diagnostic is
  separating actual positioning errors from the independent raster oracle.
- `script-baseline-y-only.trx`: measured callback origins establish that
  DirectWrite preserves fractional advance-axis positions and snaps the
  baseline's Y coordinate. The corrected candidate passes 78/80 focused cases,
  zero skips, without relaxing color-byte comparisons. The current Context7
  color path and wrapped-script raster checks pass; two forced Context4
  compatibility cases still lose shifted emoji and remain open under P1-001.
- `touch-contact-observer-api-final.trx`: the sample-only observation contract
  passes 1/1 after replacing an invalid `PointerPointProperties.IsInContact`
  reference with the verified `PointerPoint.IsInContact` API and registering
  window cleanup after observer initialization. The sample must still be rebuilt
  and tested live; a source contract alone does not establish routed input behavior.
- The October 12 22:32 UTC Release/x64 sample rebuild succeeds with zero
  warnings or errors after the observer API/initialization corrections. It
  includes the 78/80 script candidate above and the final touch/contact fixes;
  it is ready for a fresh Sandbox checkpoint, not proof that P1-001 or V1-002
  is closed.
- The first live batch (`20261012T202732Z-815b804776934ef1969d2d12e2820c46`) stops
  during harness setup because unpackaged guest launch omits `UiTargetArgs`.
  The sample actually launches; no live assertion ran and no app defect is
  inferred from this harness error.
- The second live batch (`20261012T203053Z-3c2b36ad45e2452f9d362c7296f7ff62`)
  also stops before app assertions: optional empty guest-probe arguments fail
  PowerShell parameter binding. The runner now omits those arguments and its
  strict-mode regression passes. No app defect is inferred from this setup error.
- The current sample builds in Release/x64 with zero warnings or errors. It
  contains P1-002/P1-003 but predates the pending script and final suppression
  changes, so it cannot certify all fixes.
- A subsequent Release/x64 sample build succeeds with zero warnings or errors
  and includes the final touch fixes plus the still-unresolved P1-001 candidate.
  Its Sandbox batch (`20261012T212307Z-3e115193130e46bfba716e7a1cafd07b`) stops
  before app assertions because Windows PowerShell's guest C# compiler rejects
  the helper's `out var` syntax. Zero interaction checks or screenshots ran.
  The helper file reached the guest with an exact host/guest SHA256 match;
  a transfer delay and an orphaned old browser-capture client were observed,
  but their causal relationship is unproven. The obsolete client was stopped
  only after its parent and guest audit runner were confirmed absent.
  All helper C# blocks will be checked with the actual older compiler before
  another live batch. The WinUI workflow's two repair/rerun cycles are exhausted;
  the user has authorized a fresh batch after the helper checks pass.
- `guest-helper-compile.json`: all three generated native helper blocks compile
  successfully in the actual Sandbox Windows PowerShell 5.1.26100.9549 process.
  The host Windows PowerShell offline parser/ABI and host-refusal checks also
  pass. These are compiler/security checks only: no app assertions, native input,
  screenshots, or display changes ran. The DPI runner refuses ambiguous
  multi-monitor targeting until the Settings selected display can be mapped
  independently; this is a test-runner safety limit, not renderer support.
- `guest-helper-compile-2206.json`: a later checkpoint of all three helper
  blocks also compiles in the actual guest's older compiler, with source hashes
  recorded. No native input or app assertions ran. Host Windows PowerShell
  harness/ABI checks and the manual-DPI mocked checks pass after the latest
  cleanup and fingertip-offset corrections.
- The user approved physical-desktop display-scaling checks and restoration.
  The selected monitor and test window currently report 100% / 96 DPI. Actual
  150% and 200% checks have not yet run; the inspected Sandbox guest did not
  expose a display-scaling control. This observation is not proof that all
  Sandbox configurations are limited to 100%.
  A subsequent host Settings preflight renders the Display page but exposes
  only window chrome through UIA, even after reopening. The observed Scale
  remains `100% (Recommended)`; no physical scale mutation has been performed.
  The user confirms the Scale control is visible and has offered to change it
  manually. A separate capture-only runner now checks the actual app DPI at
  each user-selected scale and never changes Windows settings. Its 11 mocked
  dispatch/receipt cases pass; those offline checks are not actual DPI evidence.
- `core-baseline.trx`: the original full core baseline completed with 1,858
  passes, one failure, and zero skips. It was compiled before T1-001's correction;
  the sole failure is that stale assertion. Its historical external-performance
  validator self-tests took most of the 55-minute run and are not active
  correctness acceptance gates. This is not a final all-fixes pass.
- `guest-helper-compile-final.json`: all three current helper C# blocks compile
  in the actual Sandbox PowerShell 5.1.26100.9549 process, with source/block
  hashes and the unchanged guest epoch recorded. This qualifies helper
  compatibility only; it does not prove app behavior.
- Fresh Sandbox batch `20261012T223954Z-a947b31b93da4033ac14547a6677eec2`
  reaches actual app checks against an exact host/deployed executable, sample
  assembly, renderer assembly, and dependency-manifest hash match. It records
  20 passes, 28 failures, and seven explicit unverified requirements. The helper
  transfer took several minutes but completed without termination or a retry.
  Failure classification is in progress: strict-mode leaf traversal, repeated
  navigation to an already-selected page after temporary source replacement,
  expected fixture text, and guest Unicode transport need runner corrections
  before inferring renderer defects. The image/table aspect-ratio and live
  script-baseline assertions also failed and are not recorded as passes.
  All 13 captured PNGs have been inspected: visible smoke content and both
  narrow touch-pan layouts remain usable, and final-SVG selection retains the
  checkmark artwork. That same final-SVG screenshot exposes P1-008. The remote
  GitHub avatar is unavailable and its network-backed success is unverified.
  No actual 150% or 200% DPI or stationary-edge-scroll validation is claimed.
- `script-baseline-legacy-trace.trx` and
  `script-baseline-legacy-layout-trace.trx`: temporary native diagnostics each
  execute successfully, but the first reads an invalid flattened x64 structure
  and both still report implausible palette/format values. These are diagnostic
  execution receipts, not rendering passes. Independent SDK review identifies
  the missing C++ base tail padding before the derived color-glyph fields;
  native ABI verification and regression validation remain required.
- `native-color-glyph-abi-and-vtable.txt` records an independent Windows SDK
  C++ probe: the derived DirectWrite structure's format/measuring fields are
  at offsets 88/92 on x64 and 64/68 on x86. A typed native call confirms that
  Windows returns combined TrueType/COLR flags for the observed emoji layers;
  those flags are not evidence of malformed glyph data. The candidate now
  preserves base-structure tail padding and supports only bounded outline-flag
  combinations. Fresh raster and full renderer tests are running; the probe
  alone does not close P1-001. Generated probe executables were removed; their
  source and output remain in the dated evidence directory.
- P1-008 has an implemented candidate: validate original SVG content before
  snapshot normalization, reject unsupported content atomically, and reuse
  metadata only when the admitted byte identity is unchanged. Regression cases
  cover scripts, SMIL/CSS animation, event handlers, `foreignObject`, and nested
  SVG data resources. Compiler, feature-suite, and live placeholder checks
  remain required before closing this defect.
- `svg-original-admission-full.trx`: 712/712 SVG/provider cases pass, zero
  skips, including the new original-content admission regressions. Live typed
  placeholders in the sample remain unverified until the matching app rerun.
- `script-baseline-abi-fixed.trx`: 22/24 focused native script checks pass,
  zero skips. Emoji are no longer missing in the two forced Context4 cases,
  but they fail comparison with the newer Context7 raster. A separate typed
  native Windows SDK/WARP probe demonstrates that the same glyph, font,
  dimensions, origin, DPI, and device yield different modern/legacy color
  rasters. This identifies an invalid cross-API-era pixel oracle; exact
  same-era rendering validation is still required, without relaxed thresholds.
- `renderer-abi-svg-candidate.trx`: the normal full native renderer project
  records 2,969 passes, the same two Context4/Context7 comparison failures, and
  zero skips. It predates the separate P1-009 HTML fix and is not a final pass.
- `html-inert-script-fix.trx`: 128/128 focused native HTML rendering cases pass,
  zero skips. Standalone exact script source, source spans, surrounding block
  order, and non-link literal text inside an enclosing link now have regression
  coverage. The strict live HTML sentinel check still must pass.
- `core-svg-observer-final.trx`: 56/56 SVG-budget and sample-observer contracts
  pass after clarifying the snapshot-only test names, zero skips. This
  supplements, rather than replaces, the normal full core receipt above.
- The October 12 23:12 UTC Release/x64 sample rebuild succeeds with zero
  warnings or errors and includes the original-SVG admission, inert HTML
  literal, script geometry/color-ABI, and touch/contact fixes. A fresh guest
  deployment and payload-hash match are required for the first repair/rerun of
  the user-approved Sandbox workflow; no live pass is inferred from the build.
- `guest-helper-compile-20261012T231600Z.json`: all three current C# helper
  blocks compile in the actual guest's PowerShell 5.1, and both ASCII-safe JSON
  serializers preserve emoji and ZWJ sequences exactly. Wrapper changes after
  extraction are recorded separately; the compiled block hashes still match.
  No UI/input/settings changes occurred during this check. The old sample
  (guest PID 5640, HWND 656006) subsequently closed normally after exact
  epoch/process/path checks; the Sandbox remains running for fresh deployment.
- `touch-relayout-provenance-final.trx`: 73/73 touch policy/session, deferred
  input, and Unicode-boundary cases pass, zero skips. Reset now retires active,
  completed, secondary, and synthetic touch provenance without admitting old
  ID-less gestures; a fresh accepted contact restores normal dispatch. No
  selection range or ScrollViewer panning behavior was changed. Live checks
  still must run against the matching rebuilt sample.
- `script-baseline-independent-final.trx`: 92/92 native script, relative-size,
  and inline/wrapped-image geometry cases pass, zero skips. The forced Context4
  reference owns its ABI/drawing calls and uses independently specified source
  ranges and baseline offsets; modern Context7 tests remain unchanged. This is
  forced legacy-path coverage on the current OS, not physical Windows 10 proof.
- `svg-nested-admission-final.trx`: the complete SVG/provider project passes
  717/717 cases, zero skips, including direct-provider rejection of nested
  script/foreign-object/SMIL/event content before worker attachment and a static
  nested SVG open/render control.
- `renderer-current-final.trx`: the normal complete native renderer project
  passes 2,974/2,974 cases, zero skips, after the script/color, literal HTML,
  SVG admission, and touch relayout changes. No cases or tolerances were waived.
- `renderer-current-2975-final.trx`: the complete native renderer project is
  rerun after adding the late-raster replacement regression and passes
  2,975/2,975 cases, zero skips (2m22s). The new case proves that a retired
  ImageBox's late, uncancelable decode is disposed exactly once and cannot
  alter the current cached local ImageBox or emit stale completion/failure.
  This is an ImageBox-level lifecycle regression, not a full-control live
  navigation result; live C07/C14 remain separate requirements.
- `core-current-final.trx`: the normal complete pure-core project passes
  1,889/1,889 cases, zero skips (54m05s), including the current touch-reset,
  deferred-input, Unicode, image-admission, and layout-handoff regressions.
  The long duration is dominated by 501 existing external-gate test fixtures
  launching individual PowerShell checks; the previous successful 1,883-case
  receipt took 52m23s. These are test-runner contract checks, not a new browser
  performance benchmark or a reason to reintroduce the retired parity goals.
- The October 12 23:30 UTC sample build succeeds with zero warnings/errors and
  contains the corrected deterministic lazy-image fixtures. First repair/rerun
  `20261012T233019Z-4b0e31d18c9b49f7953762c397576274` completes with 30 passes,
  19 failures, and seven explicitly unverified checks. All 25 listed PNGs have
  been inspected. Unsupported SVGs now show atomic placeholders; HTML raised/
  lowered phrases, narrow SVG table containment, and final SVG/Mermaid selected
  artwork remain visible. The batch's aggregate is not a Phase 1 pass.
  The remaining failures include incorrect inspection scope (shell/popup/raw
  status), an unsupported coordinate-click command, a wrong UIA enum namespace,
  absent collapsed diagnostics, obsolete fixture anchors, HTML-case comparison,
  and an overlong source-replacement command. Native canceled-contact injection
  fails before reaching the app assertion and remains unverified.
  C11's second contact lands inside the previous Arabic end-handle hit target;
  it does not prove a combining-character selection bug. C14's resize oracle
  incorrectly demands cleared selection although frozen C13 requires preserved
  selection with updated geometry; receipts show the selected word at the new
  position. Both runner assertions require correction, not a product change.
  One final repair/rerun remains within the approved UI workflow's two-cycle
  limit. The normal complete pure-core project subsequently passes as recorded
  in `core-current-final.trx`; the aggregate live batch still is not passing.
- The first physical 100% / 96-DPI startup attempts stop before any input or
  scale change: imported helper lookup omits Raw-view-only sample indicators,
  then imported fingerprint code loses its script-local path. Both failures and
  restored app bounds are retained in `dpi/manual-20261012T235516Z-*` and
  `dpi/manual-20261012T235621Z-*`. The runner fixes are under offline review;
  no physical interaction or 150%/200% result is claimed.
- The original physical batch exhausts its two repair cycles at
  `dpi/manual-20261012T235905Z-*` on initial theme setup, before touch. Windows
  scaling is unchanged. Following the user's fresh-batch approval, the test
  app is explicitly normalized to Light. `dpi/manual-20261013T000153Z-*`
  identifies a managed UIA test-helper error: `GetBoundingRectangles()` returns
  `Rect[]`, not a flat native array. The corrected converter and all 11 mock
  DPI-case dispatch contracts pass offline checks.
- Fresh physical first repair `dpi/manual-20261013T000425Z-*` passes owned
  100% word long-press: exactly `family`, both 44-DIP handles, endpoint distance
  21.52 DIPs, and native Copy menu; its PNG is visually reviewed. Ancestor
  double-tap at `(580,502)` selects `selects`, not `family`, and remains an
  unclassified failure because the target's pre/post-scroll geometry was not
  captured. Every recorded cleanup assertion passes. No 150%/200% evidence
  is inferred.
- Fresh physical second repair `dpi/manual-20261013T001350Z-*` adds stable,
  clipped-viewport geometry sampling and preparation captures, then stops
  before touch on an additional `RangeFromPoint` diagnostic (`0x8000000B`,
  `E_BOUNDS`). Its sole PNG is reviewed. The same live root and block UIA
  methods succeed on currently visible FullDemo text, so this is not evidence
  that the method is generally unsupported. The diagnostic failure is retained
  separately from the unchanged exact-selection/handle assertions. Physical
  input is paused at the skill's retry limit pending a new batch approval;
  the user has been asked. Test app state/bounds restore successfully and
  actual scaling remains 100%/96 DPI. Further runs must capture the exact
  target/viewport before injection to classify the ancestor failure.
- PR #99 still points to remote revision
  `1c00c83ddfd2e8b65e05cb57e359a3f092b37271`; its September 29 CI failures
  do not test this local checkpoint. The preview-test failure was the old
  one-second foreground-readiness test, which has since been reworked in local
  history and passes in the current full core receipt. Historical top-500
  browser-parity failures are retained, not reclassified as current correctness
  passes. No changes have been pushed during this Phase 1 continuation.
- Final Sandbox repair `20261013T002813Z-9f69ceef67984eaaa6cbcdc3475fbe76`
  records 0 passes, 51 failures, and seven unverified checks, with no captures.
  These are not 51 newly reproduced renderer defects: the process-scoped
  helper cannot find the sample's editor/status elements, so navigation/setup
  fail before their app assertions. A subsequent exact-guest, read-only query
  finds each uniquely under the verified app HWND in Raw view, but those WinUI
  descendants report PID 0; the helper incorrectly rejects them even though
  their root has the exact approved PID/HWND. Root discovery must stay strictly
  process-scoped; PID-0 children may be admitted only within that verified root,
  and explicit foreign PIDs must remain excluded. The approved Sandbox batch
  has exhausted both repair cycles.
  The replacement helper must pass offline and exact-guest read-only lookup
  checks before requesting a fresh live batch; no passing result is inferred.
- The user approves a new physical 100% batch. Its original attempt,
  `dpi/manual-20261013T003418Z-*`, passes owned word selection/handles/menu but
  stops setting up ancestor mode because the previous native flyout is still
  open. The runner now closes the popup and clears selection before each
  viewport/appearance setup, with offline ordering checks. Its first repair,
  `dpi/manual-20261013T003632Z-*`, again passes owned mode and captures the
  stable ancestor-coordinate defect P1-013. All four PNGs are inspected;
  P1-014's separate point-query failure is retained in diagnostics. Cleanup
  restores the original test-app state and actual scaling remains 100%/96 DPI.
  One second/final repair remains in this newly approved physical batch.
- P1-013's candidate maps all four transformed owner corners through the active
  `XamlRoot.CoordinateConverter`, avoiding clipped peer bounds and the absent
  root-peer fallback. The project-restored Windows App SDK 1.8 API supports
  this statically referenced path. Its source-wiring check is not independent
  geometric proof: native tests, rebuilt sample, and actual DPI validation
  remain required before recording the defect as fixed.
- The final repair of that physical batch, `manual-20261013T005103Z-*`,
  stops before touch on P1-015's startup-theme mismatch. No display setting is
  changed. The user authorizes a fresh physical 100% batch and fresh Sandbox
  batch after build/helper checks.
- Fresh physical original `manual-20261013T005726Z-*` reaches Light setup,
  then exposes a regression in the first P1-013 candidate: the reported word
  rectangle moves by the native client origin (+24,+47), and the injected
  gesture selects `logically` instead of `family`. Both captures are reviewed.
  All cleanup checks pass; actual display scale remains 100%/96 DPI. Two
  repair/reruns remain in this fresh approved batch. This is product-fix
  evidence, not a passing DPI result.
- A temporary sample-only probe records root-local transform and in-process
  peer coordinates separately from physical screen coordinates, confirming
  the duplicated origin. The probe is removed after diagnosis. Microsoft's
  [WinUI UIA wrapper source](https://github.com/microsoft/microsoft-ui-xaml/blob/1bb076e974442d0a5633ea714ff8468692aba261/dxaml/xcp/win/shared/UIAPatternProviderWrapper.cpp)
  translates rectangles client-to-screen and points screen-to-client around
  the provider callbacks. The replacement uses the unclipped root transform
  and one DIP-to-client-pixel scale, leaving the screen-origin conversion to
  WinUI. Actual non-100% geometry validation is still mandatory.
- `guest-helper-compile-after-keyboard-20261013.json` qualifies the three
  frozen helper blocks under the exact active Sandbox's Windows PowerShell
  5.1.26100.9549 x64. All compile/ABI checks and emoji/ZWJ ASCII-JSON roundtrips
  pass. The receipt records source/block hashes and that no app launch, UI
  input, staging, or display mutation occurred. C09 now targets the supported
  focused selection-handle Right/Left path, not a nonexistent document caret.
  These compatibility checks do not replace the newly approved live batch.
- Corrected client-space sample build succeeds with zero warnings/errors.
  `provider-local-coordinate-focused.trx` passes 9/9, and the normal complete
  native suite `renderer-client-coordinate-final.trx` passes 2,978/2,978 with
  zero skips. The temporary coordinate probe is absent from this payload.
- Physical 100% first repair `manual-20261013T011406Z-*` passes both owned
  long-press and ancestor double-tap at the same actual painted `family`
  rectangle `(562,175,37,19)`: exact text, both native 44-DIP handles,
  and the owned long-press native selection menu. All four captures are
  inspected and every cleanup check passes. P1-013 has actual 100% proof,
  not yet full DPI proof. P1-014's separate COM point-query failure persists
  in both modes and remains open.
- The user manually sets 150% and the HWND independently reports 144 DPI.
  Its original `manual-20261013T011539Z-*` and first repair
  `manual-20261013T011808Z-*` refuse touch because the window loses foreground;
  no gesture correctness result is inferred. The shared helper now verifies
  native editor focus before target scroll/sampling, with an offline guard.
  The user brings the app forward, and final repair
  `manual-20261013T012212Z-*` correctly selects `family` and shows both handles
  and the native Copy menu. It stops on a handle-size assertion: UIA reports
  a 65-pixel width / 1.5 = 43.333 DIPs, versus the 44-DIP minimum. All six
  captures from the three attempts are inspected. Determine actual native
  hit geometry versus UIA quantization before changing either the control or
  oracle; the seven-case 150% batch is not passing. The physical batch's
  retry cap is exhausted; 200% is unrun and Windows remains user-set 150%.
- The newly approved Sandbox batch closes only the exact previously qualified
  guest sample normally, then launches the current payload in the same
  Sandbox epoch. Its fresh run is `20261013T012343Z-*`. Helper staging completes,
  and payload identity matches all required host/guest binaries. The original
  attempt records 0 passes, 51 failures, and seven unverified cases, with no
  screenshots: its PowerShell collector adds a provenance wrapper to a
  `List<AutomationElement>` and fails before app assertions. The corrected
  `List<object>` collector passes a functional verified-root/PID-zero/foreign-PID
  regression under both host PowerShell 7 and Windows PowerShell 5.1. Exact-guest
  read-only qualification subsequently passes 57/57 helper checks, compiles all
  three C# blocks, and matches all six staged source hashes without UI input;
  its receipt is `phase1-20261013/20261013T013512Z-*/collector-diagnostic-receipt.json`.
  First repair `20261013T014348Z-*` is running against the matched payload;
  one final repair remains afterward. These are
  helper failures, not 51 newly reproduced renderer defects. The frozen runner
  now checks identity at each case boundary
  and before mutating actions, verifies the canceled-contact footprint misses
  both handles, and rechecks the exact checkbox originally tapped.
- The user approves a fresh physical 150% batch after the handle fix and code
  checks pass, then asks whether Sandbox can provide those DPI tests instead.
  Physical testing is paused: the answer to the diagnostic-first question is
  not approval for that separate diagnostic run. Scaling remains manually set
  to 150%; no 200% result or restored Windows display setting is claimed.
  A temporary opt-in sample-only exception/handle probe was built but never
  enabled. It is removed after the exact native P1-014 reproduction identifies
  the cause; no trace-enabled physical run took place.
- `touch-selection-peer-callback-repro.trx` captures P1-014's exact failure:
  semantic spans 52–54 (`Word selection`, `44 epx handles`, `Native menu`) have
  no visible Mermaid bounds and return `Rect.Empty`. The eager point-coercion
  pass maps every semantic span, including those far from the target, and
  `LayoutPlacementGeneration.ToLogicalRect` reconstructs the sentinel through
  a constructor that rejects negative dimensions. `touch-selection-empty-rect-fix.trx`
  passes the exact owned/ancestor callback plus placement regressions, 9/9,
  zero skips. The fix preserves the sentinel, keeps finite zero-area caret
  translation, and retains ordinal validation. The same absent-geometry
  guard is added before the peer's screen conversion; live COM validation
  and a complete native rerun remain pending.
- `touch-selection-mermaid-bounds.trx` adds the adjacent bounding-range
  regression, 2/2 passing with zero skips: three real clipped Mermaid semantic
  ranges remain absent after placement translation and the actual peer's
  screen-conversion empty branch, and publish no UIA rectangles. Collapsed
  caret behavior is unchanged. The normal full native renderer suite then
  passes 2,980/2,980, zero skips, in
  `phase1-20261013/renderer-empty-geometry-final.trx` (2m18s). A diagnostic-free
  Release/x64 sample rebuild succeeds with zero warnings/errors (11.76s).
  The active Sandbox first repair still uses its earlier matched renderer
  binary, so it cannot qualify this newly built fix. Live COM validation
  remains separate from these native receipts.
- DPI testing target investigation: Microsoft documents remote-session scale
  separately from client zoom, but neither documented `.wsb` options nor
  WinApp CLI 0.7's Sandbox connection exposes that setting. The current guest
  app measures 96 DPI despite the host's 144 DPI. This is not proof that Sandbox
  can never use another scale, and screenshot/client zoom cannot qualify the
  matrix. Any guest route must independently report both 144/192 window DPI
  and 1.5/2.0 XamlRoot scale before its checks count. No registry workaround,
  guest restart, host scale change, or client repositioning is performed.
- Read-only code review confirms the absent-geometry fix reaches the core UIA
  point and bounding-range consumers without changing finite rectangles. A
  separate `ScrollIntoView` audit remains open: an entirely clipped Mermaid
  label can supply only empty geometry to the scrolling path. Do not infer
  that all accessibility operations are qualified from the point-query fix.
- Active Sandbox first repair records a C09 runner failure because its
  generated guest parameter validation omits `focus-state`, although that
  dispatcher and the calling test exist. The host generator is corrected;
  functional PowerShell parameter-binder checks pass under both PowerShell 7
  and Windows PowerShell 5.1, including rejection of unknown modes. The active
  guest script is not overwritten, and its original failure remains evidence.
  C14's navigation/replacement case is renamed to say it clears selection,
  not disables selection. Actual `IsSelectionEnabled=false` live coverage
  remains required; replacement/navigation success cannot close that subgate.

- Sandbox first repair `20261013T014348Z-dd63d3b884f74b119f4a64e61d97b7bf`
  finishes with 37 passes, 14 failures, and seven unverified cases. All 34
  captures are inspected. This is not an aggregate passing result. Failures
  include sparse UIA nodes without an AutomationId, omitted closed Math
  diagnostics, an omitted validated `focus-state` mode, legacy touch-injection
  error 87, an oversized command-line Markdown replacement, and resize bounds
  sampled before restoring wide layout. The last case's word bounds agree
  with the current painted range; its runner compared them with stale narrow
  renderer bounds. These runner problems are corrected or being corrected
  together before the one remaining final repair; their failed receipts are
  preserved. New production accessibility fixes are not qualified by this
  batch's older matched payload.
- The adjacent `ScrollIntoView` absent-geometry defect is fixed: entirely
  clipped semantic ranges now skip `Rect.Empty` and use the existing logical
  block fallback instead of scrolling to negative-infinity geometry. Finite
  zero-width caret rectangles remain eligible. The real TouchSelection
  regression `artifacts/phase1-20261012/touch-selection-scroll-into-view.trx`
  passes 2/2, zero skips. The same regression covers the three real clipped
  Mermaid semantic spans, a finite collapsed caret, mixed empty/caret input,
  and all-empty fallback. Actual live COM point-query and scrolling validation
  still require the newly built payload.
- The complete native renderer suite after the `ScrollIntoView` correction
  passes 2,980/2,980, zero skips, in 2m14s. Its original TRX is
  `MarkdownRenderer.GitHub.Tests/TestResults/neroc_ALIENWARE_2026-10-12_19_31_39_net10.0.trx`,
  preserved with an identical copy at
  `phase1-20261013/renderer-scroll-geometry-final.trx`. The outer shell command
  exits 1 because its unquoted logger semicolon starts a subsequent nonexistent
  command after the successful test run; this is recorded as an invocation
  mistake, not a clean aggregate command result. The TRX itself reports 2,980
  executed/passed and zero failed/error/aborted/not-executed tests.
- The opt-in sample-only selection-disable probe is implemented for both
  viewport modes. It drives the real `IsSelectionEnabled` property and checks
  disabled UIA selection support, empty selection/handles/menu, blocked touch
  and keyboard selection, and restored behavior after re-enabling. Offline
  runner checks and Windows PowerShell 5.1 parsing pass; build and live checks
  remain pending. Navigation/replacement alone is not used as disable proof.
- A direct COM point-range follow-up is added for the narrow TouchSelection
  fixture in both viewports. It requires the exact word at the painted center,
  no visible rectangles for the searchable clipped diagram label, and
  restoration of the original window size even on failure. Its offline
  functional checks reject null ranges, wrong text, and missing, duplicate,
  or noncontaining geometry; these mock checks are not live COM proof.
- Further read-only DPI investigation confirms that installed WinApp 0.7 and
  `wsb connect --help` expose no scale argument. Microsoft's documented
  `wsb connect --id` opens a remote-session window, but does not promise same
  interactive-session reattachment or DPI renegotiation. No connection,
  restart, registry mutation, or physical input is performed to guess at that
  behavior. Actual 150%/200% checks remain open, with physical testing paused
  following the user's Sandbox question. Client zoom is not accepted as DPI.

- The combined Release/x64 sample rebuild, including the empty-geometry
  scrolling fix, observational touch diagnostics, and opt-in selection-disable
  window, succeeds with zero warnings/errors in 13.80s. The observer contract
  passes 1/1, zero skips, in
  `phase1-20261013/touch-observer-source-contract-final.trx`. Both offline UI
  harnesses pass under PowerShell 7 and Windows PowerShell 5.1 after replacing
  the test-only unsupported `ConvertFrom-Json -AsHashtable` call with an explicit
  dictionary fixture; the missing-ID and dictionary-shape checks remain intact.
  The manual-DPI runner's 11 mocked cases also pass. These are build/helper
  qualifications, not live touch, accessibility, or actual-DPI results.
- Exact-guest cold qualification then passes both guards under Windows
  PowerShell 5.1.26100.9549, compiling all three embedded native interop blocks
  and matching all eight staged source hashes to the host. Receipt:
  `phase1-20261013/cold-qualification-final.json`. Two overlapping setup-file
  transfers return `sandbox_target_ambiguous`; both are retried sequentially
  only after all transfer commands finish. The original guest/epoch remains
  intact. No app launch, UI input, display change, or live test result is
  claimed by this cold qualification. The final approved Sandbox repair is
  ready but has not started; the existing sample still uses the earlier build.
- The final approved repair is subsequently invoked as
  `20261013T031656Z-d3c437f85cf84914a8a951ff988dcb5d`, with both the suppression
  and real selection-disable investigations enabled. It is still in target
  qualification; no new live assertion or screenshot is counted yet. The old
  exact sample PID 8564 accepts a normal close but exceeds the ten-second exit
  wait; a later exact-guest read proves it exited, and the same-epoch snapshot
  marks its deployment exited. No forced termination or Sandbox reset occurs.
  Actual 150%/200% physical testing remains paused following the Sandbox question.
- The final repair's project-mode launch returns success in the original
  Sandbox epoch, and two subsequent window inventories identify the new sample
  process 7004 / HWND 24445608. The generated guest helper stages successfully.
  This is launch/identity progress only: payload matching and live assertions
  still run afterward, so no case is promoted to passing from discovery alone.
  Sandbox transport calls are taking minutes; the existing run is preserved
  rather than relaunched because of slow output.
- The new process subsequently qualifies all four deployment payload hashes
  against the final host build (`run.json`, `payloadIdentity.status=MATCH`),
  including the changed sample and renderer DLLs. Guest metadata independently
  reports 96 DPI / scale 1.0. The batch reaches the frozen document cases and
  starts producing fresh captures; this cannot qualify 150% or 200%.
  A read-only shutdown audit finds no evidenced synchronous wait/deadlock in
  the sample's renderer, highlighter, engine, SVG-worker or performance-session
  disposal chain. The prior bounded exit-wait timeout remains an observation,
  not a newly asserted product defect or a passing disposal test.
- That final repair is now terminal: **37 PASS, 18 FAIL, 7 UNVERIFIED**, exit 1,
  `complete=false`. All 32 delivered captures are visually inspected. Visible
  core text, scripts, the four local lazy SVGs, narrowed SVG table containment,
  and the Arabic selection/menu are intact in the captured states. This review
  does not certify hidden content, actual 150%/200% DPI, or actual Windows High
  Contrast. Raw results and failure evidence are not overwritten.
- The final failures are classified from the saved commands before changing
  behavior. C11's Arabic range is correct; one Escape closes its flyout but
  preserves selection, so the combining fixture is never attempted. V1-004's
  successful guest receipt omits an outer status. T1-002's external fixture
  link opens OpenWith and blocks eleven later touch attempts. C14's two fresh
  ranges contain no text/rectangles; cleanup misreads the start timestamp while
  the exact second process is still alive. The large source replacement's
  exact comparison needs native line-ending handling; the original mismatch
  was not recorded, so line endings are a diagnosis, not proven raw evidence.
  The canceled-contact failure lacks the observer deltas needed to distinguish
  injection failure from renderer behavior and remains unresolved.
- Offline repairs retain the contracts: canonicalize only CRLF/CR/LF for the
  TextBox comparison while retaining the source-byte hash; preserve all seven
  fractional UTC timestamp digits across both PowerShell JSON models; accept
  a collapsed, rectangle-free caret only when Single is supported; require
  no handles/menu in every cleared state; close the menu before a second
  renderer-focused Escape. Rejected cancellation now includes full before/after
  observer snapshots and all event deltas without relaxing the predicate.
  Both main and follow-up guards pass under PowerShell 7 and Windows PowerShell
  5.1. Follow-up checks also cover bottom-edge caret anchors across unequal-height
  rows, explicit owned-viewport setup for High Contrast, and measured stationary
  hold duration. These are offline receipts, not live behavior passes.
- Sample-only reserved-link tests pass **11/11**, zero skips, in
  `artifacts/phase1-20261013/sample-link-routing.trx`; the revised Release/x64
  sample builds with **zero warnings/errors** in 10.41s. This sample differs from
  the final repair's matched payload and is not qualified by that older run.
- The original PID 7004 is present immediately before the opt-in second
  project-mode launch and absent immediately afterward; PID 1572 is genuinely
  alive with the expected executable. These observations do not prove who
  terminated the original app. Do not retarget the missing main app's follow-up
  to the second process. A fresh batch must avoid replacing the original
  deployment/process when launching the probe and verify both identities.
- The WinUI testing skill's two repair/rerun cycles for this approved batch are
  exhausted. No new live batch, guest reset/reconnect, normal-desktop input, or
  scaling mutation is performed. Physical testing remains paused following the
  user's question about Sandbox. Microsoft's `.wsb` configuration and installed
  WinApp/Sandbox command help expose no guest-DPI setting; the existing guest
  still reports 96 DPI/1.0 despite the host's 144 DPI. This is not a categorical
  claim that Sandbox can never run another DPI. A future guest route only counts
  when the same app window independently reports 144/192 DPI and 1.5/2.0
  XamlRoot scale. Remaining live work includes cancellation/deferred-terminal
  paths, the COM geometry follow-up, crossing/stationary edge-scroll, actual
  selection disable/re-enable, and actual DPI/High Contrast checks.

- Further local work replaces the opt-in second project-mode launch with an
  exact-executable guest launch. The original PID/name/path/exact UTC start is
  checked before and after; the new window's process is checked against the
  actual launch receipt before payload hashes, UI actions, or normal-close
  cleanup. Independent review catches and closes a PID-reuse race in the first
  candidate. Both PS7 and Windows PowerShell 5.1 functional guards pass, including
  a one-tick (100 ns) changed new-process start time. These are mocked launcher
  checks, not evidence that the original app survives a real second launch.
- The full native renderer suite is rerun with the internal deferred-touch probe
  present: **2,980/2,980**, zero failures/skips, 2m22s, a clean command exit 0.
  Receipt: `phase1-20261013/renderer-deferred-probe-current.trx`. This suite does
  not construct a real XAML host and therefore does not exercise the new live
  probe. Subsequent probe-only receipt fields are separately compiled below.
- P1-005/P1-006 now have an opt-in genuine WinUI test host, selected only by
  `--renderer-deferred-touch-probe`. Real routed touch establishes ownership and
  selection; a dedicated worker holds the actual snapshot monitor; a narrowly
  internal friend-assembly method requires the exact queued terminal and invokes
  the existing production layout-failure callback. It never fabricates input,
  pre-clears the queue, or directly retires a policy/session in place of the real
  reset path. It observes queue, epoch, deferred capture/timer, handles, flyout,
  selection, and a subsequently accepted fresh contact in either viewport.
  Acquisition/hold deadlines and window-close release prevent an abandoned test
  from retaining the monitor. This is implemented instrumentation, **not a live
  P1-005/P1-006 pass**.
- Review strengthens that probe's oracle: Cancel with a preexisting selection
  must preserve the pre-contact range captured at Arm, while Release preserves
  the range before its terminal. The matching terminal count must be zero when
  worker contention is established. The receipt uses source-generated JSON;
  normal sample startup and the public renderer API are unchanged.
- The final probe/sample Release/x64 build succeeds with **zero warnings/errors**
  in 1m03.52s. The focused offline suite passes **67/67**, zero skips, in
  `phase1-20261013/deferred-touch-probe-offline-final.trx`. Nine cases execute a
  real monitor on the dedicated worker (same-thread release, abandonment,
  acquisition/release errors, bounded deadlines); the rest are logic or wiring
  checks. Neither this build nor these source guards certify real routed touch,
  the failure callback, COM geometry, actual DPI, or High Contrast.
- Main/follow-up guards are rerun under PS7 and Windows PowerShell 5.1 with
  clean exits. No guest launch, touch input, connection/reset, host display
  setting, or normal-desktop input is performed in this continuation. The old
  terminal live report remains **37 PASS / 18 FAIL / 7 UNVERIFIED**, rather than
  being rewritten after offline fixes. A new approved live batch is required
  after the testing workflow's exhausted two repair/rerun cycles.
- Further pre-live review finds two missing probe/driver integration points:
  the dedicated window did not expose the raw routed-contact observer or the
  expected ancestor ScrollViewer ID. Both are corrected. Observation attaches
  to the active renderer, detaches on viewport replacement/close, remains out
  of the focus/reading order, and does not handle or synthesize input. The
  failure receipt also records whether the worker actually holds the snapshot
  immediately before the production callback. The revised sample builds with
  **zero warnings/errors** in 9.81s and the focused suite passes **68/68**, zero
  skips, in `phase1-20261013/deferred-touch-probe-offline-observer.trx`.
  These are still build/offline receipts, not executed guest-contact evidence.
- The same review adds a failed-probe lifecycle guard: a viewport change is
  refused until the worker has completed and the active contact, queue,
  deferred captures/retry timer, and flyout have retired. The checkbox restores
  the actual mode instead of throwing from a routed event or detaching the
  observer before cleanup evidence. The final Release/x64 sample builds with
  **zero warnings/errors** in 8.46s; **69/69** focused offline cases pass without
  skips in `phase1-20261013/deferred-touch-probe-offline-final-reviewed.trx`.

- Final pre-live review keeps the diagnostic receipt at the existing fixed
  144-DIP sample token height, so JSON growth cannot change viewport size and
  invalidate the exact snapshot/contact under test. The revised Release/x64
  sample builds with **zero warnings/errors** in 7.95s; the focused suite passes
  **69/69**, zero skips, in
  `phase1-20261013/deferred-touch-probe-offline-fixed-viewport.trx`.
- The new guest-only deferred-touch driver is bound to the exact process,
  executable, sample DLL and renderer DLL before input and at finalization.
  It uses real routed touch and a bounded foreground-checked input pump, not
  synthetic control-state changes. Its receipts retain the original held
  pointer/epoch through completion while separately validating the new fresh
  contact; both terminal pump metrics are saved for audit. A collapsed empty
  caret is accepted only as an empty selection, and this short fixture requires
  the correct ScrollPattern rather than artificial vertical overflow. Missing
  UIA geometry is allowed only for the exact BeforeTerminal sequence while the
  matching snapshot is demonstrably held; geometry remains required before
  contention and after release. Independent offline guard runs pass **54/54**
  under both PowerShell 7 and Windows PowerShell 5.1. Both shared and deferred
  native C# declaration blocks also compile in a fresh host PowerShell 5.1
  process without invoking native methods, guest bootstrap or UI/input.
  This is not guest cold qualification. This driver has **not** executed a live
  guest contact, and P1-005/P1-006 remain open.
- Sandbox remains the preferred live target. Read-only review of installed
  WinApp 0.7/WSB help and Microsoft documentation finds no documented guest-DPI
  command/configuration setting. That is not proof of a universal Sandbox
  limitation or of a working manual/remote-session alternative. The saved
  guest evidence is still 96 DPI/1.0; actual 144/192 DPI with corresponding
  WinUI scale must be observed before 150%/200% can pass. No new guest launch,
  reconnect/reset, live input, physical display change or scheduled run occurs;
  the previous batch's **37 PASS / 18 FAIL / 7 UNVERIFIED** remains unchanged.

### Latest offline continuation: keycap selection and Sandbox runner integration

- P1-016 has independent failing characterization receipts before its fix:
  `phase1-20261013/keycap-adjacent-characterization.trx` records 11 failures and
  six passes. A second eight-case characterization proves that U+20E3 attached
  to an ineligible Latin/Arabic-digit base must not split an ordinary word.
  The correction recognizes only the existing ASCII digit/`#`/`*` keycap bases,
  keeps the emoji grapheme atomic, and stops neighboring word selection and
  navigation from crossing it. The 113 focused cases and eight native
  formatted-run/LTR/RTL cases pass without skips. The final full native suite
  passes **2,988/2,988**, zero failures/skips, 2m20s, exit 0, in
  `phase1-20261013/renderer-keycap-selection-final.trx`.
- The revised Release/x64 unpackaged sample builds with **zero warnings/errors**
  in 23.90s. Its sample DLL hash is
  `CA0CB72800A18BFF9B9AFEA6BF3A47FC025CA05FC51CAC4C42F271E4AAC0F2F8`;
  its renderer DLL hash is
  `A8B0C9A1C6A7CA59E14EEF9DD71FA65E57313327746202596CDD218824FB004C`.
  These bytes have not been deployed or live-qualified. New real hold/double-tap
  and exact UIA/clipboard cases cover both viewport modes without changing the
  frozen sample corpus. The final-SVG oracle now requires the exact independently
  authored alt/source and selected image bounds, not generic selection growth.
- The new deferred-touch orchestrator validates the actual direct child JSON
  emitted by `target exec`, then strictly validates the guest Identity record.
  Only after verifying its actual `exePath` against the approved deployment
  does it derive the canonical process name/path. It rejects missing identity
  mode, reused PID/start time, changed payload, wrong epoch, contradictory
  exit/report status, and incomplete cleanup. The guest driver retains positive
  worker-join proof after ordinary Stop and emits exactly one metrics receipt.
  The host does not close a probe or continue cases while its input worker's
  retirement is uncertain. Independent guards pass **58/58 host** and
  **63/63 guest driver** under both PS7 and PS5.1. These functional mocks and
  source guards do not certify a real Sandbox contact or P1-005/P1-006.
- The current core-correctness slice initially records **1,411 passes and one
  failure**, zero skips, in
  `phase1-20261013/core-correctness-keycap-current.trx`: the public XML-doc
  filter exceeds its unchanged 45-second subprocess deadline. No timeout is
  raised and no filter behavior is changed. Its focused rerun passes **3/3** in
  seven seconds (`xml-filter-timeout-focused-rerun.trx`); repeating the entire
  same slice passes **1,412/1,412**, zero skips, 1m12s, exit 0, in
  `core-correctness-keycap-current-rerun.trx`. The timeout's cause is not proven;
  the original failure is retained. This slice excludes only the unchanged
  retired browser/performance evidence fixtures, not any current correctness
  failure, and is explicitly not a full pure-core run.
- C14 now explicitly records active-contact capture loss, unload, and disposal
  as **UNVERIFIED** because this main batch does not induce and observe them.
  Offline lifecycle coverage is not substituted for live reset evidence.
  No new Sandbox launch/reconnect, UI input, guest setting change, physical
  display change, commit, push, or scheduled run occurs. Host scaling remains
  the user's 150%; saved guest evidence remains 96 DPI/1.0. The old live
  **37 PASS / 18 FAIL / 7 UNVERIFIED** report is unchanged. Guest 144/192 DPI
  and matching XamlRoot scale still need observation before 150%/200% qualifies;
  absence of a documented Sandbox DPI switch is not proof that it is impossible.

### Sandbox DPI investigation: read-only checkpoint

- The already-connected guest retains epoch
  `218a2f04-395a-43f3-88b9-40ebad4967fe:3363A2EAC7303DD9FEA523D207994236`.
  Its Settings window (PID 4536, HWND 14286918) independently reports 96 DPI
  and scale 1.0. A depth-10 UIA inspection finds 112 nodes on its System page,
  including Sound, Storage, Multitasking, and About, but no Display page or
  Scale control. No navigation or setting mutation is performed. This is a
  limitation of the inspected guest/session, not a demonstrated universal
  Sandbox limitation.
- Microsoft's documented [Sandbox configuration options](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file)
  do not include a DPI setting. Installed WinApp 0.7/WSB command help also has
  no guest-scale parameter. The remote-session `desktopscalefactor` setting
  documented for Remote PC/RDS/AVD is not evidence of a supported Sandbox route.
  Client-window resizing, screenshot downscaling, text scaling, and renderer
  zoom do not count as actual app DPI qualification.
- A fresh Sandbox-only batch and reversible guest-scale experiment have been
  requested, not yet authorized or run. A guest restart would require separate
  approval and evidence preservation. Physical input and host scaling changes
  remain paused; Windows remains at the user's 150%. The existing live report
  remains **37 PASS / 18 FAIL / 7 UNVERIFIED**. Actual app-window 144/192 DPI
  plus XamlRoot scale 1.5/2.0 remains required.

### Latest offline continuation: bounded active-touch lifecycle probe

- The sample's explicit `--renderer-touch-lifecycle-probe` startup option
  selects a test-only window; ordinary startup remains unchanged. It observes
  a real long press, native selection-Button capture, and routed touch movement
  before applying one actual action: `ReleasePointerCaptures`, visual-tree
  removal, or public `Dispose`. The control addition only reads internal state
  on its UI dispatcher; it does not inject input, reset state, or add public API.
  Its native `TouchLifecycleCase` ComboBox selects the three fixed cases and
  `TouchLifecycleArmCase` arms one; `TouchLifecycleAncestorViewport` selects
  viewport ownership. `TouchLifecycleReceipt` exposes the bounded JSON report.
- Cleanup observations retain the exact native Buttons, so absence from the
  visual tree cannot prove capture release. A passive terminal observer also
  remains on the captured Button after removal. Capture loss is not contact-end
  proof: completion requires a real same-pointer release/cancel reporting
  `IsInContact == false`, with the recorded gesture epoch, plus empty captures,
  queues, retry/frame work, and flyouts. Unload reattaches only after that proof
  and then requires a fresh long press with a new epoch and a different range.
  Ordinary unload is not required to clear a previously committed selection.
- Root and captured-Button observers are distinguished in each receipt entry;
  their callbacks for one bubbled terminal are not presented as distinct input
  contacts. Review also replaced the three stacked command rows with the
  native picker and one Arm button, shortened instructions, and made the
  token-bounded receipt share flexible height with the document. This removes
  the fixed 144-DIP diagnostics reservation in a short/high-DPI window. Actual
  narrow and 200% fit remains a live requirement, not a source-guard PASS.
- Review corrected two compiler errors, failed source-guard locators, a
  precondition that incorrectly required a sustained frame clock outside the
  edge-scroll band, stale Button references after reattachment, and loss of
  terminal observation through a detached root route. These are probe repairs,
  not a claim that production lifecycle behavior is fixed or live-qualified.
  The initial failing guard receipts remain intact:
  `touch-lifecycle-probe-initial-guards.trx` (0 pass / 2 fail) and
  `touch-lifecycle-probe-wiring-focus.trx` (8 pass / 1 fail).
- The final reviewed correctness slice passes **1,462/1,462**, zero failures or
  skips, exit 0, in
  `phase1-20261013/core-correctness-touch-lifecycle-reviewed.trx`.
  The earlier 1,460-case passing receipt is retained in
  `core-correctness-touch-lifecycle-current.trx`. Its eight new probe tests are
  wiring guards only. The slice also contains the
  full currently authored keycap regression cases; it excludes only the
  unchanged retired browser/performance evidence fixtures and is not a full
  pure-core run. The full native-renderer suite passes **2,988/2,988**, zero
  failures/skips, exit 0, in
  `phase1-20261013/renderer-touch-lifecycle-probe-current.trx` (2m22s).
  Both TRX counters were independently read after completion.
- After the final observer-label and compact-layout changes, the Release/x64
  unpackaged sample builds with **zero warnings/errors** in 9.55s. The sample
  DLL SHA-256 is
  `449EE24363C208D810D4B9DDB62DC864F0CD2E860072386DAFBC3C6AEC251CF9`;
  its build-layout renderer DLL SHA-256 is
  `82CAFEC298EB2D9CB4187F094A3E38DD47132686BCA4526E83CD8D60AFE1F867`.
  These bytes have not been deployed or live-qualified.
- The fresh Sandbox-only batch/guest-scaling experiment remains pending
  approval after the WinUI UI-testing workflow's two repair/rerun cycles.
  A read-only snapshot confirms the same connected guest epoch and existing
  old selection-disable probe, not the new window. No guest restart, app
  launch, synthetic input, or display-setting change occurred. The old live
  **37 PASS / 18 FAIL / 7 UNVERIFIED** aggregate remains unchanged. Physical
  checks remain paused at the user's 150%; actual guest 144/192 DPI and
  matching XamlRoot scale remain unverified. C14 and Phase 1 are still open.

### Latest production repair: deferred cancellation during unload

- Caller review reproduced a concrete P1-006 ordering gap: `OnUnloaded` cleared
  the tap policy's gesture epoch before `ResetDeferredPointerInput` could match
  an exact-owner queued Cancel. The shared terminal helper was correct in
  isolation, but this caller prevented its normal cancellation disposition.
  The pre-fix receipt `phase1-20261013/deferred-cancel-unload-characterization.trx`
  records **2 pass / 1 fail**, zero skips. Its failing check is explicitly a
  source-order guard, not actual XAML unload evidence.
- The internal `PrepareForReset` stage retains the existing recognizer-provenance
  barrier without clearing active/completed/unowned ownership or suppression
  state. Unload first stages that barrier, retires queued terminals using the
  still-valid owner epoch, then clears ownership. This preserves a range
  committed before a canceled contact and clears a range created by that
  contact, without making ordinary unload clear selection. A fresh accepted
  contact retires the barrier; an old epoch cannot cancel a reused pointer ID.
- Nine new cases cover caller order, both cancellation dispositions and all six
  existing provenance conditions. An initial CS0051 test-signature compilation
  error was corrected before the focused tests ran; it produced no passing TRX.
  The focused policy/session/deferred-input/probe-wiring receipt passes
  **65/65**, zero failures/skips, exit 0, in
  `phase1-20261013/deferred-cancel-unload-focused.trx`. Independent code review
  found no further actionable ordering/reentrancy issue. These cases do not
  prove a real control unload with an already queued Cancel.
- The first full correctness slice records **1,469 pass / 2 fail**, zero skips,
  in `phase1-20261013/core-correctness-deferred-cancel-unload-current.trx`.
  `FilterPublicXmlDocumentationTests.Filter_uses_exact_overload_and_generic_signature_ids`
  exceeds its unchanged 45-second subprocess deadline, and
  `PerformanceCounterbalancedOrchestrationContractTests.PreflightDistinguishesNestedRuntimeContentBeyondTheFiveExplicitArtifacts`
  exceeds its unchanged 30-second deadline. The native build was running at
  the same time; causation is not established. No limit, assertion or filter is
  relaxed. After native completion, a serial rerun of those exact two cases
  passes **2/2**, zero skips, in
  `deferred-cancel-unload-script-timeout-focused.trx`; the entire same slice
  then passes **1,471/1,471**, zero failures/skips, about 15 seconds wall time,
  exit 0, in
  `core-correctness-deferred-cancel-unload-current-rerun.trx`. Original failures
  remain intact. This slice excludes only the unchanged retired
  `PerformanceExternalGateFixtureTests`; it is not a new full pure-core receipt.
- The full native-renderer suite for this repair passes **2,988/2,988**, zero
  failures/skips, 2m30s, exit 0, in
  `phase1-20261013/renderer-deferred-cancel-unload-current.trx`. All six new TRX
  counters (characterization, focus, initial slice, timeout focus, slice rerun
  and native suite) were independently read after completion.
- The final Release/x64 unpackaged sample builds with **zero warnings/errors**
  in 10.42 seconds. Its sample DLL SHA-256 is
  `FD2129B262CE238A68EC6DD38FA0BFE6C49800A08C5613255AEB5CAADD55873F`;
  its build-layout renderer DLL SHA-256 is
  `77400F2F7084C1E9D40C64DD022EFFFB644C8C60C49709742534E511C13648FF`.
  The renderer copy in the sample's `win-x64` output has the same hash.
  This is a build receipt, not AOT or live deployment qualification.
- No new Sandbox launch, restart/reconnect, synthetic input, guest display
  mutation or physical-desktop check occurs. The fresh Sandbox-only batch and
  reversible guest-scale experiment remain pending approval under the WinUI
  UI-testing workflow's two repair/rerun limit. Host scaling remains the user's
  150%; saved guest DPI remains 96/1.0. Actual 144/192 DPI with matching app
  scale, narrow/high-DPI probe fit and live C14 coverage are still required.
  The old **37 PASS / 18 FAIL / 7 UNVERIFIED** live report is unchanged.
  Phase 1 remains open. No commit, push or scheduled run is created.

### October 14: DTD repair and first-canvas WebP regression

- P2-001 is a demonstrated admission-contract mismatch, not evidence of DTD
  resolution or execution. The pre-fix host characterization fails **1/1** and
  the corrected provider characterization records **1 pass / 2 fail**. The
  initial provider characterization used an invalid nested XML fixture and is
  preserved, but is not evidence of valid-content behavior.
- Root host preflight and direct provider preflight now reject every real
  `DocumentType` node as documented. Resolver access remains disabled, entity
  expansion bounded, source limits unchanged, and failures are typed before a
  worker source attachment is created. The now-dead inert-DOCTYPE stripping
  path is removed. Comment, CDATA and escaped-text marker cases remain valid;
  UTF-8/BOM, UTF-16 LE/BE with/without BOM, and valid non-DTD encoding
  normalization are covered. The caller's bytes remain unchanged. An internal
  XML-helper round-trip is not a public admission path and remains covered.
- Focused host tests pass **59/59**. The initial provider focus has **23 pass /
  1 fail** because a test expected an internal reason token in the public
  diagnostic; the corrected assertion passes **24/24**. The first full provider
  run has **719 pass / 1 fail** because a reservation test still used a
  DTD-bearing success fixture. Replacing only that fixture with valid Latin-1
  input retains exact normalized content, description, caller immutability,
  reservation shrink/accounting and release assertions. The full rerun passes
  **720/720**, zero skips. Original failures are retained.
- P2-I01 gains a bounded final-ImageBox WARP pixel regression, not a speculative
  decoder rewrite. Its independently authored two-frame WebP has a 3×1 canvas,
  an offset half-alpha black first-frame pixel at x=2 and a later white sentinel
  at x=0. The independent canvas oracle rejects raw subframes, lost offsets,
  lost alpha and later-frame content; it permits the WebP specification's
  optional black background hint. Fixture decoding proves validity but does
  not supply expected output pixels. Both absent-provider and registered-static-
  provider modes publish BGRA `00000000000000000000007F`, and the static provider
  receives neither capability queries nor decode calls. The final isolated run
  passes **1/1** and the full native suite passes below. No production WebP
  defect was reproduced by this case. The retained initial colored-pixel test
  failure exposed oracle/rounding ambiguity, not a proven decoder defect; this
  bounded regression does not qualify arbitrary color or animation variants.
- Current-candidate broad receipts in `artifacts/phase2-20261014/` pass:
  core correctness slice **1,475/1,475** (only the unchanged retired
  `PerformanceExternalGateFixtureTests` excluded), full native renderer
  **2,989/2,989**, SVG provider **720/720**, math **76/76**, HTML **10/10**,
  Mermaid **115/115**, TextMate **61/61**, and conformance test project **9/9**.
  All have zero failures/skips and terminal exit 0; the TRX counters were read
  independently. The separate pinned offline semantic conformance run also
  passes **1,322/1,322** (652 CommonMark and 670 GFM), zero exceptions/failures,
  in `conformance-current.json`; the JSON summary and pinned corpus identities
  were inspected. Its raw-HTML comparison oracle is not a native drawing or
  production-profile security test. These are not live/DPI or complete Phase 2
  matrix passes.
- Release/x64 sample build with the previously approved command-line
  `WindowsPackageType=None` experiment passes with **zero warnings/errors** in
  11.81s; the checked-in packaged default is unchanged and no app is launched.
  The sample-layout DLL SHA-256 values are sample
  `86EFF558551102236D56DC4843EC9CA59A558FDAE460D1341B75CE7668132CB9`,
  renderer `28649492CFE28BB6AFAF082E95EC8D38C926741ECB9B37DF5CE7A776882C9DAB`,
  and provider `43A9D058C03DA504C38B3CB3141BFB4BE81DD20440FBC518AF81D04A00E73EA1`.
  This is a build receipt, not NativeAOT or live deployment qualification.
- Phase 1 remains open. The fresh Sandbox-only live batch and guest-DPI
  experiment remain unrun; no host display change, guest restart, synthetic
  input, commit, push or automation is performed by this offline validation.

### October 14: x86 worker startup diagnosis

- Installed WinDbg contains both x86 and amd64 CDB even though neither is on
  PATH. Diagnostics use only owned headless console processes, preserve the
  worker's restricted primary token, low integrity, job and process mitigations,
  and apply no registry/global loader flags, system ACL or desktop changes.
  A bounded 45-second watchdog retires only the diagnostic process tree and
  records any remaining owned host/worker processes. A debugger exit 0 is not a
  rendering pass.
- Historical repro host/worker SHA-256 values are
  `4CB8E5506C62F6B274779BE3055D5C4E6E49853332C7B0E3B19A1C89FF503B64` /
  `A207D116D8578808310BE2804AEC71DECCBDF3114ABA780103D5034CF78ADCFC`.
  Attempt 07 reaches natural worker `0xC0000022` termination in the native
  64-bit WOW64 initialization path. Attempt 08 records early denied opens but
  hits a later initial breakpoint and times out; its missing public
  `ntdll!_OBJECT_ATTRIBUTES` type prevents interpreting object names. Neither
  proves a current-candidate render. Attempts 09–11 are failed syntax/type
  discovery, not successful startup evidence. All raw failures remain intact.
- A small artifact-only C++ probe uses the installed SDK 10.0.26100.0
  `winternl.h` and exact x64 compiler to materialize its SDK-defined object
  attributes/string types in its own PDB. This supplies names without guessed
  structure offsets or loading a new DLL into the worker. Type-gate attempt 13
  verifies both types. Attempt 14 identifies names but retains a diagnostic
  syntax error; attempt 15 confirms the names but also retains command parsing
  errors. They are superseded by the clean current-candidate trace below.
- A fresh Release NativeAOT win-x86 smoke host is published from the current
  dirty candidate. The initial `--no-restore` publish fails NETSDK1112 because
  that runtime pack was not yet restored; the ordinary restore/publish then
  succeeds, exit 0, without suppressing trim/AOT analysis. Current host/worker
  SHA-256 values are
  `F975D9B649D290FD4C66DAA04920C061E892728CFD3984D9DEA64D9A4867FCD6` /
  `E60010BCE124CCBB5CB90BB63683B58E0D047A0758BEB045E1576C74AA9B57F4`.
- `x86-cdb-loader/cdb-loader-attempt16-current-aot.log` and its process receipt
  record a clean native loader trace of those exact bytes (no diagnostic syntax
  errors, timeout or orphan process). The SDK-typed `ObjectName` is
  `\??\C:\WINDOWS`; `NtOpenFile` requests `0x00100080` and returns
  `0xC0000022` at `wow64!InitializeWindirFullObjectName+0x87`, called from
  `Wow64pInitializeFilePathRedirection`, `ProcessInit` and
  `Wow64LdrpInitialize+0x142`. Disassembly shows the failing `ProcessInit`
  status copied to EBX, tested and passed to `NtTerminateProcess`.
  Earlier current-directory/registry denials are separate and are not used as
  the causal inference. The observed Windows-directory DACL contains no
  Restricted Code ACE; it is inspected only, never modified.
- The worker naturally exits `0xC0000022`; the smoke host's
  `--expect-unavailable` path then exits 0 as designed. This proves the current
  fallback/startup defect, not a successful SVG render, an architecture waiver
  or overall NativeAOT correctness. P2-002 remains open. A possible
  initial-thread-only trusted bootstrap followed by verified reversion is an
  artifact-level investigation; token-object DACL, owner rights, retained-token
  recovery, actual WOW64 startup and fail-closed ordering must be tested before
  any production adoption. No restricting SID is added to the primary worker
  token and no protection is relaxed in shipping code.

### October 14: portable WebP characterization and x86 startup candidate

- P2-I02's deterministic wrapper preserves the original pinned VP8 chunk and
  adds a raw constant-alpha plane. Source SHA-256 is
  `B8502B8946730BC3A6B5B952437D25F882EE612F0534BB6A66DAA8B2FB460530`;
  wrapped 106,512-byte SHA-256 is
  `8B1E9A7913986E7EAC97DAE408288B69FF4A8E6D9F84D7F2F4CCB6D065C43164`.
  One-off WIC validation independently verifies a static 256×256 canvas with
  alpha 127. The permanent provider test pins the exact bytes and has no WIC
  dependency: OS WebP support is optional, while this native provider must not
  need an installed codec. It checks display-sized output, alpha and
  premultiplication, exact resource failure and unrelated-content recovery.
  `phase2-lossy-alpha-portable-current.trx` passes **1/1** and
  `phase2-provider-full-721-current.trx` passes **721/721**, zero skips. These
  receipts precede the x86-startup changes below, not their qualification.
- Artifact-only x86 bootstrap attempts 01–03 remain failed diagnostics. Attempt
  04 removes an invalid diagnostic dependency on LSA privilege lookup from the
  restricted child: the broker supplies its queried privilege LUID through
  diagnostic-only environment fields, and the child still checks the exact
  enabled privilege. The first restricting-SID combination is rejected by
  CreateRestrictedToken; this is retained, not treated as success.
- `x86-bootstrap-probe/attempt05-no-retention.json` records the stronger benign
  control/treatment experiment. Control reproduces `0xC0000022`; treatment
  exits 0, no timeout/orphans. Its process primary stays RC-only/low before main
  and after reversion. Only its initial thread receives a low RC+Users startup
  context. The token object's actual owner/DACL and separate medium mandatory
  label are recorded; OWNER_RIGHTS has READ_CONTROL only, with no query,
  duplication, impersonation or ACL-write grant. OpenThreadToken attempts for
  QUERY, DUPLICATE, IMPERSONATE, WRITE_DAC and WRITE_OWNER are denied under both
  OpenAsSelf modes. PROCESS_DUP_HANDLE opens of the broker are denied before
  main, before revert and after revert. Pseudo-handle duplication must fail
  without returning a handle; its ACCESS_DENIED/INVALID_HANDLE refusal is not
  substituted for the independent token-object access checks. RevertToSelf and
  subsequent ERROR_NO_TOKEN checks pass. Broker startup handles close before
  ResumeThread; no handle is inherited.
- Production integration is a **candidate under validation**, not a closed
  P2-002 defect. The C# proposal attaches the protected startup context only to
  suspended x86 workers after job assignment, closes its handles before resume,
  and leaves the original primary restriction and all mitigations unchanged.
  The matching Rust guard must revert before arguments, pipes, document data,
  fonts or threads, verify no thread token remains, and validate the unchanged
  primary. Rebuilt/source-bound workers, actual x86 SVG/raster/text output,
  NativeAOT, cancellation/shutdown and x64 regressions remain required. No live
  app batch, guest scaling mutation or physical desktop input has occurred.

- The first production startup candidate now has an actual successful
  **NativeAOT x86 render** receipt, not `--expect-unavailable`:
  `startup-x86-aot-first-run.json`, host
  `D9C5B253C9B6D40B1C2F71F2FC3B821D237A9AE3BAC9607F10B1DE9D185F7DF1`,
  worker `287820E4C4B402E197297BF571A62F9D39C6732AF8FDBE3C4EAA1B607B48590E`.
  It passes simple SVG, RTL/embedded raster, all 182 unique embedded images,
  static/streamed WebP/ICC, 100 render/evict cycles, verified same-worker
  isolation before/after those cycles, eight startup/shutdown races, disposal
  reaping, and retained-source restart recovery. Aggregate handle growth is 0;
  private-byte growth is 3,620,864, within the unchanged 8 MiB bound. No timeout
  or orphan child is recorded. Live module checks find no GUI/browser DLLs.
  The focus startup-policy tests also pass **4/4**. These are headless native
  process checks, not an app UI/DPI pass.
- That first candidate imports ADVAPI32 for three startup APIs. The existing
  worker import-boundary test correctly does not admit it. Its reviewed regular
  dependency closure is headless on this host, but its unrelated delayed USER32
  import is an avoidable expansion. The final candidate instead uses two exact
  Windows core/security API-set contracts through Rust compiler-generated PE
  imports, with explicit undecorated x86 names; no runtime discovery or native
  dependency is added. Token-predicate tests link/run **2/2** on x64. Matching
  three-RID independent rebuilds, import inspection and final render/regression
  receipts are pending. Initial payload reproducibility passes after the repo's
  standard PE timestamp/checksum/CodeView normalization; raw file hashes differ
  and are retained separately. This is not a claim of raw-byte equality.
- Native worker unit tests before the import refinement pass **89**, fail 0,
  with one pre-existing explicitly ignored manual font microbenchmark. An
  additional strict Clippy run fails existing vendored style diagnostics; the
  `--no-deps` run fails five existing raster-decode style/argument-count
  diagnostics, with no reported diagnostic in the new guard. No lint is
  suppressed and no unrelated vendored/raster code is changed to turn that
  check green. Final supported-OS/architecture qualification remains open.

### October 14: final x86 repair qualification

- Final native source fingerprint is
  `445add6eebceff4271a2d102eec5cd8273c41b451f2cb564cb18592b060ee7e0`.
  All three RID payloads pass independent builds and the unchanged normalized
  executable-payload comparison, with separate official identity receipts.
  The strict PE check admits only the exact audited core/security contracts;
  inspection of every RID verifies named imports for the three required token
  functions, no ADVAPI32/GUI import, no ordinal substitute, and no delay imports.
  The x86 stdcall export names are explicitly undecorated. This is compile-time
  PE linking, not runtime DLL probing or an in-process renderer fallback.
- Final host/worker SHA-256 pairs are:

  - x86 host `D9C5B253C9B6D40B1C2F71F2FC3B821D237A9AE3BAC9607F10B1DE9D185F7DF1`,
    worker `42D798F2843453EC4FACF20566A03B0B58648F1103F6F12E593D1C72138A6FFE`.
  - x64 host `6B619DCD6AAF14CF89D449535D715570BCFD931A0AAE3299E6D422B13D2A88F1`,
    worker `CEBB7F2842B22A8F1EE9CB631FA925A5EDC3E6C3F56FDF8179325BB3C2F75AF1`.

  `startup-x86-aot-final-run.json` and `startup-x64-aot-final-run.json` both
  record exit 0 without `--expect-unavailable`, no timeout or orphan, and all
  simple/rich/RTL/182-image/WebP/ICC, 100-cycle, isolation, shutdown-race and
  retained-source restart assertions passing. Final aggregate retained growth
  is **2 handles / 4,653,056 bytes** on x86 and **0 handles / 5,537,792 bytes**
  on x64, within the unchanged four-handle/eight-MiB limits. Worker identity is
  unchanged during each measured lifecycle. The external probe verifies an
  enabled low-integrity primary, RC-only restriction, disabled dangerous
  privileges, no remaining thread impersonation, required queryable mitigations
  and job membership; live module inspection rejects GUI/browser DLLs.
- The final full provider run passes **725/725**, zero skips, in
  `phase2-provider-startup-final.trx`. Final native renderer run passes
  **2,989/2,989**, zero skips, in
  `phase2-renderer-startup-explicit-x64-final.trx`; the explicit x64 invocation
  emits no Win2D platform warning. The preceding omitted-Platform invocation
  also passes all 2,989 tests but emits WIN2D0001; it is retained and not called
  a warning-free build. Final native Rust tests pass **89**, fail 0, with the
  one unchanged ignored manual font microbenchmark; the two new token predicates
  separately link/run on x86 as well as x64.
- Release NativeAOT smoke publishing succeeds for **x86, x64 and ARM64** without
  disabling trim/AOT diagnostics. ARM64 host SHA-256 is
  `CAA04D5B14233520D6F55DF0B9CA4A26875A1BAA98338856BA45DDA01C8C19A5`;
  it is not executed or treated as an architecture pass on this x64 machine.
  ARM64 worker SHA-256 is
  `CC729F79CB694543E0D2EEA18D6018EEA4AAA3278153531531FB7C9B751D8C3D`.
  An actual ARM64 Windows target, minimum-supported-OS target and packaged/Store
  execution are still needed for the corresponding Phase 2 matrix cells.
- The sample's final Release/x64 build, using only the previously approved
  command-line unpackaged experiment, passes **zero warnings/errors** in
  17.35s (`sample-startup-final.binlog`). Its DLL SHA-256 is
  `761DDB9131A088146D6B89E5349ABCCD5704F8D0FADF75A60D730FFDF835BE4F`;
  provider DLL is
  `8A592BBD0E7718F3EF76FFCFB0F0E990D989498C80C84B7B0CD93B3CF1E9C23A`.
  The sample is not launched/deployed and packaged defaults are untouched.
- P2-002 is a completed targeted functional repair, not completion of Phase 1,
  the full 32-case Phase 2 matrix or the current-candidate 500-document audit.
  Live Sandbox/DPI reruns remain pending; no host/guest display setting, guest
  lifecycle, synthetic input, scheduled task, commit or push is changed.

### HTML decoding and public-engine disclosure corrections

- P2-003 was recorded before its production fix. The initial pure-parser
  characterization has **5 FAIL / 6 PASS**, reproduced in a second receipt.
  Eleven native URL cases reproduce changed link/image destinations before
  the fix. The normalizer now validates the already-decoded attribute value;
  it still trims and enforces the existing length, control-character and scheme
  rules. A downstream audit finds no extra HTML decode in image resolution,
  host-mediated link activation, JitHub navigation or CF_HTML serialization.
- The initial native helper used a legacy Markdig pipeline without precise
  locations, so two new image-span assertions later failed on its `0+1` spans.
  That was test setup, not evidence of a new renderer source-span defect.
  The helper now uses the actual public GitHub engine and its parsed document;
  the exact authored image-span and CF_HTML assertions remain unchanged and
  pass. An initial compact-picture fixture used the inline fallback path;
  the corrected explicit HTML-block fixture reproduces the URL fault on the
  supported typed picture-source selection path. Both earlier receipts remain.
- P2-005 was independently reproduced through that public pipeline:
  `html-entities-public-pipeline-before-alt-fix.trx` has **17 PASS / 9 FAIL**.
  Six block image fallbacks double-decode authored alt text, while six inline
  controls preserve it; three compact generic summaries also double-decode.
  `CollapseDecodedWhitespace` now separates whitespace normalization from HTML
  decoding. Raw text is decoded once, and already-decoded attributes/summaries
  are not reinterpreted. The original image source ranges, inert text rendering,
  style/link context and host policy are preserved.
- P2-006 was found by requiring the actual engine to produce the advertised
  Markdown-bearing disclosure container. Its default, options and derived
  paths all fail before the callback repair, although legacy registry parsing
  already installs the parser. After the engine fix, **four control-builder
  cases still fail** until both public builders use the same static callback.
  Four CommonMark/GFM literal-profile checks already pass and continue to pass;
  the callback only installs details while `HtmlBlockParser` is enabled.
  `UseSafeHtml` replacing the registry was an investigated but disproven
  hypothesis: `ConfigurePresentation` composes the existing registry.
- With the real GitHub disclosure path reachable, its separate summary helper
  also reproduces three exact entity-content failures in
  `github-details-parser-fixed-summary-before-fix.trx` (**3 PASS / 3 FAIL**).
  It now uses the same decoded whitespace boundary. Compact generic and
  Markdown-bearing GitHub summaries have separate regression fixtures.
- Current Release/x64 receipts under `artifacts/phase2-20261014/`:
  `html-decoded-whitespace-focus.trx` **12/12**;
  `html-entities-core-final.trx` **1,511/1,511** (excludes only the unchanged,
  retired `PerformanceExternalGateFixtureTests`, not a complete pure-core run);
  `html-entities-and-disclosures-native-full.trx` **3,029/3,029**, zero skips.
  New tests use independently authored content/URL/span expectations, WARP and
  no remote-image resolver; they do not write to the desktop clipboard or
  qualify live UIA/selection/input behavior.
- P2-007 is reproduced separately after that full run. The existing root-unknown
  `KeepTextContent` fixture passes; it is not evidence of a failure in that path.
  Four new root/nested policy fixtures plus the default literal control produce
  **2 PASS / 3 FAIL** in `html-nested-script-policy-characterization.trx`.
  Scoping the literal-script exception to `RenderLiteral` fixes the three
  nested cases without changing P1-009's default literal contract. Independent
  code review finds no blocking issue in this policy boundary.
- The final Release/x64 native run, including all these fixes and five new
  script-policy cases, passes **3,034/3,034**, zero skips, in 2m20s:
  `html-correctness-final-native-full.trx`. The sample also builds in 10.71s
  with **zero warnings/errors**, using only the previously approved build-only
  `WindowsPackageType=None` command-line experiment:
  `sample-html-correctness-final.binlog`. Packaged project defaults are untouched;
  this build is not an app launch or live UI/DPI qualification.
- P2-004 remains open, deliberately not hidden or skipped:
  `mermaid-current-known-source-defect.trx` has **115 PASS / 1 FAIL**.
  Arabic/Latin/emoji label commands preserve their text, but two distinct node
  semantics both map to the first occurrence. The first characterization
  incorrectly expected one whole-label draw command; the final regression
  groups its three native text runs by semantic identity and fails only on
  independently authored UTF-16 source spans. The adapter's `source.find`
  cannot establish node provenance. Pinned Merman's editor facts do not retain
  an explicit final-model label ownership relation; source remapping can drop
  facts, and model merging can ignore definitions still emitted as editor
  facts. A label-order or adjacent-fact heuristic is not accepted as the fix.
  The next repair needs final-model-owned source provenance and a stable render
  identity, preserving the current pin, resource limits and licensing boundary
  unless a separately reviewed source patch is necessary.
- Physical input remains paused and no new Sandbox guest/scaling batch is run.
  The last live aggregate remains **37 PASS / 18 FAIL / 7 UNVERIFIED**.
  No host/guest display settings, scheduled runs, commit or push are changed.
  These targeted fixes do not close any whole phase or the 500-document audit.

### Mermaid final-model source repair: implementation and verification

- P2-004's six additional public-engine regressions reproduce incorrect source
  ranges after a later same-label definition, with LF, CRLF, CR, YAML frontmatter
  and init directives. All six fail only their exact authored source-span
  assertion in `mermaid-final-label-provenance-before-fix.trx`; no diagnostics
  or missing vector scenes are reported.
- Four adapter characterizations initially fail: identical labels map to the
  first occurrence, spatial label resolution overwrites a supplied range,
  missing exact provenance guesses another label, and malformed attributes are
  ignored. All four pass after the adapter repair; the complete native adapter
  suite passes **27/27** against the original upstream source. A subsequent
  independent review adds equal-offset and u32-overflow rejection cases before
  the final patched build.
- The narrow Merman patch retains final merged-node provenance by canonical ID,
  remaps through the existing preprocessing map and emits it on the actual SVG
  wrapper. No label-order, editor-fact-order or generated-ID heuristic is used.
  The original base commit/tree, resource controls, C ABI, MMIR schema, MIT
  selection and no-ELK boundary are unchanged. Shape-data replacements and
  inexact remaps intentionally have no exact range; the adapter preserves the
  safe whole-diagram source fallback rather than inventing an association.
- The independent source-preparation helper initially passes **17** offline assertions,
  including exact input identities, cache reuse and rejection of source edits,
  ignored artifacts, wrong hashes/base and path traversal. Its real production
  preparation also succeeds without modifying the shared Cargo checkout.
  Actual patched Cargo metadata matches all **196** inventory entries exactly;
  only six existing pinned crates become path-sourced from that verified copy.
  No registry version, license or package membership changes.
- Independent review then strengthens the helper to bind the raw manifest and
  patch-set digests to provenance, and to verify compiled Rust source bytes
  exactly rather than normalizing line endings. Its expanded suite passes
  **24** assertions, including a self-consistent manifest swap and a CRLF-only
  source mutation that Git's text comparison could otherwise conceal. The
  compiled upstream files and tracked patch inputs use canonical LF bytes.
- The first static-CRT x64 payload passes the full **122/122** managed Mermaid
  suite in `mermaid-provenance-first-patched-x64.trx`, including the original
  duplicate Unicode-label failure and all six final-definition cases. That is
  an intermediate payload, not final three-architecture qualification.
- Deeper upstream tests expose a missing remap on the alternate synchronous/
  asynchronous public parse path. Both ordinary and controlled paths now remap
  through the same original-source contract; **5/5** focused core fixtures pass
  and compare both paths. The initial test expectation that `#quot;` was
  unmappable was wrong: the complete placeholder maps exactly to authored token
  bytes `[22,28)`. The corrected independent source-slice assertion is retained.
- Upstream renderer integration passes **26/26** and its canonical-ID escaping
  unit passes **1/1**, using XML-parser round-trip identity rather than demanding
  one serializer spelling for the XML-valid `>` character. An initial raw-string
  delimiter compile error in that new test was repaired. The supplemental
  upstream workspace uses its own pinned Rust 1.95; product native builds use
  the recorded Rust 1.96. Incremental-directory access warnings were emitted
  by those supplemental renderer runs, despite exit 0; they are not described
  as warning-free.
- A superseded build of patch hash `334ed987...` was explicitly interrupted
  before runtime-payload publication after that test-only literal correction.
  Its failure receipt is an intentional cancellation, not a successful build
  or a renderer crash. Current source patch 1 is `0462b167...`; the final native
  adapter tests are running against its independently verified cache. The
  entity-rendering failure is separately recorded as P2-008 and is not hidden
  by changing the expected rendered character.
- The final source-patch-1 product native run now passes **29/29** with no
  ignored cases, including a full Merman-to-MMIR winning-label regression and
  a canonical-ID/visible-label-limit distinction. The standard three-RID
  rebuild is running from that exact verified source; no cross-architecture
  execution or final package consumer pass is claimed from this native test.
- Phase 1's separate source review finds no additional
  demonstrated implementation defect in the remaining point-query/lifecycle
  cases. Those remain live qualification gaps, not grounds to change geometry
  speculatively. Physical/Sandbox/DPI checks and the last live aggregate remain
  unchanged.

### Mermaid entity repair: reproduced public-pipeline boundary

- Source patch 1 (`0462b167...`) finishes the standard x64, x86 and ARM64
  static-CRT builds with exit 0. The published DLL SHA-256 values are
  `e98c914bf2fe5dc3b40947e94739d3a83ac6614c58d3e2b82197cdbd184d497b`
  (x64), `0907fd28e8c4193129fc82f02f06e9f0cb184ea2433960e5c05be6c0496014fe`
  (x86), and `94e840dc8acc2f37e42f06f1b3313a9e89d440e5ba662703c7288e3f034d3978`
  (ARM64). These identify this intermediate source-mapping repair, not the
  still-in-progress entity fix or execution on an ARM64 machine.
- `mermaid-final-provenance-x64-known-entity-failure.trx` runs the complete
  129-case managed Mermaid suite against that final x64 payload: **124 PASS /
  5 FAIL / 0 SKIP**. All five failures are the newly recorded P2-008 entity
  cases; they are neither filtered out nor reported as a green whole-suite run.
- Expanded public-engine assertions require the actual native draw text,
  accessible node name and independently authored Markdown UTF-16 span to agree.
  Numeric/Unicode/quote cases reproduce the literal-reference defect; encoded
  ampersand and encoded `<br>` remain passing controls. Three added geometry
  oracles compare encoded numeric labels with separately authored literal `#`,
  emoji and combining text. A final-text-only patch cannot pass their required
  scene viewport and node-bound equality.
- The first edit of the managed test used `IndexOf` on `IReadOnlyList` and did
  not compile. It was corrected to indexed tuple enumeration before recording
  the seven executed characterization cases; that compile failure is not a
  renderer result.
- The implementation is kept in a second reviewed source patch, separate from
  the first patch's stable index. It preserves Mermaid-preprocessor provenance
  only for inert SVG text after word/tag/line-break classification; HTML and
  Markdown paths must retain their existing sanitized projections. Layout,
  wrapping, accessible text and final SVG emission must use the same decoded
  text. Focused core/renderer tests and a fresh selected-RID rebuild remain
  required before P2-008 can close.
- No new Sandbox batch, guest scale experiment or physical-desktop input has
  been run. The live/DPI matrix and remaining Phase 1 qualification are unchanged.

- An additional P2-009 boundary regression reproduces root SVG title metadata
  `<b>literal</b> &quot;` becoming `literal &quot;`: the newly moved raw-label
  classifier is being applied to already literal XML title metadata. The
  focused native test fails 0/1 before the repair. Titles must retain their
  XML-decoded literal value; they are accessibility metadata, not label markup.

### Entity-patch integration and semantic-copy follow-up

- The previous goal turn rechecked the already known Sandbox/DPI constraints
  but made no implementation progress. This continuation resumes concrete
  source changes and executed regressions; no new UI batch is started.
- The immutable second patch is now tracked as
  `0002-flowchart-svg-text-entities.patch` (`653b355a...`), with the 21-file
  source manifest (`1fd408a7...`) and ordered patch-set hash `65c2ea65...`.
  It is included in package provenance and required package-entry checks.
  Independent preparation passes **24/24** ownership/tamper checks. The new
  focused CI source-test helper actually executes **19/19** named upstream
  regressions with Rust 1.96 and verifies the shared Cargo checkout unchanged.
  Its initial invalid Git ignored-mode flag and blank-line PowerShell parameter
  binding failures were corrected; they were harness failures, not renderer
  results. Owned temporary test directories are removed after verification.
- Product native tests pass **40/40**, zero ignored. The standard static-CRT
  rebuild completes for all three RIDs. Intermediate two-patch payloads are:

  | RID | Bytes | SHA-256 |
  | --- | ---: | --- |
  | win-x64 | 16,330,752 | `b0601751cd7d187acf749b2288b2818775c6fb57c2af5908961576337d02abce` |
  | win-x86 | 11,919,360 | `c5f2657890d9b7a78014a935ce0da5acd305e7639f6e5fad5782b7cc266cf373` |
  | win-arm64 | 13,234,688 | `acb3bb2f1324b279b67cc1fc9228bfbe335af3c14bfb49afd2c02d1abfab2c11` |

  PE machines independently read as `0x8664`, `0x014c`, and `0xaa64`.
  x86/ARM64 compilation does not establish runtime qualification. Six
  dead-code warnings from the source projection wrappers remain visible;
  the build is not described as warning-free.
- Matching x64 native-renderer coverage passes **3,034/3,034**, zero skipped,
  in `mermaid-entity-patch2-native-renderer-full.trx`. The separate Mermaid
  integration receipt `mermaid-entity-patch2-full-x64.trx` has **148 PASS /
  11 FAIL / 0 SKIP** from 159 tests. It was captured while the semantic-copy
  follow-up also had two deliberately failing diagnostic draft cases; the
  eleven must not be reported as eleven independently established product
  defects. The original receipt is retained, and those draft probes are being
  replaced by fixed expectation-based regressions, not waived requirements.
- Ordinary quote/numeric/Unicode entity labels and encoded literal formatting
  now pass their exact native text, semantic-name, source-span and geometry
  checks. Independent public-path checks still fail for three self-loop
  geometry cases, default shape-data entity text, and four P2-010 collision
  cases. A self-loop repair now resolves synthetic layout helpers through the
  original model edge owner and passes its focused upstream layout/SVG test;
  it is not yet in the two-patch DLLs above. Default shape-data labels take the
  Markdown path, unlike the explicit-string core fixture; that asymmetry is
  under repair without removing Markdown label support.
- P2-011's first identity-based repair fixes distinct equal flowchart labels,
  but deeper review exposes two additional copy boundaries: root SVG title
  metadata must not replace equal visible title/note/message occurrences, and
  shape-only accessibility aliases must not double-count text-owning semantic
  records. Sequence diagrams paint participant labels at both top and bottom;
  two actual occurrences must remain, not one globally deduplicated name or
  four accessibility-record copies. Follow-up assertions use text-command
  ownership, not guessed geometry or generic source-search ranges.
- PR #99 is still open at remote head `1c00c83d...`; its reported checks date
  from September 29, not this unpushed correctness candidate. The historical
  preview failure is a performance-readiness test timeout; old browser-parity
  audit failures are not reclassified as current correctness qualification.
  No commit, push, schedule, host display change, guest-scale experiment or
  physical input is performed. The live aggregate remains **37 PASS / 18 FAIL /
  7 UNVERIFIED**, and all three phases remain open.

### Final copy-order repair and remaining Markdown-label path

- Independent review catches that a semantic-table-first projection can put a
  node name before an earlier root-owned visible title. The final implementation
  instead walks actual text commands once and emits each explicit non-root
  semantic label at its first command. It needs only a per-semantic Boolean
  array; the earlier fallback-index and rendered-owner prescans are removed.
  The accessibility name resolver remains unchanged. Root's rerun passes all
  **23/23** focused cases with no skips in
  `mermaid-semantic-copy-order-final.trx`, including a deliberately reversed
  semantic-table/paint-command ordering oracle. Temporary diagnostic tests are
  absent; historical failing receipts remain retained.
- The SVG title metadata regression first fails **0/1**, then the repair
  preserves its literal XML-decoded text without applying raw-label formatting
  classification. The full product native suite now passes **41/41**, zero
  ignored. This last metadata repair has not yet been rebuilt into the three
  runtime DLLs listed above.
- Expanded default-Markdown shape-data characterization is retained in
  `mermaid-shape-data-and-self-loop-characterization.trx`: **156 PASS / 14 FAIL /
  0 SKIP**, 170 cases, without the earlier diagnostic draft tests. The failures
  are four literal-marker cases, three self-loop geometry cases, six additional
  shape-data text/formatting cases and the existing shape-data owner quote case.
  Unknown entities, one-level encoded ampersand/quote, double-encoded angle
  brackets and encoded line-break literals remain passing controls. Three
  further independent shape-data geometry oracles are added but not yet run.
- Default shape-data uses Markdown labels. Its source retains `&quot;` or
  numeric references, and both SVG measurement and emission use an intentionally
  smaller sanitizer-entity decoder. The fix must classify Markdown/line breaks
  before decoding exactly once, measure those same literal fragments, preserve
  authored adjacency across bold runs, and mark final XML text before native
  classification. A final-output-only decode is not sufficient. The self-loop
  repair was initially saved separately, then folded into the combined patch
  `0003-final-markdown-text-and-self-loop-labels.patch` with the Markdown-label
  implementation. The intermediate two-patch DLLs above do not contain it.
- P2-010 remains a real open defect. A proposed dynamically chosen delimiter is
  not accepted as a proof merely because it is absent from raw input: YAML
  Unicode escapes can synthesize it before label normalization, and formatting
  transformations can join literal fragments. No global identifier/URL escape,
  new unsupported-content rejection or probabilistic collision workaround is
  introduced to hide the defect.

### Combined Markdown/self-loop source checkpoint

- The preceding Sandbox-answer turn clarifies an already known limitation but
  makes no implementation progress. This continuation registers the combined
  third source patch (`514fe960...`) with a 24-file manifest (`74ab3ac5...`),
  ordered patch-set identity `928ac1c3...`, package entries and CI requirements.
  Source preparation passes **24/24** tamper/ownership checks against that exact
  snapshot; the shared Cargo checkout remains unchanged and owned test
  directories are removed. The 25-case source regression helper and standard
  all-RID rebuild are running; they are not yet final passing receipts.
- Patch 3 classifies Markdown before decoding each final text fragment once,
  measures and emits that same text, preserves authored adjacency across styled
  runs, and resolves synthetic self-loop layout edges through their original
  model owner. The root SVG title retains literal XML-decoded metadata. Review
  finds P2-012's additional encoded-whitespace boundary, which stays open with
  new public-pipeline regressions rather than being omitted from validation.
- No app/Sandbox launch, reconnect, display change, synthetic input, commit,
  push or scheduled run occurs. Physical testing remains paused, and the live
  aggregate and phase completion status are unchanged.

### Public-pipeline convergence and encoded-whitespace follow-up

- The three-patch x64 DLL is rebuilt and runs every managed Mermaid case:
  `mermaid-patch3-complete-characterization.trx` records **172 PASS / 6 FAIL /
  0 SKIP** out of 178. The ordinary entity, default shape-data text/formatting,
  authored style adjacency and all three self-loop geometry cases now pass.
  Four P2-010 literal-marker regressions and two newly executed P2-012 whitespace
  geometry regressions remain red, with original assertions unchanged.
- The same three-patch product native suite passes **41/41**, and its isolated
  registered upstream suite passes **25/25**, with no shared Cargo mutation or
  retained owned test directory. Its x86/ARM64 build continues from the same
  immutable `928ac1c3...` checkout, independent of later development edits.
- Reviewed patch 4 is `0004-final-markdown-entity-whitespace.patch`
  (`ec5b494b...`). Its cumulative 24-file manifest is `76d356bc...` and ordered
  patch-set identity is `b08b9717...`; package/CI entries require all four patches
  and 29 exact upstream tests. Preparation again passes **24/24**. The new
  source regression helper is running; matching all-RID publication and the
  unfiltered public-pipeline rerun are still required.
- A fresh core correctness slice passes **1,511/1,511**, zero skipped, in
  `core-correctness-patch3-continuation.trx`. As before, it explicitly excludes
  the three historical external-performance/report/release-workflow fixture
  classes; it is not described as a full-project test run.

### October 14 verified source and engine milestones

- The final four-patch Mermaid source identity is `b08b9717...`, with manifest
  SHA-256 `76d356bc...`. Preparation passes **24/24** checks, the isolated named
  upstream suite **29/29**, and product native coverage **41/41**, zero ignored.
  Standard x64/x86/ARM64 builds finish with exit zero. Their exact payload hashes
  and PE machine values are recorded in `MERMAN_PATCH_LEDGER.md`; x86/ARM64 are
  cross-built, not runtime-qualified. Eight Rust warnings remain visible.
- The matching native renderer receipt
  `mermaid-patch4-native-renderer-full.trx` passes **3,034/3,034** before the later
  disclosure parser repair. The complete Mermaid receipt
  `mermaid-patch4-with-yaml-markers.trx` records **174 PASS / 8 FAIL / 0 SKIP**,
  182 total; all eight remaining strict regressions are P2-010. P2-012's plain
  and bold encoded-space layout oracles now pass. Valid authored Unicode/YAML
  is not rejected or reclassified to hide the remaining collision.
- C01's actual pinned official-example suites, not just their harness tests,
  pass **1,322/1,322**: CommonMark 0.31.2 has 652 and GFM 0.29 has 670. The
  subsequent `--offline` rerun gives the same result, with zero exceptions or
  allowlisted examples. Reports are `conformance-all-current.json` and
  `conformance-all-current-offline.json` under `artifacts/phase2-20261014/`.
  Harness self-tests separately pass **9/9**. The HTML comparison oracle enables
  raw HTML solely for official reference comparison; the locked production
  HTML policy is unchanged. This is parsing/semantic integrity, not live layout.
- Additional complete x64 feature receipts pass SVG **725/725**, math **80/80**
  (including four exact unsupported-command fallbacks), HTML **10/10**, and
  TextMate **61/61**, without skipped tests. The sample Release/x64 command-only
  unpackaged build completes with zero warnings/errors; it is not launched.
- The new private 500-input engine runner's own outcome-accounting tests pass:
  attempted parses are distinct from successful parses, unclassified Mermaid
  outcomes cannot disappear from the aggregate, and file lengths are bounded
  before allocation. Its fresh result fixes the rank-184 exception without
  changing corpus bytes or parser safety ceilings. The run's `summary.json`
  SHA-256 is `6bfe3d3aa18d01283bab4f5177389ddb68fdfdb4cf7ce504579f85efc66640cb`;
  `cases.jsonl` is `6afa79febc05402dd984b770abc4bec10e4b98de983094b60bc6084a62c1afaa`.
- Independent review subsequently finds that informational-only diagnostics
  could leave the run aggregate clean even when a case says non-clean. Schema 3
  counts every diagnostic, including unrecognized future severity values; the
  new self-tests pass. The private utility also explicitly opts out of packing
  (`IsPackable=false`, independently queried). A fresh Release build succeeds
  with zero warnings/errors using the installed pinned-SDK `mt.exe` via the
  command-only `ManifestTool` property after the shell's missing-tool-path
  failure; SDK/package pins and machine settings are unchanged. The schema-3
  full rerun `headless-parse-615c56723fb1-details-fix-schema3-20261014T123131776`
  again parses **500/500** with zero exceptions/invariant failures, exactly two
  classified math diagnostics and 19 Mermaid scenes. Its cases file has the
  identical SHA-256 above; its summary is
  `7f065e45fc2e08d651b153b454b3d8a8f12d4285a242174574b43c81b4a18a82`.
  The nonzero diagnostic exit remains intentional; no full-render audit is
  inferred from the parse-only scope.
- The final source-acquisition harness passes **89/89** assertions independently
  on PowerShell 7.6.6 and Windows PowerShell 5.1.26100.9549. Its HTTP path now
  requests headers before bounded body streaming, so declared/chunked oversize
  bodies retain HTTP 200 and a deterministic over-budget category rather than
  being buffered and retried as network failures. Real `HttpClient` handler
  regressions verify one attempt, a maximum-plus-one detection bound, and
  cancellation before stream disposal. The original frozen manifest and
  already acquired source/status artifacts remain unchanged. Script SHA-256:
  `0a224afae82ba77880bde19a39681db0a5413a8b24cfe716b50dfce644ae3018`;
  tests: `bdacbc9e9c0eed84689d56b80bb1aabd81434ffac894774a90ba1e93ceb7fb07`.
- A Debug-only fixture defect is also reproduced: eight cases in relative-script,
  scene-residency and table-typography tests call `InlineContainerBox.Add` with
  the reserved block ID zero. The three fixture files now assign valid one-based
  identities, without removing the production assertion or changing semantic
  expectations. Their focused Debug rerun passes **68/68**. The next full Debug
  run records **3,041 PASS / 1 FAIL / 0 SKIP**, 3,042 total: the one new list-exit
  ownership assertion is being investigated against pinned Markdig behavior.
  This is not described as a passing full Debug suite. Overlapping build-copy
  retry warnings are retained; subsequent builds must be serialized by output
  directory rather than racing a live testhost.
- No physical input, Sandbox launch/reconnect, display-scale change, commit,
  push or scheduled run is performed. The same recorded guest remains a
  96-DPI/1.0 observation, not proof of 150%/200% coverage. The live aggregate
  remains **37 PASS / 18 FAIL / 7 UNVERIFIED**. All phases remain open; headless
  parsing does not substitute for actual layout, image, selection or DPI gates.

### October 14 continuation: disclosure regression closure and audit inputs

- The previous goal turn answered the Sandbox-capability question but did not
  advance implementation or qualify a new DPI route. This continuation resumes
  safe local correctness work; it does not infer permission for a new UI batch.
- P1-017's final pre-EOF-repair Debug/x64 receipt is
  `artifacts/phase2-20261014/github-details-final-debug-full-v3.trx`:
  **3,044 PASS / 0 FAIL / 0 SKIP**, including all ten then-current disclosure
  regressions. Receipt SHA-256 is
  `ae0d0a89a34383f2dee9af3c45b01b4bca92469e479cd42037ecf621eaedd8e9`.
  This replaces the earlier red list-indentation characterization for that
  repair, not evidence for later source changes or live interaction.
- P1-018 is separately reproduced in four LF/CRLF EOF cases and minimally fixed;
  `github-details-eof-fence-span-afterfix-focused.trx` passes **14/14**. P1-019
  is then independently demonstrated by four final-layout summary-image source
  assertions. Its explicit piecewise UTF-16 projection passes exact image/copy
  cases for same-line, multiline, repeated and multi-attribute tags, CRLF and
  block-quote prefixes. Helper tests also cover endpoint affinity, scoped mapper
  restoration, link URL/title spans, cancellation and Markdig's logical line
  count rather than its pooled backing-array capacity. The final two quoted
  multi-attribute fixtures initially found an expected-boundary mistake: the
  first following `>` was a Markdown quote prefix, not the tag's closing token.
  The independently authored final attribute now defines that expected end;
  no production change was made for those two fixture failures.
- Further P1-019 characterization reproduces CR-only and mixed LF/CR separator
  mapping failures before repair (`markdown-summary-cr-projection-red.trx`):
  authored `[24+1]` becomes `[25+0]`, and authored `[31+1]` reuses the prior
  `[24+1]`. Selecting the nearest CR or LF fixes both without changing rendering
  semantics; all **36** focused cases pass. The preceding 3,068-case Debug and
  Release suites were green, but the fresh 3,070-case Debug run records
  **3,069 PASS / 1 FAIL / 0 SKIP**. The retained failure is the existing full-SVG
  table relayout lease-reference assertion (expected 3, actual 4); ownership and
  retirement timing are being diagnosed, not waived or described as green.
  The matching full Release run passes **3,070/3,070**, zero skips
  (`markdown-details-projection-p1-019-release-final.trx`). Three serialized
  `--no-build` Debug reruns of the lease-count case each pass, so the failure is
  not stable in isolation; its cause is not inferred from successful reruns.
  The matching sample Release/x64 build also succeeds with zero warnings/errors;
  it is not launched and therefore supplies no new live or DPI evidence.
- Independent T1-007 review identifies that prepared image clones acquire a
  temporary bitmap lease and `CompleteImageRetirementAsync` deliberately
  force-yields before release. The failing SVG-table fixture counted those
  leases before its already-required context drain, unlike the neighboring
  table fixtures. Its exact equality is moved after that barrier; no count,
  tolerance, timeout, production path or cleanup is weakened. The table-image
  class passes **36/36**, then fresh serialized full suites pass
  **3,070/3,070 Debug and 3,070/3,070 Release**, zero skips. Final receipts:
  `artifacts/phase2-20261014/core-disclosure-source-retirement-final-debug.trx`
  (SHA-256 `fdd4d29254c8418b8308b09ae272bceaa32864ae07c366049a31de75f01dee5a`)
  and `core-disclosure-source-retirement-final-release.trx`
  (`735c93a727626f49165664a515b7dfa16d908c6de1fb623dee0aa29fc61c6db7`).
  Earlier red receipts remain historical evidence, not current failures or
  discarded observations. This closes the headless disclosure/source/retirement
  checkpoint, not Phase 1's outstanding live touch, navigation and DPI cases.
- The final disclosure/source candidate builds the private audit utility with
  zero warnings/errors and reruns all 500 frozen raw READMEs. Receipt directory:
  `headless-parse-615c56723fb1-final-disclosure-source-20261014T135156606`.
  All **500** parses succeed, with zero case exceptions/invariant failures,
  19 Mermaid scenes and the same two classified unsupported-math diagnostics.
  Its summary SHA-256 is
  `a9d2e578a5c07869fd94d4c426872397dc4da3ee301ba1470fd76228568179a3`;
  cases are `335827d2a0bd38db498cef247bb5ff808e0bcbc043901979c19c08cc7a9c6f24`.
  Compared with the preceding parse receipt, only ranks 184/208 change counts:
  four/one additional code blocks and source-map entries, respectively; all
  other recorded per-case values are identical. This remains parse/semantic
  evidence, not layout, image loading, selection, clipboard or DPI qualification.
- The unchanged Release/x64 SVG provider component is rerun separately with
  `--no-build`: `artifacts/phase2-20261014/svg-headless-component-qualification.trx`
  passes **725/725**, zero skips. Its test DLL SHA-256
  `61a2043836b3746165c89237d0424aea0d197c4bcdf208cddc7a8fa9e512fa14`,
  provider DLL `f45f4b35a331f1b04a7fea8c9e38c0851b7b2ecfc937d2ea3cb6c889236c02f2`
  and worker-identity manifest
  `989963215d509cffd1e1f2ff6b6ec5e3cc6e087406adf5f298e2447088b2583b`
  are unchanged before/after. The receipt SHA-256 is
  `8f908d780b428b8ff22c90a4d3f558e7e6525cd0289386e7946cb604df6ec0c2`.
  A read-only portable-PDB comparison matches all 43 available provider source
  documents, with zero mismatches; ten generator-produced virtual source
  documents are not materialized on disk. This is scoped provider evidence,
  not a fresh build of the concurrently changing document-renderer candidate,
  ARM64 runtime qualification, or actual-app image/DPI validation.
- A read-only retained-corpus inventory finds 94 same-byte manifest paths.
  Of these, 42 schema-2 captures match the frozen September 28 repository,
  commit, path and Git-blob identity, covering only **five unique ranks: 1, 59,
  60, 203 and 318**. Every declared README, rendered HTML and asset file in
  those 42 directories passes its recorded size/SHA-256 check; the README hash
  also matches the independently verified raw Git-blob object store. The
  historical schema-2 browser record remains input provenance, not a current
  browser-fidelity or performance requirement.
- The 51 matching schema-1 captures are not interchangeable with that current
  application contract: 49 lack their captured README files and 912 asset
  references are missing. Only ranks 1/26 have all declared bodies present in
  that older subset, and schema 1 is still rejected by the current resolver.
  Neither a `complete:true` manifest nor those old CI results proves a usable
  current offline corpus. All 500 separately acquired raw Markdown Git blobs
  remain available and verified; no source/asset is refetched or modified by
  this inventory. The other 495 ranks still require complete compatible input
  materialization before a full offline actual-app audit can be claimed.
- A distinct correctness-only automation path now builds around the existing
  production app traversal; historical Edge-relative gates remain unchanged.
  Its accounting and exact-text self-tests pass **16/16**. Independent review
  corrects three evidence defects before live use: the old normalized UIA text
  cannot be the expected value for an exact raw selection; observed unavailable
  images must be a failed resource check, not merely unevaluated; self-described
  non-rendered capture metadata cannot substitute for provenance. The new raw
  snapshot path preserves CRLF, tabs and repeated spaces, refuses truncation,
  and leaves the old parity text behavior unchanged. The no-UI preflight checks all
  500 raw source identities and finds **5 ready / 495 audit-input-missing /
  0 integrity failures**. Missing capture material is neither an upstream
  image failure nor a renderer pass. Receipt:
  `artifacts/correctness-preflight-20261014-r3/readme-correctness-input-preflight-20261014-133623-b7bc3b3115944a268375e735c604ac85.json`.
  One exact copy of each ready corpus is staged under
  `artifacts/readme-top500/correctness-five-verified-inputs-20261014T132647444-ccc984cdd1114dbfb65b33db7009430f`:
  53 files / 51,953,985 bytes, with original manifests, source, HTML and assets
  verified before/after copying; no capture metadata is fabricated. The
  source-acquisition metadata SHA-256 is
  `4a24e9bdc56cdfb4650ff304574b980fb506665f42446b2c57735a40e15746e6`.
  Actual full/partial selection, isolated clipboard copy, route-away/back,
  independent completeness/resource classification and live execution must
  still be evidenced. The probe explicitly leaves unevaluated dimensions open;
  neither preflight nor the successful 500-input parser run closes Phase 3.
  No new actual-app or Sandbox run has occurred.
- PR #99 is still open at remote head
  `1c00c83ddfd2e8b65e05cb57e359a3f092b37271`; its September 29 CI does not test
  this dirty local candidate. The preview lane's retained failure is the old
  benchmark-visibility readiness test timing out, and the old top-500 parity
  consolidation failed. These historical results are not relabeled as current
  correctness passes, nor are retired parity/performance gates reintroduced.

### October 14 continuation: origin-aware labels and response acquisition

- The Mermaid origin repair remains a private candidate, not a qualified
  runtime. The preliminary fifth-patch registration is superseded only after
  review, a final immutable source export, and matching native rebuilds. The
  currently executed public-pipeline native payload is still the four-patch
  x64 `f67db73dd73855e216ff62ffb02ce5af19d16abd4c802f3a38a24904c4752a16`.
- A fresh unfiltered public-engine baseline includes the added HTML-spelling,
  private-scalar and four-family marker regressions: **184 PASS / 14 FAIL /
  0 SKIP**, 198 total. Receipt
  `artifacts/phase2-20261014/mermaid-expanded-198-four-patch-baseline.trx`
  SHA-256 is `7a55b3b4d858c4a02dab8cb1c5b9e7750c34a7f308fbd269e7d2099a6802d589`.
  The test assembly is
  `5f86d3f3ddfeca44636944356ab448dee7c5ff0a8b3f75f9370c481e86ee4d73`.
  This is intentionally retained red evidence, not a pass with cases filtered
  away. The four newly proven non-flowchart collisions remain in P2-010 even
  if the flowchart-only candidate passes its own tests.
- P2-013 is separately reproduced for ordinary Markdown-string and explicit
  shape-data Markdown labels: unrelated authored `&nbsp;` source becomes NBSP.
  Its two public red cases assert exact paint and semantic copy, and compare
  geometry with a separately authored Mermaid ampersand escape. Six additional
  authored-PUA-frame guards pass against the unchanged baseline. Their first
  fixture version incorrectly concatenated wrapped paint without separators;
  the retained initial receipt is a fixture-oracle error, not product evidence.
  A contiguous `literal:` prefix removes that unrelated line-wrap assumption.
- Independent candidate review requires explicit ownership in measurement as
  well as emission. String resemblance cannot establish that a private token
  was generated. The non-Markdown icon predicate must run on final escaped
  text; Markdown classification must precede atom expansion; node/icon metric
  fallbacks must preserve the exact source mode. P2-I04/P2-I05 record these
  integration findings before repair. No public API or MMIR change is required.
  The propagation review includes the shared shape helper, its 16 callers,
  explicit icon/stadium recomputation and viewbox fallbacks. Eight new public
  shape guards compare entity text with independently authored Unicode in exact
  paint/copy, viewport and node bounds; all **8/8 pass on the four-patch
  baseline**, so a candidate that measures its internal atom cannot pass by
  changing an expected geometry. Receipt:
  `mermaid-shape-entity-geometry-four-patch-baseline.trx`, SHA-256
  `409f5e7dfb4848195a0942577d89c1a6fb28f11d17ac838d7983e2353c1e19a6`.
- T2-001 is fixed with ordinal path/status sorting. All 24 preparation and
  tampering checks pass on the preliminary five-patch source identity. Exact
  paths, change modes, file hashes, count and extra-file rejection remain
  enforced; repeat that qualification for the final source export.
- The separate server-response acquisition harness passes **69 assertions in
  PowerShell 7 and 69 in Windows PowerShell 5.1**, independently rerun by the
  root agent. Harness SHA-256 is
  `12679f90b6ac3ca0e9d10b66beaa063b630a1780abcdc1be1ba216a2fcb0e228`;
  tests are `89b78e3375ee4681c3080d19fb5d980ecd382b7060b65be9880be552fb86421f`.
  Receipt: `artifacts/phase2-20261014/correctness-html-acquisition-root-tests.json`.
  It validates the frozen source/URL identity, performs one bounded anonymous
  streaming request per case, rejects redirects, and retains immutable
  outcome/body hashes. It does not launch a browser or parse an article.
  Progress accounting no longer rehashes every prior body for each request;
  final integrity verification still reads every distinct captured body.
- The full 500-case HTTP acquisition finishes under
  `artifacts/readme-top500/github-commit-html-615c56723fb1-20261014`.
  Its `github-commit-page-response-only-v1` contract is deliberately separate
  from complete schema-2 browser captures. Failed HTTP requests and rejected
  redirects are retained outcomes, not renderer failures or completed content
  audits. `auditInputReady`, `captureReady` and `browserRendered` remain false;
  no article, assets, native layout, selection, clipboard or navigation gate is
  qualified by response acquisition alone. All 500 requests have terminal
  receipts: **486 captured / 13 HTTP 503 / 1 rejected HTTP 301 redirect**,
  zero local integrity failures. The redirect at rank 254 points from the
  frozen `OpenBB-finance/OpenBB` path to `openbq-org/OpenBB`; it is not followed
  or silently substituted. A separate post-run read-only check verifies the
  exact size/SHA-256 of all **486 captured bodies / 210,560,205 bytes**, bound
  to the same frozen repository/commit/path index. Receipt:
  `artifacts/phase2-20261014/github-commit-html-captured-subset-integrity-20261014.json`.
  This is a verified captured subset, not an all-500 body-integrity pass: the
  acquisition status correctly keeps `all500ResponsesCaptured=false` and
  `fullIntegrityVerification=not-run`. No failed case is retried or relabeled.
- Read-only Sandbox diagnostics leave the guest and host unchanged. The
  existing Settings window reports 96 DPI / scale 1.0; that is neither a
  150%/200% app result nor proof that guest scaling is impossible. Receipt:
  `artifacts/phase2-20261014/sandbox-existing-settings-readonly-20261014.json`.
  A new UI batch and guest-only scaling experiment remain pending; no display
  setting, guest restart, app launch or input is performed by these checks.
  The live aggregate and all phase statuses remain unchanged.

### October 14: final-source qualification and diagnostic source inventory

- Patch-set `873895d2868f1be9f40cc3f6838393e53e319664c57b8feb3a250b6e8d7ce208`
  passes preparation **24/24**, individually discovered source checks **67/67**,
  and product native tests **41/41**, with unchanged Cargo checkout ownership.
  These source-only results do not qualify the actual public render pipeline.
- The rebuilt x64 native payload is
  `f4276051eee94043adb79c52b9c843aedd1c31331c215e4cdb8b2de091091874`.
  The complete managed suite in **both Debug and Release is 176 PASS / 30 FAIL /
  0 SKIP**, 206 total. The candidate regresses default shape-data entity
  finalization: generated markers become visible, and all eight independent
  shape-geometry guards fail. The four known non-flowchart authored-marker
  collisions also remain. Candidate qualification is explicitly failed;
  P2-008/P2-009/P2-012 historical passing receipts are not promoted to this
  candidate. The next fix must cover the public facade path, not only private
  parser/layout helpers. Receipts:
  `artifacts/phase2-20261014/mermaid-final-origin-projection-release.trx`
  (`34d29480a8d39dbc3ef845ffb7a498457e3a9ad0a95e4a5c70e83f70e102b8f4`)
  and `mermaid-final-origin-projection-debug.trx`
  (`a7c12de571ca736b72d316b28dda3c4865bc404f99532eb2841f3bdfe93cb53d`).
- The offline Audit runner gains additive, source-generated heading/code/link/
  image projections and paired fixture tests. Release/x64 build has zero
  warnings/errors and its self-tests pass. Its fresh 500-source parse run is
  `artifacts/readme-top500/headless-parse-615c56723fb1-origin-candidate-20261014-inventory`:
  all 500 parse, zero case exceptions/invariant failures, 19 Mermaid scenes,
  and the same two math fallback diagnostics. **Despite the directory name,
  its recorded native hash is the older four-patch `f67db73d...`, not the
  new failing payload.** Binary receipt identity, not a directory label,
  determines candidate coverage. No layout, image loading, selection, copy,
  navigation or live-app pass is inferred.
- A pinned Marked 18.0.5 source-token inventory compares only fixture-tested
  common-subset projections against those exact raw Git blobs. Its first
  diagnostic run under
  `artifacts/readme-top500/source-inventory-615c56723fb1-20261014-first`
  reports 229 matching cases, 262 mismatches requiring classification and
  9 non-exact token-coverage cases. These are not 262 proven renderer bugs or
  full-document passes. Independent review finds that 10 of the matching
  cases have no common-subset elements at all; this vacuous-match accounting
  must be fixed before interpreting the match count. Raw receipts remain
  unchanged, including 132,118 explicit uncompared evidence entries. The
  Node tests initially passed 8/8; subsequent independent review required
  stronger identity, non-vacuous, table traversal and raw-HTML scope fixtures.
  All three correctness phases remain open.

- The source-inventory harness now passes **18/18** Node regressions. It rejects
  a different supplied source string even when native/inventory hashes match;
  pairs candidates by exact source span while retaining missing, unexpected
  and duplicate entries; counts table cell tokens in the traversal budget;
  and reports empty subsets as not compared. The paired Audit C# fixtures
  build Release/x64 with zero warnings/errors and their self-tests pass at
  `Program.cs` SHA-256
  `c0308c6e98b3c1f788323da6cab050c31b6ce77c13a83057fc14b6cfc6153a4d`
  and `AuditSelfTests.cs`
  `26ee262e540ea30cf7e2bc7f126f001152afc162ea0ec0460113846a22293545`.
  Matching source-span conventions for a closing code fence's line terminator
  are recorded explicitly, not silently normalized away.
- A permissive HTML-container tag scan was not sound: a JavaScript string
  `"</div>"` could falsely close an outer container and admit later headings.
  The harness now recognizes only standalone attribute-free HTML tag lines.
  Unrecognized raw HTML makes subsequent container scope explicitly partial;
  it never claims that arbitrary HTML was parsed. Script/style/textarea spoofed
  close-tag regressions pass. The prior broader comparison receipts are
  preserved but are not interpreted as robust HTML-scope qualification.
- The safe-scope fresh run
  `artifacts/readme-top500/source-inventory-615c56723fb1-failed-origin-candidate-20261014-safescope`
  reports **223 exact common-subset matches + 14 with recorded code-span
  conventions; 254 cases have no comparable elements; 9 have non-exact Marked
  raw-token coverage**. Exactly **5,221/5,221 admitted elements** compare with
  no differences; 362 cases have explicitly partial HTML-container scope.
  These counts describe narrow parser evidence, not completed document audits.
  The paired fresh native parse under
  `headless-parse-615c56723fb1-failed-origin-candidate-20261014-final-inventory`
  records the actual failing candidate hash `f4276051...`: 500 parsed, zero
  case failures, 19 Mermaid scenes and two math fallback diagnostics. This
  demonstrates why exception-free parsing and common-subset matching cannot
  overrule known incorrect Mermaid paint/copy or qualify native layout,
  image loading, selection, clipboard, navigation or the full Phase 3 audit.
- The product-native path now has a dedicated failing regression
  `pinned_shape_data_svg_text_is_final_for_product_configuration`, checking
  exact MMIR paint and semantic names for nine shapes and five entity/literal
  inputs. On the frozen failing candidate its first rectangle/emoji case
  reproduces `leftﬂ°°128512¶ßright` instead of `left😀right`.
  `artifacts/phase2-20261014/merman-product-svg-label-projection-red.log`
  retains that red result. A default HTML-label facade test passes, while the
  exact `htmlLabels=false` SVG configuration fails; this identifies the
  wrongly chosen legacy SVG-text projection. Additional public regressions
  reproduce the same defect on Markdown edges, self-loops and subgraphs;
  `mermaid-svg-markdown-label-owners-red.trx` is 5 PASS / 4 FAIL, including
  the existing shape-data failure. With the enclosing-quote characterization,
  the full public suite now has 211 cases.
  A node-only source fix cannot close this shared-projection defect.

- The corrected SVG-mode patch-set
  `c1ba92288d8076d3361b1bbf4a429a592d418200d31cac1dbd8e4eb912613407`
  independently passes **24/24 preparation/tampering checks** and **70/70
  individually discovered source regressions**. Complete observed output and
  final machine-readable results are preserved in
  `artifacts/phase2-20261014/merman-final2-source-gate.log`,
  `merman-final2-source-gate.json` and `merman-final2-preparation.json`.
  The source gate confirms its owned temporary tree was removed and the shared
  Cargo checkout was not written. These are source-level results only: the
  product-native and unfiltered managed suites must still run against the
  matching rebuilt DLL before the SVG-mode defect can be marked fixed.

- The matching final2 x64 DLL (`f3c8cded...`) passes the product-native **42/42**
  test suite, but the unfiltered public suite in both Debug and Release is
  **206 PASS / 5 FAIL / 0 SKIP, 211 total**. Flowchart shape-data, edge,
  self-loop and subgraph regressions now pass. A shared projection change
  nevertheless regresses swimlane Markdown edges, exposing an opaque internal
  atom instead of the final quote. The four non-flowchart authored-marker
  collisions remain. This candidate is **not qualified**. The failed receipts
  are `mermaid-final2-release.trx`, `mermaid-final2-debug.trx` and
  `merman-final2-public-qualification-failed.json` under the same artifact root.
  Expanding the owner cases and asserting both paint and semantic-copy output
  gives **11 PASS / 2 FAIL**: swimlane normal and self-loop Markdown edges fail;
  swimlane Markdown nodes, subgraphs and shape-data labels pass. The expanded
  full public suite now has **215** cases and still needs an unfiltered run
  after the next correction. No assertion was weakened or failing case skipped.

- The expanded unfiltered Release suite is now observed at **209 PASS / 6 FAIL /
  0 SKIP, 215 total**, in `mermaid-final2-expanded-release.trx`: exactly the
  two swimlane edge routes and the four known non-flowchart collisions fail.
  A new product-native test also reproduces the swimlane atom leak in exact
  MMIR paint (**0 PASS / 1 FAIL / 42 filtered**); its checked invocation records
  Cargo exit 101 in `merman-final2-product-swimlane-checked-red.log`. The first
  ad-hoc PowerShell invocation masked Cargo's exit with its final environment
  restoration assignment; its console still records the failed assertion and
  is retained separately, never interpreted as a passing native test. The
  checked retry makes command failure explicit. This does not change the
  frozen final2 build's earlier 42-test count or qualify the new 43-test suite.

- A response-only addendum retries only the original 13 HTTP-503 reference-page
  requests against the unchanged frozen manifest. Ranks **85, 221 and 271** now
  return integrity-verified HTTP 200 bodies (879,460 bytes in total); ten still
  return 503, and rank 254's separately rejected redirect is untouched. Root
  independently rechecks all 13 source blob identities/URLs and all three new
  object hashes/sizes. The original 486 bodies and addendum provide **489
  reference responses**, not 489 completed README audits. The immutable
  addendum remains on C: to avoid filling the work drive; exact paths, response
  hashes and non-claims are recorded in
  `artifacts/phase2-20261014/readme-http-addendum-subset-integrity.json`.
  All 500 raw Markdown blobs were already available; reference-page HTTP
  failures do not establish missing Markdown content or waive native audits.
- The two rank-340 math diagnostics are classified against the exact frozen
  `jingyaogong/minimind` README (blob `dadc82c4...`, SHA-256 `1af72ca8...`).
  Their complete formulas at lines 916 and 1191 use `\underbrace` and
  `\texttt`, commands explicitly absent from the frozen parser. Existing
  `PopularReadmeSyntaxTests` already require both complete formulas to retain
  exact atomic source fallback with `MATH100` and no substituted scene. This
  is an unsupported-command classification, not a supported-feature defect or
  a claim that the source is invalid in general TeX. The classification receipt
  `frozen-rank340-math-fallback-classification.json` records the evidence and
  that no new live/test run was performed. No command support or acceptance
  gate was changed; Phase 3 native layout/selection/copy remains unevaluated.

- The corrected Swimlane source is registered as patch-set `15775d7c...` with
  63 raw-hash-verified changed paths. Preparation passes **24/24**, the individually
  discovered source gate **77/77** (all prior 70 identities retained), private
  renderer library **1,203/1,203**, flowchart SVG **83/83**, and product native
  **43/43**. All three static-CRT RID builds finish successfully; x86/ARM64 are
  cross-built and PE-verified only. The matching x64 `c0c7bbed...` DLL is copied
  into both executed public configurations. All **13/13** Flowchart/Swimlane
  exact paint/semantic-copy routes pass in both; the full original 215-case
  suites are **211 PASS / 4 FAIL**, closing bounded P2-014 while preserving the
  four non-flowchart P2-010 family failures. Exact payload sizes, hashes, build
  logs, TRX digests and failed identities are recorded in
  `artifacts/phase2-20261014/merman-swimlane-product-qualification.json`.
- Matching full core renderer suites pass **3,070/3,070** in Debug and Release,
  without skips. Moving the new Debug build outside the repository first exposes
  **134 source-discovery failures** (120 missing files, 14 missing directories).
  The red TRX remains unchanged. Explicit C:-build-root junctions to the same
  `MarkdownRenderer` and `.github` worktree directories make those exact source
  probes discoverable; the same compiled binary then passes all cases, with no
  test/filter/assertion changes. The sample builds Release/x64 with **zero
  warnings/errors**, and its copied native hash is `c0c7bbed...`. No sample was
  launched. `swimlane-current-core-sample-and-mixed-evidence.json` records the
  paths, hashes, setup failure and successful rerun separately.
- Twelve additional public P2-010 regressions mix authored marker spelling with
  genuine entities, repeated entities and emoji/combining adjacency. Their
  focused result is **4 PASS / 16 FAIL, 20 total**; the expanded unfiltered
  Release suite is **211 PASS / 16 FAIL / 0 SKIP, 227 total**, in
  `mermaid-cross-family-expanded-beforefix-release.trx`. The additional failures
  characterize the same four family defects, not new supported features or
  permission to weaken output. The private follow-up carries parser-known byte
  origins through final owner-indexed render sidecars for Sequence, Mindmap,
  State and Class; it is not yet a registered or runtime-qualified candidate.
- The headless 500 runner now declares the existing pinned Windows SDK build
  tools directly. A clean normal-shell C:-isolated build reproduced `mt.exe`
  missing (MSB3073/9009); the direct private build dependency repairs it without
  changing PATH or SDK versions. The corrected Release/x64 build has zero
  warnings/errors and self-tests pass. A fresh run of all frozen **500/500**
  inputs uses the actual `c0c7bbed...` native payload: zero case/invariant/source
  mismatches or disposal exceptions, **19/19** Mermaid scenes, and only rank
  340's two classified unsupported-command math fallbacks. Root independently
  rehashes all **18,023,117 raw bytes**, Git blob SHA-1s/SHA-256s and all ordered
  rank/repository/commit/path identities. The runner deliberately exits 1 for
  its diagnostics, not a clean full-audit pass. Large receipts remain on C:;
  `readme500-swimlane-current-parse-verification.json` records their exact paths
  and hashes. Layout, images, selection/copy and navigation are **not** evaluated
  by this runner; every correctness phase remains open.

- The non-flowchart follow-up now also has 15 public Sequence route cases for
  participant/actor labels, plain/escaped YAML aliases, box/title/note labels,
  loop/opt/alt/par/critical/break labels, wrapping and message ownership after
  activation. All 15 reproduce authored-marker corruption on the still-shipped
  `c0c7bbed...` payload. The expanded Release suite contains **242 cases** and
  retains every prior 227 test identity. It initially reports **210 PASS / 32
  FAIL**, including a separate provenance failure: six patched dependencies had
  reverted to upstream Git source entries in `native/Cargo.lock`. Removing only
  those six source entries reproduces the already-audited lock hash
  `5c3f2838...` exactly, with no package/version/hash-expectation changes. The
  identical compiled test binary then reports **211 PASS / 31 FAIL**, with the
  provenance test green and all content failures retained. Full TRX identities
  and hashes are in `sequence-visible-routes-beforefix-evidence.json`; the
  console was truncated and is not treated as a complete receipt. The private
  replacement is still under implementation and is not runtime-qualified.

- The private non-flowchart parser/core integration passes an unfiltered
  **1,568/1,568** `merman-core --lib --locked` suite, with no skipped cases.
  Exact lexer/parser payload spans now survive Sequence whitespace/wrap
  directives, final message indexes, actor redefinitions, YAML alias decoding,
  boxes and titles. State captures description parts and both note consumers;
  Class strips its relation delimiter from the source projection just as its
  existing model does. The actual Class span defect was fixed, not hidden by an
  expected-value change. Earlier compile/test failures remain on C: and the
  source-layer oracle corrections are explicit in
  `nonflow-origin-core-verification.json`. Layout, SVG emission, the public
  managed pipeline, selection/copy and live DPI checks are still separate open
  gates; this private core receipt does not replace the registered native
  payload or close P2-010.

- Public final-text boundary coverage now retains all prior cases and adds an
  encoded-vs-raw formatting control plus independently scalar-escaped label
  references for each non-flowchart family. The unchanged registered native
  payload reports **212 PASS / 36 FAIL / 0 SKIP, 248 total**. The raw Sequence
  formatting control passes; the encoded literal wrapper is incorrectly
  removed. All four independent-reference cases fail at their text assertion
  before geometry is compared, so these failures do not establish a geometry
  difference. The renderer's legacy XML escape helper also decodes entities;
  the replacement must finalize once and use raw escaping plus the native
  final-text marker. Exact source/TRX/payload digests are recorded in
  `nonflow-final-text-boundary-beforefix-evidence.json`.
- A separate public Class regression reproduces multiword caption copy as one
  newline per SVG text fragment: both English and Arabic/emoji cases fail on
  the unchanged payload. The new native fix joins path/caption ownership using
  explicit canonical relationship metadata, not label matching or geometry,
  while charging one relationship against the edge budget. The strict public
  origin tests now require the caption's corrected **Edge** owner instead of
  its previous diagram-root owner; their exact paint/copy assertions remain.
  The complete product-native suite passes **47/47**, including four new
  ownership/order/budget/malformed-identity fixtures, with no skipped tests;
  `class-canonical-edge-native-verification.json` records the exact source/log
  digests and unchanged prepared dependency identity. Replacement-payload and
  public-pipeline qualification are still pending. This is an additional
  confirmed copy defect, not a completed phase or UI result.

- The combined native scene boundary fixes pass the unfiltered **51/51** suite,
  with no skipped cases. It covers the Class path/caption identity, real
  attribute/CSS/inline `display:none` pruning (including hidden tspans, links
  and node names), visible override controls and literal root description
  metadata. An initial **49 PASS / 1 FAIL** run is retained: the new cascade
  control exposed a missing `display` entry in the CSS declaration allowlist;
  that production omission is corrected without changing the control. The
  public baseline is now **211 PASS / 43 FAIL / 0 SKIP, 254 total**, retaining
  all prior 248 identities and adding exact caption, hidden-menu and metadata
  regressions. The unchanged registered payload still fails them; new native
  unit tests are not substituted for rebuilt public-pipeline validation.
  `mermaid-native-boundary-verification.json` binds the exact source and log
  digests, before-fix public TRX and earlier failed run. No UI or display
  setting change, commit, push, or scheduled run occurs.

- A later independent scene review adds three controls: root-only metadata
  ownership, hidden-root suppression before background/metadata publication,
  and visible-label limits that exclude hidden tspans. The full native suite
  passes **54/54**, and a persisted full private-core rerun passes **1,570/1,570**.
  The renderer checkpoint passes **1,213/1,213** default library tests,
  **1,223/1,223** with math, and **86/86** Flowchart SVG controls. Review then
  catches a real remaining gap: Sequence accessibility slots 4/5 are captured
  by the parser but bypassed by the SVG root writer, which still re-decodes
  authored marker-shaped text. That repair is in progress; these component
  passes do not qualify the unrebuilt public payload. A separate Mindmap
  command executes zero tests without `layout-cytoscape`; it is explicitly
  rejected as coverage, and the source gate is being corrected to require the
  real feature-enabled tests. Exact logs and limitations are recorded in
  `mermaid-boundary-54-and-core-1570-verification.json`.

### October 14: four-family public pipeline and deployment verification

- The registered patch-set `f32de317...` fixes the known Sequence, State,
  Mindmap and Class origin/final-text failures through the actual native
  SVG-to-MMIR path. State/Class paths and captions share canonical ownership;
  hidden Sequence menus stay out of paint/copy, and literal root accessibility
  descriptions survive. Mindmap's resvg-safe fallback now carries final-text
  ownership, decodes ordinary HTML entities once, preserves literal backslash-n
  and retains unmarked legacy behavior. No public API, ABI, MMIR, dependency
  version or license boundary changes.
- Matching x64 public Mermaid tests pass **259/259 in Debug and Release**,
  retaining every previous 254 identity and adding five cases. Full managed
  integration passes **3,070/3,070 in each configuration**, with zero skips.
  The first broader run's sole missing-workflow-context failure is retained;
  its correction supplies the actual source file without changing assertions.
  Product native tests pass **54/54**, source preparation **49/49**. All three
  static-CRT RID builds complete and their PE machines match; only x64 is
  executed. The final isolated source gate passes **117/117, zero failures or
  ignored cases**, preserving all previous 77 package/test identities in order.
  Its shared checkout is unchanged and its verified GUID-owned temporary tree
  is removed; root independently verifies the log/result hashes and absence.
- The current Release/x64 sample builds with its WinUI analyzer and zero
  warnings/errors; it is not launched. The published NativeAOT Mermaid smoke
  has no CLR header/managed metadata and renders eight vector commands. Its
  old success condition also accepted an unavailable engine or failed render;
  the corrected deployment gate requires a successful nonempty scene without
  fallback/error diagnostics. An identical exe-only control without the native
  DLL correctly returns 1; the unmodified valid publish returns 0.
- A fresh matching headless audit parses **500/500** frozen READMEs with zero
  source/invariant/disposal failures and **19/19** Mermaid scenes. Independent
  verification rehashes all **18,023,117** bytes and ordered repository/blob
  identities. The cases file is byte-identical to the preceding receipt. The
  same two rank-340 unsupported-math diagnostics remain, so the runner exits 1.
  This does **not** evaluate layout, image loading, selection, clipboard or
  navigation, and is not the full 500-page audit.
- Exact source, RID, TRX and log hashes and the retained failed candidates are
  in `artifacts/phase2-20261014/merman-four-family-product-verification.json`.
  Fresh live validation, actual high DPI and all three phase completions remain
  open; headless or AOT deployment checks do not substitute for them.

### October 14: approved guest-only DPI experiment

- The user authorizes display changes and guest-session restart only inside
  Windows Sandbox. The existing guest's `ms-settings:display` deep link still
  opens System without a Display/scaling control; its screenshot is inspected.
- A reversible trial sets only guest HKCU `LogPixels` / `Win8DpiScaling` to
  **144/1**, restarts the guest, and measures a fresh per-monitor-aware sample
  window: **96 DPI**, not 144. Repeating with **192/1** and another guest restart
  again measures **96 DPI**, not 192. Registry values survive both restarts,
  but registry readback is not runtime DPI evidence. No XamlRoot scale or touch
  pass is inferred from these failed necessary Windows-DPI conditions.
- The original guest settings are restored exactly (`LogPixels` absent,
  `Win8DpiScaling=0`); a live window and WinApp inspection agree on **96 DPI /
  1.0** afterwards. The guest connection is repaired, the final screenshot is
  visually reviewed, and the UI workflow is yielded. **No host display or
  registry setting is changed.** The user's earlier physical 150% setting is
  not claimed restored or rechecked.
- This installed Sandbox remote-session path does not qualify true 150%/200%
  through the tested guest settings; it is not proof that every Sandbox/VM setup
  lacks high-DPI support. Actual high-DPI validation and the existing 150%
  handle/accessibility failure remain open. Existing guest binaries are used
  only for the Windows DPI-delivery experiment, not current-source correctness.
  Exact identities, observations, restoration and retained evidence hashes are
  in `artifacts/phase1-20261014/sandbox-dpi-experiment.json`.
- A subsequent read-only check of installed `wsb 0.8.107.0` / `winapp 0.7.0`
  command help finds no guest DPI option. Microsoft's [Sandbox configuration](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file)
  likewise exposes no display-scale field. Capture resizing/remote-view zoom
  cannot qualify the actual per-monitor [window DPI](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforwindow).
  A separately provisioned Windows VM with supported guest scaling is a possible
  unverified alternative, not a capability claimed for the current Sandbox.
- Read-only review of the retained physical 150% run confirms that long press
  selected exactly `family` before the size gate failed: one handle's UIA bounds
  imply **65 × 66 physical pixels**, versus the required 66 × 66 at 144 DPI.
  The earlier `RangeFromPoint` error was a separate non-blocking diagnostic.
  The current control requests 44 × 44 DIPs, but the failed run did not retain
  raw handle rectangles or app-side actual layout sizes. It therefore cannot
  distinguish layout/clipping from physical-bound projection/rounding. The
  next high-DPI diagnostic must preserve the 44-DIP gate and sample settled
  UIA bounds together with actual Button size, rasterization scale and clipping;
  no speculative enlargement or tolerance change is made.

### October 14: code-copy and terminal-content repair verified

- Two reproduced C08 defects are fixed: rich-copy code whitespace/style loss
  (P1-020) and removal of authored final code lines/hard breaks (P1-021). The
  repair uses committed semantic content and fixed HTML tags, without source
  reparsing, new public APIs, dependencies, or relaxed link/security policy.
- Final native integration suites pass **3,094/3,094 in both Debug and Release,
  zero failures or skips**. Independent TRX-name comparison confirms that all
  **3,070** prior identities remain in both configurations; the **24** added
  cases cover exact content, nested linked code, partial ranges, chunked code,
  terminal/decorative cases and LF/CRLF source mapping. The four old assertions
  requiring authored HTML-break deletion are replaced by stricter exact-content
  and one-unit-span oracles, with their names, fixtures, geometry and source
  checks retained. All original red and intermediate receipts remain available.
- The matching Release/x64 sample rebuilds with **0 warnings and 0 errors**.
  Its copied renderer DLL matches the Release-tested DLL exactly. Its native
  Mermaid payload is unchanged at SHA-256
  `384aa86f0e8bb8a2db74d054b03e547397670d21b760d199d775b041d2ad0c47`.
  The sample is **not launched**. Independent final source review finds no
  concrete correctness/security defect; this does not substitute for live proof.
- Exact source, DLL, TRX, log hashes and retained failing-oracle explanations are
  in [code-copy-correctness.json](../../artifacts/phase1-20261014/code-copy-correctness.json).
  This supersedes the earlier 3,070-case count for current managed C08 source,
  not the historical receipts or the unchanged native-provider verification.
  Fresh Sandbox reading/selection/clipboard and actual 150%/200% handle/lifecycle
  qualification remain open. No phase is closed and no 500-document live audit,
  WinUI NativeAOT deployment, commit, push or scheduled run is inferred.
- Separate current-source offline receipts pass **1,322/1,322** pinned
  CommonMark/GFM examples without exceptions/waivers, the conformance harness
  **9/9**, Math **80/80**, HTML **10/10**, TextMate **61/61**, and isolated SVG
  provider **725/725**, all without failures/skips. These are component smoke
  and reference/semantic checks, not live composition proof. A separately labeled
  pure-logic correctness slice passes **1,511/1,511, zero failures or skips**;
  its only filter excludes `PerformanceExternalGateFixtureTests`, the historical
  performance-benchmark validator family (457 statically inventoried cases).
  The original full run was progressing, not demonstrably hung; each external
  fixture spawns PowerShell and would dominate the run outside the revised
  correctness goal. It is gracefully canceled after the correctness slice passes,
  its exact owned process tree is verified exited, and its log/partial fixture
  evidence is retained as **CANCELED_NOT_QUALIFIED**. No assertion is changed or
  waived and no full pure-logic or historical performance-gate pass is claimed.
  Evidence and execution boundaries are in
  [parsing-and-provider-smoke.json](../../artifacts/phase1-20261014/parsing-and-provider-smoke.json).

### October 14 continuation: diagnostic classification before repair

P2-018: the two retained rank-340 MATH100 bodies use `underbrace` and `texttt`,
which are absent from the pinned CSharpMath command implementation. Their exact
UTF-16 spans and authored fallback are preserved; these are unsupported-command
cases, not evidence that the source is malformed. The old default diagnostic
"The formula is not valid TeX." was too categorical. The repaired default says
"The formula is invalid or uses TeX syntax this renderer does not support."
MATH100, the public localization key, result kind, spans, command coverage, and
exact authored fallback are unchanged. The throwing-provider assertion is
retained with the corrected wording. The full current-source Release Math suite
passes **80/80, zero failed or skipped**. Exact source/TRX hashes and the two
frozen authored formulas are recorded in
[math-diagnostic-wording.json](../../artifacts/phase2-20261014/math-diagnostic-wording.json).
This does not add new TeX support, waive either corpus diagnostic, or qualify live
Math composition or a complete README audit.

### October 14 continuation: table, code and authored-source repairs verified

- P1-022 through P1-026 now have passing managed/native-layout regressions:
  structured rich table copy, exact long-code and HTML-pre source ranges,
  Unicode-safe line-preserving chunks, correct NoWrap inter-chunk rows, and
  preservation of authored private-use characters during GFM preparation.
  Disclosure summaries rebuild private projections from verified original
  ranges, and extension-owned code uses the owning/shared layout builder rather
  than a second proportional mapping implementation. Normalized LF retains the
  complete authored CRLF span at both the summary and final-layout stages.
- Both complete integration suites pass **3,162/3,162 in Debug and Release**,
  with zero failed or skipped cases. Exact `(testId,testName)` multiset comparison
  retains every **3,094** previous case and adds **68**, including duplicate
  display names and repeated theory IDs. The first full run's **3,160 pass / 2
  fail** receipt is retained: one test invoked an obsolete private constructor,
  and another expected registration in the deleted reduced code builder. Their
  identities and other assertions remain; the repairs strengthen recorded-array
  storage/derived-state and shared-builder/attach-before-adopt checks.
- The separately labeled correctness slice passes **1,532/1,532** after the
  final parser and harness changes. Its only filter remains the unchanged
  `PerformanceExternalGateFixtureTests` family; no unfiltered suite pass is
  claimed. Current pinned official conformance passes **1,322/1,322 without
  exceptions or waivers**. The reference HTML oracle's existing raw-HTML
  exception is confined to comparing official examples; production HTML safety
  policy is unchanged and separately tested. Math **80/80**, HTML **10/10**,
  TextMate **61/61**, and the conformance harness **9/9** also pass.
- The Release/x64 sample builds with **0 warnings and 0 errors**. Its renderer,
  Core and GitHub assemblies match the Release-tested copies. Independent final
  source review finds no concrete remaining correctness/security issue in this
  repair set. No whole-app NativeAOT or actual high-DPI pass is inferred.
- A new run of all **500 frozen source objects** through JitHub's engine profile
  records **500 parses, zero case/source/disposal exceptions, 19 Mermaid scenes,
  and exactly two MATH100 unsupported-command fallbacks** at the unchanged
  rank-340 spans. Exit **1** intentionally retains those diagnostics. This is
  still parsing/semantic-snapshot-only, not image loading, layout, rendering,
  selection, clipboard or a completed 500-page audit.
- The user explicitly authorizes a fresh Sandbox reading/selection/clipboard
  batch after the full suites pass. The exact old DPI-experiment sample is
  normally closed after verifying guest machine, PID, HWND and executable hash;
  no force termination or host action is used. The runner's optional absolute
  `BuildArtifactsPath` now routes both project-mode launch and host payload
  hashing to the same C: build. Offline helper checks pass under PowerShell 7
  and 5.1, including paths with spaces, unchanged default routing and relative
  path rejection. **The fresh UI batch is in progress, not a pass.**
- The first live attempt exposes a test-runner scope defect before current
  candidate qualification: assigning an empty value to
  `$script:BuildArtifactsPath` also clears the script's bound parameter. Host and
  guest hashes therefore match each other at the older default E: output, not
  the intended tested C: output. Root detects this in `run.json`, stops only the
  verified owned driver, yields its UI workflow and normally closes its exact
  sample window. No result or screenshot from that run qualifies the candidate.
  The initial helper transfer was slow but completed before any intervention;
  it is not recorded as a timeout. The first bounded repair adds a regression
  executing actual top-level parameter initialization, rather than trusting the
  earlier extracted-helper checks. Cancellation and retained evidence are in
  [sandbox-fresh-batch-routing-canceled.json](../../artifacts/phase1-20261014/sandbox-fresh-batch-routing-canceled.json).
- The script-scope regression reproduces the old failure as an actual `.ps1`
  file; corrected initialization then passes in PS7 and PS5.1. The replacement
  run records the intended C: root and the exact Release-tested renderer hash.
  A separate guest-side check also matches all eight renderer/feature-pack
  assemblies against the C: sample payload. This is **candidate identity only**;
  the UI assertions and visual review are still in progress, not a pass. See
  [sandbox-fresh-batch-candidate-identity.json](../../artifacts/phase1-20261014/sandbox-fresh-batch-candidate-identity.json).
- Exact source, DLL, TRX, log, test-retention and 500-input receipt hashes are in
  [table-code-source-correctness.json](../../artifacts/phase1-20261014/table-code-source-correctness.json).
  Original failures and draft diagnostics are preserved, including the explicitly
  mixed-binary structural probe that is not counted as a correctness pass.
  Actual 150%/200%, live lifecycle/clipboard and complete feature/corpus gates
  remain open. No phase is closed; no commit, push or scheduled run is made.

### Fresh Sandbox r2 and its next repair (October 14, continued UTC October 15)

R3 live findings while the final bounded repair run is still active:

- P1-030 remains open. Commands 1276–1285 confirm that contact 81 actually
  reaches the end handle (one press, 19 moves, one ordinary release, no native
  cancel/capture loss, final point within 0.02 DIP of the independent target),
  but `touchtarget` collapses to an empty selection and the handles disappear.
  This is not synthetic-input nondelivery. Source tracing is continuing without
  changing the active candidate.
- R3 commands 1366/1367 still omit the final SVG alt from selected text. The
  block-owned image's canonical after position `(block,0,1)` converts to
  `TextStart`, whereas `PositionFromSpanEnd` produces that same position for
  `TextEnd`. Block embeds/hosted elements share this inconsistent inverse
  fallback. A public-engine semantic/source-copy regression is required before
  repairing it; the existing endpoint/pixel regressions do not cover this path.
- A neighboring image rectangle is not proof of image selection: the current
  `GetDocumentRects` scan admits a nonempty image span starting exactly at the
  range's exclusive end. Preserve an independent preceding-text-only geometry
  oracle when repairing atomic boundary mapping. T1-014 separately records the
  current SVG gesture's overly broad target geometry.
- Both final-Math captures now show readable, content-bounded translucent
  selection rather than the old opaque viewport-wide band. Commands 1458 and
  1513 include the complete independently authored dollar-delimited source in
  the clipboard for owned and ancestor modes. This is bounded 100% evidence;
  the overall batch, remaining Mermaid states and actual high DPI are not yet
  qualified.

The previously in-progress r2 batch is terminal: **39 pass, 14 fail, 10
unverified**, with all 36 delivered PNGs inspected. It is not qualified. The
compact result and screenshot-hash record is
[sandbox-fresh-batch-r2-results.json](../../artifacts/phase1-20261014/sandbox-fresh-batch-r2-results.json).
Fourteen failed assertions do not establish fourteen product defects: they
include empty-array and enabled-state harness failures, independently authored
native-editor line-ending expectations, and an unqualified child startup.
Actual math/image source omission, vector selection readability and relayout
handle loss remain distinct production repairs.

The next candidate builds Release/x64 with zero warnings/errors in a new C:
artifacts tree, without replacing the still-running old guest payload. The
combined focused native repair set passes **66/66, zero skipped**, including
caption, vector paint, math source mapping, final-image endpoint and native
cancellation cases. The full native Release run has already exposed two
additional assertions requiring diagnosis; its result is not a pass. Debug,
the pure-logic correctness slice, final helper checks and the rebuilt live
repair initially needed terminal receipts. The completed code reruns now pass
**3,184/3,184 in both Debug and Release**, with every one of the prior 3,162
stable test-ID/name pairs retained, plus **1,541/1,541** logic correctness cases.
Math **80**, HTML **10**, TextMate **61**, conformance-harness **9**, and pinned
official semantic conformance **1,322** cases pass without skips or conformance
exceptions. All eight sample renderer/feature assemblies match the exact
Release-tested candidate. No whole-app AOT or rebuilt live pass is inferred.
The six missing source-fixture-path failures in the first logic attempt are
retained; an artifact-only C: junction fixes setup, and the unchanged test DLL
then passes. The two original native-oracle failures are likewise retained;
their corrected tests verify the new background/foreground pipeline, including
an additional exact High Contrast block-fallback case, rather than weakening
capacity, source, cache, cleanup or geometry expectations.

Final host/guest routing and lifecycle-launcher helpers pass offline checks in
PowerShell 7 and Windows PowerShell 5.1. C10's new routed-input correlation is
implemented as real host functions, not accidentally calling functions that
exist only inside the guest-script here-string. It preserves both observer
snapshots and raw/effective release/cancellation evidence on failure. These are
offline checks, not live gesture qualification.

The exact old guest PID/HWND/start-time/path/renderer hash is rechecked before
normal WindowPattern close. That process exits normally; there is no force
termination. The final bounded r3 repair then starts in the same Sandbox epoch
from the new tested C: payload. Code receipts, hashes, retained failures and the
live run path are recorded in
[sandbox-r3-code-verification.json](../../artifacts/phase1-20261014/sandbox-r3-code-verification.json).

The WinUI UI-testing workflow permitted one remaining repair/rerun in this fresh
batch; r3 uses that final cycle, not a reset of the retry budget. Guest-only 100%
evidence does not qualify actual 150%/200% display scaling; no host display
setting was changed or restored during this continuation. All three phases
remain open.

### R3 terminal result and post-run repairs (UTC October 15)

The approved fresh Sandbox batch is now terminal: **47 PASS / 7 FAIL / 8
UNVERIFIED**, exit **1**, with **all 51 delivered screenshots inspected**.
The compact result, per-capture hashes/reviews, exact raw-report hashes and
crash evidence are preserved in
[portable broad R3 receipt](evidence/2026-10-17/sandbox-broad-r3-results.json).
This supersedes the explicitly labeled in-progress visual record. It is not a
qualified batch, a phase completion, or a count of seven independent bugs.

Meaningful passing outcomes include all 22 sample navigation/smoke pages,
deterministic local SVGs and narrow image-table containment, visible safe HTML
caption/inert script source, raised/lowered script geometry, mouse/keyboard
cross-list/table selection, and exact full/partial Markdown clipboard checks.
All 12 adjacent-keycap touch combinations pass in both viewport modes. Arabic
logical selection, decomposed combining text and whole emoji also pass. The
near-origin link sequence is now within its original 1.5-second envelope
(1,379.3346 ms at 10 DIPs), and native Copy Link is enabled and copies the exact
independent URL without activation. Final Math and Mermaid remain readable
while selected and copy their complete independently authored source in both
viewport modes. Light/Dark and the sample's forced High Contrast/text-scale
state pass, but this is **actual 100% only**, not system High Contrast or
150%/200% DPI proof.

The failed assertions split into distinct categories:

- **P1-032, critical native crash:** after the valid narrow selection state,
  wide restoration kills the verified sample process. Guest Application Error
  1000 records PID 6676, exact deployed path and start time, `0xc0000374`, and
  `ntdll.dll+0x1181f5`. The matching StackHash WER report is retained, but its
  referenced temporary `.mdmp` had already been discarded. The copied WER is
  exactly 98,264 bytes, SHA-256
  `F21CD6A25E38697525C211A719B3D7BCA0E3D698565690395623414BE4E1383B`.
  A subsequent source-replacement failure and fatal harness entry follow the
  same dead PID/HWND and do not independently reproduce more crashes. No
  corrupting module/call stack is known, and no speculative production fix is
  made. Read-only exact-deployment/parent-process inventory finds no remaining
  matching process; this is not a universal orphan-process audit.
- **C10 handle press/drag:** the owned end-handle is partly below the short
  text canvas. A real contact reaches it, but the root outside-click hook later
  sees its point outside the canvas and clears the range. Merely marking the
  routed event handled cannot suppress a handler registered with
  `handledEventsToo`. The new repair exempts ancestry of only this renderer's
  two handle buttons, after preserving other-renderer clearing and the existing
  code-action exception. Ordinary overlay presses remain dismissible. A valid
  before-fix source-order failure and passing post-fix contract are retained;
  this contract is not live routed-pointer proof. The old swipe destination is
  also in blank document space, so future validation must separately prove
  press/blank-space non-collapse and real text-range extension, rather than
  infer extension from arbitrary viewport coordinates.
- **Final SVG selection:** canonical block-image after-endpoints map back to
  semantic start; block embeds/hosted elements share that inconsistent inverse.
  A separate half-open geometry bug returns the next image's rectangle even
  when a text-only selection ends exactly at its start. Three exact public
  engine/layout/source-copy regressions fail first, then pass after correcting
  those two paths. The final focused set passes **54/54**, including unchanged
  vector, inline-image and semantic-index compatibility tests. One intermediate
  overbroad vector-geometry change is rejected by an existing inclusive-vector
  oracle and removed, not waived. T1-014 remains a separate harness defect:
  broad full-width image-peer bounds are not the fixed 96×96 artwork bounds.
- **Cancellation delivery:** the injected canceled-up sequence reaches WinUI as
  ordinary Release with `IsCanceled=false`. The failed cancellation oracle is
  retained; it does not demonstrate the renderer mishandling a genuinely
  canceled native packet. Source/flag classifier tests pass, but actual native
  cancellation delivery remains unqualified.

The current post-r3 code passes **3,188/3,188 in Debug and Release**, each with
zero failed/skipped cases, plus the separately labeled **1,541/1,541** logic
correctness slice. Its only logic filter remains the historical
`PerformanceExternalGateFixtureTests` family; no unfiltered logic-suite pass is
claimed. Independent exact `(testId,testName)` multiset comparison retains the
3,184 preceding native cases and adds exactly the three semantic regressions and
one root-hook ordering contract in each configuration. The matching Release/x64
sample builds with **zero warnings/errors**; its renderer DLL matches the
Release-tested copy exactly. It is not launched or NativeAOT-published.
Independent source review finds no concrete defect in these two repairs. Exact
source/binary/TRX/log hashes, retained red and intermediate failures, sample
identity and test-retention checks are recorded in
[sandbox-r4-post-run-code-verification.json](../../artifacts/phase1-20261014/sandbox-r4-post-run-code-verification.json).
This is component/integration evidence only; it does not resolve the native
crash or retroactively pass any r3 interaction.

The final repair/rerun budget is used. New guest-only dump-capturing reproduction
and focused selection validation have been requested separately; neither is
launched without renewed user direction. Per-app dump registry values, host
desktop input and host display settings remain untouched. The selection-disable
child, COM point-range/edge-scroll follow-up, six deferred-touch cases and six
active lifecycle cases have not run in this batch. The crashed PID/HWND must
not be reused. Actual high DPI, crash resolution, remaining live lifecycle
proof, Phase 2's frozen matrix and Phase 3's full 500-page audit remain open.
No phase, whole-app AOT deployment, commit, push or scheduled run is inferred.

A subsequent **read-only** preflight confirms the same guest epoch, effective
administrator context, x64 process/OS and absence of both the LocalDumps base
and sample-specific keys. No setting is applied. The exact failing r3 build is
still retained for replay; a fresh r4 build must not silently replace that
diagnostic baseline. The proposed per-executable dump setup must preserve and
restore registry types/data/existence, verify disk capacity and keep any dump
local and uncommitted. A focused no-crash replay is inconclusive because the
preceding batch may have conditioned the heap. No case filter or dump runner is
yet implemented or executed. Readiness and boundaries are recorded in
[sandbox-r3-crash-diagnostic-readiness.json](../../artifacts/phase1-20261014/sandbox-r3-crash-diagnostic-readiness.json).

Independent read-only lifetime review finds no demonstrated lock escape,
double disposal or unsafe ownership handoff in snapshot retirement, paint/UIA
geometry leases or the passive sample observer. Snapshot disposal and geometry
reads share the layout lock; geometry callbacks materialize detached values and
revalidate snapshot/document identity. The observer records state without
capturing/handling input or mutating selection. Native glyph-call signatures
examined against the installed Windows SDK do not reveal a demonstrated ABI
mismatch. These negative findings are not proof that native lifetimes are safe
under every interleaving, nor do they identify the corruption source. No product
code is changed on the strength of this crash audit alone.

### Crash-diagnostic preparation without another UI run (UTC October 15)

The preceding goal turn is **progress**, not a wait: it records terminal live
evidence, fixes two selection defects and verifies the rebuilt code. The new
continuation rereads the user objective and completes safe offline diagnostic
preparation while renewed Sandbox launch/dump-capture approval remains pending.

- `-ResizeCrashDiagnosticOnly` is now an explicit, opt-in route through the
  existing main runner. It always runs exact C00 identity and only the existing
  C14 resize/selection case, without changing that case's assertions. Every
  omitted case remains UNVERIFIED; diagnostic reports are explicitly incomplete.
  Conflicting suppression/second-app switches fail before initialization. Default
  full-suite behavior is unchanged. A focused no-crash result is inconclusive
  about heap conditioning, not a full-batch or Phase 1 pass.
- Guest-only dump policy/adapter helpers are authored but **not executed in the
  guest**. They restrict changes to the three fixed per-executable HKLM/64-bit
  WER values, require explicit approval and independently identified Sandbox
  context, reject host/ambiguous/replacement context and unsafe filesystem paths,
  and preserve prior raw registry kinds/data through typed JSON and strict UTF-8
  receipts. Receipts are created without overwriting existing evidence. The
  actual Prepare catch/rollback is executed against an injected second-write
  failure in a fake registry, not against the host registry.
- Review changes the proposed cleanup deliberately: the concrete adapter **does
  not delete registry keys**. Checking an empty key and later deleting it could
  erase another writer's new value. It restores/verifies the three managed values
  and explicitly lists any retained inert/foreign keys, with
  `exactKeyExistenceRestored=false` when earlier absence cannot safely be restored.
  **This inert guest-only residue must be disclosed before live Prepare.** It is
  not represented as exact registry restoration or silently cleaned recursively.
  The earlier read-only readiness artifact records the proposal at that time;
  this newer behavior/evidence supersedes that proposal, not its observations.
- Offline tests pass in both **PowerShell 7 and Windows PowerShell 5.1**:
  routing **12/12**, pure policy **14/14**, actual adapter/fake registry **45/45**,
  plus the existing full UI-harness helper checks. Pure-plan tests that emulate
  deletion are not concrete Windows key-deletion proof. The retained initial
  adapter failure exposed dropped empty multi-string arrays in JSON. Review
  separately catches PS5.1's ANSI interpretation of non-ASCII script literals;
  final Unicode expectations use ASCII escapes/scalar checks and traverse the
  real nested policy → UTF-8 receipt file → typed decode path. Foreign values
  inserted at the key-removal seam survive, and a concurrent managed-value
  change aborts preplanned writes. Independent final review finds no blocking
  defect; none of these helper tests prove live WER collection or renderer health.
- Exact source/log hashes and retained intermediate findings are recorded in
  [sandbox-crash-diagnostic-offline-preparation.json](../../artifacts/phase1-20261014/sandbox-crash-diagnostic-offline-preparation.json).
  Host coordination must still pin a fresh epoch/process/payload and restore
  settings in `finally`. No app launch, registry change, dump capture/transfer,
  host input/display action, product-source edit, commit, push or scheduled run
  occurs in this continuation. P1-032 and every remaining live gate stay open;
  no phase is complete. The WinUI testing retry limit is not bypassed by the
  new diagnostic route, and execution still requires renewed user direction.

### Artwork selection repair and corrected gesture targeting (UTC October 15)

This continuation makes component progress without another UI launch. P1-033 is
registered before implementation and reproduces four incorrect after-image
endpoints from points inside visible artwork. The narrow repair shares the
paint destination instead of dividing the caption-inclusive block. Ordinary
hit testing and post-artwork/margin fallback stay compatible. Both complete
native suites pass 3,192/3,192, retain every prior case, and exercise the final
test source after a naming/comment-only cleanup. The logic correctness slice
passes 1,541/1,541 with its same explicitly recorded historical performance
fixture exclusion. The Release/x64 unpackaged JIT sample builds without warnings
or errors and contains the exact tested Release renderer; it is not launched or
NativeAOT-published.

The C10 runner keeps its original blank-space gesture as a distinct delivered
handle/non-collapse check. A separate authored hard-break/bold-text fixture uses
measured range geometry and fingertip offset, then requires exact independently
authored extended text in both viewport modes. SVG targeting uses the promoted
Body paragraph's effective margins, not the ImageBox constructor defaults.
Independent review rejects the intermediate six-DIP offset before live use.
The final 61 target-helper checks, 12 diagnostic-routing checks and existing
UI-harness checks pass under explicit PowerShell 7.6.6 and Windows PowerShell
5.1.26100.9549. These are arithmetic/helper/static checks, not actual DPI or
touch proof. The C14 crash-case body remains byte-identical; changed preceding
tests do not reproduce the old heap conditioning by definition.

Exact receipts, sources, binaries, log hashes, retained intermediate findings
and test-retention comparisons are saved in
[sandbox-r5-image-selection-code-verification.json](../../artifacts/phase1-20261014/sandbox-r5-image-selection-code-verification.json).
P1-032's native heap corruption remains the highest-priority open defect. The
WinUI UI-testing retry limit still requires renewed direction for another
Sandbox batch or live dump setup. No guest registry settings, host input/display
settings, schedules, commits or pushes are changed. Existing managed dump-value
restoration can retain inert guest-only keys and must be disclosed before use.
Actual high DPI and the outstanding live lifecycle gates remain open. No phase
is complete.

### Headless crash-reproduction feasibility (UTC October 16)

A bounded source/tooling review checks a safe alternative while renewed live-run
approval is pending. Existing native tests cover snapshot construction,
CanvasRenderTarget painting, geometry leases and retirement. They do not exercise
the actual C14 HWND resize, CanvasVirtualControl compositor, control-owned handle
tree and COM UIA publication together. The source-wiring tests explicitly state
that limitation. An additional component stress pass would not resolve or explain
the un-attributed native crash, so no narrower replacement acceptance test or
speculative production repair is added.

Installed .NET SDK 10.0.402's CLI help also explicitly says its default
`--blame-crash` path does not collect native-code crashes for .NET 5+ processes;
that Windows path requires ProcDump. ProcDump/cdb are not found on the current
tool path, and `PROCDUMP_PATH` is unset. The installed DumpMinitool's presence is
not a qualified automatic native-crash monitor. No crash, test run, download,
installation, app launch or registry change is attempted. Exact inspected-source
hashes and capability limits are recorded in
[p1-032-headless-feasibility-20261016.json](../../artifacts/phase1-20261014/p1-032-headless-feasibility-20261016.json).
The next decisive step remains an approved Sandbox dump-capturing reproduction,
preserving the retained R3 failing payload before separate R5 qualification.
No source candidate, live gate or phase is newly passed by this assessment.

### Approved Sandbox crash investigation (UTC October 16; in progress)

Renewed human approval authorizes Sandbox-only investigation with temporary
per-executable dump capture. The first replay uses the retained R3 failing
Release build, with checked host/guest payload hashes, a fresh guest epoch,
actual 100% DPI and the original 3060-by-776 window bounds. Its focused C00/C14
diagnostic completes with **2 PASS, 0 FAIL, 60 UNVERIFIED**. Exact `hold` selection
and both 44-DIP handles survive 760-by-860 narrowing and restored width; all
three screenshots are inspected. There is no crash, dump or app crash event.
That isolated result does **not** resolve P1-032 or qualify the current R5 build.

The canonical transferred guest receipt verifies restoration of all three
managed dump values to their previous absence. Empty guest-only keys remain as
disclosed. The first host coordinator's shortened restore receipt and its
transfer-error exit-code limitation are preserved and documented; neither is
used as a restoration or test-pass oracle. Those coordinator defects are fixed
before the broader replay. A command-line debugger is found within the existing
WinDbg installation; none is downloaded or installed.

The first broader replay finishes **48 PASS, 4 FAIL, 10 UNVERIFIED** with no
crash, dump or app crash event. All 52 PNGs are individually reviewed without a
clear visual defect. C14 passes on the old build in the owned viewport, as did
the isolated replay; this does not resolve the original native crash. Three
failures reproduce the old-build handle/final-SVG selection defects; the fourth
still delivers ordinary release rather than a genuinely canceled native packet.
The newer R4/R5 repairs are not live-qualified by an old-build replay.

Forensic comparison identifies an important replay omission: the original
`IncludeSuppressionInvestigation` and `IncludeSelectionDisableInvestigation`
switches were both enabled, while the first full replay disabled them. V1-004
runs before C14, so this is a material state-history difference, not an identical
reproduction. Both original and replay C14 captures prove owned viewport and
100% DPI; neither independently records every app theme/text-scale/RTL input.
The transferred full-run receipts verify all three dump values restored and
both pre-existing empty keys retained. Exact terminal receipts and limits are in
[p1-032-sandbox-full-replay-20261017.json](../../artifacts/phase1-20261014/p1-032-sandbox-full-replay-20261017.json).

A corrected original-switch R3 replay runs separately with capture
armed and the harness source archived before execution; its terminal crash and
dump findings are recorded below. The attempted R5 run
stops in a cross-build binary-hash guard before guest setup or app launch; it is
not a UI result or product failure. Changed earlier harness interactions still
prevent promising identical heap conditioning to the original batch. No product
fix, current-candidate live pass, actual high-DPI pass or completed phase is
claimed. Exact focused receipts
and caveats are in
[p1-032-sandbox-focused-reproduction-20261016.json](../../artifacts/phase1-20261014/p1-032-sandbox-focused-reproduction-20261016.json).

### Native crash reproduced with a full dump (UTC October 18)

The corrected R3 replay terminates **38 PASS, 5 FAIL, 19 UNVERIFIED**. It
reproduces native heap corruption in the approved Sandbox, PID 4036, before the
resize case is reached. The last successful UI receipt is editor source
replacement (command 1310, 00:36:25 UTC); the next editor read fails and the
target snapshot confirms process exit. Temporal adjacency does not establish
which operation corrupted ownership. V1-004's near-origin tap sequence passes
before this; ordinary-release cancellation qualification and the old-build
handle-collapse failure remain separate failures. Tests skipped after process
loss do not become SVG, Math, Mermaid or resize assertion failures.

A full-memory dump is retained **outside the repository**, locally only:
831,187,019 bytes, SHA-256
`BFEE178004EE12737B7B802F77774ADE6461CCA5BFBC4C79845C1E16B268BCE9`.
Its MDMP header, exclusive-read availability and matching host/guest hash are
verified. The coordinator verifies restoration of all three per-executable dump
values and transfers the canonical guest receipts. The pre-existing empty
guest-only keys remain; host input/display settings are unchanged. No dump is
uploaded or committed, and no intentional crash or PageHeap/IFEO setting is used.

The installed debugger opens the dump successfully. Its initial analysis reports
`HEAP_FAILURE_BLOCK_NOT_BUSY` / double free during Win2D `CanvasImageSource`
destruction, called from native XAML image-peer cleanup on the UI thread. This
is a substantially narrower detection path than the original ntdll-only WER
report, **not yet proof of the first invalid release or a production fix**.
The renderer's selection overlay uses this surface type; its ownership and
WinRT composition are under investigation. A matching upstream
[WinUI issue](https://github.com/microsoft/microsoft-ui-xaml/issues/10195)
contains the same destructor/cleanup stack, but its stale closure is not a fix
and does not establish that our crash has the same root cause.

All 42 manifest PNGs are present, nonempty and individually reviewed without an
obvious visual defect. These are old R3, actual-100%-DPI captures, not current R5
qualification. The original R3 host harness was untracked and only its hash was
retained; the original generated guest probe is retained, but the current replay
cannot claim byte-identical host harness behavior or identical heap conditioning.
The [portable terminal crash receipt](evidence/2026-10-17/p1-032-original-options-crash.json)
records the timeline, native symbol identity, restoration and evidence hashes.
P1-032 remains open; no phase is complete. Renewed human approval had authorized a
Sandbox-only debugger batch to record the earlier surface release. Its opt-in
startup barrier and 16 passing setup checks were prepared, but that live batch
was never started. The user subsequently requests a different investigation
method; the next section supersedes that unused approach.

### Managed lifetime investigation instead of native debugging (UTC October 18)

The current work uses ordinary source review, managed tests, normal builds and
public-action Sandbox UI validation. No new debugger attachment, breakpoint,
dump capture, disassembly or low-level process probe is performed. The unused
debugger barrier and its two new repository helper files are removed, with
recoverable temporary copies retained outside the repository. The canonical UI
runner is restored to SHA-256
`7A0233FBE702C73BFDE8AED05CC43A8196ECB91D1A3B41D8A41B163BF8292B21`;
its existing case bodies and acceptance assertions are unchanged. No host
input/display settings, guest dump settings, schedules, commits or pushes change.

The R6 candidate addresses three inspectable ownership/publication boundaries:

- End the selection-adornment drawing session before committing clean geometry
  or updating XAML handles, and revalidate source/request/snapshot generations
  after drawing-session disposal.
- Keep the previous image-source owner alive through XAML replacement, then
  publish the new owner/dimensions only after successful attachment and current
  lifecycle/version checks.
- Keep the previous owner alive through XAML detachment and reject an obsolete
  release from clearing newer state.

The three new source-order contracts fail **3/3 before production edits**. The
post-change focused run passes **20/20**, and the full Release suite passes
**3,195/3,195**, with zero failures/skips. The Release/x64 JIT sample builds with
zero warnings/errors and contains the same renderer bytes as the tested Release
assembly. The complete Debug suite also passes **3,195/3,195**, with zero
failures/skips and the same test-identity/outcome multiset as Release. All 3,192
R5 Release test identities are retained; only the three new lifetime contracts
are added. These checks protect source invariants; they do not inject actual
XAML assignment failures or establish the native crash's cause.

The ordinary focused Sandbox replay finishes **2 PASS, 0 FAIL, 60 UNVERIFIED**
with its intended exit code 2 and `complete=false`. Its verified R6 payload runs
in the existing guest at actual 100% DPI. The exact selected `hold`, matching
current target/selection geometry and both UIA 44-by-44-DIP handles survive
760-by-860 narrowing and restoration to 3060-by-776. All three PNGs are
individually reviewed; selection and both endpoint dots remain visible. No
process loss occurs during these recorded cases. The restored in-process
observer has null handle entries, so it is not used to claim restored in-process
layout qualification; current UIA rectangles and screenshots are separate
evidence. This focused run does not exercise the full state history before the
intermittent crash, source replacement, the ancestor viewport or actual high
DPI. P1-032 therefore stays open. Exact source/build/test/UI hashes and limits
are in the [portable R6 managed-investigation receipt](evidence/2026-10-17/p1-032-managed-lifetime-investigation-r6.json).

Independent review retains two follow-ups rather than overstating this repair:
an expected XAML-detach exception can abort one-shot unload cleanup or skip a
device-recovery handler, and catching device loss without notifying the failed
Win2D device may leave recovery using the same canvas device, contrary to the
documented [manual device-loss recovery contract](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/handling-device-lost).
Neither is proved to have occurred in P1-032. Reentrant dependency-property mutation ordering also
remains unproven by the source-order tests. Actual 150%/200% DPI, active-touch
lifecycle coverage and current-source end-to-end qualification remain open.

### Fresh-machine continuation and structural P1-032 candidate (October 19–20)

Work resumes on a freshly provisioned machine in a dedicated worktree of this
branch. The October 17 raw artifacts, R3 payload and crash dump are not on this
machine; only the portable evidence archive survives, and all seven archived
files plus the frozen manifest hash verify unchanged. Installed prerequisites:
.NET SDK 10.0.402 (runtime 10.0.13), Windows SDK 10.0.26100, Visual Studio
2022 Build Tools with MSVC 14.44.35207 x86/x64/ARM64, and Rust 1.96.0.

**Baseline before any edit (`4a9f292`).** GitHub.Tests passes 3,195/3,195 in
Debug and, on a warm rerun, Release; the first cold Release run is recorded as
T1-015. Conformance 9/9, Math 80/80, Mermaid 259/259, provider 725/725, TextMate
61/61 and Html 10/10 pass; the Release/x64 sample builds with zero warnings and
both offline harness self-tests pass. PixelTests records 540/739: all 199
failures are `Rasterize_Fixture_MatchesBrowser` exact-alpha IoU differences
against this machine's Edge, mostly text, path and icon antialiasing. That is
browser pixel parity, not a correctness gate; it is carried into the P2-SVG-01
classification rather than counted as renderer defects.

**CI repairs.** PR #99's red checks had three causes, none a product defect:
NU1004 lock drift to the SDK's ILCompiler/ILLink 10.0.13 (re-locked through
`Restore-NativeAot.ps1 -UpdateLocks`, with the NativeAOT dependency ledger
regenerated: five version-only changes, 58 dependencies verified); two
source-contract tests matching LF-joined text in files Windows CI checks out
with CRLF (2/2 fail before, 2/2 pass after; no assertion weakened); and five
Clippy errors in `raster_decode.rs`. The Clippy fix is codegen-neutral:
unpatched and patched sources yield identical normalized PE payloads for x86,
x64 and ARM64, and repeat builds are identical. Neither source reproduces the
previously shipped workers here because the statically linked MSVC CRT and
Windows SDK libraries differ from the original build machine, so all three
workers are rebuilt locally and `RESVG_WORKER_IDENTITY.json` is refreshed
through `Build-ResvgWorker.ps1 -RefreshIdentity`. Requalification: identity for
all three RIDs, provider 725/725, GitHub.Tests 3,217/3,217, and win-x64/win-x86
NativeAOT smokes with zero IL/AOT warnings that pass their isolation, raster,
resource and shutdown gates. ARM64 remains build-verified only. JitHub.WinUI.Tests
passes 3,628/3,628 locally.

**Structural P1-032 candidate (`3d4343d`).** The R6 analysis left the final
release of each replaced `CanvasImageSource` to XAML image-peer cleanup or the
projection finalizer, in no fixed order. Every snapshot commit released the
adorner surface and removed its Image from the overlay, and every viewport size
change created another surface. The candidate removes that churn:

- `SelectionAdornerSurfaceLifetime` owns one grow-only surface per adorner
  Image (256-DIP buckets, recreated only for DPI/device change or a >4x
  oversized allocation). The Image is sized to the allocation at 1:1 and
  clipped to the viewport, so a reused surface is never stretched.
- A release or ensure requested during a Source assignment is deferred, never
  nested; replaced surfaces stay rooted for two rendered frames; release never
  throws, and a new adorner Image is always rebound explicitly.
- Snapshot commits only invalidate the adorner and keep it in the overlay.
  Unload release is exception-safe so one-shot cleanup and snapshot retirement
  always run. The unguarded release before `HandleCanvasDeviceLost` is removed
  and surface creation/attachment runs inside the device-loss guard.
- Device loss purges the bitmap cache of the device that owns the snapshot
  bitmaps and notifies each genuinely lost Win2D device through
  `IsDeviceLost`/`RaiseDeviceLost`. Paint is refused when the snapshot's layout
  device differs from the live shared device, which a new test confirms is a
  stable projected identity.

Five new source contracts fail **5/5 before production edits**; 17 behavioral
lifetime tests execute actual reentrant, throwing and device-change Source
assignments through fake hosts. Both complete GitHub.Tests suites pass
**3,217/3,217**, retaining all 3,195 prior identities. This is component
evidence only. P1-032 closes only after the original-option Sandbox batches
(`-IncludeSuppressionInvestigation -IncludeSelectionDisableInvestigation`) run
five consecutive times without process loss, together with a select/replace/
resize/viewport-toggle stress sequence in both viewport modes. Those runs are
waiting on a pending Windows restart that currently blocks Sandbox startup.

**Phase 3 inputs.** All 500 frozen README bodies are re-acquired and verified
against the manifest (`615c5672…`) with no upstream or integrity failure. The
current-candidate headless audit parses 500/500 with zero case failures and 19
Mermaid scenes; its only diagnostics are rank 340's two MATH100 fallbacks
(U3-001). The runner's nonzero exit for recorded diagnostics is retained. This
is still engine-only evidence, not a page or interaction audit.

### Sandbox batches, launcher root causes and the P1-032 root cause (October 21)

**Batch 1** (`20261021T052930Z-0310…`, candidate `3d4343d`) ran the full
original-option sequence without losing the sample: 42 PASS, 11 FAIL and
10 UNVERIFIED.
- Eight of the failures are winapp input commands rejected while the guest
  Search flyout held the foreground.
- One is P1-034, now fixed at component level.
- One is the selection-disable child that "exits before its window": the
  launcher's own process tree was being terminated (T1-017).

**Launchers.** `winapp target exec` closes its job when the guest script
returns, which kills every `Start-Process` descendant. That explains T1-012
and the deferred-touch child with a LAUNCHED receipt but no window. All three
runners now start probes with `Win32_Process.Create` and the launcher's full
environment block (T1-016/T1-017).

**Batch 2** (`20261021T071738Z-8026…`, candidate `5b80fd1`) lost the sample at
07:39:38Z with `0xc0000374` in ntdll, during the touch keycap case. The
structural candidate therefore does not resolve P1-032.

**Batch 3 (page heap).** With the user's approval for this diagnosis, normal
page heap and a full per-executable LocalDumps policy were enabled for
`MarkdownRenderer.Sample.exe`, inside the Sandbox guest only. The guest's prior
absence of both keys is recorded so they can be removed afterwards. Batch 3
(`20261021T074145Z-f553…`) crashed during the final-SVG case and left a 2.1 GB
full dump. That dump is local, uncommitted process memory.

`cdbX64` analysis:
- An access violation in `Microsoft.UI.Xaml` `AddRef` on freed memory, reached
  from `UIAffinityReleaseQueue::DoCleanup`. A finalized XAML element is being
  released, and its child-reference reset calls
  `CMultiParentShareableDependencyObject::RemoveParent`, then
  `DXamlCore::TryGetPeer`.
- The peer at that address has a `DirectUI::SurfaceImageSource` vtable, so
  XAML still held the aggregated inner object.
- Page heap attributes the freed allocation to Win2D's
  `CanvasImageSource::scalar deleting destructor`, freed from
  `System.Runtime.InteropServices.ComWrappers+NativeObjectWrapper.Finalize`.

The finalizer destroyed Win2D's outer object while XAML still referenced the
inner one.

The managed heap holds seven surface owners:
- Six have released cleanly, with no surface, host or pending deferred release.
- The live renderer holds the one remaining surface.

The exact element is therefore not attributed to a missed `Source = null`. The
hazard is the type itself. Any XAML image source aggregated around a Win2D
outer depends on .NET finalization order relative to XAML's UI-thread
teardown.

**The fix.**
- The adorner is now an inert `Border` placeholder hosting a `SpriteVisual`
  through `ElementCompositionPreview.SetElementChildVisual`.
- It draws into a grow-only `CompositionDrawingSurface` created by a
  per-control `CompositionGraphicsDevice` from `CanvasComposition`.
- Composition objects have no XAML peer, and every retired, released or
  unloaded surface, visual and graphics device is disposed deterministically.
- A replaced Win2D device is switched in place, and `RenderingDeviceReplaced`
  forces a repaint.
- Audit capture skips reading back the placeholder, because
  `RenderTargetBitmap` cannot see child visuals. It paints the committed
  adorner state under the same snapshot paint lease instead.

**Evidence and next steps.**
- Red/green: four presentation contracts fail 4/4 before the change, and the
  three new behavioral disposal tests fail to compile. The receipts are kept
  under `artifacts/correctness/m1-p1-032-composition/`.
- Both complete GitHub.Tests suites pass 3,226/3,226.
- P1-032 closes only after five consecutive original-option batches and the
  stress sequence run without process loss on this candidate.

The [October 12 checkpoint](implementation-checkpoint-2026-10-12.md) preserves
the old architecture/evidence. The [progressive performance plan](progressive-performance-plan.md)
is historical and its performance/browser-parity gates no longer block this goal.
