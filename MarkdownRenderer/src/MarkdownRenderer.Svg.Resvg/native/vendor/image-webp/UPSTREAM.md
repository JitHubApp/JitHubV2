# Vendored image-webp provenance

This directory is based on the published `image-webp` 0.2.4 crate from
`image-rs/image-webp`, pinned to revision
`08b57d473c90148dd4b37a075df365afc3dfcc2b`. The original crate archive
SHA-256 is `525e9ff3e1a4be2fbea1fdf0e98686a6d98b4d8f937e1bf7402245af1909e8c3`.
The upstream license expression is `MIT OR Apache-2.0`; the local bounded
decoder additions are MIT-licensed and are listed separately in the adjacent
provenance manifest.

The local patch adds request-scoped allocation accounting, bounded static
image admission, source-resolution straight-RGBA decoding, optional bounded
ICC/EXIF metadata ranges, and bounded resizing. Legacy decoder behavior stays
separate from the metadata-aware API. The source inventory, all changed-file
hashes, added-file license evidence, and independently pinned patch digest are
checked by the provider supply-chain verifier. No captured corpus images or
browser screenshots are included in this vendor package.
