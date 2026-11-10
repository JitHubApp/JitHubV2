using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A built root-local container document that owns its snapshot and layout
/// context, retiring both on dispose.
/// </summary>
internal sealed class ContainerImageDocument(
    MarkdownLayoutContext context,
    LayoutSnapshot snapshot,
    MarkdownSourceMap sourceMap,
    ContainerResolver resolver) : IAsyncDisposable
{
    internal MarkdownLayoutContext Context { get; } = context;
    internal LayoutSnapshot Snapshot { get; } = snapshot;
    internal MarkdownSourceMap SourceMap { get; } = sourceMap;
    internal ContainerResolver Resolver { get; } = resolver;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Snapshot.Retire().WaitAsync(ContainerRelayoutTestTree.AsyncDeadline);
        }
        finally
        {
            await Context.DrainOwnedRetirementsAsync().WaitAsync(ContainerRelayoutTestTree.AsyncDeadline);
        }
    }
}
