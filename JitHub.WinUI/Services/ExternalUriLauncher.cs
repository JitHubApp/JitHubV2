using System;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public interface IExternalUriLauncher
{
    Task<bool> LaunchAsync(Uri uri, CancellationToken cancellationToken = default);
}

public sealed class WindowsExternalUriLauncher : IExternalUriLauncher
{
    public async Task<bool> LaunchAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        return await Windows.System.Launcher.LaunchUriAsync(uri);
    }
}

internal sealed class LoginLaunchFailureExternalUriLauncher : IExternalUriLauncher
{
    public Task<bool> LaunchAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<bool>(new System.Runtime.InteropServices.COMException(
            "Simulated unexpected URI launcher failure."));
    }
}
