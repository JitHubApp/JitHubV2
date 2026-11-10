using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MarkdownRenderer.Layout.Internal.Boxes;

namespace MarkdownRenderer.Layout.Internal.Context;

/// <summary>
/// The active construction scope of one layout context. Builders register
/// newly-created block roots and staged resources here, adopt them once a parent
/// accepts ownership, and discard deliberately omitted ones. Registration is a
/// no-op while no scope is active.
/// </summary>
internal sealed class LayoutConstructionOwnership
{
    private readonly MarkdownLayoutContext _context;
    private MarkdownLayoutConstructionScope? _scope;

    internal LayoutConstructionOwnership(MarkdownLayoutContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Begins synchronous construction ownership for one layout pass. Builders
    /// register newly-created block roots here and remove them only after the
    /// parent successfully accepts ownership. The context is single-build at a
    /// time, matching its other mutable per-pass registries.
    /// </summary>
    internal MarkdownLayoutConstructionScope Begin()
    {
        if (_scope is not null)
            throw new InvalidOperationException("A layout construction scope is already active.");

        return _scope = new MarkdownLayoutConstructionScope(_context);
    }

    internal void RegisterBlock(BlockBox block)
    {
        ArgumentNullException.ThrowIfNull(block);
        _scope?.Register(block);
    }

    internal void RegisterResource(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _scope?.Register(resource);
    }

    internal void AdoptBlock(BlockBox child)
    {
        ArgumentNullException.ThrowIfNull(child);
        _scope?.Adopt(child);
    }

    internal void AdoptResource(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _scope?.Adopt(resource);
    }

    internal void ForgetBlock(BlockBox block)
    {
        ArgumentNullException.ThrowIfNull(block);
        _scope?.Forget(block);
    }

    /// <summary>Disposes a deliberately omitted build object and observes its async retirement.</summary>
    internal void DiscardBlock(BlockBox block)
    {
        ArgumentNullException.ThrowIfNull(block);
        ForgetBlock(block);
        var retirementTasks = new List<Task>();
        try
        {
            block.CollectRetirementTasks(retirementTasks);
        }
        catch (Exception exception)
        {
            retirementTasks.Add(Task.FromException(exception));
        }

        try
        {
            block.Dispose();
        }
        catch (Exception exception)
        {
            retirementTasks.Add(Task.FromException(exception));
        }

        foreach (Task task in retirementTasks)
        {
            try
            {
                _context.RegisterRetirement(task);
            }
            catch
            {
                // Discard is best-effort and must not replace a primary build failure.
            }
        }
    }

    internal void End(MarkdownLayoutConstructionScope scope)
    {
        if (ReferenceEquals(_scope, scope))
            _scope = null;
    }
}
