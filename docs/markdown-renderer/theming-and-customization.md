# Theming and customization

The renderer uses a native object model for styling rather than CSS. Consumers
provide a `MarkdownTheme`, and the renderer resolves it against Windows 11
defaults and the current WinUI `ActualTheme`.

## Main types

| Type | Purpose |
| --- | --- |
| `MarkdownTheme` | Consumer-owned theme object with `AccentColor`, `Overrides`, `Revision`, and `Changed`. |
| `ElementStyle` | Fully resolved immutable-ish style used by layout and paint. |
| `ElementStyleOverride` | Nullable partial style override supplied by consumers. |
| `ThemeResolver` | Merges Win11 defaults, current light/dark theme, accent color, and overrides. |
| `ThemeSnapshot` | Resolved style set captured for a layout pass. |
| `MarkdownElementKeys` | Built-in style keys for markdown element categories. |

## Built-in element keys

Core keys:

- `Body`
- `Heading1` through `Heading6`
- `CodeInline`
- `CodeBlock`
- `Quote`
- `Link`
- `Strong`
- `Emphasis`
- `Strikethrough`
- `ListMarker`
- `ThematicBreak`
- `ImageCaption`

GFM keys:

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

theme.Overrides[MarkdownElementKeys.Link] = new ElementStyleOverride
{
    Foreground = Colors.MediumPurple,
    Underline = false,
};

renderer.Theme = theme;
theme.Invalidate();
```

## Dynamic theme switching

The control listens to `ActualThemeChanged`. If a `MarkdownTheme` is assigned,
the control calls `theme.Invalidate()`, which raises `Theme.Changed` and triggers
a rebuild. If no custom theme is assigned, it rebuilds directly.

Current behavior is correct but coarse: theme changes re-run parse and layout.
Future work should add a restyle-only path that reuses the parsed AST and only
rebuilds text metrics and colors.

## Important current limitation

`MarkdownTheme.Overrides` is a plain dictionary. Mutating it does not raise a
change notification automatically:

```csharp
theme.Overrides[MarkdownElementKeys.Link] = new ElementStyleOverride
{
    Underline = false,
};

theme.Invalidate(); // required today
```

A mature API should replace this with an observable collection or setter methods
that invalidate automatically.

## Styling gaps

The current theme model does not yet cover:

- link hover and focus variants;
- table alignment styling;
- list nesting depth styling;
- code syntax highlighting tokens;
- code block borders and radius;
- context-aware variants such as "Link inside Quote";
- CSS-like cascading or style composition;
- text shadow, letter spacing, and text transform.

