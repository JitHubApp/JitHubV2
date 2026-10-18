namespace MarkdownRenderer.Sample;

internal static partial class SampleDocuments
{
    internal const string SelectionSample = """
        ## DOM-Style Text Selection

        Click and drag to select any text in this rendered document.
        The selection spans across different element types seamlessly.

        ### Try selecting across these:

        - A list item with **bold** and *italic* text
        - Another item with `inline code` inside it
        - A third item with a [link to GitHub](https://github.com)

        > A blockquote with some text inside it.

        Once you have a selection, press **Ctrl+C** to copy.

        The default clipboard payload contains **rendered plain text** plus rich
        `CF_HTML`, so pasting into either a text editor or a rich editor is useful.
        Choose **Copy as Markdown** from the target-aware context menu when you
        need the original `.md` source for the selected region.

        ### How it works

        Each rendered character position maps back to a half-open UTF-16 source
        range. When you copy:

        1. The rendered selection is resolved across block and inline boundaries
        2. Rendered text and a sanitized rich HTML fragment are placed on the clipboard
        3. **Copy as Markdown** uses the source map to preserve the original syntax

        Press **Ctrl+A** to select the entire document.
        """;

    internal const string TouchSelectionSample = """
        # Touch selection

        Use one finger to scroll. **Press and hold a word** or **double-tap it**
        to select it, then drag either round handle to extend the range. Release
        a long press—or tap inside an existing selection—to open the native Copy,
        Copy as Markdown, and Select All menu.

        A plain tap keeps its normal meaning: [this link remains touchable](https://example.invalid/touch-link),
        while tapping elsewhere clears an active selection.

        ## Unicode and mixed direction

        Drag across emoji and combining text without splitting a visible character:
        👩🏽‍💻 family 👨‍👩‍👧‍👦 and café written as café.

        Mixed bidi text remains logically selectable: English before العربية שלום
        and English after. اسحب مقابض التحديد عبر هذا السطر لاختبار الاتجاه المختلط.

        ## Formatting boundaries

        Selection crosses **bold words**, *emphasized words*, `inline code`, and
        [linked labels](https://learn.microsoft.com/windows/apps/design/input/).

        | Surface | Expected touch behavior |
        |---|---|
        | Body text | Hold or double-tap selects a word |
        | Link label | Hold selects; tap activates |
        | Table text | Handles cross cell content atomically |
        | Hosted control | Receives direct touch input |
        | Diagram | Non-selectable regions remain direct interaction targets |

        - [ ] Native task checkbox remains independently touchable

        ```button:Touch_hosted_action
        ```

        ```csharp
        // Code remains selectable, while its Copy button remains a native control.
        string greeting = "Touch selection stays grapheme-safe";
        ```

        ```mermaid
        flowchart LR
            Pan[One-finger pan] --> Hold[Long press]
            Hold --> Word[Word selection]
            Word --> Handles[44 epx handles]
            Handles --> Copy[Native menu]
        ```

        ## Edge-scroll exercise

        Select a word above, drag the end handle into the bottom edge band, and
        hold your finger still. The viewport must continue scrolling while the
        endpoint extends. Repeat upward after crossing the handles.

        1. This paragraph provides distance for continuous edge scrolling.
        2. Selection must continue across list items and inline formatting.
        3. The document surface must not relayout or repaint its tiles per frame.
        4. Scroll inertia must remain owned by the platform ScrollViewer.
        5. Canceling the gesture must leave no capture, timer, flyout, or glyph residue.

        > Touch testing should also cover Light, Dark, Windows High Contrast,
        > 100%, 150%, and 200% display scaling, narrow snap widths, and RTL flow.

        Final selection target: **stationary edge dragging continues here**.
        """;

    internal const string KeyboardNavSample = """
        # Keyboard Navigation

        The renderer supports full keyboard navigation without a mouse:

        | Key | Action |
        |-----|--------|
        | **Tab** | Move focus to the next link or embedded control |
        | **Shift+Tab** | Move focus to the previous link or embedded control |
        | **Enter** or **Space** | Activate the focused link (fires `LinkClick`) |
        | **Escape** | Clear the keyboard focus ring (or text selection) |
        | **Ctrl+A** | Select all text |
        | **Ctrl+C** | Copy rendered plain text plus rich `CF_HTML` |

        ---

        ## Links to navigate with Tab

        Click in the renderer below to give it keyboard focus, then press
        **Tab** to cycle through the links and **Enter** to follow one.

        1. [GitHub](https://github.com) — open source home
        2. [Microsoft](https://microsoft.com) — WinUI and Win2D
        3. [CommonMark](https://spec.commonmark.org/0.31.2/) — the core specification
        4. [Win2D](https://github.com/microsoft/Win2D) — DirectWrite canvas

        ---

        > **Tip:** A focus ring (accent-colored border) appears around the
        > currently focused link. Pressing Escape clears it and returns focus
        > traversal to the start.

        ---

        ## GitHub README footnotes[^kn1][^kn2]

        Use Tab to reach each reference and press Enter to jump to its definition.
        From the definition, Tab to the ↩ link and press Enter to return.

        [^kn1]: First keyboard-nav footnote.
        [^kn2]: Second keyboard-nav footnote. Press Enter on ↩ to return.
        """;

