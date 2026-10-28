# Packaging and distribution

MarkdownRenderer is intentionally split so a basic viewer does not pull every
syntax feature, native DLL, or grammar into an application.

| Package | Payload |
| --- | --- |
| `MarkdownRenderer.Core` | Immutable document and extension API with no WinUI or native payload. |
| `MarkdownRenderer` | Lean Core + native WinUI viewer convenience package. |
| `MarkdownRenderer.Gfm` | Strict GFM and separately opt-in Markdown Extra. |
| `MarkdownRenderer.GitHub` | GitHub README profile and safe-HTML composition. |
| `MarkdownRenderer.Html` | Native safe-HTML subset parser/painter and budgets. |
| `MarkdownRenderer.Performance` | Opt-in progressive image-source preparation and bounded per-account resource session. |
| `MarkdownRenderer.Math` | Managed CSharpMath-based vector typesetter and bundled hash-locked math fonts. |
| `MarkdownRenderer.Mermaid` | Selected-RID Merman native engine plus validated MMIR scene contracts. |
| `MarkdownRenderer.Svg.Resvg` | Isolated, architecture-specific resvg 0.48.1 static-SVG worker. |
| `MarkdownRenderer.SyntaxHighlighting.TextMate` | Async TextMate integration, cancellation/deduplication, budgets, and provider contracts; no grammar or native payload. |
| `MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common` | Curated C/C++, C#, web, JVM, Go, Rust, scripting, data, Docker, and shader grammars. |
| `MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.All` | Complete pinned upstream TextMateSharp grammar/resource corpus. |
| `MarkdownRenderer.All` | Explicit meta-package for all features and large payloads. |

Most applications should reference `MarkdownRenderer` plus the smallest set of
optional packs they actually use. `MarkdownRenderer.All` is a convenience for
apps that consciously accept the full dependency and size cost.

## Platform packages

The native viewer targets WinUI on Windows and builds for x86, x64, and ARM64.
`MarkdownRenderer.Core` carries no WinUI or native payload. The resvg worker
belongs to the optional `MarkdownRenderer.Svg.Resvg` package and must be
validated for each architecture only when that pack is selected. Provider
registration is explicit; trimming and NativeAOT never probe assemblies or load
an engine dynamically.

## Package size budgets

`eng/Test-PackageSizeGates.ps1` fails a build whose packages exceed these
budgets. The resvg release and worker-signing scripts enforce the same resvg
limits.

| Gate | Budget | Measured 2026-10-26 |
| --- | --- | --- |
| Core + WinUI, compressed | 900 KiB | 820,589 bytes |
| Core + WinUI, managed DLLs | 2,150 KiB | 1,974,784 bytes |
| resvg selected deployment (worker + provider DLL), per RID | 4,500 KiB | 4,136,448 bytes (x64) |
| resvg package, all RIDs, compressed | 5,950 KiB | 5,524,179 bytes |
| Performance, compressed | 128 KiB | 117,324 bytes |
| Math, compressed | 2.3 MiB | 701,640 bytes |
| Grammars.Common, compressed | 6 MiB | 654,534 bytes |
| Mermaid native DLL, compressed, per RID | 12 MiB | 5,969,501 bytes (x64) |

The first four budgets were raised on 2026-10-26 to the measured size plus about
10%. The earlier budgets (525 KiB, 1.2 MiB, 3.5 MiB, 5 MiB) were set in
September. Later work grew past them: the progressive renderer, the correctness
fixes, and the resvg WebP row decoder. Earlier failing CI steps hid the overrun.
The budgets catch regressions; they are not size targets. Raise one only with a
new measurement and a note here.

## Package reproducibility

The preview workflow rebuilds and packs every shipping package twice into
clean directories. Strict package compliance then requires the two package sets
to match byte for byte.

Both passes compile through one shared compiler server (`VBCSCompiler`). The
CsWinRT AOT source generator orders the vtable classes it emits by string hash
codes, which .NET randomizes in each process
([microsoft/CsWinRT#2562](https://github.com/microsoft/CsWinRT/issues/2562)).
Two compiler processes therefore build different `MarkdownRenderer.dll` bytes
from the same inputs: the same code in a different order, and with different
names for the shared vtable classes. A single server keeps that order the same
in both passes, and each pass still rebuilds every project from scratch. The
workflow fails if the server restarts between passes. Go back to separate
compiler processes (`UseSharedCompilation=false`) once that issue is fixed.

## AOT and trimming

Immutable profiles, exact syntax-kind dispatch, and declarative extension output
avoid reflection-based plug-in discovery. Each selected package still needs its
own trim/AOT verification, including native asset resolution and TextMate grammar
resource loading where applicable.

Trimmed and NativeAOT consumers should construct `CommonTextMateGrammarProvider`
or `AllTextMateGrammarProvider` explicitly. Reflection-based optional-pack
discovery remains available only as a compatibility convenience and is annotated
with `RequiresUnreferencedCode` and `Obsolete`. The parameterless view helpers
transfer the discovered highlighter/provider to the view, which disposes them.
Provider-taking compatibility helpers borrow the caller's provider but transfer
their adapter to the view. Explicit highlighter overloads remain caller-owned.

Each grammar pack carries the deterministic `GRAMMAR-PROVENANCE.json` inventory
and third-party notices. Run `eng/Invoke-TextMateReleaseEvidence.ps1` to verify
the exact upstream archives/resources, package reproducibility, the Common
6 MiB selected-RID cap, AnyCPU managed assets, SBOM/notices, and x86/x64/ARM64
trimmed and NativeAOT package consumers.

## Preview and versioning

The packages use preview versions while the supported API and conformance gates
are being completed. Every MarkdownRenderer package ships with the same version,
set once in `MarkdownRenderer/src/Directory.Build.props`. Do not infer 1.0
readiness from the presence of package metadata. Full conformance, API compatibility, accessibility, architecture,
packaging, and publishing checks remain release gates.
