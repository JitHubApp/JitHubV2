using System.Runtime.CompilerServices;
using MarkdownRenderer.Extensions.Vectors;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Vectors;

/// <summary>
/// Weak references to a vector scene, its command list, first command and
/// first path, and the collection check that proves a terminally retired
/// owner no longer roots any of them.
/// </summary>
internal static class VectorSceneGraphLiveness
{
    internal sealed class WeakSceneGraph(
        WeakReference<MarkdownVectorScene> scene,
        WeakReference<IReadOnlyList<MarkdownVectorCommand>> commands,
        WeakReference<MarkdownVectorCommand> firstCommand,
        WeakReference<IReadOnlyList<MarkdownVectorPathOperation>> firstPath)
    {
        internal WeakReference<MarkdownVectorScene> Scene { get; } = scene;
        internal WeakReference<IReadOnlyList<MarkdownVectorCommand>> Commands { get; } = commands;
        internal WeakReference<MarkdownVectorCommand> FirstCommand { get; } = firstCommand;
        internal WeakReference<IReadOnlyList<MarkdownVectorPathOperation>> FirstPath { get; } = firstPath;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static WeakSceneGraph CreateWeakSceneGraph(MarkdownVectorScene scene)
    {
        MarkdownVectorCommand command = scene.Commands[0];
        return new WeakSceneGraph(
            new WeakReference<MarkdownVectorScene>(scene),
            new WeakReference<IReadOnlyList<MarkdownVectorCommand>>(scene.Commands),
            new WeakReference<MarkdownVectorCommand>(command),
            new WeakReference<IReadOnlyList<MarkdownVectorPathOperation>>(command.Path));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool IsSceneGraphAlive(WeakSceneGraph graph) =>
        graph.Scene.TryGetTarget(out _) ||
        graph.Commands.TryGetTarget(out _) ||
        graph.FirstCommand.TryGetTarget(out _) ||
        graph.FirstPath.TryGetTarget(out _);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void AssertSceneGraphCollected(
        WeakSceneGraph graph,
        params object[] owners)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!IsSceneGraphAlive(graph))
            {
                KeepOwnersAlive(owners);
                return;
            }
            Thread.Sleep(10);
        }

        KeepOwnersAlive(owners);
        Assert.False(
            IsSceneGraphAlive(graph),
            "A terminally retired owner retained the scene, command collection, command, or path graph.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void KeepOwnersAlive(object[] owners)
    {
        foreach (object owner in owners)
            GC.KeepAlive(owner);
    }
}
