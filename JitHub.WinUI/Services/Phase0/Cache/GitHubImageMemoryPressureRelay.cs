using System;

namespace JitHub.Services;

/// <summary>
/// Forwards Windows memory-pressure notifications without letting the
/// process-wide event keep a disposed image service alive.
/// </summary>
internal sealed class GitHubImageMemoryPressureRelay(GitHubImageService owner)
{
    private readonly WeakReference<GitHubImageService> _owner = new(owner);

    internal void OnAppMemoryUsageIncreased(object? sender, object args)
    {
        if (_owner.TryGetTarget(out GitHubImageService? service))
            service.OnAppMemoryUsageIncreased(sender, args);
    }
}
