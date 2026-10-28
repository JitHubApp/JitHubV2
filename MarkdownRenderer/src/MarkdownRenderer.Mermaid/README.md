# MarkdownRenderer.Mermaid

Native Mermaid rendering for MarkdownRenderer, built on a pinned Merman revision compatible
with Mermaid 11.16.1. The selected-RID native engine parses and lays out diagrams without a
browser or JavaScript runtime, measures labels with DirectWrite, and transfers a validated,
backend-neutral little-endian MMIR scene to managed code.

The package provides immutable scene contracts, UTF-8-to-UTF-16 source mappings, generated
fixed-width `cdecl` bindings, `SafeHandle` ownership, cancellation/deadline/budget enforcement,
and atomic original-code fallback. Register it with `new MarkdownEngineBuilder().UseMermaid()`;
normalized `mermaid` fences then become renderer-neutral vector scenes that the WinUI package
paints and exposes through UI Automation. Mermaid/Markdown content can never execute scripts,
load files, perform external fetches, or supply callbacks. The only callback is the host-owned,
explicitly configured diagnostic string provider described below.

The options-based `UseMermaid()` overload stores a reusable owned-service factory. Every
`MarkdownEngineBuilder.Build()` call, including a build derived through `UseExtensions`, creates
an independent renderer that is disposed with that engine. Builders and frozen extension
templates therefore remain reusable after any earlier engine is disposed. For an intentionally
shared lifetime, construct `MermaidRenderer` yourself, pass it to `UseMermaid(renderer)`, and
dispose the renderer only after every engine that borrows it has stopped parsing.

Decoded scenes use a renderer-local weighted LRU (32 MiB by default, configurable or disabled)
with in-flight request deduplication. Keys include the source digest, Mermaid/Merman/MMIR/ABI
versions, layout and theme configuration, every render budget, and the immutable font-catalog
fingerprint. Unique running-plus-queued renders are admitted against both count and aggregate
UTF-8 source-byte budgets; callers for an already admitted key still share that work. The
end-to-end deadline includes admission, cache lookup, semaphore queueing, native rendering, and
scene decoding. Failures, overloads, timeouts, cancellation, and code fallbacks are never retained.

## Themes and Windows High Contrast

`MermaidRenderOptions.Theme` selects an explicit palette from the pinned Mermaid 11.16.1
implementation. `Default` and `Light` use Mermaid's documented default theme; `Dark`, `Forest`,
and `Neutral` use their matching Mermaid themes. The native adapter retains the resulting
authored node, edge, note, cluster, text, and categorical colors instead of replacing them with
host-editor colors. The selected theme also owns an opaque diagram canvas, so its intended
contrast remains stable when embedded in a differently themed window.

When Windows High Contrast is active, painting resolves diagram surfaces to the system `Window`
color and diagram text, outlines, edges, and markers to `WindowText`; actionable links use the
system hyperlink color. This remapping happens at draw time and does not destroy the authored
palette cached in the scene. Semantic labels and relationships remain exposed through UI
Automation, so meaning is not communicated by color alone.

Set `MermaidRenderOptions.StringProvider` to an immutable, thread-safe
`IMermaidStringProvider` to localize diagnostics. Providers receive a stable
`MermaidStringKeys` key, the renderer's diagnostic culture, and immutable formatting arguments.
Set `MermaidRenderOptions.DiagnosticCulture` explicitly for a localized engine; when omitted, the
renderer snapshots `CurrentUICulture` at construction. This makes cached documents deterministic
when ambient culture later changes. A null, invalid, or throwing provider safely falls back to the
built-in English diagnostic while keeping the diagnostic code, source range, and exact Mermaid
source fallback unchanged.

ELK is intentionally excluded for the 1.0 licensing boundary. An explicit `layout: elk` or
equivalent parsed renderer directive returns `UnsupportedLayout` and preserves the original
Mermaid code fence. Ordinary labels containing words or JSON such as `layout: elk` remain data
and do not trigger that diagnostic. See `MERMAN_PROVENANCE.json` and
`MERMAN_PATCH_LEDGER.md` in the package for the exact source, parity, feature, license, and
downstream-adapter record.
