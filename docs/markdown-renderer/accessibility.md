# Accessibility

The renderer is designed to expose native UI Automation rather than browser DOM
accessibility. This is a major requirement for production maturity.

## Current support

Implemented:

- `MarkdownAutomationPeer` exposes the control as `AutomationControlType.Document`;
- the root peer implements `ITextProvider` and exposes `ITextRangeProvider`
  ranges for document text, visible ranges, selection, range-from-point,
  text search, word/line/paragraph movement, bounding rectangles, children, and
  scroll-into-view;
- `ITextProvider.RangeFromChild` maps UIA child providers back to semantic text
  ranges for links, images, table/list nodes, and hosted WinUI embeds;
- `ITextRangeProvider.GetAttributeValue` and `FindAttribute` expose common
  native text attributes: read-only/hidden/active/culture/flow direction, font
  family/size/weight, foreground/background color, underline, strikethrough,
  style id/name, and superscript;
- document text is built from a semantic model over the committed layout
  snapshot and is also aggregated into the root peer name with a length cap;
- `MarkdownBlockPeer` exposes inline-container blocks such as paragraphs,
  headings, code blocks, list content, and table cell text;
- headings expose `GetHeadingLevelCore`;
- `MarkdownLinkPeer` implements `IInvokeProvider` for links;
- link activation through UIA raises the same control path as pointer activation;
- link peers report tight text-run bounds rather than the whole renderer;
- list and list-item peers expose native UIA `List` / `ListItem` control types;
- table and cell peers expose `Table`, `Grid`, `TableItem`, and `GridItem`
  patterns with row/column/header metadata;
- image peers expose alt text / SVG title as name and SVG description as help text;
- fenced code block language info is exposed as help text;
- keyboard focus ordering coordinates painted links and hosted WinUI embeds;
- hosted WinUI embeds expose their normal XAML UIA peers through the visual tree.

## Current peer model

```text
MarkdownAutomationPeer (Document)
  MarkdownBlockPeer (paragraph/heading/code)
    MarkdownLinkPeer (Invoke)
  MarkdownNodePeer (List)
    MarkdownNodePeer (ListItem)
      MarkdownBlockPeer (...)
  MarkdownNodePeer (Table: Grid + Table)
    MarkdownNodePeer (TableCell: GridItem + TableItem)
      MarkdownBlockPeer (...)
  MarkdownNodePeer (Image)
  Hosted WinUI element peers through XAML visual tree and TextRange children
```

The root peer builds a semantic accessibility model from the committed
`LayoutSnapshot`. Block peers are cached by `InlineContainerBox` identity and
link peers by `LinkRun` identity; semantic node peers are cached for the current
snapshot.

## Remaining gaps

The foundational UIA surface is now in place. Remaining work is depth and
real-world validation:

- manual Narrator smoke across Windows contrast themes and system languages;
- deeper `ITextRangeProvider` attribute coverage for advanced attributes not
  represented in the markdown style model, such as annotation objects, tabs,
  margins, and paragraph spacing;
- arrow-key spatial navigation and pointer-resume semantics that match native
  controls in more edge cases;
- richer row-header modeling for future table syntaxes that support row headers;
- broader verification around virtualized embeds and offscreen text ranges.

## Accessibility testing

Current automation checks verify tree shape, TextPattern document text/ranges/
bounds/selection, `RangeFromChild`, text attributes, heading/list/table/image/link
roles, table dimensions, hosted button focus order, keyboard traversal, focus
dismissal, deterministic forced high contrast, and selection reliability. Manual
smoke should still cover Narrator phrasing and all built-in/custom Windows
contrast themes because those are OS-environment dependent.

