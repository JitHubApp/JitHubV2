# MarkdownRenderer resvg worker protocol v10

The host and worker exchange fixed-size little-endian frames over one private
duplex named pipe. Encoded image bytes and premultiplied pixels are never carried by the
pipe. Each data request borrows an anonymous Windows shared-memory section
handle duplicated into that exact worker. `Open` attaches a document token from
a source mapping; later renders receive output-only mappings.

Every 512-byte request contains the `MSVG` magic, protocol version, operation,
monotonic request ID, 128-bit session nonce, immutable resource ceilings,
SHA-256 of the source, rendering inputs, locale, and mapping handle. Every
256-byte response echoes the magic, version, request ID, and nonce. Unknown
versions, nonces, IDs, flags, reserved bytes, lengths, or mappings are fatal
protocol violations.

Request bytes 240 through 247 contain the little-endian UInt64 worker-local
handle. Bytes 170 through 175 and 248 through 367 are reserved and must be zero;
v6 name fields are not accepted. `Open`, `Render`, and `RasterDecode` require a nonzero,
addressable, non-pseudo handle. `FontCatalog` uses the same field for its
read-only catalog-table section. Other control operations require zero. The
host transfers only the access needed by the operation, without inheritance,
after taking the transaction gate. The worker maps and unmaps its view but
never closes a borrowed handle. For Open/Render/RasterDecode, the host closes the remote
handle before releasing the transaction gate; on cancellation, transport
failure, or malformed response it first terminates the worker. This avoids
opening the session named-object namespace under a restricted primary token
and requires no namespace ACL or token relaxation.

Request flag bit 0 identifies a tiled render. Bit 1 explicitly records that a
semantic color is present, so opaque white (`0xffffffff`) cannot collide with
the absence of a semantic color. Bit 2 indicates an exact fractional physical
viewport for a render. When set, bytes 376 through 383 contain its IEEE-754
little-endian `f64` width and bytes 384 through 391 its `f64` height, both in
physical pixels. Each value must be finite and positive, and its mathematical
ceiling must equal the corresponding integer complete-output target dimension.
The output mapping still contains the exact integer-sized raster (or integer
tile); the viewport changes SVG scale and filter extents, not allocation size.
When bit 2 is absent, both `f64` fields must be all-zero and the integer target
continues to define the viewport exactly. Open, FontCatalog, and control
operations must not set bit 2. Bytes 392 through 511 remain reserved and zero
except for the explicitly separate `RasterDecode` fields below.

## Static raster decoding

`RasterDecode` (operation 7) decodes an admitted static WebP source without
using an installed WebP codec. It shares the existing worker pool, restricted
process, deadline, cancellation/preemption and failure-quarantine rules. It
does not create a document token, use font state or retain a parsed-tree cache
entry. An error never causes an in-process or platform-codec retry.

In addition to the common frame header, only these fields are populated:

| Offset | Type | Meaning |
| --- | --- | --- |
| 36 | u32 | Exact encoded source length |
| 40 | u32 | Exact output byte length |
| 44, 48 | u32, u32 | Oriented output width and height |
| 125 | u8 | Premultiplied sRGB byte order: 1 RGBA, 2 BGRA |
| 136 | 32 bytes | SHA-256 of the exact encoded source |
| 240 | u64 | Borrowed worker-local section handle |
| 392 | u64 | Total admitted explicit working bytes |
| 400, 404 | u32, u32 | Expected unrotated source width and height |
| 408 | u32 | Expected EXIF orientation, 1 through 8 |

All SVG policy, locale, theme, flags, document, viewport and font fields are
zero, as are bytes 412 through 511. The worker independently validates the
source hash, container, dimensions and orientation rather than trusting host
hints. Upscaling is rejected. Input is capped at 32 MiB, each source dimension
at 16,384 pixels, source area at 40,000,000 pixels and output at 64 MiB. A
request's lower working limit remains authoritative, with a hard ceiling of
64 MiB on x64/ARM64 and 32 MiB on x86.

The section contains source bytes, zero padding to a 64-byte boundary, the
exact output range and the usual 64-byte trailer. The working charge includes
this whole section and explicitly owned decoder/intermediate allocations.
The allocation ledger is not an RSS promise: native color-management internals,
allocator overhead and process-wide state remain subject to the independent
kill-on-close Job Object commit ceiling and active request deadline. An
exhausted ledger returns a resource-limit failure; worker loss is typed and
quarantines the content without replay.

