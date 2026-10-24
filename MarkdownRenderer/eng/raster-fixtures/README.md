# Self-owned WIC fixture

This test-only MIT tool generates the oversized, nonuniform 2048 x 8193 WebP
fixture used by the WIC memory probe. It uses the same pinned MIT/Apache-2.0
`image-webp` 0.2.4 dependency already present in the resvg license corpus, not
downloaded artwork or a new shipping decoder. No generated full-size pixels
or encoder enter the renderer NuGet packages.

Regenerate from the repository root:

```powershell
cargo run --release --locked --offline --manifest-path MarkdownRenderer/eng/raster-fixtures/Cargo.toml -- MarkdownRenderer/MarkdownRenderer.GitHub.Tests/Fixtures/oversized-static.webp
```

The checked-in fixture and Cargo.lock are deterministic. The WIC probe must
exercise the installed Windows codec; this generator does not establish a
decoder peak-memory bound, an AVIF qualification, or browser parity.

The generated file is 51,112 bytes; its SHA-256 is
`6FEB51F257EF7FAC608FFA54E0D942EA4FE4DE11DECD165A12EA254C3886DB85`.
