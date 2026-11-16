# MarkdownRenderer.Math third-party notices

## CSharpMath

The internal TeX parser and typesetter are derived from CSharpMath v0.5.2,
commit `2d7dec98695dac6944027d90c73ec50fe45c5964`.

Copyright (c) 2017 William Jockusch, Hadrian Tang and contributors.
Licensed under the MIT License. See `licenses/CSHARPMATH-LICENSE.txt`.

## Typography

The internal CFF-only OpenType/MATH-table reader and glyph-outline adapter are
derived from the Typography submodule pinned by CSharpMath v0.5.2 at commit
`d331968f3ee714332a8ea4fa95a21c15812a8d93`.

Typography is MIT licensed as a whole and contains files retaining their
upstream MIT and Apache-2.0 notices. See `licenses/TYPOGRAPHY-LICENSE.md` and
the headers retained in the source snapshot.

Only the explicit source allowlist recorded by `licenses/PROVENANCE.json` is
compiled. WOFF/WOFF2, TrueType, bitmap/SVG/color, variable-font, and unrelated
general font readers are excluded from the package.

## Embedded fonts

- Latin Modern Math 1.959: copyright 2012–2014 B. Jackowski,
  P. Strzelczyk and P. Pianowski on behalf of TeX user groups. Licensed under
  the GUST Font License. See `licenses/GUST-FONT-LICENSE.txt`.
- AMS Capital Blackboard Bold 001.000: derived from
  AMSMathematicalSymbol-Medium5, copyright 1997 and 2009 American Mathematical
  Society, Reserved Font Name MSBM9; extracted by Hadrian Tang in 2018.
  Licensed under SIL Open Font License 1.1. See `licenses/OFL-1.1.txt`.
- Cyrillic Modern 4.002: copyright 1997 and 2009 American Mathematical Society,
  Reserved Font Name CMR10; copyright 2012–2014 Andrey V. Panov. Licensed
  under SIL Open Font License 1.1. See `licenses/OFL-1.1.txt`.

The exact source, submodule, and font hashes are recorded in
`licenses/PROVENANCE.json`.
