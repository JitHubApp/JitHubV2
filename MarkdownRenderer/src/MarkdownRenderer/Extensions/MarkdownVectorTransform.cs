using System;

namespace MarkdownRenderer.Extensions;

/// <summary>A finite affine transform in painter-neutral scene coordinates.</summary>
public readonly record struct MarkdownVectorTransform
{
    /// <summary>Creates an affine transform.</summary>
    public MarkdownVectorTransform(float m11, float m12, float m21, float m22, float m31, float m32)
    {
        if (!float.IsFinite(m11) || !float.IsFinite(m12) || !float.IsFinite(m21) ||
            !float.IsFinite(m22) || !float.IsFinite(m31) || !float.IsFinite(m32))
        {
            throw new ArgumentOutOfRangeException(nameof(m11), "Vector transforms must be finite.");
        }

        M11 = m11;
        M12 = m12;
        M21 = m21;
        M22 = m22;
        M31 = m31;
        M32 = m32;
    }

    /// <summary>Gets the identity transform.</summary>
    public static MarkdownVectorTransform Identity { get; } = new(1, 0, 0, 1, 0, 0);
    /// <summary>Gets the horizontal scale/rotation coefficient.</summary>
    public float M11 { get; }
    /// <summary>Gets the vertical shear/rotation coefficient.</summary>
    public float M12 { get; }
    /// <summary>Gets the horizontal shear/rotation coefficient.</summary>
    public float M21 { get; }
    /// <summary>Gets the vertical scale/rotation coefficient.</summary>
    public float M22 { get; }
    /// <summary>Gets the horizontal translation.</summary>
    public float M31 { get; }
    /// <summary>Gets the vertical translation.</summary>
    public float M32 { get; }
}
