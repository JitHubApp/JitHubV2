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

Selection drag is a special case. Mature behavior requires a temporary drag shield
above embeds so pointer movement remains with the markdown selection system while
the drag is active. The shield should be disabled immediately on release so
normal control interaction resumes.

## Accessibility

Hosted controls expose their own UIA peers through XAML. The hard part is
semantic bridging: screen readers should experience a coherent document where
painted markdown and hosted controls share a predictable reading and focus order.
This is partially implemented through focus traversal and visual-tree peers but
needs further work.

## Design rules for embed authors

- Never touch WinUI APIs from `CanCreate` or `MeasureHeight`.
- Make `MeasureHeight` deterministic for a given block and width.
- Keep hosted controls lightweight; many embeds can exist in long documents.
- Use `RecycleBlock` to detach event handlers or dispose expensive resources.
- Do not assume the same `FrameworkElement` instance will be reused.
- Treat embed realization as viewport-dependent.

