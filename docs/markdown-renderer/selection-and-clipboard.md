# Selection and clipboard

The native views support pointer and keyboard selection across rendered markdown.
Selection positions map to half-open UTF-16 source spans in the immutable
document, but the normal clipboard experience is presentation-oriented.

## Default copy

Keyboard Copy, the context-menu Copy action, and
`CopySelectionToClipboard()` with no options write:

- rendered semantic text as the plain-text payload;
- formatted `CF_HTML` when `IncludeHtml` is `true` (the default).

This makes a normal paste preserve readable text and, where supported, markdown
formatting such as emphasis, links, lists, and tables.

## Copy markdown source

Exact source is an explicit alternative, not the default:

```csharp
view.CopySelectionToClipboard(new MarkdownCopyOptions
{
    PlainTextMode = MarkdownPlainTextCopyMode.SourceMarkdown,
    IncludeHtml = true,
});
```

`CopySelectionAsMarkdown()` is the convenience method and the host command model
also distinguishes `CopyRendered` from `CopyMarkdown`.

Source copy uses the selected document range. Delimiter inclusion at selection
boundaries follows the source-map range represented by the selection; callers
should not describe arbitrary visual selections as a byte-for-byte recovery of
an entire original document.

## Hosted elements and accessibility

Viewport-realized WinUI elements remain interactive and expose their normal UIA
peers. The viewer coordinates selection drag, focus order, and source ranges
around them. Release validation must still cover Narrator, high contrast,
cross-block selection, formatted paste, and explicit source copy in real target
applications.
