# Troubleshooting

## The page has nested or unstable scrolling

Use `MarkdownScrollView` only when the markdown component owns vertical
scrolling. Inside a page or workspace that already owns a `ScrollViewer`, use
`MarkdownDocumentView` so the renderer observes the ancestor's effective
viewport.

## A document is parsed repeatedly

Create one immutable `MarkdownEngine`, call `ParseAsync`, and assign the returned
`MarkdownDocument` to one or more views. Reassigning `Markdown` is a convenience
path that transfers parse ownership back to each view.

## GFM or GitHub syntax is missing

The lean package defaults to CommonMark. Install `MarkdownRenderer.Gfm` and call
`UseGitHubFlavoredMarkdown()` for strict GFM. Install `MarkdownRenderer.GitHub`
and call `UseGitHubReadme()` for the broader GitHub README profile. Neither is
enabled merely because the base viewer is installed.

## SVGs render as placeholders

The resvg provider is optional. Confirm that `MarkdownRenderer.Svg.Resvg` is
installed, one shared `ResvgMarkdownSvgRenderer` is assigned to `SvgRenderer`,
and the selected x86, x64, or ARM64 output contains the matching worker. Also
verify image resolver policy, document/base URI, third-party remote-image
configuration, static-SVG validity and resource limits, worker diagnostics, and
graphics-device recovery. A typed placeholder is expected for executable,
external, over-budget, quarantined, or unsupported content.

## Math or Mermaid content falls back to source

Check extension registration, input validity, configured budgets, capability
results and diagnostics. Math includes its formula processor; Mermaid requires
the matching selected-RID native payload. Missing payloads, unsupported syntax
or layout (including ELK), and exceeded limits retain fallback deliberately.
Keep fallback visible rather than treating package installation as proof that
every input can render.

Invalid TeX returns `InvalidSource` with diagnostic `MATH100` and source fallback;
it does not throw an internal validation exception. The Audit matrix deliberately
includes `\notacommand{sample}` to exercise this path, while the primary Math page
is a clean valid-input showcase. Its display summation should render as a vector
equation, including after editing in a WinUI TextBox, which can return CR-only line
endings. Display extraction supports LF, CRLF and CR without normalizing the
original UTF-16 source ranges.

## Mermaid labels or arrowheads are displaced

Rebuild and deploy the native Mermaid payload together with the managed pack.
The scene converter must retain text-run positions, centered measurement
extents, marker instances, curved paths, and the SVG canvas background. A
successful native status or a `MarkdownDiagram` automation peer does not prove
that the pixels are correct. Use `eng/Test-MermaidSampleUi.ps1` and inspect its
screenshots. Only the Audit matrix's explicitly labeled negative ELK fixture should
fall back; the primary Mermaid and Diagram pipeline pages should not emit `MMR0002`.

## Normal Copy produces rendered text

Rendered semantic text plus `CF_HTML` is the default. Use the explicit source
path when markdown is required:

```csharp
view.CopySelectionToClipboard(new MarkdownCopyOptions
{
    PlainTextMode = MarkdownPlainTextCopyMode.SourceMarkdown,
});
```

`CopySelectionAsMarkdown()` is the convenience method.

## A hosted element is not realized

Confirm that the extension emitted a hosted-element request with the expected
factory key and that the view's `HostedElementFactory` handles it. Creation is
viewport-aware and cancellation-aware; elements outside the realization band
may not exist, and recycled elements must not retain stale app state.

## Theme changes do not appear

The viewer follows WinUI/Fluent environment resources by default. Check the
view's `StyleSheet`, `Theme`, application resources, actual theme, contrast mode,
text scale, and flow direction. GitHub-specific behavior must be opted into; it
is not the base default.
