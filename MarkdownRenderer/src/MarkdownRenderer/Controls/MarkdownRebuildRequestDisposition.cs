namespace MarkdownRenderer.Controls;

/// <summary>What a rebuild request does to the pipeline currently being built.</summary>
internal enum MarkdownRebuildRequestDisposition
{
    Deferred,
    Queue,
    Supersede,
}
