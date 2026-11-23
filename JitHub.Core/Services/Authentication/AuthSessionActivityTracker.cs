using System;

namespace JitHub.Core.Services.Authentication;

/// <summary>
/// Holds the current auth activity. Work takes a lease, and ending it clears the
/// activity only if newer work has not replaced it.
/// </summary>
internal sealed class AuthSessionActivityTracker
{
    private readonly object _gate = new();
    private readonly Action _changed;
    private AuthSessionActivity _current;

    public AuthSessionActivityTracker(Action changed) => _changed = changed;

    public AuthSessionActivity Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public Lease Begin(AuthSessionActivity activity)
    {
        Set(activity);
        return new Lease(this, activity);
    }

    public void Clear() => Set(AuthSessionActivity.Idle);

    private void Set(AuthSessionActivity activity)
    {
        lock (_gate)
        {
            if (_current == activity)
            {
                return;
            }

            _current = activity;
        }

        _changed();
    }

    private void End(AuthSessionActivity activity)
    {
        lock (_gate)
        {
            if (_current != activity)
            {
                return;
            }

            _current = AuthSessionActivity.Idle;
        }

        _changed();
    }

    /// <summary>Ends its activity when disposed. Use it in a <c>using</c> declaration.</summary>
    public readonly struct Lease : IDisposable
    {
        private readonly AuthSessionActivityTracker _owner;
        private readonly AuthSessionActivity _activity;

        internal Lease(AuthSessionActivityTracker owner, AuthSessionActivity activity)
        {
            _owner = owner;
            _activity = activity;
        }

        public void Dispose() => _owner?.End(_activity);
    }
}
