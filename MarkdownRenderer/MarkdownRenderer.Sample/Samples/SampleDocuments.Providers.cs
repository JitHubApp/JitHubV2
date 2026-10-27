namespace MarkdownRenderer.Sample;

internal static partial class SampleDocuments
{
    // One section per optional provider. The Providers page swaps the engine,
    // grammar provider, hosted-control factory and optional providers while
    // this document is live; with minimal providers every section must keep
    // its documented safe-text or original-source fallback.
    internal const string ProvidersSample = """
        # Provider configuration

        The commands above this preview replace the engine, the grammar
        provider and the hosted-control factory while the document stays live,
        remove every optional provider, or add a second view that shares them.

        ## Safe HTML

        <details>
        <summary>Provider HTML summary</summary>

        Provider HTML body text.

        </details>

        ## Math

        Inline provider math $a^2 + b^2 = c^2$ stays in its sentence.

        $$
        \int_0^1 x\,dx = \frac{1}{2}
        $$

        ## Mermaid

        ```mermaid
        flowchart LR
          ProviderStart --> ProviderEnd
        ```

        ## Highlighted code

        ```csharp
        public sealed class ProviderProbe : IDisposable { }
        ```

        ```python
        provider_probe = 42
        ```

        ## SVG

        ![Provider SVG probe](data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSI4MCIgaGVpZ2h0PSI0OCI+PHJlY3Qgd2lkdGg9IjgwIiBoZWlnaHQ9IjQ4IiBmaWxsPSIjMGI4YTNjIi8+PC9zdmc+)

        ## Hosted control

        ```button:Provider_action
        ```

        Provider page end.
        """;
}
