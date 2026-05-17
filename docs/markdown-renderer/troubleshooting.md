# Troubleshooting

This page collects production integration problems that are easier to diagnose
with concrete symptoms.

## SVGs render as placeholders

Check the selected architecture first. The core package and repo build support:

- `win-x86`
- `win-x64`
- `win-arm64`

The selected output should contain `thorvg.dll`. Package builds should contain:

- `runtimes\win-x86\native\thorvg.dll`
- `runtimes\win-x64\native\thorvg.dll`
- `runtimes\win-arm64\native\thorvg.dll`

If the file is missing, the build should fail. If the file is present but SVGs
still do not render, verify that the SVG bytes are valid and that the app is not
blocking file/URL access for the image source.

## Driver install, monitor reset, or sleep/resume causes graphics errors

Win2D can throw transient DXGI/D2D device-loss exceptions when the GPU device is
recreated. The control catches known device-loss HRESULTs from virtual-canvas
paint paths, logs them, and schedules a delayed rebuild/invalidate retry.

The machine may still need a driver restart or reboot if the whole desktop stack
is degraded. Unknown paint exceptions still surface because they usually point to
renderer bugs.

## Theme override changes do not appear

Direct mutations should invalidate automatically:

```csharp
theme.Overrides[MarkdownElementKeys.Link] = new ElementStyleOverride
{
    Foreground = Colors.DodgerBlue,
};
```

For advanced integrations that mutate external objects referenced by a theme,
call `theme.Invalidate()` after the external state changes.

## Selected images look different from selected text

Text selection is drawn on a lightweight overlay so pointer drag does not repaint
the base DirectWrite canvas. Images remain visible under a translucent selection
tint and receive a selected outline instead of being fully covered by an opaque
rectangle. This is expected and keeps the selected image recognizable.

## Hosted controls steal selection drags

Normal clicks over hosted controls go to the hosted WinUI element. Once a
markdown selection drag starts, the renderer enables a temporary transparent
drag shield so pointer moves continue extending selection. If an app-hosted
control captures input permanently, make sure it releases capture on cancel/lost
capture and does not hold pointer capture after the drag ends.

## Embed factory throws thread exceptions

`IMarkdownEmbedFactory.CanCreate` and `MeasureHeight` run on the background
layout thread. They must not instantiate WinUI controls, access dependency
properties, read `ActualTheme`, or call the dispatcher.

Move WinUI work to `CreateBlock` and keep background callbacks based on Markdig
block data and primitive values only.

## Large documents feel slow

The renderer uses viewport-relative top-level lazy layout and cooperative
cancellation. Remaining hot spots usually come from:

- one enormous table or list item that still measures as a single top-level block;
- an embed factory doing expensive work from `CanCreate` or `MeasureHeight`;
- image sources that block or retry slowly;
- app code forcing repeated `Markdown` assignment instead of batching source changes.

Use the stress sample and diagnostics from [Testing and diagnostics](testing-and-diagnostics.md)
to isolate whether the cost is parse, layout, paint, image load, or hosted
control realization.

## Clipboard output is markdown, not rendered text

This is the default. Source markdown is the document of record, so keyboard and
context-menu copy write the exact markdown source slice as plain text plus an
HTML payload.

Use rendered plain text explicitly:

```csharp
control.CopySelectionToClipboard(new MarkdownCopyOptions
{
    PlainTextMode = MarkdownPlainTextCopyMode.RenderedText,
});
```