Successful output is fully initialized, premultiplied sRGB, color managed
before resampling and EXIF orientation. Response dimensions and stride must
exactly match the request. Intrinsic width/height and aspect ratio describe
the oriented source. SVG dependency flags and font generation are zero.
Failures carry no output or intrinsic metadata; only status and a bounded,
content-free detail are permitted. The host retains the section through GPU
upload, then releases source and pixel views together. Static VP8, VP8L and
VP8X are distinct from animated WebP; animation is not admitted by this opcode.

## SVG and font operations

An `Open` mapping contains source bytes and has no output range. It returns a
host-selected, nonce-scoped document token after hashing, security inspection,
and parsing. A `Render` mapping contains only the exact output raster; the
worker resolves its document token without re-copying, re-hashing, or
re-parsing the source. If another worker needs the token, the host attaches the
source bytes through a temporary source mapping before rendering. No source
mapping is retained on a document handle. A tile request describes
its coordinates in the complete output; the output mapping contains only the
cropped tile. `TrimCache` discards parsed trees and decoded resources, and
`CloseDocument` forgets the token.

All mappings reserve a 64-byte trailer after the aligned source and exact
output range. For `Open`, the worker release-stores a one-byte, content-free
phase marker in the trailer as the request advances through mapping, hash
verification, XML parsing, security inspection, font setup, tree construction,
and document attachment. Values 8 through 12 bracket post-font-gate setup:
8 marks entry to SVG options initialization, 9 marks completed options
construction, 10 marks completed theme transformation, 11 marks input ready
immediately before `usvg` tree conversion, and 12 marks completed conversion.
The host acquire-loads it only if the hard request deadline expires. The trailer never
changes source hashing, raster dimensions, or the output lease exposed to
consumers; neither source nor pixels are logged with a timeout.

`FontCatalog` (operation 6) transfers one complete host-selected font catalog
to the worker. It is the only operation that can start or replace a catalog;
`Hello` never discovers files or starts work. For `FontCatalog`, `source_length`
is the exact table byte length, not font-file data length, and the handle at
byte 240 identifies an anonymous section mapped from offset zero for exactly
`source_length` bytes. The table is little-endian:

| Offset | Size | Meaning |
| --- | --- | --- |
| 0 | 4 | Magic `MFNT` (`0x544e464d`) |
| 4 | 4 | Catalog table version, currently 1 |
| 8 | 4 | Number of font entries, 1–4096 |
| 12 | 4 | Catalog generation; must match the request |
| 16 + 16*i | 8 | Worker-local handle for read-only font bytes |
| 24 + 16*i | 8 | Exact byte length of that font |

The table length must equal exactly `16 + 16 * count`; trailing and truncated
bytes are rejected. Font handles must be nonzero, addressable, and unique.
Each font is 1 byte through 128 MiB. The checked aggregate file-backed virtual
address budget is 1.25 GiB on x86 and 4 GiB on x64/ARM64; this limits mapped
views, not the private-memory job ceiling. No font is silently skipped or
truncated. All mappings must succeed before the worker returns status 0. That
acceptance acknowledgement has zero output length, dimensions, stride, pixel
format, and metadata flags: it confirms only that every read-only view was
mapped. The host then closes its duplicated worker handles after validating
the acknowledgement. Mapped views keep the section contents alive and are
owned by the worker; it never closes the borrowed handles. On malformed input,
or if any entry cannot be mapped, the worker returns typed status 6 and does
not publish a partial catalog.

If a replacement catalog is still initializing, a newer `FontCatalog` request
returns status 5 with detail `font-catalog-busy` and the currently published
generation. This response is not an acknowledgement: the request generation is
not accepted, and the worker does not map or retain the table or font handles.
The host keeps that bounded transfer's duplicated handles alive and retries the
same request/table after polling the previous generation to a terminal result,
within the existing initialization deadline. The host must not rebuild or
substitute the table on a busy retry. A deadline or transport failure retires
the worker before those handles are released.

