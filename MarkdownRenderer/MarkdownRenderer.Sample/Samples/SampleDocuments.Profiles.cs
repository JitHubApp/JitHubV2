namespace MarkdownRenderer.Sample;

internal static partial class SampleDocuments
{
    internal const string AlertsSample = """
        ## GitHub README Alerts

        Alerts and footnotes are GitHub README additions, not part of strict
        GFM 0.29. This showcase composes the GitHub README profile, so each marker
        below is rendered as a native alert with its corresponding semantics.

        > [!NOTE]
        > Useful information that users should know, even when skimming content.

        > [!TIP]
        > Helpful advice for doing things better or more easily.

        > [!IMPORTANT]
        > Key information users need to know to achieve their goal.

        > [!WARNING]
        > Urgent info that needs immediate user attention to avoid problems.

        > [!CAUTION]
        > Advises about risks or negative outcomes of certain actions.

        ---

        A regular blockquote (not an alert) still works:

        > "The best performance optimization is eliminating unnecessary work."
        > — unknown

        ## Footnotes (GitHub README addition)

        The GitHub README profile renders references without cluttering the main
        text.[^1] Its footnotes expose navigable references and definitions.[^2]

        [^1]: This is the first footnote definition.
        [^2]: Superscript characters: ¹²³⁴⁵⁶⁷⁸⁹
        """;

    internal const string FootnotesSample = """
        # Footnote back-links

        This showcase uses the GitHub README profile. References become navigable
        markers and each definition receives a **↩** back-link to its inline
        citation.

        ---

        Here is a sentence with a footnote.[^1]

        And here is another with two more.[^2][^3]

        A longer paragraph that contains a reference to the first footnote
        again.[^1] And ends with a reference to the fourth.[^4]

        ---

        ## More content below footnotes

        This paragraph exists to make the fixture long enough to validate the
        pack's scrolling behavior.

        Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod
        tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim
        veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex
        ea commodo consequat.

        Duis aute irure dolor in reprehenderit in voluptate velit esse cillum
        dolore eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat non
        proident, sunt in culpa qui officia deserunt mollit anim id est laborum.

        ---

        [^1]: This is the **first** footnote. Click ↩ to return to the text.
        [^2]: Second footnote with `inline code`. Click ↩ to go back.
        [^3]: Third footnote — [a link](https://github.com). Click ↩ to return.
        [^4]: The fourth footnote contains a longer explanation that wraps onto
              multiple lines to demonstrate that the back-link renders correctly
              even in multi-line footnote definitions. Click ↩ to return.
        """;

    internal const string MarkdownExtraSample = """
        # Markdown Extra

        This page exercises opt-in syntax that is intentionally separate from
        strict GitHub-flavored markdown.

        ## Abbreviations

        HTML and SVG expansions are exposed to accessibility and document queries.

        *[HTML]: Hyper Text Markup Language
        *[SVG]: Scalable Vector Graphics

        ## Definition Lists

        Renderer
        :   A native WinUI control that parses markdown, lays it out off the UI
          thread, and paints it with Win2D.

        Extension
        :   A parser-independent `IMarkdownExtension` that emits declarative
          semantic content through `MarkdownExtensionBuilder`.

        ## Figure Candidate

        ![A small blue circle figure sample](data:image/svg+xml;utf8,%3Csvg%20xmlns%3D%27http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%27%20width%3D%2764%27%20height%3D%2764%27%20viewBox%3D%270%200%2064%2064%27%3E%3Ccircle%20cx%3D%2732%27%20cy%3D%2732%27%20r%3D%2724%27%20fill%3D%27%230078D4%27%2F%3E%3C%2Fsvg%3E "Blue circle")

        Figure: A caption rendered when the active profile emits figure semantics.
        """;

    internal const string HtmlSample = """
        # Native safe HTML

        The GitHub README profile preserves HTML candidates and the
        `MarkdownRenderer.Html` pack parses a bounded, non-executable subset.
        Supported markup joins the same native layout, selection, theming, and
        accessibility pipeline as Markdown—there is no browser or JavaScript VM.

        <section aria-label="Safe HTML overview">
        <h2>Semantic block and inline markup</h2>
        <p>HTML supports&nbsp;<strong>strong text</strong>;&nbsp;<em>emphasis</em>;&nbsp;<mark>marked text</mark>;&nbsp;<code>inline code</code>; and&nbsp;<kbd>Ctrl+C</kbd>.</p>
        <blockquote>A native blockquote remains selectable and theme-aware.</blockquote>
        <ul><li>Lists retain document semantics</li><li>Host-routed link:&nbsp;<a href="https://example.invalid/safe-html">safe HTML example</a>.</li></ul>
        </section>

        <details open>
        <summary>Native disclosure with ExpandCollapse semantics</summary>
        <p>HTML disclosure body: this content can be expanded and collapsed without a web view.</p>
        </details>

        <table>
        <caption>Native HTML table</caption>
        <thead><tr><th>Capability</th><th>Behavior</th></tr></thead>
        <tbody><tr><td>Tag-filtered scripts</td><td>Inert literal source</td></tr><tr><td>Frames and forms</td><td>Suppressed</td></tr><tr><td>Links and images</td><td>Host mediated</td></tr></tbody>
        </table>

        <figure>
        <img alt="Blue safe HTML sample square" src="data:image/svg+xml;utf8,%3Csvg%20xmlns%3D%27http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%27%20width%3D%2748%27%20height%3D%2748%27%20viewBox%3D%270%200%2048%2048%27%3E%3Crect%20width%3D%2748%27%20height%3D%2748%27%20rx%3D%278%27%20fill%3D%27%230078D4%27%2F%3E%3C%2Fsvg%3E">
        <figcaption>A data-URI image still goes through the host image pipeline.</figcaption>
        </figure>

        ## Security boundary

        Tag-filtered scripts and unknown elements remain visible as inert literal
        source. Frames and form controls are suppressed from rendering and the
        accessibility tree.

        <script>unsafe-script-sentinel</script>
        <iframe src="https://example.invalid/blocked">unsafe-frame-sentinel</iframe>
        <form><button onclick="alert('blocked')">unsafe-form-sentinel</button></form>
        <custom-card>Unknown element content remains visible as literal source.</custom-card>
        """;
}

