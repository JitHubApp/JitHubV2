namespace MarkdownRenderer.Sample;

internal static partial class SampleDocuments
{
    internal const string TypographySample = """
        # Heading 1 — The quick brown fox
        ## Heading 2 — The quick brown fox
        ### Heading 3 — The quick brown fox
        #### Heading 4 — The quick brown fox
        ##### Heading 5 — The quick brown fox
        ###### Heading 6 — The quick brown fox

        ---

        Regular paragraph text. Lorem ipsum dolor sit amet, consectetur adipiscing elit.
        Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.

        **Bold text** is rendered with semibold weight.
        *Italic text* is rendered with italic style.
        ***Bold and italic*** combined.
        ~~Strikethrough~~ for deleted content.
        Inline `code span` uses a monospace font with a subtle background.

        A [hyperlink](https://github.com/JitHubApp/JitHubV2) with underline decoration.
        An autolink: <https://microsoft.com>

        > A blockquote with an accent bar on the left.
        > It can span multiple lines and contain **inline formatting**.
        >
        > Multiple paragraphs in a blockquote are supported.

        ---

        Thematic breaks (`---`) divide sections.
        """;

    internal const string ListsSample = """
        ## Unordered Lists

        - First item
        - Second item with **bold** text
        - Third item with `inline code`
          - Nested level 2
          - Another nested item
            - Nested level 3
        - Back to top level

        ## Ordered Lists

        1. First step
        2. Second step with [a link](https://example.com)
        3. Third step
           1. Sub-step A
           2. Sub-step B
        4. Fourth step

        ## Task Lists (GFM)

        - [x] Design the layout engine
        - [x] Implement Win2D painter
        - [x] Threading safety with ThemeSnapshot
        - [x] Full ITextProvider accessibility
        - [ ] Performance profiling on real documents

        ## Mixed content in list items

        - Item with a paragraph

          Followed by a second paragraph inside the same list item.

        - Another item

        1. An ordered item containing a code block:

           ```csharp
           int sum = Enumerable.Range(1, 10).Sum();
           ```

        2. And back to normal.
        """;

    internal const string TablesSample = """
        ## GFM Tables

        | Feature | Status | Notes |
        |---------|--------|-------|
        | Headings H1–H6 | ✅ Done | Win11 typography tokens |
        | Bold / Italic | ✅ Done | Per-run style spans |
        | Inline code | ✅ Done | Monospace + bg fill |
        | Code blocks | ✅ Done | Syntax-agnostic |
        | Blockquotes | ✅ Done | Accent bar |
        | Unordered lists | ✅ Done | 2-column ListItemBox |
        | Ordered lists | ✅ Done | Numeric marker |
        | Task lists | ✅ Done | Read-only document semantics by default |
        | Tables | ✅ Done | Intrinsic columns + local overflow |
        | GitHub README alerts | Optional pack | Enabled in this showcase |
        | Footnotes | Optional pack | Enabled in this showcase |
        | Thematic breaks | ✅ Done | Styled separator |
        | Links | ✅ Done | Click-to-launch |
        | Selection + Copy | ✅ Done | Rendered text + `CF_HTML`; Markdown is explicit |
        | AOT compatibility | ✅ Done | Explicit grammar provider, exact-type dispatch |
        | Live theme switch | ✅ Done | No reload needed |

        ## Smaller Table

        | Name | Type | Default |
        |------|------|---------|
        | `Markdown` | `string` | `""` |
        | `Theme` | `MarkdownTheme` | Win11 defaults |
        | `Engine` | `MarkdownEngine` | Shared immutable CommonMark engine |
        | Viewer control | `MarkdownScrollView` | Owns one viewport |
        | Embedded view | `MarkdownDocumentView` | Uses its ancestor's viewport |
        | `FlowDirection` | `FlowDirection` | `LeftToRight` |
        """;

