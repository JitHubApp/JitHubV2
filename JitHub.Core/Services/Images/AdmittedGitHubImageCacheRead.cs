using System;

namespace JitHub.Services;

/// <summary>
/// A cache read whose bytes are charged to a source-byte admission until disposed.
/// <c>partial</c> only so CsWinRT can generate its ABI projection of IDisposable.
/// </summary>
internal sealed partial record AdmittedGitHubImageCacheRead(
    GitHubImageCacheRead Value,
    IDisposable Lease) : IDisposable
{
    public void Dispose() => Lease.Dispose();
}
