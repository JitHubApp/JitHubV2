using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal sealed class WorkerContentFailureMarker(Action? callback) : IDisposable
{
    private Action? _callback = callback;

    public void InvokeOnce() => Interlocked.Exchange(ref _callback, null)?.Invoke();

    public void Dispose() => Interlocked.Exchange(ref _callback, null);
}
