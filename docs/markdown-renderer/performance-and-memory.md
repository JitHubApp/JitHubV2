# Performance and memory

The renderer is built around a simple rule: parsing, layout, scrolling, hovering,
clicking, and selection should not block the next paint longer than necessary.

## Current performance architecture

Implemented:

- parsing runs off the UI thread through `Task.Run`;
- layout build runs off the UI thread;
- rebuilds are cancellable and superseded by newer rebuilds;
- `CanvasVirtualControl` repaints invalidated regions instead of the full document
  when possible;
- selection rectangles update on the XAML overlay without canvas invalidation;
- hosted embeds are virtualized around the viewport;
- images lazy-load around the viewport;
- bitmap and SVG caches are bounded;
- SVG cache stores raw bytes and rasterized output for a theme/DPI tuple;
- scroll anchoring preserves read position when content above the viewport changes.

## Rebuild cost

Current rebuild is coarse:

```text
markdown -> parse -> source map -> theme snapshot -> full layout -> snapshot swap
```

This happens for markdown changes, width changes, theme changes, extension changes,
embed factory changes, and some image load events.

Known issue: theme changes should not need to re-parse markdown. A restyle-only
path should reuse the parsed AST and rebuild only style-dependent metrics and
paint resources.

## Paint cost

Text and graphics are painted by block into virtual canvas regions. Hosted WinUI
elements are separate XAML children and do not paint through Win2D.

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

## Scale limits and roadmap

Current limitations:

- the whole document is measured before first paint;
- no lazy block layout for very large documents;
- no object pooling for layout boxes and inline runs;
- cancellation is coarse at parse/layout stage;
- embed factory measurement relies on consumers following the thread contract;
- large markdown updates allocate a new layout tree.

Priority future work:

1. restyle-only theme invalidation;
2. lazy/viewport-relative layout for very large documents;
3. more granular cancellation during layout;
4. object pooling or recycling for high-churn updates;
5. stress tests for 10K-line and 100K-word documents.

