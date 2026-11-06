using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Attempts every owned-resource cleanup in a container before surfacing
/// failures. This is used only on retirement paths, so the success path is a
/// small value type with no allocation; exception storage is bounded and is
/// allocated only after the first cleanup failure.
/// </summary>
internal struct BlockBoxCleanupScope
{
    private const int MaximumFailureDetails = 8;
    private const int MaximumAggregateDepth = 8;

    private List<Exception>? _failures;
    private bool _hasAdditionalFailures;

    internal void TryDispose(BlockBox? box)
    {
        if (box is null)
            return;

        try
        {
            box.Dispose();
        }
        catch (Exception exception)
        {
            RecordFailure(exception);
        }
    }

    internal void TryDispose(IDisposable? resource)
    {
        if (resource is null)
            return;

        try
        {
            resource.Dispose();
        }
        catch (Exception exception)
        {
            RecordFailure(exception);
        }
    }

    internal void TryDisposeAndClear<T>(ref T? resource)
        where T : class, IDisposable
        => TryDisposeAndClear(ref resource, disposer: null);

    internal void TryDisposeAndClear<T>(ref T? resource, Action<IDisposable>? disposer)
        where T : class, IDisposable
    {
        T? current = resource;
        if (current is null)
            return;

        try
        {
            if (disposer is null)
                current.Dispose();
            else
                disposer(current);
            resource = null;
        }
        catch (Exception exception)
        {
            // Keep the resource reachable by its owner so a later retirement
            // attempt can retry cleanup. Clearing is deliberately conditional
            // on successful Dispose; a throwing native dispose has unknown
            // ownership state.
            RecordFailure(exception);
        }
    }

    internal void TryCollectRetirementTasks(BlockBox? box, List<Task> pendingTasks)
    {
        if (box is null)
            return;

        try
        {
            box.CollectRetirementTasks(pendingTasks);
        }
        catch (Exception exception)
        {
            RecordFailure(exception);
        }
    }

    /// <summary>Disposes every box of a row-major grid, such as a table's cells.</summary>
    internal void TryDisposeRows<T>(T[][] rows)
        where T : BlockBox
    {
        for (int row = 0; row < rows.Length; row++)
        {
            T[] boxes = rows[row];
            for (int column = 0; column < boxes.Length; column++)
                TryDispose(boxes[column]);
        }
    }

    /// <summary>Collects the retirement tasks of every box of a row-major grid.</summary>
    internal void TryCollectRetirementTasks<T>(T[][] rows, List<Task> pendingTasks)
        where T : BlockBox
    {
        for (int row = 0; row < rows.Length; row++)
        {
            T[] boxes = rows[row];
            for (int column = 0; column < boxes.Length; column++)
                TryCollectRetirementTasks(boxes[column], pendingTasks);
        }
    }

    internal void ThrowIfAny(string operation)
    {
        if (_failures is not { Count: > 0 } failures)
            return;

        if (failures.Count == 1 && !_hasAdditionalFailures)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();

        if (_hasAdditionalFailures)
        {
            failures.Add(new InvalidOperationException(
                "Additional resource-retirement failures were omitted."));
        }

        throw new AggregateException(
            $"One or more errors occurred while {operation}.",
            failures);
    }

    internal void RecordFailure(Exception exception)
    {
        if (_hasAdditionalFailures)
            return;

        if (exception is not AggregateException aggregate || aggregate.InnerExceptions.Count == 0)
        {
            RecordLeafFailure(exception);
            return;
        }

        // Flatten only a bounded prefix. AggregateException may contain many
        // nested failures; retaining it as one item would bypass our detail
        // bound and could keep an arbitrarily large exception graph alive.
        var pending = new Stack<AggregateFailureCursor>(MaximumAggregateDepth);
        pending.Push(new AggregateFailureCursor(aggregate.InnerExceptions, 0, depth: 0));
        while (true)
        {
            AggregateFailureCursor cursor = pending.Pop();
            Exception current = cursor.InnerExceptions[cursor.NextIndex];
            int nextIndex = cursor.NextIndex + 1;
            if (nextIndex < cursor.InnerExceptions.Count)
                pending.Push(new AggregateFailureCursor(cursor.InnerExceptions, nextIndex, cursor.Depth));

            if (current is AggregateException nested && nested.InnerExceptions.Count > 0)
            {
                int nestedDepth = cursor.Depth + 1;
                if (nestedDepth >= MaximumAggregateDepth)
                {
                    RecordLeafFailure(new InvalidOperationException(
                        "Nested resource-retirement failures exceeded the diagnostic depth limit."));
                    _hasAdditionalFailures = true;
                }
                else
                {
                    pending.Push(new AggregateFailureCursor(nested.InnerExceptions, 0, nestedDepth));
                }
            }
            else
            {
                RecordLeafFailure(current);
            }

            if (_hasAdditionalFailures || pending.Count == 0)
                return;
        }
    }

    private void RecordLeafFailure(Exception exception)
    {
        _failures ??= new List<Exception>(MaximumFailureDetails + 1);
        if (_failures.Count < MaximumFailureDetails)
        {
            _failures.Add(exception);
            return;
        }

        _hasAdditionalFailures = true;
    }

    private readonly struct AggregateFailureCursor
    {
        internal AggregateFailureCursor(
            IReadOnlyList<Exception> innerExceptions,
            int nextIndex,
            int depth)
        {
            InnerExceptions = innerExceptions;
            NextIndex = nextIndex;
            Depth = depth;
        }

        internal readonly IReadOnlyList<Exception> InnerExceptions;
        internal readonly int NextIndex;
        internal readonly int Depth;
    }
}
