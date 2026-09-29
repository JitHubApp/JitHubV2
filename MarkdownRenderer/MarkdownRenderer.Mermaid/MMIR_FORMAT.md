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

The managed decoder treats every byte as untrusted: it checks exact total size, alignment,
section overlap and uniqueness, strides/counts, finite numbers, enums/flags, reference graphs,
semantic depth and node/edge budgets, UTF-8 scalar boundaries, and all configured limits before
publishing an immutable scene. Native UTF-8 ranges are translated to half-open UTF-16 ranges.
A major version changes incompatible layout; a newer minor version is rejected until supported.
