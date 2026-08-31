using System.Threading;

namespace JitHub.Services;

internal sealed class ModalSessionStateGate
{
    private int _isActive = 1;
    private int _mutationInProgress;

    public bool IsActive => Volatile.Read(ref _isActive) != 0;

    public bool IsMutationInProgress => Volatile.Read(ref _mutationInProgress) != 0;

    public bool TryBeginMutation()
    {
        if (!IsActive || Interlocked.CompareExchange(ref _mutationInProgress, 1, 0) != 0)
        {
            return false;
        }

        if (IsActive)
        {
            return true;
        }

        Interlocked.Exchange(ref _mutationInProgress, 0);
        return false;
    }

    public bool EndMutation() => Interlocked.Exchange(ref _mutationInProgress, 0) != 0;

    public bool TryDeactivateForClose()
    {
        if (Interlocked.CompareExchange(ref _isActive, 0, 1) != 1)
        {
            return false;
        }

        if (!IsMutationInProgress)
        {
            return true;
        }

        Interlocked.Exchange(ref _isActive, 1);
        return false;
    }

    public void Reactivate() => Interlocked.CompareExchange(ref _isActive, 1, 0);

    public void Deactivate()
    {
        Interlocked.Exchange(ref _isActive, 0);
        Interlocked.Exchange(ref _mutationInProgress, 0);
    }
}
