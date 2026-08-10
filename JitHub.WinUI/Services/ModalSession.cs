using System;

namespace JitHub.Services;

public interface IModalSessionAware
{
    void AttachModalSession(ModalSession session);
}

public interface IModalContentLayout
{
    bool OwnsScrolling { get; }

    void SetModalViewport(double width, double height);
}

public sealed class ModalSession
{
    private readonly ModalService _owner;
    private readonly ModalSessionStateGate _state = new();

    internal ModalSession(ModalService owner, long operationLease, long presentationLease)
    {
        _owner = owner;
        OperationLease = operationLease;
        PresentationLease = presentationLease;
    }

    internal long OperationLease { get; }

    internal long PresentationLease { get; }

    public bool IsActive => _state.IsActive;

    public bool IsMutationInProgress => _state.IsMutationInProgress;

    public bool TryBeginMutation()
    {
        if (!_state.TryBeginMutation())
        {
            return false;
        }

        _owner.NotifyDismissalStateChanged();
        return true;
    }

    public void EndMutation()
    {
        if (_state.EndMutation())
        {
            _owner.NotifyDismissalStateChanged();
        }
    }

    public bool TryClose() => IsActive && _owner.TryClose(this);

    internal bool TryBeginClose() => _state.TryDeactivateForClose();

    internal void RestoreAfterCloseFailure() => _state.Reactivate();

    internal void MarkClosed() => _state.Deactivate();
}
