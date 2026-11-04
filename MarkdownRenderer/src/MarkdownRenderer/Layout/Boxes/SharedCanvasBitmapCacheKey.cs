using System;
using System.Runtime.CompilerServices;
using Microsoft.Graphics.Canvas;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Identifies one uploaded bitmap in a <see cref="SharedCanvasBitmapCache"/>:
/// the device that owns it and its ordinal content identity.
/// </summary>
internal readonly struct SharedCanvasBitmapCacheKey : IEquatable<SharedCanvasBitmapCacheKey>
{
    internal SharedCanvasBitmapCacheKey(CanvasDevice device, string identity)
    {
        Device = device;
        Identity = identity;
    }

    internal readonly CanvasDevice Device;

    internal readonly string Identity;

    public bool Equals(SharedCanvasBitmapCacheKey other) =>
        ReferenceEquals(Device, other.Device) &&
        StringComparer.Ordinal.Equals(Identity, other.Identity);

    public override bool Equals(object? obj) => obj is SharedCanvasBitmapCacheKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        RuntimeHelpers.GetHashCode(Device),
        Identity is null ? 0 : StringComparer.Ordinal.GetHashCode(Identity));
}
