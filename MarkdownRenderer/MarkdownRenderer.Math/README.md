# MarkdownRenderer.Math

This package provides a native, renderer-independent TeX pipeline for `$...$` and
`$$...$$`: exact UTF-16 source ranges, an audited CSharpMath parser/typesetter snapshot,
OpenType MATH-table layout, immutable vector outline/rule scenes, structural accessibility
text, cancellation checkpoints, a two-second default cooperative deadline, conservative
working-memory accounting, and lossless invalid-source fallback.

`MathFormulaProcessor.ProcessAsync` moves native compilation and synchronous host
callbacks off its caller thread; then paint the returned `MathScene` through the
host's Win2D scene adapter. Host callbacks share bounded process-wide admission, so a
stuck provider cannot create an unbounded number of detached workers. When callback
admission or localization exceeds the deadline, the processor returns a provider-free
English diagnostic and a bounded-size TeX preview; exact source remains available through
the result's fallback and accessibility copy text. An uncooperative callback may retire
after the caller has received `MATH106`; its admission slot remains occupied until the
callback actually finishes. Direct use of `DefaultMathAccessibilityFormatter` and
`MathDelimiterScanner` performs localization synchronously and therefore requires a prompt,
non-blocking string provider. The default formatter hard-bounds structural speech and
original-TeX help text for UI Automation while retaining the exact TeX by reference as copy
text. The scene contains only immutable
renderer-owned paths, rectangles, and rules. It owns no graphics-device objects and
does not use Skia, WebView, SVG output, JavaScript, or bitmap formula caches.
`MathProcessingOptions` lets hosts lower source, nesting, scene, retained/working-memory,
dimension, and processing-time limits. Font initialization is retryable and bounded to the
three hash-locked CFF resources; no external font or filesystem input is accepted.
Set `MathProcessingOptions.LocalizationCulture` for localized diagnostics and accessible text.
When it is omitted, the processor or engine extension snapshots `CurrentUICulture` when it is
constructed, so a cached immutable document cannot change language with ambient caller culture.
Host `IMathStringProvider` and `IMathAccessibilityFormatter` implementations must be thread-safe
and stable for that lifetime. A replacement accessibility formatter receives the caller's exact
request unchanged and owns its localization policy when that request has no language tag; the
configured culture continues to govern diagnostics and the built-in formatter.

Backslash and bracket delimiters (`\(...\)` and `\[...\]`) are intentionally not enabled
for 1.0. See `THIRD-PARTY-NOTICES.md`, `UPSTREAM-PATCHES.md`, and the package's `licenses`
directory for provenance and license details.
