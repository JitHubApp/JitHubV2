using MarkdownRenderer.Images;

namespace MarkdownRenderer.Performance.Internal.Session;

/// <summary>Opens a document scope on its session; the session implements it explicitly.</summary>
internal interface IMarkdownDocumentScopeOpener
{
    MarkdownPerformanceDocumentScope OpenDocument(
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context);
}
