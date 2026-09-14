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
| `MarkdownRenderer.Math` | Managed CSharpMath-based vector typesetter and bundled hash-locked math fonts. |
| `MarkdownRenderer.Mermaid` | Selected-RID Merman native engine plus validated MMIR scene contracts. |
| `MarkdownRenderer.Svg.ThorVG` | Architecture-specific native ThorVG SVG assets. |
| `MarkdownRenderer.SyntaxHighlighting.TextMate` | Async TextMate integration, cancellation/deduplication, budgets, and provider contracts; no grammar or native payload. |
| `MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common` | Curated C/C++, C#, web, JVM, Go, Rust, scripting, data, Docker, and shader grammars. |
| `MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.All` | Complete pinned upstream TextMateSharp grammar/resource corpus. |
| `MarkdownRenderer.All` | Explicit meta-package for all features and large payloads. |

Most applications should reference `MarkdownRenderer` plus the smallest set of
optional packs they actually use. `MarkdownRenderer.All` is a convenience for
apps that consciously accept the full dependency and size cost.

## Platform packages

The native viewer targets WinUI on Windows and builds for x86, x64, and ARM64.
`MarkdownRenderer.Core` carries no WinUI or native payload. ThorVG assets belong
to the optional `MarkdownRenderer.Svg.ThorVG` package and must be validated for
each architecture only when that pack is selected.

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
are being completed. Do not infer 1.0 readiness from the presence of package
metadata. Full conformance, API compatibility, accessibility, architecture,
packaging, and publishing checks remain release gates.
