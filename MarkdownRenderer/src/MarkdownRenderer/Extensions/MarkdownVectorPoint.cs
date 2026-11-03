using System;

namespace MarkdownRenderer.Extensions;

/// <summary>A finite point in device-independent scene coordinates.</summary>
public readonly record struct MarkdownVectorPoint
{
    /// <summary>Creates a point.</summary>
    public MarkdownVectorPoint(float x, float y)
    {
        ThrowIfNotFinite(x, nameof(x));
        ThrowIfNotFinite(y, nameof(y));
        X = x;
        Y = y;
    }

    /// <summary>Gets the horizontal coordinate.</summary>
    public float X { get; }

    /// <summary>Gets the vertical coordinate.</summary>
    public float Y { get; }

    private static void ThrowIfNotFinite(float value, string name)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(name, "Vector coordinates must be finite.");
    }
}
