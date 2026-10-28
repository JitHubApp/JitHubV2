using System;
using System.Collections.Generic;
using MarkdownRenderer.Controls;

namespace MarkdownRenderer.Selection;

internal static class MarkdownSelectionCoordinator
{
    private static readonly object Gate = new();
    private static readonly List<WeakReference<MarkdownRendererControl>> Controls = new();

    public static void Register(MarkdownRendererControl control)
    {
        lock (Gate)
        {
            PruneLocked();
            foreach (var weak in Controls)
            {
                if (weak.TryGetTarget(out var existing) && ReferenceEquals(existing, control))
                    return;
            }

            Controls.Add(new WeakReference<MarkdownRendererControl>(control));
        }
    }

    public static void Unregister(MarkdownRendererControl control)
    {
        lock (Gate)
        {
            Controls.RemoveAll(weak =>
                !weak.TryGetTarget(out var existing) ||
                ReferenceEquals(existing, control));
        }
    }

    public static void ClearSelectionsExcept(MarkdownRendererControl? owner)
    {
        MarkdownRendererControl[] snapshot;
        lock (Gate)
        {
            PruneLocked();
            var live = new List<MarkdownRendererControl>(Controls.Count);
            foreach (var weak in Controls)
            {
                if (weak.TryGetTarget(out var control) &&
                    !ReferenceEquals(control, owner))
                {
                    live.Add(control);
                }
            }

            snapshot = live.ToArray();
        }

        foreach (var control in snapshot)
            control.ClearSelectionFromCoordinator();
    }

    private static void PruneLocked()
    {
        Controls.RemoveAll(static weak => !weak.TryGetTarget(out _));
    }
}
