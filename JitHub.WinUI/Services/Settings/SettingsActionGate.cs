using System;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public sealed class SettingsActionGate
{
    private int _isActive;

    public bool IsActive => Volatile.Read(ref _isActive) != 0;

    public event EventHandler? StateChanged;

    public async Task<bool> TryRunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (Interlocked.CompareExchange(ref _isActive, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            await action().ConfigureAwait(true);
            return true;
        }
        finally
        {
            Volatile.Write(ref _isActive, 0);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
