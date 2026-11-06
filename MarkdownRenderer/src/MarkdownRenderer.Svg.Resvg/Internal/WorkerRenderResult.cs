using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal sealed class WorkerRenderResult : IDisposable
{
    private SharedMemoryLease? _outputMemory;

    internal WorkerRenderResult(WorkerResponse response, SharedMemoryLease outputMemory)
    {
        Response = response;
        _outputMemory = outputMemory ?? throw new ArgumentNullException(nameof(outputMemory));
    }

    internal WorkerResponse Response { get; }

    internal SharedMemoryLease TransferOutputMemory() =>
        Interlocked.Exchange(ref _outputMemory, null) ??
        throw new ObjectDisposedException(nameof(WorkerRenderResult));

    public void Dispose()
    {
        SharedMemoryLease? outputMemory = Interlocked.Exchange(ref _outputMemory, null);
        if (outputMemory is not null)
            ((IDisposable)outputMemory).Dispose();
    }
}
