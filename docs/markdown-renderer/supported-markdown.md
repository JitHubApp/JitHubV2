# Supported markdown

Support is compositional. Installing the `MarkdownRenderer` package does not
implicitly enable every syntax or native payload: its engine defaults to
CommonMark, and every other profile is an explicit builder call.

| Area | Package/profile | Current status |
| --- | --- | --- |
| CommonMark document/viewer baseline | `MarkdownRenderer` | Native headings, paragraphs, emphasis, code, quotes, lists, rules, links, images, selection, queries, and accessibility. Full conformance certification is still a 1.0 gate. |
| Strict GFM 0.29 | `MarkdownRenderer` + `UseGitHubFlavoredMarkdown()` (`MarkdownRenderer.Gfm`) | Tables, task lists, strikethrough, autolinks, and related native rendering. |
| Markdown Extra | `MarkdownRenderer` + `UseMarkdownExtra()` (`MarkdownRenderer.Gfm`) | Separate opt-in for definitions, abbreviations, figures, and extra inline forms. |
| GitHub README profile | `MarkdownRenderer` + `UseGitHubReadme()` (`MarkdownRenderer.GitHub`) | GFM plus alerts, footnotes, emoji, generic attributes, and safe-HTML composition. The GitHub profile/theme is opt-in; Fluent remains the base visual default. See [GitHub README profile](#github-readme-profile). |
| Safe HTML | `MarkdownRenderer` + `UseSafeHtml()` (`MarkdownRenderer.Html`) | Bounded native subset parser and painter, including inline tags and cross-block scopes. No script, CSS layout, browser DOM, or direct I/O capability. See [Safe HTML](#safe-html). |
| Math | `MarkdownRenderer.Math` | CSharpMath-based native vector typesetting, dollar delimiters, immutable scenes, accessibility, and lossless invalid-source fallback. |
| Mermaid | `MarkdownRenderer.Mermaid` | Selected-RID Merman engine, validated MMIR vector scenes, bounded processing and fallback. ELK is excluded. |
| SVG | `MarkdownRenderer.Svg.Resvg` | Optional isolated resvg 0.48.1 provider for x86, x64, and ARM64. The admitted static subset includes geometry, CSS/presentation attributes, text, gradients, patterns, markers, clipping, masks, filters, embedded data images, transforms, `viewBox`, `preserveAspectRatio`, `currentColor`, and inline light/dark color-scheme rules. Executable, external, unsupported, or over-budget input uses the atomic accessible fallback. |
| Syntax highlighting | `MarkdownRenderer.SyntaxHighlighting.TextMate` | Optional provider integration. Choose a separate grammar pack or provide your own grammar source. |

## Configure profiles

```csharp
using MarkdownEngine commonMark = new MarkdownEngineBuilder().Build();

using MarkdownEngine gfm = new MarkdownEngineBuilder()
    .UseGitHubFlavoredMarkdown()
    .Build();

using MarkdownEngine githubReadme = new MarkdownEngineBuilder()
    .UseGitHubReadme()
    .Build();
```

Profiles and extension sets are immutable after `Build()`. A view should use the
same engine that produced its assigned `MarkdownDocument`.

## GitHub README profile

The GitHub README profile (`MarkdownRenderer.GitHub` namespace) is opt-in. It
composes strict GFM with GitHub alerts, footnotes, emoji shortcodes, generic
attributes, and the safe-HTML capability profile.

```csharp
using MarkdownRenderer.GitHub;

var control = new MarkdownRendererControlBuilder()
    .UseGitHubReadme()
    .WithMarkdown(source)
    .BuildScrollView();
```

Use `BuildDocumentView()` when an ancestor owns the scroll viewport. The base
viewer follows WinUI/Fluent styling; the GitHub README profile is opt-in.

### Primer visual comparison profile

For a controlled Light-mode visual comparison, opt into the versioned Primer
profile on the one control being compared:

```csharp
var visualProfile = GitHubReadmeVisualStyleProfiles.PrimerLightV7;
var control = new MarkdownRendererControlBuilder()
    .UseGitHubReadme()
    .WithTheme(visualProfile.CreateTheme())
    .WithStyleSheet(visualProfile.StyleSheet)
    .WithCodeBlockChromeMode(visualProfile.CodeBlockChromeMode)
    .WithCodeBlockLineNumberMode(visualProfile.CodeBlockLineNumberMode)
    .WithCodeBlockCopyEnabled(visualProfile.IsCodeBlockCopyEnabled)
    .WithMarkdown(source)
    .BuildScrollView();
```

The profile ID is `github-primer-light`, version `7`; its fingerprint is the
SHA-256 of the canonical `github-primer-light-v7.css` bytes (source:
`MarkdownRenderer/src/MarkdownRenderer/GitHub/Profiles/`). Version 7 matches
table-cell line height, relative inline-code font size, and inline-image middle
alignment to that CSS. Record that fingerprint with visual evidence on both
renderers. This profile does not alter renderer defaults or the caller's
application theme, and High Contrast continues to use mandatory system colors.
It is a stable audit fixture, not a promise that native layout reproduces every
live GitHub website detail.

It retains version 6's selective inherited foreground matching inside block
quotes and the plain code-block presentation. Versions 1–6 retain their
original identities and behavior. Inline-code chip padding remains expressed
in DIPs; relative font sizing alone does not promise CSS em-padding parity.

For another theme, `styleSheet.WithRelativeScriptFontSize(0.75f)` opts into the
same proportional sizing without adopting this Light profile. Its appended rules
override inherited resource sizes; later explicit stylesheet font-size rules can
still override them. Nested scripts compound the factor, with a 0.1-DIP floor
for derived sizes. This sets the nominal font size; glyph substitution and
baseline positioning retain the existing font-dependent OpenType
subscript/superscript behavior. It does not establish CSS `vertical-align`
parity or full-page visual equivalence.

## Safe HTML

The `MarkdownRenderer.Html` namespace contains the native safe-HTML parser and
Win2D painter, immutable options, audited default resource budgets, and a stable
capability descriptor.

Safe HTML never executes script, applies CSS layout, exposes a browser DOM, or
performs direct filesystem/network access. The capability contract is
deliberately unable to enable any of these. Links and images are always declared
as host-mediated resources and continue to flow through the renderer's host
policies. The native safe-HTML subset parser and painter are still incomplete in
this preview.

Use `UseSafeHtml` with an engine profile that preserves HTML nodes; the GitHub
README profile does this automatically. CommonMark and strict GFM continue to
render raw HTML literally. The `MarkdownEngineBuilder.UseSafeHtml(options)`
overload freezes the policy with the engine; view/control overloads create an
immutable view-specific override when needed.

Configured budgets apply to HTML blocks, inline tags and cross-block scopes.
Inline node/input accounting starts at the first HTML tag in the inline
container; cross-block tag accounting is shared for the document's top-level
HTML scopes. Nesting counts actual elements, not a synthetic document root.
Over-budget content emits a localized omission notice. These rendering limits
do not replace the engine's overall Markdown source and parsing limits.

## Fallback policy

Unsupported, disabled, unavailable, timed-out, or invalid optional content must
degrade to safe semantic text or original source content. Engine implementation
does not imply full syntax parity or completion of the release-validation matrix.
