using System.Windows.Input;

namespace JitHub.Services;

internal sealed class ModalOperationGate
{
    private readonly object _gate = new();
    private ICommand? _callback;
    private bool _isOpen;
    private long _lease;

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _isOpen;
            }
        }
    }

    public bool TryBegin(ICommand? callback, out long lease)
    {
        lock (_gate)
        {
            if (_isOpen)
            {
                lease = 0;
                return false;
            }

            _isOpen = true;
            _callback = callback;
            lease = ++_lease;
            return true;
        }
    }

    public bool TryComplete(out ICommand? callback, out long lease)
    {
        lock (_gate)
        {
            if (!_isOpen)
            {
                callback = null;
                lease = 0;
                return false;
            }

            callback = _callback;
            lease = _lease;
            _callback = null;
            _isOpen = false;
            return true;
        }
    }

    public bool TryComplete(long expectedLease, out ICommand? callback)
    {
        lock (_gate)
        {
            if (!_isOpen || expectedLease != _lease)
            {
                callback = null;
                return false;
            }

            callback = _callback;
            _callback = null;
            _isOpen = false;
            return true;
        }
    }

    public void Abort(long lease)
    {
        lock (_gate)
        {
            if (!_isOpen || lease != _lease)
            {
                return;
            }

            _callback = null;
            _isOpen = false;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _callback = null;
            _isOpen = false;
            _lease++;
        }
    }

    public void RestoreAfterCloseFailure(long lease, ICommand? callback)
    {
        lock (_gate)
        {
            if (_isOpen || lease != _lease)
            {
                return;
            }

            _isOpen = true;
            _callback = callback;
        }
    }
}
