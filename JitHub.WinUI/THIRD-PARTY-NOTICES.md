# JitHub third-party notices

JitHub is distributed under the MIT License. The MarkdownRenderer feature stack
used by JitHub includes the following reviewed components:

- MarkdownRenderer libraries: MIT
- resvg 0.48.1 isolated SVG renderer and its reviewed Rust dependencies: MIT,
  Apache-2.0, BSD, ISC, and other permissive licenses recorded in the packaged
  resvg notice, provenance, and native dependency inventory
- TextMateSharp, Onigwrap, and the curated TextMate grammars: MIT, BSD,
  Apache-2.0, and other permissive bundle licenses recorded in the packaged
  TextMate notice and provenance inventory
- CSharpMath and Typography-derived math rendering source: MIT and Apache-2.0;
  bundled math fonts use the GUST Font License or SIL Open Font License 1.1
- Merman 0.8.0-alpha.6 and its source-derived Mermaid compatibility components:
  MIT, Apache-2.0, ISC, and other permissive licenses recorded in its packaged
  notice and native dependency inventory
- Four unmodified Rust CSS parsing crates in the Mermaid native binary:
  MPL-2.0. Their exact source locations and versions are provided in
  `ThirdPartyNotices/MarkdownRenderer.Mermaid/MPL2-SOURCE.md`.
- Microsoft WebView2 and its loader: BSD 3-Clause. Its license and bundled
  third-party notices are installed in `ThirdPartyNotices/Microsoft.Web.WebView2`.
- Microsoft Windows App SDK runtime components: Microsoft Windows App SDK
  redistribution terms. The exact license files and applicable bundled notices
  are installed in `ThirdPartyNotices/Microsoft.WindowsAppSDK`.

The corresponding license texts and notices, together with the available source
provenance and dependency inventories, are installed in the `ThirdPartyNotices`
directory beside this file. The EPL-licensed optional ELK layout is not built or
distributed by JitHub.
