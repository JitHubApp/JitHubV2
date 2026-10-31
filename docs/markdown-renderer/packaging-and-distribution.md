# Packaging and distribution

MarkdownRenderer ships one viewer package plus feature packs, so a basic viewer
does not pull a native DLL, an extra rendering engine, or a grammar corpus into
an application.

| Package | Payload |
| --- | --- |
| `MarkdownRenderer` | `MarkdownRenderer.dll`: the engine and immutable document API, CommonMark, strict GFM, Markdown Extra and GitHub README profiles, the budgeted native safe-HTML subset, the opt-in progressive performance session, and the native WinUI viewer. No native payload of its own. |
| `MarkdownRenderer.Math` | Managed CSharpMath-based vector typesetter and bundled hash-locked math fonts. |
| `MarkdownRenderer.Mermaid` | Selected-RID Merman native engine and its internal validated MMIR scene decoder. |
| `MarkdownRenderer.Svg.Resvg` | Isolated, architecture-specific resvg 0.48.1 static-SVG worker. |
| `MarkdownRenderer.SyntaxHighlighting.TextMate` | Async TextMate integration, cancellation/deduplication, budgets, and provider contracts; no grammar or native payload. |
| `MarkdownRenderer.SyntaxHighlighting.TextMate.Engine` | The TextMateSharp-backed tokenizer both grammar packs build on. It brings TextMateSharp and the Onigwrap native regex engine; install a grammar pack rather than this package. |
| `MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common` | Curated C/C++, C#, web, JVM, Go, Rust, scripting, data, Docker, and shader grammars. |
| `MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.All` | Complete pinned upstream TextMateSharp grammar/resource corpus. |
| `MarkdownRenderer.All` | Explicit meta-package for all features and large payloads. |

Most applications should reference `MarkdownRenderer` plus the smallest set of
feature packs they actually use. `MarkdownRenderer.All` is a convenience for
apps that consciously accept the full dependency and size cost.

The profiles inside `MarkdownRenderer` stay off until they are selected: the
default engine is CommonMark, and GFM, Markdown Extra, the GitHub README profile
and safe HTML are builder calls. Why these live in one assembly is explained in
[Tech stack and decisions](tech-stack-and-decisions.md#one-renderer-assembly).

Package dependencies (third-party grammar and TextMate engine packages omitted):

```text
MarkdownRenderer                     -> Markdig, Microsoft.Graphics.Win2D (-> Windows App SDK)
MarkdownRenderer.Math                -> MarkdownRenderer
MarkdownRenderer.Mermaid             -> MarkdownRenderer
MarkdownRenderer.Svg.Resvg           -> MarkdownRenderer
...SyntaxHighlighting.TextMate       -> MarkdownRenderer
...TextMate.Engine                   -> ...SyntaxHighlighting.TextMate, TextMateSharp
...TextMate.Grammars.Common          -> ...TextMate.Engine
...TextMate.Grammars.All             -> ...TextMate.Engine
MarkdownRenderer.All                 -> MarkdownRenderer, Math, Mermaid, Svg.Resvg,
                                        SyntaxHighlighting.TextMate, Grammars.All
```

Every feature pack depends on `MarkdownRenderer`, so each one brings Win2D and
the Windows App SDK with it.

## Platform packages

The native viewer targets WinUI on Windows and builds for x86, x64, and ARM64.
`MarkdownRenderer` is AnyCPU managed code with no native payload of its own;
Win2D supplies its own per-RID native assets. The resvg worker
belongs to the optional `MarkdownRenderer.Svg.Resvg` package and must be
validated for each architecture only when that pack is selected. Provider
registration is explicit; trimming and NativeAOT never probe assemblies or load
an engine dynamically.

## Package size budgets

`eng/Test-PackageSizeGates.ps1` fails a build whose packages exceed these
budgets. The resvg release and worker-signing scripts enforce the same resvg
limits.

| Gate | Budget | Measured |
| --- | --- | --- |
| `MarkdownRenderer`, compressed | 1,028 KiB | 932,824 bytes (2026-10-27) |
| `MarkdownRenderer`, managed DLL | 2,475 KiB | 2,302,464 bytes (2026-10-27) |
| resvg selected deployment (worker + provider DLL), per RID | 4,500 KiB | 4,136,448 bytes (x64, 2026-10-26) |
| resvg package, all RIDs, compressed | 5,950 KiB | 5,524,179 bytes (2026-10-26) |
| Math, compressed | 2.3 MiB | 701,640 bytes (2026-10-26) |
| Grammars.Common, compressed | 6 MiB | 654,534 bytes (2026-10-26) |
| Mermaid native DLL, compressed, per RID | 12 MiB | 5,969,501 bytes (x64, 2026-10-26) |

The viewer and resvg budgets were raised on 2026-10-26 to the measured size plus
about 10%. The earlier budgets (525 KiB, 1.2 MiB, 3.5 MiB, 5 MiB) were set in
September. Later work grew past them: the progressive renderer, the correctness
fixes, and the resvg WebP row decoder. Earlier failing CI steps hid the overrun.

The `MarkdownRenderer` compressed budget is the sum of the earlier engine and
viewer (900 KiB) and performance-session (128 KiB) budgets. Its managed budget
is the measured `MarkdownRenderer.dll` plus about 10%: the GFM, GitHub README,
safe-HTML and performance code it now holds never had a managed budget of its
own, so the earlier engine and viewer budget (2,150 KiB) did not cover it.

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

Apps construct `CommonTextMateGrammarProvider` or `AllTextMateGrammarProvider`
explicitly and pass it to a `TextMateCodeBlockSyntaxHighlighter`; nothing loads
grammar packs by name. The highlighter is caller-owned: keep it alive while any
view uses it, then dispose it. It disposes its provider only when it was
constructed with `ownsProvider: true`.

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
