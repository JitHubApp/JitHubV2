using Microsoft.Graphics.Canvas;

namespace MarkdownRenderer.GitHub.Tests;

internal sealed class GatedResourceCreator(CanvasDevice device) : ICanvasResourceCreator, IDisposable
{
    private readonly ManualResetEventSlim _release = new();
    private readonly TaskCompletionSource _entered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _gateEntered;

    internal Func<bool>? ShouldGate { get; set; }
    internal Task Entered => _entered.Task;

    public CanvasDevice Device
    {
        get
        {
            if (ShouldGate?.Invoke() == true &&
                Interlocked.Exchange(ref _gateEntered, 1) == 0)
            {
                _entered.TrySetResult();
                if (!_release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The test resource-creation gate was not released.");
            }
            return device;
        }
    }

    internal void Release() => _release.Set();
    public void Dispose() => _release.Dispose();
}
