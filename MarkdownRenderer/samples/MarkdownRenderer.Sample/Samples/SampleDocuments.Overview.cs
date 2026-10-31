namespace MarkdownRenderer.Sample;

internal static partial class SampleDocuments
{
    internal const string FullDemoSample = """
        # MarkdownRenderer

        A **fully native** Win2D + DirectWrite markdown renderer for WinUI 3.
        It turns source into a reusable immutable document, realizes indexed
        display bands for the active viewport, and paints with native APIs.

        ## Core Features

        - **Off-thread pipeline** — parsing and layout run away from the UI thread;
          only version-checked commits and painting run on it
        - **Native Fluent defaults** — Segoe UI Variable, semantic type, color,
          spacing, and High Contrast enforcement work with zero configuration
        - **Live theme switching** — light ↔ dark without reloading or re-parsing
        - **Explicit viewport ownership** — `MarkdownScrollView` owns one scrolling
          viewport; `MarkdownDocumentView` observes an ancestor-owned viewport
        - **Document selection** — `Ctrl+C` copies rendered text plus `CF_HTML`;
          **Copy as Markdown** preserves the original source
        - **GitHub README profile** — strict GFM plus alerts, footnotes, emoji,
          generic attributes, and bounded native safe HTML
        - **Markdown Extra opt-ins** — definition lists, abbreviations, and figures
        - **AOT-friendly configuration** — this sample supplies the TextMate Common
          grammar provider explicitly instead of discovering optional packs

        ---

        ## Inline Formatting

        **Bold**, *italic*, ***bold italic***, ~~strikethrough~~, `inline code`.
        A [hyperlink](https://github.com) with click-to-launch.

        ## Blockquote

        > Theming follows Win11 design tokens and switches with the system
        > automatically — no page reload, no flicker.

        ## Declarative Extensions

        > Extensions implement `IMarkdownExtension` and emit semantic text,
        > containers, tables, images, code, hosted controls, and accessibility
        > roles without receiving parser or Win2D lifetime objects.

        ## Task List

        - [x] Shared immutable engine and reusable documents
        - [x] Indexed, cancellable off-thread parsing and layout
        - [x] `MarkdownScrollView` and `MarkdownDocumentView` viewport contracts
        - [x] Win2D display-band painter
        - [x] Fluent, theme, text-scale, flow-direction, and DPI environment inputs
        - [x] Exact-type declarative extension dispatch
        - [x] GitHub README: strict GFM plus alerts, footnotes, emoji, and safe HTML
        - [x] Markdown Extra: definition lists, abbreviations, figures
        - [x] Document/TextPattern accessibility with stable semantic ranges
        - [x] Async, cancellation-aware TextMate syntax highlighting

        Task markers above describe read-only document state. They do not expose
        `TogglePattern` unless a host explicitly enables editable task behavior.

        ## Table

        | Layer | Technology | Purpose |
        |-------|-----------|---------|
        | Parsing | `MarkdownEngine` | Standards profile → immutable document |
        | Layout | Immutable display bands | Indexed viewport realization |
        | Rendering | Win2D / DirectWrite | GPU-accelerated text |
        | Theming | Fluent resources + stylesheet | Semantic tokens and rules |
        | Selection | UTF-16 source map | Rendered copy or explicit Markdown |

        ## Code

        ```csharp
        private readonly MarkdownEngine _engine = new MarkdownEngineBuilder()
            .UseGitHubReadme(SafeHtmlOptions.Default)
            .UseMarkdownExtra()
            .UseMathematics()
            .UseMermaid()
            .Build();

        private readonly TextMateCodeBlockSyntaxHighlighter _highlighter = new(
            new CommonTextMateGrammarProvider(),
            options: null,
            ownsProvider: true);

        private MarkdownScrollView CreatePreview(string markdownSource)
        {
            // This control owns its viewport. Inside an existing ScrollViewer,
            // create MarkdownDocumentView instead.
            var view = new MarkdownScrollView
            {
                Engine = _engine,
                Markdown = markdownSource,
                Theme = new MarkdownTheme(),
            };
            view
                .UseGitHubReadme(_engine, SafeHtmlOptions.Default)
                .UseMarkdownExtra(_engine)
                .UseSafeHtml(SafeHtmlOptions.Default);
            view.UseTextMateSyntaxHighlighting(_highlighter);
            return view;
        }

        // Dispose both _engine and _highlighter with the window that owns the
        // preview. The highlighter releases its grammar provider after admitted
        // work has exited.
        ```

        ---

        *Select any text above and press **Ctrl+C** to copy rendered text and rich HTML. Use **Copy as Markdown** when you need the exact source.*
        """;
}

