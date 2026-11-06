using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Tracks renderer-created boxes and staged inline resources until their parent or published snapshot has
/// accepted ownership. Only the current synchronous build uses this ledger.
/// </summary>
internal sealed class MarkdownLayoutConstructionScope : IDisposable
{
    private readonly MarkdownLayoutContext _context;
    private HashSet<object>? _roots = new(ReferenceEqualityComparer.Instance);
    private int _disposed;

    internal MarkdownLayoutConstructionScope(MarkdownLayoutContext context)
    {
        _context = context;
    }

    internal void Register(object resource)
    {
        if (Volatile.Read(ref _disposed) != 0 || _roots is not { } roots || !roots.Add(resource))
            return;
    }

    /// <summary>
    /// A snapshot of the unadopted roots of type <typeparamref name="T"/> this
    /// scope would retire if it ended now; empty once the scope has ended.
    /// </summary>
    internal List<T> GetOwnedRoots<T>() where T : class
    {
        var owned = new List<T>();
        if (_roots is { } roots)
        {
            foreach (object root in roots)
                if (root is T typed)
                    owned.Add(typed);
        }
        return owned;
    }

    internal void Adopt(object child) => _roots?.Remove(child);

    internal void Forget(BlockBox block) => _roots?.Remove(block);

    internal void TransferToSnapshot(IReadOnlyList<BlockBox> publishedRoots)
    {
        ArgumentNullException.ThrowIfNull(publishedRoots);
        if (_roots is null || _roots.Count == 0 || publishedRoots.Count == 0)
            return;

        // Some built-in renderers attach children directly instead of going
        // through a builder helper that can explicitly adopt each child. Walk
        // only the actual snapshot-owned graph, removing those boxes from the
        // construction ledger while leaving genuinely unpublished objects for
        // Dispose() to retire.
        var visited = new HashSet<BlockBox>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<BlockBox>(publishedRoots.Count);
        for (int index = 0; index < publishedRoots.Count; index++)
            pending.Push(publishedRoots[index]);

        while (pending.Count > 0)
        {
            BlockBox current = pending.Pop();
            if (!visited.Add(current))
                continue;

            _roots.Remove(current);
            switch (current)
            {
                case StackBox stack:
                    for (int index = 0; index < stack.Children.Count; index++)
                        pending.Push(stack.Children[index]);
                    break;
                case ListItemBox listItem:
                    pending.Push(listItem.Marker);
                    pending.Push(listItem.Content);
                    break;
                case CodeBlockBox codeBlock:
                    for (int index = 0; index < codeBlock.Chunks.Count; index++)
                        pending.Push(codeBlock.Chunks[index]);
                    break;
                case TableBox table:
                    foreach (InlineContainerBox cell in table.GetCellBoxes())
                        pending.Push(cell);
                    break;
                case InlineContainerBox inline:
                    IReadOnlyList<InlineRun> runs = inline.Runs;
                    for (int index = 0; index < runs.Count; index++)
                        if (runs[index] is InlineVectorSceneRun vector)
                            _roots.Remove(vector);
                    IReadOnlyList<(InlineImageRun Run, int Offset)> images = inline.InlineImageRuns;
                    for (int index = 0; index < images.Count; index++)
                        pending.Push(images[index].Run.Image);
                    break;
            }
        }
    }

    internal List<Task> DisposeRootsBestEffort()
    {
        var retirementTasks = new List<Task>();
        HashSet<object>? roots = _roots;
        if (roots is null || roots.Count == 0)
            return retirementTasks;

        object[] pendingRoots = new object[roots.Count];
        roots.CopyTo(pendingRoots);
        roots.Clear();
        foreach (object root in pendingRoots)
        {
            try
            {
                if (root is BlockBox block)
                    block.CollectRetirementTasks(retirementTasks);
            }
            catch (Exception exception)
            {
                retirementTasks.Add(Task.FromException(exception));
            }
        }

        foreach (object root in pendingRoots)
        {
            try
            {
                if (root is BlockBox block)
                    block.Dispose();
                else
                    ((IDisposable)root).Dispose();
            }
            catch (Exception exception)
            {
                retirementTasks.Add(Task.FromException(exception));
            }
        }

        return retirementTasks;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            // This is a last-resort guard for future build paths that forget to
            // explicitly abort or transfer. Normal callers drain returned
            // barriers in their failure path before leaving the scope.
            foreach (Task task in DisposeRootsBestEffort())
            {
                try
                {
                    _context.RegisterRetirement(task);
                }
                catch
                {
                    // Teardown remains best-effort and never masks construction failure.
                }
            }
        }
        finally
        {
            _roots = null;
            _context.EndConstructionScope(this);
        }
    }
}
