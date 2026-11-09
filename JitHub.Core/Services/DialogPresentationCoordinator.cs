using System;

namespace JitHub.Services;

public enum DialogPresentationKind
{
    NativeContentDialog,
    ShellOverlay
}

public sealed class DialogPresentationCoordinator
{
    private readonly object _gate = new();
    private bool _isPresenting;
    private long _lease;
    private DialogPresentationKind? _activeKind;

    public bool IsPresenting
    {
        get
        {
            lock (_gate)
            {
                return _isPresenting;
            }
        }
    }

    public DialogPresentationKind? ActiveKind
    {
        get
        {
            lock (_gate)
            {
                return _activeKind;
            }
        }
    }

    public bool TryBegin(DialogPresentationKind kind, out long lease)
    {
        lock (_gate)
        {
            if (_isPresenting)
            {
                lease = 0;
                return false;
            }

            _isPresenting = true;
            _activeKind = kind;
            lease = ++_lease;
            return true;
        }
    }

    public bool Complete(long lease)
    {
        lock (_gate)
        {
            if (!_isPresenting || lease != _lease)
            {
                return false;
            }

            _isPresenting = false;
            _activeKind = null;
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _isPresenting = false;
            _activeKind = null;
            _lease++;
        }
    }
}
