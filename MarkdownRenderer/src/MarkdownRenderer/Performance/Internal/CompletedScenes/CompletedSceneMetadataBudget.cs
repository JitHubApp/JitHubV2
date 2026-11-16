using System.Collections.Generic;
using System.Threading;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Performance.Internal.CompletedScenes;

/// <summary>Approximate byte budget for one descriptor's reconstruction metadata.</summary>
internal sealed class CompletedSceneMetadataBudget
{
    private readonly long _maximumWeightBytes;
    private readonly HashSet<string> _strings = new(ReferenceEqualityComparer.Instance);
    private long _weightBytes;

    internal CompletedSceneMetadataBudget(long maximumWeightBytes, int rootCount)
    {
        _maximumWeightBytes = maximumWeightBytes;
        Add(104); // descriptor, weak-result handle, and root descriptor array
        Add(CompletedSceneWeightMath.Multiply(rootCount, 8));
    }

    internal long WeightBytes => _weightBytes;

    internal bool IsWithinBudget => _weightBytes <= _maximumWeightBytes;

    internal bool TryAdd(MarkdownContent node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Add(160);
        Add(CompletedSceneWeightMath.Multiply(node.Children.Count, 8));
        Add(CompletedSceneWeightMath.Multiply(node.Attributes.Count, 16));
        Add(node.Children.Count == 0 ? 0 : 24);
        Add(node.Attributes.Count == 0 ? 0 : 24);
        Add(CompletedSceneWeightMath.Multiply(node.VectorScene is null ? 1 : 2, 40));
        AddString(node.Text);
        AddString(node.Destination);
        AddString(node.Language);
        AddString(node.FactoryKey);
        AddString(node.CustomKind);
        AddString(node.AccessibilityName);
        AddString(node.AccessibilityDescription);
        AddString(node.SemanticText);
        if (!IsWithinBudget)
            return false;

        int index = 0;
        foreach (KeyValuePair<string, string> attribute in node.Attributes)
        {
            if ((index++ & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            AddString(attribute.Key);
            AddString(attribute.Value);
            if (!IsWithinBudget)
                return false;
        }

        return IsWithinBudget;
    }

    private void AddString(string? value)
    {
        if (value is not null && !_strings.Contains(value))
        {
            _weightBytes = CompletedSceneWeightMath.SaturatingAdd(
                _weightBytes,
                CompletedSceneWeightMath.SaturatingAdd(24, CompletedSceneWeightMath.Multiply(value.Length, sizeof(char))));
            if (_weightBytes <= _maximumWeightBytes)
                _strings.Add(value);
        }
    }

    private void Add(long bytes)
    {
        _weightBytes = CompletedSceneWeightMath.SaturatingAdd(_weightBytes, bytes);
    }
}
