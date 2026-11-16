# MPL 2.0 source availability

`MarkdownRenderer.Mermaid.Native.dll` statically links the following unmodified
Rust crates under the Mozilla Public License 2.0:

| Package | Version | Exact source |
|---|---:|---|
| cssparser | 0.36.0 | https://crates.io/crates/cssparser/0.36.0 |
| cssparser-macros | 0.6.1 | https://crates.io/crates/cssparser-macros/0.6.1 |
| dtoa-short | 0.3.5 | https://crates.io/crates/dtoa-short/0.3.5 |
| selectors | 0.37.0 | https://crates.io/crates/selectors/0.37.0 |

Their exact resolved versions are also recorded in `NATIVE_RUST_DEPENDENCIES.json`
and `native/Cargo.lock`. The source is available from the links above under the
Mozilla Public License 2.0: https://www.mozilla.org/MPL/2.0/

JitHub and MarkdownRenderer do not modify these crates. The MPL applies to the
MPL-covered source files; the surrounding Larger Work remains under its stated
MIT license.
