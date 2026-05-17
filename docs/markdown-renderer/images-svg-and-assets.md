# Images, SVG, and assets

Images are rendered by `ImageBox`. The renderer supports bitmap images and SVG
payloads, with lazy loading and caching.

## Standalone and inline images

When a paragraph contains only a single image link, `LayoutBuilder` promotes it to
an `ImageBox`.

```markdown
![Alt text](image.png)
```

Images inside paragraph text render as atomic inline image cells. They use the
same bitmap/SVG loading and cache path as standalone images, reserve a compact
placeholder before load, and re-layout after intrinsic size is known.

Alt text is used as:

- loading placeholder;
- caption text when non-empty;
- accessibility fallback.

## Lazy loading

The control records image plans after layout. Images start loading when they enter
the viewport plus `LazyImageOverscanPx`.

If an image load changes intrinsic layout size, the control rebuilds layout. If
only pixel content changed, it repaints.

If bitmap/SVG materialization fails because the Win2D graphics device was lost
during a driver or monitor reset, the image is not permanently marked failed.
The control resets the load attempt and lets the next device-recovery rebuild
retry.

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

## Native ThorVG assets

ThorVG is included for:

- `native\win-x86\thorvg.dll`;
- `native\win-x64\thorvg.dll`;
- `native\win-arm64\thorvg.dll`.

The repo-level build target copies the correct DLL to the output root for x86,
x64, and ARM64 builds, and the core project packs all three files under
NuGet-style `runtimes\win-*\native` paths. The runtime resolver also probes the
app root, the project-reference `MarkdownRenderer\` subfolder, and RID-native
folders so SVG rendering survives common project-reference and package layouts.
