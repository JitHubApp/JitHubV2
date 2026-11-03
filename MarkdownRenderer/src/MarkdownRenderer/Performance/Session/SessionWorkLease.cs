using System;
using System.Threading;

namespace MarkdownRenderer.Performance;

/// <summary>Releases one unit of admitted session work exactly once.</summary>
internal sealed class SessionWorkLease(MarkdownPerformanceSessionLifetime lifetime) : IDisposable
{
    private MarkdownPerformanceSessionLifetime? _lifetime = lifetime;

    public void Dispose() => Interlocked.Exchange(ref _lifetime, null)?.RetireWork();
}
