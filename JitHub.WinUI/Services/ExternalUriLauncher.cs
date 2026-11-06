using System;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Markdown;

namespace JitHub.Services;

/// <summary>
/// Launches a URI in the reader's default handler. Markdown hosts use the same
/// <see cref="IMarkdownLinkLauncher.LaunchAsync"/> contract for document links.
/// </summary>
public interface IExternalUriLauncher : IMarkdownLinkLauncher
{
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
