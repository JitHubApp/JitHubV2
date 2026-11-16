namespace MarkdownRenderer.Math.Internal.Scenes;

/// <summary>A finite point in device-independent scene coordinates.</summary>
internal readonly record struct MathPoint
{
    /// <summary>Creates a point.</summary>
    public MathPoint(float x, float y)
    {
        MathSceneValidation.ThrowIfNotFinite(x, nameof(x));
        MathSceneValidation.ThrowIfNotFinite(y, nameof(y));
        X = x;
        Y = y;
    }

    /// <summary>Gets the horizontal coordinate.</summary>
    public float X { get; }

    /// <summary>Gets the vertical coordinate.</summary>
    public float Y { get; }
}
