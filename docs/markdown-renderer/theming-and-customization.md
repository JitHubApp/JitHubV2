# Theming and customization

The renderer uses a native object model for styling rather than CSS. Consumers
provide a `MarkdownTheme`, and the renderer resolves it against Windows 11
defaults, the current WinUI `ActualTheme`, and Windows high contrast settings.

## Main types

| Type | Purpose |
| --- | --- |
| `MarkdownTheme` | Consumer-owned theme object with `AccentColor`, `Overrides`, `Revision`, and `Changed`. |
| `ElementStyle` | Fully resolved immutable-ish style used by layout and paint. |
| `ElementStyleOverride` | Nullable partial style override supplied by consumers. |
| `ThemeResolver` | Merges Win11 defaults, current light/dark theme, high contrast system colors, accent color, and overrides. |
| `ThemeSnapshot` | Resolved style set and surface color captured for a layout pass. |
| `MarkdownElementKeys` | Built-in style keys for markdown element categories. |

## Built-in element keys

Core keys:

- `Body`
- `Heading1` through `Heading6`
- `CodeInline`
- `CodeBlock`
- `CodeBlockHeader`
- `CodeBlockLanguage`
- `CodeBlockGutter`
- `CodeBlockLineNumber`
- `Quote`
- `Link`
- `Strong`
- `Emphasis`
- `Strikethrough`
- `ListMarker`
- `ThematicBreak`
- `ImageCaption`
- `Subscript`
- `Superscript`
- `Inserted`
- `Marked`
- `Abbreviation`
- `DefinitionTerm`
- `DefinitionDescription`
- `Figure`
- `FigureCaption`
- `Diagram`

GFM keys:

- `Table`
- `TableHeader`
- `TableCell`
- `AlertNote`
- `AlertTip`
- `AlertImportant`
- `AlertWarning`
- `AlertCaution`

Custom extensions may use any string key.

## Style properties

`ElementStyle` supports:

- font family;
- font size;
- font weight;
- font style;
- foreground color;
- optional background color;
- optional accent bar color;
- underline;
- strikethrough;
- hover foreground;
- focus foreground;
- border brush;
- border thickness;
- corner radius;
- list indent;
- nested list indent;
- margin;
- padding;
- line-height multiplier.

`ElementStyleOverride` mirrors these properties as nullable values so a consumer
can override one field without losing the rest of the default.

## Example

```csharp
var theme = new MarkdownTheme
{
    AccentColor = Colors.MediumPurple,
};

theme.Overrides[MarkdownElementKeys.CodeBlock] = new ElementStyleOverride
{
    Background = Color.FromArgb(0x22, 0x80, 0x80, 0x80),
    Padding = new Thickness(12),
};

theme.Overrides[MarkdownElementKeys.CodeBlockHeader] = new ElementStyleOverride
{
    Background = Color.FromArgb(0x10, 0x80, 0x80, 0x80),
};

theme.Overrides[MarkdownElementKeys.CodeBlockLanguage] = new ElementStyleOverride
{
    Foreground = Colors.Gray,
};

theme.Overrides[MarkdownElementKeys.CodeBlockLineNumber] = new ElementStyleOverride
{
    Foreground = Colors.DimGray,
};

theme.Overrides[MarkdownElementKeys.Link] = new ElementStyleOverride
{
    Foreground = Colors.MediumPurple,
    Underline = false,
};

renderer.Theme = theme;
```

## Dynamic theme switching

The control listens to `ActualThemeChanged`. If a `MarkdownTheme` is assigned,
the control calls `theme.Invalidate()`, which raises `Theme.Changed` and triggers
a rebuild. If no custom theme is assigned, it rebuilds directly.

The control also listens to `Microsoft.UI.System.ThemeSettings.Changed` for the
current window. When Windows enters or leaves a contrast theme, the renderer
rebuilds against system colors such as Window, WindowText, Hotlight, Highlight,
and HighlightText. The canvas clear color comes from `ThemeSnapshot.SurfaceColor`
so high contrast does not leave a light/dark hardcoded background behind.

High contrast defaults avoid scheme-name-specific palettes. The role mapping
lives in `MarkdownHighContrastDefaults` and is unit-tested with deterministic
roles; the sample automation also forces a fake high-contrast palette and checks
the resulting UIA text attributes. Consumer `MarkdownTheme` overrides are still
honored as explicit overrides, so app authors remain responsible for ensuring
custom colors meet contrast requirements.

Theme changes reuse the cached parsed AST when the markdown source and extension
registry revision are unchanged. Layout/text metrics and colors are rebuilt from
a fresh `ThemeSnapshot`. Real Windows contrast-theme smoke still needs to cover
every built-in theme plus customized palettes because those OS settings are
intrusive and environment-dependent.

## Override mutation

`MarkdownTheme.Overrides` is dictionary-shaped for source compatibility, but its
backing store is observable. Direct mutations raise `Changed` and trigger theme
rebuilds:

```csharp
theme.Overrides[MarkdownElementKeys.Link] = new ElementStyleOverride
{
    Underline = false,
};

// No explicit Invalidate call is required for normal mutations.
```

`MarkdownTheme.Invalidate()` remains available as an explicit escape hatch for
advanced callers.

## Styling non-goals

The current theme model intentionally does not cover:

- code syntax highlighting tokens;
- text shadow, letter spacing, and text transform.
