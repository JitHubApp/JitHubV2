using System.Threading;

namespace JitHub.Services;

internal sealed class DialogSubmissionGate
{
    private int _isSubmitting;

    public bool IsSubmitting => Volatile.Read(ref _isSubmitting) != 0;

    public bool TryBegin() => Interlocked.CompareExchange(ref _isSubmitting, 1, 0) == 0;

    public void Complete() => Interlocked.Exchange(ref _isSubmitting, 0);
}
