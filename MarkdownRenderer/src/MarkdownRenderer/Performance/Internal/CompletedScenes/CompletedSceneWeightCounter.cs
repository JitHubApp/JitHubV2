using System.Collections.Generic;

namespace MarkdownRenderer.Performance.Internal.CompletedScenes;

/// <summary>Saturating byte counter that counts each distinct string once.</summary>
internal sealed class CompletedSceneWeightCounter
{
    internal long Value { get; private set; }

    internal void Add(long bytes) => Value = CompletedSceneWeightMath.SaturatingAdd(Value, bytes);

    internal void AddString(string? value, HashSet<string> seen)
    {
        if (value is not null && seen.Add(value))
            Add(CompletedSceneWeightMath.SaturatingAdd(24, CompletedSceneWeightMath.Multiply(value.Length, sizeof(char))));
    }
}
