# MMIR 1.0 transport contract

MMIR is a private, little-endian, four-byte-aligned display-list transport. It is not a public
serialization format. The 32-byte header contains `MMIR`, major/minor version, header size,
total size, directory offset/count, and two zero fields. Each 20-byte directory entry contains
section kind, offset, byte length, record count, and fixed stride (`0` only for strings).

Section kinds are viewport (one `float32 x/y/width/height`), length-prefixed UTF-8 strings,
32-byte styles, `float32` geometry, 32-byte draw commands, 32-byte semantic records, 16-byte
links/actions, 24-byte diagnostics, and 20-byte UTF-8 source mappings. Indices use `UINT32_MAX`
for absent optional references. Paths are flattened polylines in geometry. MMIR 1.1 text
commands use geometry `[baseline-x, baseline-y, font-size, OpenType-weight]`, command word 6
for the resolved font-family string, and command word 7 for italic, RTL, and start/middle/end
anchor flags. Each native-resolved line is painted at that baseline and is never reflowed by
the managed decoder.

Text and semantic-name strings are final literal display content, not HTML or
Mermaid source markup. The SVG compiler resolves raw presentation wrappers and
character-entity provenance before measuring text and producing MMIR commands.
The managed adapter must preserve these strings, including literal angle brackets
or character-reference spellings, without another markup or entity pass. Inert
SVG source-classification metadata is consumed inside the native compiler; it does
not add a flag, sentinel string, or record to MMIR.

Canonical SVG relationship metadata also joins separately emitted path and
caption groups into one Edge semantic record. Caption shaping fragments share
that record and its complete literal name; distinct model relationships remain
distinct even when their captions match. The compiler charges one edge per
canonical relationship, validates repeated metadata, and uses the whole source
range when no exact model range is available instead of guessing from labels.

The managed decoder treats every byte as untrusted: it checks exact total size, alignment,
section overlap and uniqueness, strides/counts, finite numbers, enums/flags, reference graphs,
semantic depth and node/edge budgets, UTF-8 scalar boundaries, and all configured limits before
publishing an immutable scene. Native UTF-8 ranges are translated to half-open UTF-16 ranges.
A major version changes incompatible layout; a newer minor version is rejected until supported.