The host enumerates and filters font files under its approved installed-font
roots, opens them itself, and sends only duplicated read-only mapping handles
and lengths. Paths, names, and filesystem access are never provided to or
performed by the worker. The worker passes immutable mapping-backed byte
sources to `fontdb`; these views remain alive with the database, so font bytes
are demand-paged rather than copied into per-font vectors. The current database
remains available while one replacement is initialized on the background font
thread. The worker admits no second mapped replacement until the first has
either been published or failed and been acknowledged by a `Hello` probe. This
keeps retained mapping-backed catalogs bounded to the published database plus
one replacement, including while a completed result awaits publication. Newer
generations may be retried only when a prior attempt received the explicit
`font-catalog-busy` response; that response does not accept or map them. Once a
mapping acknowledgement succeeds, that generation is accepted exactly once.
Repeated or older accepted generations are rejected, and the host must restart
a worker rather than retrying the same accepted generation after a catalog
failure.

`Hello` is a nonblocking text-readiness probe. Its `font_generation` field is
the target catalog generation. With no accepted `FontCatalog`, it reports
pending. After mapping acknowledgement, the worker initializes fontdb and the
text pipeline in the background. After returning one completed result, the
font thread waits for the protocol thread to publish or discard it before
starting another initialization. A response returns the actually published
catalog generation in bytes 76 through 79. For `Hello`, status 5 means the
target is not ready and status 0 means the published generation is at least the
target. Status 6 reports a failed initialization for the current target; a
superseded target remains pending. The response frame remains 256 bytes; bytes
76 through 79 are no longer reserved.

The worker keeps its currently published database usable only for requests
bound to that exact generation while a new database is being built. A text
`Open` whose requested generation is not the exact published generation
returns status 5 after source metadata inspection rather than waiting on the
font catalog. Detail `font-generation-changed` means a newer generation has
already published; the response still includes that actual generation. A
failed current initialization returns status 6, which is distinct from content failure.
On successful publication, old text cache entries and document aliases are
retired before the new database and its fresh font-fallback cache are installed together;
geometry-only cache entries remain. Text renders with retired aliases return
`DocumentMissing` before mapping access. Geometry opens and renders do not
wait for or trigger catalog refresh. Pending `Hello` response details contain
only a content-free phase name: `font-worker-startup`, `font-catalog-mappings`,
`font-family-defaults`, or `text-pipeline-warmup`. `font-catalog-busy` is used
only on a not-accepted `FontCatalog` request, not as a `Hello` phase. Legacy
values for the former per-user scan phases are not emitted by protocol v9. No
paths, font names, or SVG content are included. The host probes before
text-bearing work under
its content-independent initialization deadline. Exceeding that deadline
does not kill a responsive worker or restart its font initialization; later requests can
probe the same worker.

The first successful `Hello` after a catalog publication may use the existing
detail field for a fixed, content-free `font-timings-v1:` payload. Its base64
body contains seven fixed-order, 9-byte records: database creation; mapped
font-source loading; reserved slots for the former per-user local and roaming
font scans; family defaults; text-pipeline warmup; and overall native
initialization. Slots 2 and 3 remain present for wire compatibility and must
be unexecuted with zero durations. Each record stores an executed byte
followed by little-endian 100-ns wall and worker-thread CPU ticks;
`0xffffffff` means CPU time was unavailable. The 63-byte body encodes to 84
base64 characters, so the detail remains below the existing 127-byte limit.
Hosts that do not collect diagnostics may ignore it. The response frame size
remains unchanged; this field carries no paths, font names, source identity,
or text. The worker reports `font-catalog-mappings` while the mapped catalog is
being loaded and is not ready until a full catalog is installed and the warmup
produces visible Latin and RTL glyphs. An empty or unreadable catalog, or a
blank text warmup, is a typed status-6 failure rather than a successful blank
render. Process warm-up does not send `Hello`. SVGs without text bypass the
probe.
The host releases primary and secondary worker slots between readiness probes,
so geometry remains dispatchable while background font initialization is pending.
Parked text callers preserve one logical admission credit, rather than adding
new queue entries or holding a slot throughout initialization. The overall
readiness window includes gate and queue waits; each control exchange is still
bounded by the three-second hard request ceiling (or the lower configured
deadline). Actual browser-relative latency, memory and fault qualification
remain mandatory; these protocol rules alone are not a performance verdict.

The protocol is deliberately not extensible in place. Any field change
increments the version and ships a matching provider and worker.
