# Performance and memory

The renderer is built around a simple rule: parsing, layout, scrolling, hovering,
clicking, and selection should not block the next paint longer than necessary.

## Current performance architecture

Implemented:

- parsing runs off the UI thread through `Task.Run`;
- layout build runs off the UI thread;
- rebuilds are cancellable and superseded by newer rebuilds;
- layout checks cancellation between block construction and measure steps;
- `CanvasVirtualControl` repaints invalidated regions instead of the full document
  when possible;
- selection rectangles update on the XAML overlay without canvas invalidation;
- hosted embeds are virtualized around the viewport;
- images lazy-load around the viewport;
- large documents use viewport-relative top-level layout: source maps and block
  plans are built up front, the initial viewport band is measured first, and
  scroll extends measured bands with scroll anchoring;
- huge fenced/indented code blocks are segmented above the monolithic text-layout
  threshold so a pathological single block does not require one enormous
  `CanvasTextLayout`;
- inline, table, list, code-block, stack, and embed measurement paths check
  cooperative cancellation during large rebuilds;
- bitmap and SVG caches are bounded;
- SVG cache stores raw bytes and rasterized output for a theme/DPI tuple;
- scroll anchoring preserves read position when content above the viewport changes.
- inline text-buffer construction reuses a small thread-local `StringBuilder`
  pool and avoids pooling native `CanvasTextLayout` / hosted-control state.

## Rebuild cost

Small-document rebuild is coarse:

```text
markdown -> parse -> source map -> theme snapshot -> full layout -> snapshot swap
```

For large documents, the layout phase switches to:

```text
markdown -> parse -> source map -> cheap block tree -> initial measured band
```

Unmeasured top-level blocks keep estimated bounds so the scroll range is
available immediately. As the viewport moves, `LayoutSnapshot` measures the next
band, reflows top-level bounds under a lock, refreshes embed/image plans, and
restores the user's scroll anchor. Documents that use a custom block
`IMarkdownEmbedFactory` stay on the eager background layout path so
`MeasureHeight` never runs on the UI dispatcher thread.

This happens for markdown changes, width changes, theme changes, extension changes,
embed factory changes, and some image load events.

Theme-only changes reuse the cached parsed AST and rebuild only style-dependent
layout/text metrics and paint resources.

## Paint cost

Text and graphics are painted by block into virtual canvas regions. Hosted WinUI
elements are separate XAML children and do not paint through Win2D.

Canvas drawing is guarded for transient graphics-device loss (`DXGI_ERROR_DEVICE_*`
and `D2DERR_RECREATE_TARGET`). If a GPU driver install, monitor reset, sleep/resume,
or adapter change invalidates the Win2D device during `CreateDrawingSession` or
paint, the control logs the HRESULT, swallows that known device-loss failure, and
coalesces a delayed rebuild/invalidate retry. Unknown paint exceptions are still
allowed to surface because they usually indicate renderer bugs.

The biggest paint correctness lesson from the current implementation: hover and
selection must not mutate DirectWrite text layouts or invalidate canvas tiles
unless text actually needs to repaint. Past mutations caused visible text shake
at 150 percent DPI.

## Selection performance

Selection uses pooled overlay rectangles:

- rectangles are created only when the pool needs to grow;
- drag updates mutate position, size, and visibility;
- the overlay visual tree is not rebuilt on every pointer move;
- no DirectWrite canvas invalidation happens during selection drag.

## Image and SVG memory

`ImageBox` keeps bounded static caches:

- bitmap URL cache;
- SVG raw/raster cache;
- failed URL cache.

Evicted `CanvasBitmap` values are not disposed immediately because live `ImageBox`
instances may share the same handle. Removing the dictionary reference is enough;
the handle is reclaimed after no live box references it.

SVG output can consume several MB per entry at high DPI, so SVG cache limits are
tighter than bitmap URL limits.

## Hosted control memory

Embed plans are cheap records of desired placement and factory state. Real
`FrameworkElement` instances are created only near the viewport and recycled when
far away. This avoids keeping hundreds or thousands of buttons/check boxes/cards
alive for long documents.

## Scale boundaries

Current limitations:

- lazy layout is top-level block based; a single enormous table/list item still
  measures as one top-level block, though inner table/list loops now cooperate
  with cancellation;
- custom block embed factories use eager background layout to preserve the
  factory thread-safety contract;
- layout boxes and inline runs are intentionally not pooled while they can own
  source-map identity, native text layouts, image events, or hosted UI state;
- embed factory measurement is guarded against UI-thread execution, but still
  relies on consumers keeping the callback pure and deterministic;
- large markdown updates allocate a new layout tree.

Potential follow-up optimizations, if real host documents need them:

1. row/item-level realization inside oversized tables and lists if real host
   documents show it is needed beyond current top-level lazy layout;
2. targeted recycling for additional proven pure managed helper objects;
3. broader automated stress runs for 10K-line and 100K-word documents.
