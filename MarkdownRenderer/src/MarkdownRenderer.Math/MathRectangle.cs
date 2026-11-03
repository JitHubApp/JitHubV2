namespace MarkdownRenderer.Math;

/// <summary>An immutable rectangle in device-independent scene coordinates.</summary>
internal readonly record struct MathRectangle
{
    /// <summary>Creates a rectangle.</summary>
    public MathRectangle(float x, float y, float width, float height)
    {
        MathSceneValidation.ThrowIfNotFinite(x, nameof(x));
        MathSceneValidation.ThrowIfNotFinite(y, nameof(y));
        MathSceneValidation.ThrowIfNotFiniteOrNegative(width, nameof(width));
        MathSceneValidation.ThrowIfNotFiniteOrNegative(height, nameof(height));
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
