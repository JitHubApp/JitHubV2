using System;
using System.Collections.Generic;
using System.Threading;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Performance;

/// <summary>Estimates the managed weight of a completed content graph, counting shared nodes once.</summary>
internal sealed class CompletedSceneGraphWeightEstimator
{
    private readonly CompletedSceneWeightCounter _weight = new();
    private readonly HashSet<string> _strings = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<MarkdownContent> _contents = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<MarkdownVectorScene> _scenes = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<MarkdownVectorCommand> _commands = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<MarkdownVectorPaintStyle> _paintStyles = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<MarkdownVectorTextStyle> _textStyles = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<MarkdownVectorSemanticItem> _semantics = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<MarkdownVectorLinkAction> _links = new(ReferenceEqualityComparer.Instance);

    internal static long Estimate(
        IReadOnlyList<MarkdownContent> items,
        IReadOnlyList<MarkdownDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var estimator = new CompletedSceneGraphWeightEstimator();
        estimator._weight.Add(80); // result and immutable fragment/list wrappers
        estimator._weight.Add(CompletedSceneWeightMath.Multiply(items.Count, 8));
        estimator._weight.Add(24); // boxed immutable diagnostic view used by restored results
        estimator._weight.Add(CompletedSceneWeightMath.Multiply(diagnostics.Count, IntPtr.Size));
        if (diagnostics.Count > 0)
            estimator._weight.Add(24); // result diagnostics reference array
        var pending = new Stack<MarkdownContent>();
        for (int index = 0; index < items.Count; index++)
        {
            if ((index & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            pending.Push(items[index]);
        }
        int processed = 0;
        while (pending.Count > 0)
        {
            if ((processed++ & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            MarkdownContent content = pending.Pop();
            if (!estimator._contents.Add(content))
                continue;
            estimator.AddContent(content, cancellationToken);
            for (int index = content.Children.Count - 1; index >= 0; index--)
            {
                if ((index & 63) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                pending.Push(content.Children[index]);
            }
        }

        for (int index = 0; index < diagnostics.Count; index++)
        {
            if ((index & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            MarkdownDiagnostic diagnostic = diagnostics[index];
            estimator._weight.Add(64);
            estimator._weight.AddString(diagnostic.Code, estimator._strings);
            estimator._weight.AddString(diagnostic.Message, estimator._strings);
        }
        return estimator._weight.Value;
    }

    private void AddContent(MarkdownContent content, CancellationToken cancellationToken)
    {
        // Include the immutable node's collection/dictionary wrappers and
        // their array headers, not only references to entries.
        _weight.Add(384);
        _weight.Add(CompletedSceneWeightMath.Multiply(content.Children.Count, 8));
        _weight.Add(CompletedSceneWeightMath.Multiply(content.Attributes.Count, 64));
        AddString(content.Text);
        AddString(content.Destination);
        AddString(content.Language);
        AddString(content.FactoryKey);
        AddString(content.CustomKind);
        AddString(content.AccessibilityName);
        AddString(content.AccessibilityDescription);
        AddString(content.SemanticText);
        int attributeIndex = 0;
        foreach (KeyValuePair<string, string> attribute in content.Attributes)
        {
            if ((attributeIndex++ & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            AddString(attribute.Key);
            AddString(attribute.Value);
        }
        if (content.VectorScene is { } scene)
            AddScene(scene, cancellationToken);
    }

    private void AddScene(MarkdownVectorScene scene, CancellationToken cancellationToken)
    {
        if (!_scenes.Add(scene))
            return;

        // Scene-owned list wrappers and array headers, including the
        // semantic-depth and link-by-semantic-index lookup arrays.
        _weight.Add(320);
        _weight.Add(CompletedSceneWeightMath.Multiply(scene.Commands.Count, 8));
        _weight.Add(CompletedSceneWeightMath.Multiply(scene.Semantics.Count, 20));
        _weight.Add(CompletedSceneWeightMath.Multiply(scene.Links.Count, 16));
        for (int index = 0; index < scene.Commands.Count; index++)
        {
            if ((index & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            AddCommand(scene.Commands[index]);
        }
        for (int index = 0; index < scene.Semantics.Count; index++)
        {
            if ((index & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            MarkdownVectorSemanticItem semantic = scene.Semantics[index];
            if (!_semantics.Add(semantic))
                continue;
            _weight.Add(88);
            AddString(semantic.Name);
            AddString(semantic.Description);
        }
        for (int index = 0; index < scene.Links.Count; index++)
        {
            if ((index & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            MarkdownVectorLinkAction link = scene.Links[index];
            if (!_links.Add(link))
                continue;
            _weight.Add(56);
            AddString(link.Target);
            AddString(link.Action);
        }
    }

    private void AddCommand(MarkdownVectorCommand command)
    {
        if (!_commands.Add(command))
            return;

        _weight.Add(136);
        _weight.Add(CompletedSceneWeightMath.Multiply(command.Path.Count, 56));
        if (command.Path.Count > 0)
            _weight.Add(48); // path collection wrapper plus array header
        AddString(command.Text);
        if (command.Style is { } paintStyle && _paintStyles.Add(paintStyle))
        {
            _weight.Add(128);
            _weight.Add(CompletedSceneWeightMath.Multiply(paintStyle.DashArray.Count, sizeof(float)));
            if (paintStyle.DashArray.Count > 0)
                _weight.Add(48); // dash collection wrapper plus array header
        }
        if (command.TextStyle is { } textStyle && _textStyles.Add(textStyle))
        {
            _weight.Add(72);
            AddString(textStyle.FontFamily);
        }
    }

    private void AddString(string? value) => _weight.AddString(value, _strings);
}