    internal const string RtlSample = """
        # دعم الكتابة من اليمين إلى اليسار

        This page demonstrates **right-to-left (RTL)** layout. Toggle the **↔ RTL**
        button in the toolbar to flip the document's flow direction. Notice how:

        - The accent bar of blockquotes flips to the right edge.
        - List bullets and numbers move to the right side of the line.
        - Table columns reverse order so the first column reads first.
        - Inline mixed-bidi text shapes correctly (Arabic + English).

        > هذا اقتباس باللغة العربية. The quote bar should appear on the right
        > side when RTL is active, and on the left otherwise.

        ## قائمة (List)

        1. العنصر الأول — مع نص إنجليزي *italic* and **bold**
        2. العنصر الثاني — with `inline code`
        3. العنصر الثالث — and a [hyperlink to github.com](https://github.com)

        ## جدول (Table)

        | الاسم  | العمر | المدينة |
        | ------ | ----- | ------- |
        | أحمد   | 32    | القاهرة |
        | فاطمة  | 28    | دبي     |
        | سارة   | 41    | الرياض  |
        """;

    internal const string LazyImagesSample = """
        # Lazy Image Loading

        These four deterministic data-URI SVG fixtures exercise viewport-demand
        image work. Their bytes are part of this document: this sample makes no
        HTTP requests. Image rendering and publication are deferred until each
        resource approaches the renderer's **800 px** overscan band.

        **How to observe this:**
        1. Start at the top and note the image placeholders farther down.
        2. Scroll toward each fixture; its local SVG resource is decoded and
           published as it enters the demand band.
        3. Compare the square, portrait, and wide fixtures to see their intrinsic
           aspect ratios after the image resource is ready.

        For remote-image policy, use **Images and SVG**. Its HTTPS examples are
        intentionally blocked because this standalone sample does not provide
        an image resolver.

        ---

        ## Images below fold

        Scroll down to see each local fixture appear as you approach it.

        ![Square blue concentric-circle SVG fixture](data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSI5NiIgaGVpZ2h0PSI5NiIgdmlld0JveD0iMCAwIDk2IDk2Ij48cmVjdCB3aWR0aD0iOTYiIGhlaWdodD0iOTYiIHJ4PSIxMiIgZmlsbD0iI2RiZWFmZSIvPjxjaXJjbGUgY3g9IjQ4IiBjeT0iNDgiIHI9IjI4IiBmaWxsPSIjMjU2M2ViIi8+PGNpcmNsZSBjeD0iNDgiIGN5PSI0OCIgcj0iMTIiIGZpbGw9IiNiZmRiZmUiLz48L3N2Zz4=)

        Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod
        tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim
        veniam, quis nostrud exercitation ullamco laboris.

        ![Portrait purple-and-gold SVG fixture](data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSI3MiIgaGVpZ2h0PSIxMjAiIHZpZXdCb3g9IjAgMCA3MiAxMjAiPjxyZWN0IHdpZHRoPSI3MiIgaGVpZ2h0PSIxMjAiIHJ4PSIxMiIgZmlsbD0iI2VkZTlmZSIvPjxyZWN0IHg9IjEyIiB5PSIxMiIgd2lkdGg9IjQ4IiBoZWlnaHQ9Ijk2IiByeD0iOCIgZmlsbD0iIzdjM2FlZCIvPjxjaXJjbGUgY3g9IjM2IiBjeT0iNDIiIHI9IjEyIiBmaWxsPSIjZmVmM2M3Ii8+PHBhdGggZD0iTTIwIDg4IDM2IDY0bDE2IDI0IiBmaWxsPSJub25lIiBzdHJva2U9IiNjNGI1ZmQiIHN0cm9rZS13aWR0aD0iOCIgc3Ryb2tlLWxpbmVjYXA9InJvdW5kIiBzdHJva2UtbGluZWpvaW49InJvdW5kIi8+PC9zdmc+)

        Duis aute irure dolor in reprehenderit in voluptate velit esse cillum
        dolore eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat non
        proident, sunt in culpa qui officia deserunt mollit anim id est laborum.

        ![Wide blue landscape SVG fixture](data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSIxOTIiIGhlaWdodD0iOTYiIHZpZXdCb3g9IjAgMCAxOTIgOTYiPjxyZWN0IHdpZHRoPSIxOTIiIGhlaWdodD0iOTYiIHJ4PSIxMiIgZmlsbD0iI2UwZjJmZSIvPjxjaXJjbGUgY3g9IjE0OCIgY3k9IjI4IiByPSIxNCIgZmlsbD0iI2Y1OWUwYiIvPjxwYXRoIGQ9Ik0wIDc1IDQ4IDM2bDQyIDM4IDM0LTI4IDY4IDQ4SDBaIiBmaWxsPSIjMDI4NGM3Ii8+PHBhdGggZD0iTTAgODMgNDggNThsNDMgMjQgMzQtMTQgNjcgMjhIMFoiIGZpbGw9IiMwMzY5YTEiLz48L3N2Zz4=)

        Curabitur pretium tincidunt lacus. Nulla gravida orci a odio. Nullam
        varius, turpis molestie dictum semper, arcu felis fermentum metus.

        ![Wide teal icon-strip SVG fixture](data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSIyNTYiIGhlaWdodD0iNjQiIHZpZXdCb3g9IjAgMCAyNTYgNjQiPjxyZWN0IHdpZHRoPSIyNTYiIGhlaWdodD0iNjQiIHJ4PSIxMCIgZmlsbD0iI2YxZjVmOSIvPjxnIGZpbGw9IiMwZjc2NmUiPjxyZWN0IHg9IjE2IiB5PSIxNiIgd2lkdGg9IjMyIiBoZWlnaHQ9IjMyIiByeD0iNiIvPjxjaXJjbGUgY3g9IjgwIiBjeT0iMzIiIHI9IjE2IiBmaWxsPSIjMTRiOGE2Ii8+PHBhdGggZD0ibTExOCAxNiAxNiAxNi0xNiAxNi03LTcgOS05LTktOXoiLz48cmVjdCB4PSIxNTgiIHk9IjE4IiB3aWR0aD0iODIiIGhlaWdodD0iOCIgcng9IjQiIGZpbGw9IiM2NDc0OGIiLz48cmVjdCB4PSIxNTgiIHk9IjM2IiB3aWR0aD0iNTgiIGhlaWdodD0iOCIgcng9IjQiIGZpbGw9IiM5NGEzYjgiLz48L2c+PC9zdmc+)

        Aliquam erat volutpat. Nam dui ligula, fringilla a, euismod sodales,
        sollicitudin vel, wisi. Morbi auctor lorem non justo.

        > **Tip:** Use the **Scroll Anchor** sample to see how the layout
        > re-stabilises when an image resource completes and its measured size
        > causes the document layout to change.

        """;

