using Microsoft.Graphics.Canvas;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Vectors;

/// <summary>
/// A resource creator that counts every <see cref="Device"/> request, so a
/// test can prove a path never reached a native factory.
/// </summary>
internal sealed class DeviceRequestCountingResourceCreator(CanvasDevice device) : ICanvasResourceCreator
{
    private int _deviceRequestCount;

    internal int DeviceRequestCount => Volatile.Read(ref _deviceRequestCount);

    public CanvasDevice Device
    {
        get
        {
            Interlocked.Increment(ref _deviceRequestCount);
            return device;
        }
    }
}