    internal const string CodeSample = """
        ## Fenced Code Blocks

        C# with a filename, line numbers, and highlighted lines:

        ```csharp filename="MarkdownRendererControl.cs" {3,8-10} startLine=120
        public sealed class MarkdownRendererControl : UserControl
        {
            private volatile LayoutSnapshot? _snapshot;

            public string Markdown
            {
                get => (string)GetValue(MarkdownProperty);
                set => SetValue(MarkdownProperty, value);
            }

            private async Task RebuildAsync(CancellationToken ct)
            {
                var device = CanvasDevice.GetSharedDevice();
                var ctx = new MarkdownLayoutContext(device, themeSnapshot, sourceMap, registry, FlowDirection);
                _snapshot = await Task.Run(() => builder.Build(doc, width), ct);
                _canvas.Invalidate();
            }
        }
        ```

        TypeScript with a title:

        ```ts title="Async preview model"
        type RenderState = "idle" | "loading" | "ready";

        export async function renderMarkdown(source: string): Promise<RenderState> {
            const response = await fetch("/api/markdown", { method: "POST", body: source });
            return response.ok ? "ready" : "idle";
        }
        ```

        Python example without line numbers:

        ```python noLineNumbers
        import asyncio

        async def render_markdown(text: str) -> LayoutSnapshot:
            pipeline = build_pipeline()
            document = await asyncio.to_thread(pipeline.parse, text)
            return await asyncio.to_thread(layout_builder.build, document)
        ```

        PowerShell example:

        ```powershell filename="build.ps1"
        dotnet test .\MarkdownRenderer\MarkdownRenderer.Tests\MarkdownRenderer.Tests.csproj -p:Platform=x64
        dotnet build .\MarkdownRenderer\MarkdownRenderer.Sample\MarkdownRenderer.Sample.csproj -p:Platform=x64
        ```

        JSON example:

        ```json
        {
          "renderer": "MarkdownRenderer",
          "codeBlockVersion": 2,
          "syntaxHighlighting": true
        }
        ```

        Diff example:

        ```diff
        - plain shaded text box
        + native code surface
        + syntax highlighting
        + copy actions
        ```

        Long line with block-local horizontal scrolling (the default is no-wrap):

        ```js filename="long-line.js"
        export const message = "This deliberately long line demonstrates that code blocks preserve readable columns and use local horizontal scrolling only when they overflow.";
        ```

        Indented code block (4 spaces):

            var x = 42;
            Console.WriteLine($"The answer is {x}");

        Inline `code` uses a background highlight.
        """;

