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

        Images are only fetched when they are within **800 px** of the current
        viewport (the "overscan band"). Images far off-screen are not loaded
        until the user scrolls close to them.

        **How to observe this:**
        1. Open DevTools / Fiddler and watch HTTP traffic.
        2. When you first load this page, only the top few images will fire
           requests. Scroll down to trigger additional loads.
        3. Images already in the cache (`BitmapCache`) always appear instantly —
           they bypass the lazy-load gate entirely.

        ---

        ## Images below fold

        Scroll down to see each image load as you approach it.

        ![GitHub Octocat](https://github.githubassets.com/images/icons/emoji/octocat.png)

        Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod
        tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim
        veniam, quis nostrud exercitation ullamco laboris.

        ![GitHub Logo](https://github.githubassets.com/assets/GitHub-Mark-ea2971cee799.png)

        Duis aute irure dolor in reprehenderit in voluptate velit esse cillum
        dolore eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat non
        proident, sunt in culpa qui officia deserunt mollit anim id est laborum.

        ![Copilot Icon](https://github.githubassets.com/assets/copilot-5a9b7f3d5c64.png)

        Curabitur pretium tincidunt lacus. Nulla gravida orci a odio. Nullam
        varius, turpis molestie dictum semper, arcu felis fermentum metus.

        ![Primer Octicons](https://github.githubassets.com/assets/primer-octicons.svg)

        Aliquam erat volutpat. Nam dui ligula, fringilla a, euismod sodales,
        sollicitudin vel, wisi. Morbi auctor lorem non justo.

        > **Tip:** Use the **Scroll Anchor** sample to see how the layout
        > re-stabilises after lazy images load and change the document height.

        """;

    internal const string ScrollAnchorSample = """
        # Scroll Anchoring

        When a lazy image finishes loading, the document height may change.
        Without scroll anchoring, the reading position would jump. With it,
        the renderer captures the first visible block before re-laying-out
        and restores the scroll offset so the user's reading position is
        preserved.

        **How to observe this:**
        1. Switch to the **Lazy Images** sample and scroll part-way down.
        2. The images above the fold finish loading and expand the document.
        3. Your reading position stays stable — the text you were reading
           doesn't move, even though the document height changed.

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

        This sample intentionally has no images. Use **Lazy Images** to see
        anchoring in action.
        """;
}
