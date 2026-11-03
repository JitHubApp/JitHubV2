using System.Collections.ObjectModel;

namespace MarkdownRenderer.Math;

/// <summary>
/// An immutable, thread-safe vector scene for one formula. The scene contains
/// outlines and rules only; it owns no Win2D, device, bitmap, or font-lifetime object.
/// </summary>
internal sealed class MathScene
{
    internal MathScene(float width, float height, float baseline, IReadOnlyList<MathSceneCommand> commands)
    {
        MathSceneValidation.ThrowIfNotFiniteOrNegative(width, nameof(width));
        MathSceneValidation.ThrowIfNotFiniteOrNegative(height, nameof(height));
        MathSceneValidation.ThrowIfNotFiniteOrNegative(baseline, nameof(baseline));
        if (baseline > height)
            throw new ArgumentOutOfRangeException(nameof(baseline), "The baseline must lie inside the scene.");

        ArgumentNullException.ThrowIfNull(commands);
        var copy = new MathSceneCommand[commands.Count];
        for (int i = 0; i < commands.Count; i++)
            copy[i] = commands[i] ?? throw new ArgumentException("Scene commands cannot contain null.", nameof(commands));

        Width = width;
        Height = height;
        Baseline = baseline;
        Commands = new ReadOnlyCollection<MathSceneCommand>(copy);
    }

    /// <summary>Gets the scene width in device-independent units.</summary>
    public float Width { get; }

    /// <summary>Gets the scene height in device-independent units.</summary>
    public float Height { get; }

    /// <summary>Gets the baseline measured from the scene's top edge.</summary>
    public float Baseline { get; }

    /// <summary>Gets drawing operations in painter order.</summary>
    public IReadOnlyList<MathSceneCommand> Commands { get; }
}
