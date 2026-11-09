namespace JitHub.WinUI.ViewModels.Common;

public sealed class LatestOperationCoordinator
{
    private readonly object _gate = new();
    private long _generation;

    public bool IsRunning { get; private set; }

    public long Begin()
    {
        lock (_gate)
        {
            _generation++;
            IsRunning = true;
            return _generation;
        }
    }

    public bool Complete(long generation)
    {
        lock (_gate)
        {
            if (generation != _generation)
            {
                return false;
            }

            IsRunning = false;
            return true;
        }
    }
}
