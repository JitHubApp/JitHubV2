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

Custom extensions may use any string key in `MarkdownTheme.Overrides`, including
the context, class, and identifier aliases produced by `MarkdownElementKeys`.
Those legacy lookup aliases are distinct from the stricter
`MarkdownStyleRole` identifiers used by style sheets and WinUI resource keys.

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

## WinUI resource overrides

Apps may place the stable keys from `MarkdownResourceKeys` in a control,
ancestor, or application resource dictionary. The resolver captures them into
the immutable theme snapshot on the UI thread; layout and paint never query
WinUI resources on a worker or per frame.

Relevant resource-key discovery is weakly cached per dictionary, while each
snapshot re-reads the current value for every discovered key. Adding or removing
a key normally refreshes discovery because the dictionary count changes. After
an advanced runtime edit that swaps one key for another without changing the
count, call `renderer.InvalidateThemeResources()`. This clears key discovery and
requests a restyle whether or not the renderer has a `MarkdownTheme` assigned.

Lookup follows WinUI dictionary precedence within each scope: the dictionary's
own value, merged dictionaries in reverse declaration order, then exactly one
selected theme dictionary. Theme selection tries `Light` or `Dark` and then
`Default`; in high contrast it tries the documented generic `HighContrast`
dictionary first, then the active `Light`/`Dark` dictionary, then `Default`.
Use the generic `HighContrast` key for renderer customizations; scheme names are
arbitrary/localized system values and are deliberately not guessed as
`HighContrastBlack`, `HighContrastWhite`, or `HighContrastCustom`.

Document-wide resources include:

- `DocumentPadding`: a `Thickness` around top-level content. Each component is
  constrained to 0–4096 DIPs.
- `BlockSpacing`: a non-negative numeric value inserted between adjacent
  top-level blocks, constrained to 0–1024 DIPs.
- `MinimumInteractiveSize`: a non-negative numeric value constrained to
  24–128 DIPs.
- `OverflowIndicatorBrush`: a `Color` or `SolidColorBrush` used by the local
  horizontal-overflow affordance on code, table, and vector blocks.

`MarkdownResourceKeys.ForRole(role, MarkdownStyleProperty.TextDecorations)`
accepts a `Windows.UI.Text.TextDecorations` value or its named string form and
maps `Underline` and `Strikethrough` independently. Invalid types and unknown
flags are ignored. In High Contrast, color resources and the overflow indicator
use mandatory system roles, and a decoration required by a semantic role cannot
be removed by an override.

Custom resource role names are canonical, case-sensitive identifiers of at most
128 characters. `MarkdownStyleRole` trims its constructor input, while a raw
`MarkdownRenderer.<role>.<property>` resource key must already contain the
canonical trimmed name. Control characters are rejected.
`Document`, `Selection`, `FocusVisual`, `Interaction`, and `Overflow` are
reserved global resource scopes and cannot be used as style roles.
These resource-role restrictions do not constrain legacy keys stored in
`MarkdownTheme.Overrides`; non-role aliases simply skip WinUI role-resource
projection and continue to receive their dictionary override.

## Dynamic theme switching

The control listens to `ActualThemeChanged` and resolves a fresh theme snapshot.
When a `MarkdownTheme` is assigned, the control raises its change notification
so every subscribing renderer can restyle; without one, it requests the restyle
directly. Theme switching does not flush resource-key discovery because it does
not change the dictionaries' key sets.

The control also listens to `Microsoft.UI.System.ThemeSettings.Changed` for the
current window. When Windows enters or leaves a contrast theme, the renderer
rebuilds against system colors such as Window, WindowText, Hotlight, Highlight,
and HighlightText. The canvas clear color comes from `ThemeSnapshot.SurfaceColor`
so high contrast does not leave a light/dark hardcoded background behind.

High contrast defaults avoid scheme-name-specific palettes. The role mapping
lives in `MarkdownHighContrastDefaults` and is unit-tested with deterministic
roles; the sample automation also forces a fake high-contrast palette and checks
the resulting UIA text attributes. When a contrast theme is active, renderer and
consumer color overrides are mapped onto mandatory system-color roles rather
than painted literally. Typography, spacing, and decoration overrides remain in
effect, while Window/WindowText, Hotlight, Highlight, and HighlightText preserve
the user's contrast scheme.

Theme changes can reuse the immutable parsed document when source and engine
configuration are unchanged. Layout/text metrics and colors are rebuilt for the
new environment. Real Windows contrast-theme smoke still needs to cover
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

`MarkdownTheme.Invalidate()` remains available when advanced mutations to the
theme object itself need an explicit change notification. It does not invalidate
WinUI resource-dictionary key discovery; use
`renderer.InvalidateThemeResources()` for that case.

## Styling non-goals

The current theme model intentionally does not cover:

- code syntax highlighting tokens;
- text shadow, letter spacing, and text transform.
