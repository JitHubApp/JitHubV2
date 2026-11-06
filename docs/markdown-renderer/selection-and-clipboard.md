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

Code blocks use preformatted HTML; inline code carries a whitespace-preserving
declaration. Authored indentation, tabs, repeated spaces, and final blank code
lines remain part of the selected text. Generated terminal block separators are
not extra content. Rich-paste appearance still depends on the receiving app's
HTML/CSS support.

Selected tables retain their rows, columns, and header cells in formatted copy.
A partial selection includes only selected cell text, while empty cells retain
the column positions within the selected rows. Authored breaks and preformatted
cell content are preserved; generated cell separators are not extra content.
The fragment is built from the committed semantic table, not by reparsing source
HTML or copying unselected cells' text and link destinations.

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

Parser-owned code content retains exact authored UTF-16 positions through
indentation removal and line-ending normalization, including code in disclosure
summaries and supported HTML preformatted content. A displayed LF representing
an authored CRLF maps to both original source units. Internal text-layout chunks
do not change the selected source range or split Unicode graphemes.

## Hosted elements and accessibility

Viewport-realized WinUI elements remain interactive and expose their normal UIA
peers. The viewer coordinates selection drag, focus order, and source ranges
around them. Release validation must still cover Narrator, high contrast,
cross-block selection, formatted paste, and explicit source copy in real target
applications.
