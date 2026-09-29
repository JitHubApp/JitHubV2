# September 8 review fixes

The five actionable findings from the checkpoint review are addressed. This is
fix-specific verification, not a declaration that every 1.0 release gate passed.

## Changes

- Reusable unload now detaches the old canvas's input/render handlers and window
  lifecycle subscription, releases its native resources, and drops the removed
  canvas. Reload creates a fresh canvas behind the existing overlay and restores
  its handlers. Final disposal remains separate from reusable unload.
- Safe-HTML options now reach inline parsing and cross-block scope tracking.
  Input, node, depth, tag and attribute limits are enforced; over-budget content
  receives the host-localized omission notice. Synthetic document roots do not
  consume nesting depth. Legacy parsing without precise spans remains bounded.
- Pointer handling uses a nonblocking snapshot interaction gate. Contended input
  records position, modifiers and time instead of retaining routed event args.
  A bounded queue coalesces adjacent moves, preserves transitions, retries on the
  dispatcher, and clears pending work on teardown. Vertical page-wheel ownership
  remains with the host. Release commits its final position before ending capture,
  so coalesced/missing move events cannot leave selection at an obsolete endpoint.
- Shutdown automation requires exit code zero plus a process-matched disposal
  marker. Access violations and fail-fast exits cannot pass merely because the
  process disappeared. The sample's marker covers every observed view.
- Documentation now describes the implemented HTML, Math, Mermaid and declarative
  adapters, correct clipboard defaults, and the distinct remaining release gates.

## Fresh verification

Release/x64, .NET SDK 10.0.401, on the review Windows host:

| Suite | Passed | Failed |
| --- | ---: | ---: |
| Core | 434 | 0 |
| GitHub/viewer contracts | 110 | 0 |
| HTML | 9 | 0 |
| Math | 32 | 0 |
| Mermaid | 51 | 0 |
| TextMate | 55 | 0 |
| Conformance harness contracts | 9 | 0 |
| **Total** | **700** | **0** |

The conformance-harness count is not a count of official specification examples
or a new full conformance certification.

Native sample build through the WinUI skill's `BuildAndRun.ps1`/project-mode
`winapp run`: zero warnings, zero errors. The attached native lifecycle probe
passed four checks: three reload cycles in both viewport modes, raw mouse
selection after reload in each mode, and clean exit with both views disposed.
The probe moves the same view instances between hosts, checks fresh loaded
canvases and current snapshots, and obtains text bounds through UIA. Selection
itself is made with mouse input, not UIA `Select()`.

The first two pointer probes failed; their traces led to committing the release
position. The final probe passed all four checks. The final disposal marker
recorded process 37640, two attached/two disposed views, and completed disposal;
the retained process handle reported exit code zero.

Local verification artifacts: `C:/fulltests/markdownrenderer-fixes/` contains the
seven TRX reports, `lifecycle-final.log`, `lifecycle-disposal-3.json`, and the
native reload screenshot. These local paths are evidence locations on the review
host, not package contents.

## Repeating the native regression

Build the sample through project-mode `winapp run` (or the skill wrapper), with
`--args '--renderer-lifecycle-probe'` and `--detach`. Set
`MARKDOWN_RENDERER_DISPOSAL_EVIDENCE` to a unique writable JSON path before launch.
Pass the returned PID to the built automation console:

```powershell
dotnet MarkdownRenderer.Sample.Automation.dll --attach-lifecycle-pid <PID>
```

Set the same evidence environment variable for the console. It attaches only to
that process, runs the four checks, then closes that probe window. Build the
automation console first; paths above are relative to its build-output folder.

## Still separate release work

Full physical architecture/install coverage (especially ARM64), reference-versus-
candidate performance comparisons, 120 Hz/ETW runs, the physical DPI/theme and
manual Narrator matrix, device-reset/memory stress, and signed publishing
rehearsal remain release gates. Existing cross-build, forced-palette and console
trim/AOT artifacts are useful but do not replace those checks. See the
[release checklist](release-checklist.md).
