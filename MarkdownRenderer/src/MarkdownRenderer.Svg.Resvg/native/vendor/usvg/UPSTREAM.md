# Vendored usvg provenance

This directory is the `crates/usvg` package from the pinned resvg repository
revision `68b14c4c3bccdb60344c777406486b54c36ec1a4`. The source subtree at that
revision is Git tree `bfeeb1c4e58b9789206e356144ece6e16c591c3a` and declares
`usvg` version `0.48.1` under `Apache-2.0 OR MIT`.

The local patch is intentionally limited to four files, whose original-source
and patched-content hashes are recorded in `UPSTREAM.json`:

- `Cargo.toml`: make the upstream workspace manifest self-contained while
  preserving its package identity, features, and dependency versions.
- `src/parser/text.rs`, `src/tree/text.rs`, and `src/text/layout.rs`: preserve
  computed text direction and nested `unicode-bidi` scopes through shaping,
  keep source byte ranges available for glyph/style mapping, and apply
  direction-aware `text-anchor` offsets. Negative aggregate advance is clamped
  only for anchor measurement, matching Edge; negative cluster advances retain
  their glyphs and still control glyph placement so tightly tracked text can
  overlap backward as in Edge. Font fallback shapes the full bidi run but
  replaces only complete missing-glyph clusters with an exact matching source
  byte range, preserving primary-font glyphs and rejecting ambiguous
  cross-cluster substitutions.

The supply-chain generator and verifier validate the complete source-file
inventory against the pinned upstream revision, the exact four-file patch set,
and the dual-license evidence. Updating the upstream revision or patch requires
reviewed changes to those checks; local Cargo path dependencies are not generally
accepted.
