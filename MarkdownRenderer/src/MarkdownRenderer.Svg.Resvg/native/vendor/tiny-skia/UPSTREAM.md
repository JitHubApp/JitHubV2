# Vendored tiny-skia provenance

This directory contains the published `tiny-skia` 0.12.0 crate under its
upstream `BSD-3-Clause` license. The crate archive SHA-256 is
`47ffee5eaaf5527f630fb0e356b90ebdec84d5d18d937c5e440350f88c5a91ea`; its
embedded VCS metadata identifies revision
`e48b9027762d33731e60e40eb17eaa4f583b0185` and repository tree
`c65bf6145b324fe1d00174aabe6349a736072591`.

The reviewed local patch adds an outer fractional viewport clip to the
destination rasterizer. It carries clip bounds through pixmap tiles, applies
them to non-antialiased spans and raster composites, and intersects the clip
with supersampled path coverage before reducing coverage to output pixels.
The exact five changed files, upstream file hashes, patched file hashes, full
200-file upstream inventory, crate archive hash, and independently pinned
patch-set digest are recorded and checked by the adjacent supply-chain
scripts. These changes establish bounded fractional clipping behavior; they
do not by themselves establish pixel parity with Edge.
