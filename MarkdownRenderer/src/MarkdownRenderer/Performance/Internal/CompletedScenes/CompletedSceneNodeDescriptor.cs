using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Extensions.Vectors;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Performance.Internal.CompletedScenes;

/// <summary>
/// Scalar copy of one completed content node. It holds only weak references
/// to the original node and its vector scene, so a completed descriptor never
/// roots the scene graph it can later rebuild.
/// </summary>
internal sealed class CompletedSceneNodeDescriptor
{
    private static readonly IReadOnlyDictionary<string, string> EmptyAttributes =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    private readonly WeakReference<MarkdownContent> _originalNode;
    private readonly WeakReference<MarkdownVectorScene>? _scene;
    private readonly MarkdownContentKind _kind;
    private readonly MarkdownStyleRole _styleRole;
    private readonly SourceSpan _sourceSpan;
    private readonly MarkdownAccessibilityRole _accessibilityRole;
    private readonly string? _text;
    private readonly string? _destination;
    private readonly string? _language;
    private readonly string? _factoryKey;
    private readonly string? _customKind;
    private readonly KeyValuePair<string, string>[] _attributes;
    private readonly CompletedSceneNodeDescriptor[] _children;
    private readonly string? _accessibilityName;
    private readonly string? _accessibilityDescription;
    private readonly string? _semanticText;

    private CompletedSceneNodeDescriptor(
        MarkdownContent content,
        CompletedSceneNodeDescriptor[] children,
        CancellationToken cancellationToken)
    {
        _originalNode = new WeakReference<MarkdownContent>(content);
        _scene = content.VectorScene is { } scene
            ? new WeakReference<MarkdownVectorScene>(scene)
            : null;
        _kind = content.Kind;
        _styleRole = content.StyleRole;
        _sourceSpan = content.SourceSpan;
        _accessibilityRole = content.AccessibilityRole;
        _text = content.Text;
        _destination = content.Destination;
        _language = content.Language;
        _factoryKey = content.FactoryKey;
        _customKind = content.CustomKind;
        _attributes = CopyAttributes(content.Attributes, cancellationToken);
        _children = children;
        _accessibilityName = content.AccessibilityName;
        _accessibilityDescription = content.AccessibilityDescription;
        _semanticText = content.SemanticText;
    }