    internal const string ScrollAnchorSample = """
        # Scroll Anchoring

        This guide explains the deterministic data-URI SVG fixtures in **Lazy
        Images**. Open that page to try them; they do not make network requests.
        When a fixture reaches the
        viewport-demand band, image work can complete and its measured aspect
        ratio can change the document layout. Scroll anchoring preserves the
        reading position across that rebuild.

        **How to observe this:**
        1. Use **Lazy Images** and scroll until a fixture is near the viewport.
        2. Compare the image placeholder with the ready square, portrait, or
           wide fixture and note any resulting layout change.
        3. Return to a nearby paragraph; its viewport position should remain
           stable when an image-size update triggers a rebuild.

        ---

        ## Implementation details

        The anchor is captured in `RebuildInternalAsync` just before the
        old snapshot is disposed:

        ```csharp
        // Before snapshot swap
        if (scroll.VerticalOffset > 0)
        {
            foreach (var b in prevSnapshot.Blocks)
            {
                if (b.Bounds.Bottom >= scroll.VerticalOffset)
                {
                    anchor = (b.BlockIndex, b.Bounds.Top - scroll.VerticalOffset);
                    break;
                }
            }
        }
        ```

        After the new snapshot is committed and `_canvas.Height` updated,
        the corresponding block is located in the new layout and the offset
        restored with `ScrollViewer.ChangeView(disableAnimation: true)`.

        The animation is disabled so there is **no visual flash** — the
        viewport jumps instantly to the correct position before the next
        frame is painted.

        ---

        Use the **Lazy Images** fixtures to exercise anchoring without depending
        on remote access or external image availability.
        """;
}
