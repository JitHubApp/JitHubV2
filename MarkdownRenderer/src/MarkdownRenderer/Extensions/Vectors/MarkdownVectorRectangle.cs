using System;

namespace MarkdownRenderer.Extensions.Vectors;

/// <summary>An immutable rectangle in device-independent scene coordinates.</summary>
public readonly record struct MarkdownVectorRectangle
{
    /// <summary>Creates a rectangle.</summary>
    public MarkdownVectorRectangle(float x, float y, float width, float height)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) ||
            !float.IsFinite(width) || !float.IsFinite(height) ||
            width < 0 || height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Vector rectangles must be finite and non-negative.");
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>Gets the left coordinate.</summary>
    public float X { get; }
    /// <summary>Gets the top coordinate.</summary>
    public float Y { get; }
    /// <summary>Gets the width.</summary>
    public float Width { get; }
    /// <summary>Gets the height.</summary>
    public float Height { get; }
}