    internal const string ImagesSample = """
        # Images, captions, and SVG

        Raster images decode asynchronously; SVGs use the configured SVG
        renderer. Layout can update when an image's intrinsic dimensions become
        known. **Alt-text becomes a caption** rendered under the image so screen
        readers and sighted readers see the same description.

        This standalone sample intentionally has no image resolver. Data-URI
        images below use bytes embedded in the document. HTTPS examples are
        policy demonstrations: they are blocked and show the unavailable-image
        fallback without making a network request. A host app must provide an
        `IMarkdownImageResolver` to load remote images.

        ## Local raster fixtures (data: URI)

        These fixtures are embedded in the document, so they decode without a
        resolver or network: a PNG with transparency, a JPEG whose EXIF
        orientation rotates it to portrait, a static GIF and a lossy WebP with an
        alpha channel.

        ![Local PNG with alpha, 64×40](data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEAAAAAoCAYAAABOzvzpAAAAVElEQVR42u3QAQ0AIAzAsB9VaEEgWnB1ZDyBzsCSZqxT0dme2bkf8XkAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAODlLnVKA1BQMA6iAAAAAElFTkSuQmCC)

        ![Local JPEG with EXIF orientation 6, displayed 40×64](data:image/jpeg;base64,/9j/4AAQSkZJRgABAQAAAQABAAD/4QAiRXhpZgAATU0AKgAAAAgAAQESAAMAAAABAAYAAAAAAAD/2wBDAAIBAQEBAQIBAQECAgICAgQDAgICAgUEBAMEBgUGBgYFBgYGBwkIBgcJBwYGCAsICQoKCgoKBggLDAsKDAkKCgr/2wBDAQICAgICAgUDAwUKBwYHCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgr/wAARCAAoAEADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD5booor+fz/XwKKKKAPUKKKK9g/wCYsKKKKAPL6KKK8c/6dAooooA9Qooor2D/AJiwooooA8vooorxz/p0CiiigD1CiiivYP8AmLCiiigD/9k=)

        ![Local static GIF, 48×48](data:image/gif;base64,R0lGODdhMAAwAIEAAHgooAAAAAAAAAAAACwAAAAAMAAwAEAITwABCBxIsKDBgwgTKlzIsKHDhxAjSpxIsaLFixgzatzIsaPHjyBDihxJsqTJkyhTqlzJsqXLlzBjypxJs6bNmzhz6tzJs6fPn0CDCh2qMSAAOw==)

        ![Local static WebP with alpha, 64×40](data:image/webp;base64,UklGRooAAABXRUJQVlA4WAoAAAAQAAAAPwAAJwAAQUxQSBQAAAABDzD/ERFCLJjMXzqFzoj+RxV8A1ZQOCBQAAAAEAQAnQEqQAAoAD4xFolDIiEhFVQAIAMEsYBnq0A/AAArnw2esZYz+gAA/vCwN//jduMN+C7//HNvjm3xzb/jWfP3d+ROVAJ3uXaCZey0AAA=)

        ## Animated images (static first canvas, no playback)

        Animated images are presented as their composed first canvas. The first
        frame of each fixture is a centered blue square on a transparent 60×60
        canvas; the second frame is solid red and must never appear.

        ![Animated GIF first canvas, 60×60 with a centered blue square](data:image/gif;base64,R0lGODlhPAA8AIEAAAAAAABayNweHgAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQFMgAAACwUABQAFAAUAAAC4kyYMGHChAkTJkyYMGHChAkTJkyYMGHChAkTJkyYMGHChAkTJkyYMGHChAkTJkyYMGHChAkTJkyYMGHChAkTJkyYMGHChAkTJkyYMGHChAkTJkyYMGHChAkTJkyYMGHChAkTJkyYMGHChAkTJkyYMGHChAkTJkyYMGHChAkTJkyYMGHChAkTJkyYMGHChAkTJkyYMGHChAkTJkyYMGHChAkTJkyYMGHChAkTJkyYMGHChAkTJkyYMGHChAkTJkyYMGHChAkTJkyYMGHChAkTJkyYMGHChAkTJkyYMGHChAkTJiwAIfkEBDIAAAAsAAAAADwAPAAAAv+UKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFH/okSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJ/xIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSv+UKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFH/okSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJ/xIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSpQoUaJEiRIlSv+UKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFGiRIkSJUqUKFHxokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKlChRokSJEiVKLAA7)

        ![Animated WebP first canvas, 60×60 with a centered blue square](data:image/webp;base64,UklGRoYAAABXRUJQVlA4WAoAAAACAAAAOwAAOwAAQU5JTQYAAAAAAAAAAABBTk1GKAAAAAoAAAoAABMAABMAAPQBAAJWUDhMEAAAAC8TwAQAB1CtKHL/AxHR/wBBTk1GKgAAAAAAAAAAADsAADsAAPQBAABWUDhMEQAAAC87wA4AB1CPctej/4GI6H8AAA==)

        ## Remote PNG policy example (blocked without a host resolver)

        ![The GitHub Octocat — square avatar PNG, 460×460](https://avatars.githubusercontent.com/u/583231?v=4)

        ## Inline SVG (data: URI, base64)

        ![A blue 64×64 circle drawn entirely in SVG](data:image/svg+xml;base64,PHN2ZyB4bWxucz0naHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmcnIHdpZHRoPSc2NCcgaGVpZ2h0PSc2NCcgdmlld0JveD0nMCAwIDY0IDY0Jz48Y2lyY2xlIGN4PSczMicgY3k9JzMyJyByPScyOCcgZmlsbD0nIzAwNzhENCcvPjwvc3ZnPg==)

        ## Inline SVG (data: URI, percent-encoded)

        ![A red 48×48 square drawn entirely in SVG](data:image/svg+xml;utf8,%3Csvg%20xmlns%3D%27http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%27%20width%3D%2748%27%20height%3D%2748%27%20viewBox%3D%270%200%2048%2048%27%3E%3Crect%20width%3D%2748%27%20height%3D%2748%27%20fill%3D%27%23D13438%27%2F%3E%3C%2Fsvg%3E)

        ## Inline SVG with filter (data: URI, isolated resvg worker)

        ![A blurred green diamond rendered with `<feGaussianBlur>`](data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSI4MCIgaGVpZ2h0PSI4MCIgdmlld0JveD0iMCAwIDgwIDgwIj48ZGVmcz48ZmlsdGVyIGlkPSJiIj48ZmVHYXVzc2lhbkJsdXIgc3RkRGV2aWF0aW9uPSIzIi8+PC9maWx0ZXI+PC9kZWZzPjxyZWN0IHg9IjE1IiB5PSIxNSIgd2lkdGg9IjUwIiBoZWlnaHQ9IjUwIiB0cmFuc2Zvcm09InJvdGF0ZSg0NSA0MCA0MCkiIGZpbGw9IiMxMDdDMTAiIGZpbHRlcj0idXJsKCNiKSIvPjwvc3ZnPg==)

        ## Remote SVG policy example (blocked without a host resolver)

        ![GitHub site hero glow — a wide SVG decoration from github.com](https://github.githubassets.com/images/modules/site/home/hero-glow.svg)

        ## Remote URL fallback (not a network/404 test)

        ![A 1×1 placeholder showing a friendly error icon](https://example.invalid/does-not-exist.png)

        Captions wrap normally and respect the document's flow direction.
        """;
}
