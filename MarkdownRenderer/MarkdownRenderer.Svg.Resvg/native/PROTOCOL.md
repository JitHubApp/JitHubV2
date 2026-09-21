# MarkdownRenderer resvg worker protocol v3

The host and worker exchange fixed-size little-endian frames over one private
duplex named pipe. SVG bytes and premultiplied pixels are never carried by the
pipe. Each request names a private Windows shared-memory mapping. `Open` pins a
document token to a source mapping; later renders name output-only mappings.

Every 512-byte request contains the `MSVG` magic, protocol version, operation,
monotonic request ID, 128-bit session nonce, immutable resource ceilings,
SHA-256 of the source, rendering inputs, locale, and mapping name. Every
256-byte response echoes the magic, version, request ID, and nonce. Unknown
versions, nonces, IDs, flags, reserved bytes, lengths, or mappings are fatal
protocol violations.

Request flag bit 0 identifies a tiled render. Bit 1 explicitly records that a
semantic color is present, so opaque white (`0xffffffff`) cannot collide with
the absence of a semantic color.

An `Open` mapping contains source bytes and has no output range. It returns a
host-selected, nonce-scoped document token after hashing, security inspection,
and parsing. A `Render` mapping contains only the exact output raster; the
worker resolves its document token without re-copying, re-hashing, or
re-parsing the source. If another worker needs the token, the host attaches the
same persistent source mapping once before rendering. A tile request describes
its coordinates in the complete output; the output mapping contains only the
cropped tile. `TrimCache` discards parsed trees and decoded resources, and
`CloseDocument` forgets the token.

`Hello` is the text-readiness barrier: it completes only after the background
Windows font catalog is available and the fixed usvg/resvg text-shaping and
glyph-raster pipeline has been primed. Process warm-up does not send it. The
host uses it before the first text-bearing request and gives this one-time,
content-independent initialization its own deadline. SVGs without text bypass
the barrier and can render while the catalog is still loading.

The protocol is deliberately not extensible in place. Any field change
increments the version and ships a matching provider and worker.
