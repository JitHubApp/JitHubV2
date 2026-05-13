# Accessibility

The renderer is designed to expose native UI Automation rather than browser DOM
accessibility. This is a major requirement for production maturity.

## Current support

Implemented:

- `MarkdownAutomationPeer` exposes the control as `AutomationControlType.Document`;
- document text is aggregated into the root peer name with a length cap;
- `MarkdownBlockPeer` exposes inline-container blocks such as paragraphs,
  headings, code blocks, list content, and table cell text;
- `MarkdownLinkPeer` implements `IInvokeProvider` for links;
- link activation through UIA raises the same control path as pointer activation;
- image alt text and SVG title/description are included in aggregated text;
- keyboard focus ring supports link/embed navigation;
- hosted WinUI embeds expose their normal XAML UIA peers through the visual tree.

## Current peer model

```text
MarkdownAutomationPeer (Document)
  MarkdownBlockPeer (paragraph/heading/list marker/table cell/etc.)
    MarkdownLinkPeer (Invoke)
  Hosted WinUI element peers through XAML visual tree
```

The root peer walks the committed `LayoutSnapshot` and creates stable block peers
using weak-table caching. Link peers are also cached by `LinkRun` identity.

## Screen-reader limitations

The current accessibility layer is useful but incomplete:

- no `ITextProvider`;
- no `ITextRangeProvider`;
- no selection pattern;
- heading levels are not fully exposed through `GetHeadingLevelCore`;
- lists are not represented as List/ListItem control types;
- tables do not implement `ITableProvider` / `ITableItemProvider`;
- code block language information is not exposed;
- image and caption semantics are flattened into text;
- hosted WinUI embeds and painted markdown are not yet bridged into one coherent
  semantic tree.

## Required maturity work

### Text pattern

Implementing `ITextProvider` and `ITextRangeProvider` is the largest accessibility
task. It should allow assistive technology to:

- retrieve text ranges;
- move by character, word, line, paragraph, and document;
- find text;
- retrieve bounding rectangles;
- query selection;
- map from screen point to text range.

The existing `DocumentPosition`, `DocumentRange`, `MarkdownSourceMap`, and
selection rectangle code provide useful foundations, but UIA requires a more
complete range abstraction.

### Heading levels

Heading blocks should expose the correct heading level. This likely requires
storing heading level metadata on `InlineContainerBox` or introducing a dedicated
heading box/semantic role.

### Tables

Tables need peers that preserve row/column/header relationships. A mature table
implementation should expose:

- row count;
- column count;
- row and column index per cell;
- header association;
- cell text ranges.

### Lists

List and list item semantics should be explicit. Markers should not be the only
way for assistive technology to infer list structure.

## Accessibility testing

Current automation checks verify tree shape, discoverability, keyboard traversal,
focus behavior, and selection effects. Future accessibility tests should include:

- Narrator/manual smoke checklist;
- UIA Text pattern compliance tests;
- table navigation tests;
- heading navigation tests;
- mixed RTL/LTR document tests;
- hosted embed focus order tests.

