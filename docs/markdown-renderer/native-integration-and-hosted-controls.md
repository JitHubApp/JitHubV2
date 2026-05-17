# Native integration and hosted controls

The renderer can host real WinUI controls inside markdown. This is a defining
feature: markdown can include app-native buttons, checkboxes, cards, pickers, or
other controls without leaving the native visual/input stack.

## Block embeds

Block embeds are created through `IMarkdownEmbedFactory`.

```csharp
public interface IMarkdownEmbedFactory
{
    bool CanCreate(Block block);
    float MeasureHeight(Block block, float availableWidth);
    FrameworkElement CreateBlock(Block block);
    void RecycleBlock(Block block, FrameworkElement element) { }
}
```

Threading contract:

- `CanCreate` runs on the layout thread and must be thread-safe.
- `MeasureHeight` runs on the layout thread and must not touch WinUI APIs.
- `CreateBlock` runs on the UI thread.
- `RecycleBlock` runs when a realized embed is removed from the overlay.

The renderer guards `CanCreate` and `MeasureHeight`: if either callback is
reached on the UI dispatcher thread, layout throws with guidance to move
thread-affine work to `CreateBlock` / `RecycleBlock`.

## Inline embeds

The core layout also has `InlineEmbedRun` support. Inline embeds are placed into
the text flow and realized as overlay elements at their computed inline rect.
They are tracked separately from block embeds for hit testing, focus, and
virtualization.

## Overlay positioning

Hosted controls live on the transparent overlay canvas above the Win2D surface.
The renderer sets:

- `Width`;
- `Height`;
- `Canvas.Left`;
- `Canvas.Top`.

The hosted element paints and handles input normally through WinUI.

## Virtualization

The renderer does not keep all hosted controls realized forever. It records
embed plans after layout and realizes only those near the viewport:

- `EmbedVirtualizationOverscanPx = 400`
- `EmbedVirtualizationDerealizeOverscanPx = 1200`

The larger derealization band prevents create/destroy thrash near viewport edges.

## Input policy

Normal clicks on hosted controls should go to the hosted controls. The renderer
suppresses its own cursor and link-hover behavior when the pointer is over an
embed so the embedded element can show its own cursor and handle input.

Selection drag is a special case. During an active markdown selection drag, the
renderer enables a temporary transparent drag shield above embeds so pointer
movement remains with the markdown selection system. The shield is disabled on
release/cancel so normal control interaction resumes.

## Accessibility

Hosted controls expose their own UIA peers through XAML. The renderer bridges
them into the markdown document by keeping focus order, selection source spans,
and `RangeFromChild` mappings coherent with the painted document. Manual release
smoke should still cover complex custom controls because app-authored peers can
vary.

## Design rules for embed authors

- Never touch WinUI APIs from `CanCreate` or `MeasureHeight`.
- Treat `CanCreate` and `MeasureHeight` as background-thread callbacks; compute
  from Markdig block data and primitive values only.
- Make `MeasureHeight` deterministic for a given block and width.
- Keep hosted controls lightweight; many embeds can exist in long documents.
- Use `RecycleBlock` to detach event handlers or dispose expensive resources.
- Do not assume the same `FrameworkElement` instance will be reused.
- Treat embed realization as viewport-dependent.
