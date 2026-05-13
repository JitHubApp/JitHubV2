# Images, SVG, and assets

Images are rendered by `ImageBox`. The renderer supports bitmap images and SVG
payloads, with lazy loading and caching.

## Standalone images

When a paragraph contains only a single image link, `LayoutBuilder` promotes it to
an `ImageBox`.

```markdown
![Alt text](image.png)
```

Alt text is used as:

- loading placeholder;
- caption text when non-empty;
- accessibility fallback.

Current limitation: inline images inside text are not `ImageBox` instances yet.
They render as alt text.

## Lazy loading

The control records image plans after layout. Images start loading when they enter
the viewport plus `LazyImageOverscanPx`.

If an image load changes intrinsic layout size, the control rebuilds layout. If
only pixel content changed, it repaints.

## SVG path

SVGs are detected by URL/content clues and rasterized through ThorVG. The result
is a BGRA buffer wrapped as a `CanvasBitmap`.

There is one paint branch:

```text
SVG/data URI/bitmap -> CanvasBitmap -> ImageBox.Paint
```

This avoids multiple SVG render paths and makes bitmap/SVG painting consistent.

## SVG theme integration

SVG cache entries keep raw bytes so theme-aware rasterization can be redone
without refetching. The cache also records:

- intrinsic size;
- SVG title;
- SVG description;
- cached BGRA output;
- output pixel dimensions;
- theme color;
- device pixel scale.

When theme color and DPI match, cached raster output can be materialized
synchronously and avoid placeholder blink.

## Accessibility metadata

`ImageBox` exposes:

- `Alt`;
- `SvgTitle`;
- `SvgDesc`.

The automation peer prefers explicit alt text, then SVG title/description.

## Cache policy

Static caches are bounded:

- bitmap cache;
- SVG cache;
- failed URL cache.

SVG entries are more memory-sensitive because they may store large BGRA buffers,
so SVG cache limits are intentionally tighter.

## Cross-architecture limitation

Only `native\win-x64\thorvg.dll` is currently included. Explicit x86 and ARM64
builds skip the DLL. When ThorVG is missing, SVG rendering falls back to the
alt-text placeholder.

Before production packaging, the library should either:

- ship ThorVG for x86 and ARM64;
- or provide a clear visible placeholder explaining unsupported SVG rendering on
  that architecture.

