# CSharpMath source snapshot and patch ledger

The package compiles an audited source snapshot instead of taking a transitive
CSharpMath prerelease dependency. The snapshot is pinned to stable tag v0.5.2,
commit `2d7dec98695dac6944027d90c73ec50fe45c5964`; its Typography submodule is
pinned to `d331968f3ee714332a8ea4fa95a21c15812a8d93`.

Only the CSharpMath parser/typesetter, CSharpMath.Rendering math backend, a
CFF-only Typography OpenType/MATH reader and outline builder, and three required
fonts compile. The explicit MSBuild allowlist contains 109 source files and
three fonts. Editor, text layout, WOFF/WOFF2, TrueType, bitmap/SVG/color and
variable-font readers, general shaping tables, examples, tests, image encoders,
Skia frontends, and all other platform frontends are excluded. This also removes
the upstream SVG reader's debug-only filesystem write path from the assembly.

Local changes are deliberately mechanical and narrow:

1. All vendored top-level and nested type declarations were changed from
   `public` to `internal`. Public members needed by the internal implementation
   remain unchanged. This prevents CSharpMath and Typography types from entering
   the package's exported surface.
2. Source files were normalized to UTF-8 and LF while internalizing declarations.
3. `CSharpMath.Rendering.BackEnd.Fonts` uses the closed, generated
   `MathFontResources.g.cs` registration table instead of discovering resource
   names. The three font bytes are otherwise unchanged and hash-locked.
4. `ThirdPartyAliases.cs` binds the upstream `Range` identifier to
   `CSharpMath.Atom.Range` when compiling against modern .NET, where
   `System.Range` also exists.
5. `ThirdParty/.editorconfig` suppresses only diagnostics present in the pinned
   legacy sources. The file is path-scoped to the snapshot; renderer-owned code
   still generates XML documentation and builds with warnings as errors.
6. The parser's main loop and character reader plus the typesetter's principal
   atom/glyph loops call a renderer-owned, thread-local cancellation checkpoint.
   This changes no accepted TeX or geometry; it lets superseded background work
   stop cooperatively without exposing CSharpMath types or callbacks.
7. Typography's reader, typeface, glyph, glyph-translator, and outline-builder
   files were reduced to the CFF1 operations exercised by the hash-locked font
   catalog. The reader accepts only seekable `OTTO` streams no larger than 1 MiB,
   at most 32 in-bounds tables and 8,192 glyphs, and requires only `head`, `maxp`,
   `hhea`, `hmtx`, `cmap`, `CFF `, and optional `MATH` tables.
8. Font loading uses a cancellation-aware, retryable initialization gate rather
   than a failure-poisoning static initializer. Generated resource names, exact
   byte lengths, and a 1 MiB aggregate input cap bound the first initialization.
   The big-endian reader, CFF evaluator, and glyph-bound pass checkpoint the
   active cooperative deadline.
9. Typography's mutable on-demand `cmap` dictionary was removed. Glyph lookup
   now scans the small immutable character-map array, eliminating an unbounded
   shared cache and making concurrent document processing safe.
10. `LaTeXParser.BuildInternal` enters a renderer-owned, thread-local recursion
    scope. The configured nesting budget is capped at a non-configurable safe
    maximum, so command-argument chains that recurse without braces cannot
    exhaust the native stack; exceeding the limit is reported as `MATH102`.

Apart from rejecting parser recursion beyond the documented resource limit, no
TeX parsing, typesetting, MATH-table layout, or CFF outline algorithm was
behaviorally changed. The font input surface was intentionally narrowed as
described above. The renderer adapter records vector outlines and rules into
immutable renderer-owned scene objects. It does not use Skia, SVG output,
WebView, JavaScript, or bitmap formula caches.
