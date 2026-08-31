using System.Threading;

namespace JitHub.Services;

internal sealed class DialogFocusRestorationGate
{
    private long _generation;

    public static DialogFocusRestorationGate Shared { get; } = new();

    public long BeginSession() => Interlocked.Increment(ref _generation);

    public bool CanRestore(long sessionGeneration, bool isDialogVisible) =>
        sessionGeneration != 0 &&
        !isDialogVisible &&
        sessionGeneration == Volatile.Read(ref _generation);
}
