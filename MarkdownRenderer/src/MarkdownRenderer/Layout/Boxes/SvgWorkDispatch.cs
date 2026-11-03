using System;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Keeps SVG preparation, Open and Render off the UI dispatcher. Work that
/// starts on the dispatcher thread moves to the thread pool; work already off
/// it runs inline.
/// </summary>
internal static class SvgWorkDispatch
{
    internal static Task RunOffDispatcher(bool dispatcherHasThreadAccess, Func<Task> runCore) =>
        dispatcherHasThreadAccess ? Task.Run(runCore, CancellationToken.None) : runCore();

    internal static Task<T> RunOffDispatcher<T>(bool dispatcherHasThreadAccess, Func<Task<T>> runCore) =>
        dispatcherHasThreadAccess ? Task.Run(runCore, CancellationToken.None) : runCore();
}