    internal static bool TryCreateGraph(
        IReadOnlyList<MarkdownContent> roots,
        long maximumWeightBytes,
        CancellationToken cancellationToken,
        out CompletedSceneNodeDescriptor[] result,
        out long estimatedWeightBytes)
    {
        result = Array.Empty<CompletedSceneNodeDescriptor>();
        estimatedWeightBytes = 0;
        var metadataBudget = new CompletedSceneMetadataBudget(maximumWeightBytes, roots.Count);
        if (!metadataBudget.IsWithinBudget)
            return false;

        var descriptors = new Dictionary<MarkdownContent, CompletedSceneNodeDescriptor>(
            ReferenceEqualityComparer.Instance);
        var visiting = new HashSet<MarkdownContent>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(MarkdownContent Node, bool Exit)>();
        int processed = 0;

        for (int rootIndex = 0; rootIndex < roots.Count; rootIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkdownContent root = roots[rootIndex];
            pending.Push((root, false));
            while (pending.Count > 0)
            {
                if ((processed++ & 63) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                (MarkdownContent node, bool exit) = pending.Pop();
                if (descriptors.ContainsKey(node))
                    continue;

                if (!exit)
                {
                    if (!visiting.Add(node))
                        throw new InvalidOperationException("A completed content tree cannot contain a cycle.");

                    pending.Push((node, true));
                    for (int childIndex = node.Children.Count - 1; childIndex >= 0; childIndex--)
                    {
                        if ((childIndex & 63) == 0)
                            cancellationToken.ThrowIfCancellationRequested();
                        MarkdownContent child = node.Children[childIndex];
                        if (!descriptors.ContainsKey(child))
                            pending.Push((child, false));
                    }
                    continue;
                }

                visiting.Remove(node);
                if (!metadataBudget.TryAdd(node, cancellationToken))
                    return false;
                var children = new CompletedSceneNodeDescriptor[node.Children.Count];
                for (int childIndex = 0; childIndex < children.Length; childIndex++)
                {
                    if ((childIndex & 63) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    children[childIndex] = descriptors[node.Children[childIndex]];
                }
                descriptors.Add(node, new CompletedSceneNodeDescriptor(node, children, cancellationToken));
            }
        }

        result = new CompletedSceneNodeDescriptor[roots.Count];
        for (int index = 0; index < result.Length; index++)
        {
            if ((index & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            result[index] = descriptors[roots[index]];
        }
        estimatedWeightBytes = metadataBudget.WeightBytes;
        return true;
    }

    internal static bool TryRestoreGraph(
        CompletedSceneNodeDescriptor[] roots,
        CancellationToken cancellationToken,
        out MarkdownContent[] restoredRoots)
    {
        var restored = new Dictionary<CompletedSceneNodeDescriptor, MarkdownContent>();
        var restoreScenes = new Dictionary<CompletedSceneNodeDescriptor, MarkdownVectorScene?>();
        var visiting = new HashSet<CompletedSceneNodeDescriptor>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(CompletedSceneNodeDescriptor Node, bool Exit)>();
        int processed = 0;

        for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompletedSceneNodeDescriptor root = roots[rootIndex];
            pending.Push((root, false));
            while (pending.Count > 0)
            {
                if ((processed++ & 63) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                (CompletedSceneNodeDescriptor node, bool exit) = pending.Pop();
                if (restored.ContainsKey(node))
                    continue;

                if (!exit)
                {
                    if (!node.TryEnterRestore(restored, restoreScenes, visiting, pending, cancellationToken))
                    {
                        restoredRoots = Array.Empty<MarkdownContent>();
                        return false;
                    }
                    continue;
                }

                visiting.Remove(node);
                restored.Add(node, node.Restore(restored, restoreScenes, cancellationToken));
            }
        }

        restoredRoots = new MarkdownContent[roots.Length];
        for (int index = 0; index < roots.Length; index++)
        {
            if ((index & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            restoredRoots[index] = restored[roots[index]];
        }
        return true;
    }

    /// <summary>
    /// First visit of a node during restoration: reuses the live original when
    /// it is still reachable, otherwise captures its weak scene and schedules
    /// its children. Returns false when the scene was collected or a cycle is
    /// found, so the whole graph is re-prepared instead.
    /// </summary>
    private bool TryEnterRestore(
        Dictionary<CompletedSceneNodeDescriptor, MarkdownContent> restored,
        Dictionary<CompletedSceneNodeDescriptor, MarkdownVectorScene?> restoreScenes,
        HashSet<CompletedSceneNodeDescriptor> visiting,
        Stack<(CompletedSceneNodeDescriptor Node, bool Exit)> pending,
        CancellationToken cancellationToken)
    {
        if (_originalNode.TryGetTarget(out MarkdownContent? originalNode))
        {
            restored.Add(this, originalNode);
            return true;
        }

        if (_scene is { } sceneReference)
        {
            if (!sceneReference.TryGetTarget(out MarkdownVectorScene? scene))
                return false;
            restoreScenes.Add(this, scene);
        }

        if (!visiting.Add(this))
            return false;

        pending.Push((this, true));
        for (int childIndex = _children.Length - 1; childIndex >= 0; childIndex--)
        {
            if ((childIndex & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            CompletedSceneNodeDescriptor child = _children[childIndex];
            if (!restored.ContainsKey(child))
                pending.Push((child, false));
        }
        return true;
    }

    /// <summary>Rebuilds this node around its already-restored children and live scene.</summary>
    private MarkdownContent Restore(
        Dictionary<CompletedSceneNodeDescriptor, MarkdownContent> restored,
        Dictionary<CompletedSceneNodeDescriptor, MarkdownVectorScene?> restoreScenes,
        CancellationToken cancellationToken)
    {
        var children = new MarkdownContent[_children.Length];
        for (int childIndex = 0; childIndex < children.Length; childIndex++)
        {
            if ((childIndex & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            children[childIndex] = restored[_children[childIndex]];
        }

        IReadOnlyList<MarkdownContent> childList = children.Length == 0
            ? Array.Empty<MarkdownContent>()
            : new ReadOnlyCollection<MarkdownContent>(children);
        restoreScenes.TryGetValue(this, out MarkdownVectorScene? restoredScene);
        return new MarkdownContent(
            _kind,
            _styleRole,
            _sourceSpan,
            _accessibilityRole,
            _text,
            _destination,
            _language,
            _factoryKey,
            _customKind,
            restoredScene,
            RestoreAttributes(_attributes, cancellationToken),
            childList,
            _accessibilityName,
            _accessibilityDescription,
            _semanticText);
    }

    private static KeyValuePair<string, string>[] CopyAttributes(
        IReadOnlyDictionary<string, string> attributes,
        CancellationToken cancellationToken)
    {
        if (attributes.Count == 0)
            return Array.Empty<KeyValuePair<string, string>>();

        var copy = new KeyValuePair<string, string>[attributes.Count];
        int index = 0;
        foreach (KeyValuePair<string, string> attribute in attributes)
        {
            if ((index & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            copy[index++] = attribute;
        }
        return copy;
    }

    private static IReadOnlyDictionary<string, string> RestoreAttributes(
        KeyValuePair<string, string>[] attributes,
        CancellationToken cancellationToken)
    {
        if (attributes.Length == 0)
            return EmptyAttributes;

        var dictionary = new Dictionary<string, string>(attributes.Length, StringComparer.Ordinal);
        for (int index = 0; index < attributes.Length; index++)
        {
            if ((index & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            KeyValuePair<string, string> attribute = attributes[index];
            dictionary.Add(attribute.Key, attribute.Value);
        }
        return new ReadOnlyDictionary<string, string>(dictionary);
    }
}
