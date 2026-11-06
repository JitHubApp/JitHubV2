using MarkdownRenderer.Layout;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A built root-local container document with math (vector) runs. It owns the
/// snapshot, its layout context and the parsing engine, retiring them in that
/// order on dispose.
/// </summary>
internal sealed class ContainerVectorDocument(
    MarkdownLayoutContext context,
    LayoutSnapshot snapshot,
    MarkdownEngine engine) : IAsyncDisposable
{
    internal MarkdownLayoutContext Context { get; } = context;
    internal LayoutSnapshot Snapshot { get; } = snapshot;
    private MarkdownEngine Engine { get; } = engine;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Snapshot.Retire().WaitAsync(ContainerRelayoutTestTree.AsyncDeadline);
        }
        finally
        {
            try
            {
                await Context.DrainOwnedRetirementsAsync().WaitAsync(ContainerRelayoutTestTree.AsyncDeadline);
            }
            finally
            {
                Engine.Dispose();
            }
        }
    }
}
