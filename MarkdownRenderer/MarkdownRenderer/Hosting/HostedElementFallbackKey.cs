using System;
using System.Runtime.CompilerServices;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Hosting;

/// <summary>
/// Identifies declarative hosted content that must render through its native
/// markdown fallback after a host factory declines or fails realization.
/// </summary>
internal readonly struct HostedElementFallbackKey : IEquatable<HostedElementFallbackKey>
{
    internal HostedElementFallbackKey(MarkdownContentFragment fragment)
    {
        Fragment = fragment ?? throw new ArgumentNullException(nameof(fragment));
    }

    internal MarkdownContentFragment Fragment { get; }

    public bool Equals(HostedElementFallbackKey other) =>
        ReferenceEquals(Fragment, other.Fragment);

    public override bool Equals(object? obj) =>
        obj is HostedElementFallbackKey other && Equals(other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(Fragment);

    public static bool operator ==(HostedElementFallbackKey left, HostedElementFallbackKey right) =>
        left.Equals(right);

    public static bool operator !=(HostedElementFallbackKey left, HostedElementFallbackKey right) =>
        !left.Equals(right);
}
