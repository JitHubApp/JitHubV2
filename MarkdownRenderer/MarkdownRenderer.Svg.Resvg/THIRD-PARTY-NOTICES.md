# Third-party notices

`MarkdownRenderer.Svg.Resvg` contains a statically linked Rust worker built
from resvg 0.48.1 and its Cargo dependency closure. resvg/usvg are used under
the MIT option of their `Apache-2.0 OR MIT` license. The exact upstream source
is commit `68b14c4c3bccdb60344c777406486b54c36ec1a4`.

The resvg MIT text is included at `licenses/RESVG-LICENSE-MIT.txt`.
`native/Cargo.lock` is the authoritative version/checksum graph and
`NATIVE_RUST_DEPENDENCIES.json` is the generated, per-package SBOM inventory.
`licenses/RUST-DEPENDENCY-LICENSES.txt` contains the deduplicated upstream
license and notice files for the entire locked closure, with SHA-256 evidence
links from every SBOM package record. That closure uses only
repository-compatible MIT, Apache-2.0, BSD-2-Clause, BSD-3-Clause, Zlib,
Unlicense/0BSD, and Unicode-3.0 terms. Dual-licensed dependencies are consumed
under MIT where offered.

No resvg trademark rights are granted. This product is not endorsed by the
resvg authors.
