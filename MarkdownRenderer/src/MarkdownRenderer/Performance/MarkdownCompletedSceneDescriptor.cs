using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Threading;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Weakly describes a completed fragment without rooting its vector command
/// graphs. While any live box/run still owns a scene, the fragment can be
/// reconstructed around that same scene instead of recompiling it.
/// </summary>
internal sealed class MarkdownCompletedSceneDescriptor
{
    private const long MaximumReconstructionMetadataBytes = 1024L * 1024;

    private readonly WeakReference<MarkdownDeferredSceneResult> _originalResult;
    private readonly WeakReference<ReconstructionMetadata>? _metadata;
    private ReconstructionMetadata? _pendingMetadata;
    private readonly ImmutableArray<MarkdownDiagnostic> _diagnostics;
    private readonly bool _canReconstruct;

    private MarkdownCompletedSceneDescriptor(
        MarkdownDeferredSceneResult result,
        ReconstructionMetadata? metadata,
        ImmutableArray<MarkdownDiagnostic> diagnostics,
        long estimatedGraphWeightBytes,
        long metadataWeightBytes,
        MarkdownSceneProjectionSignature? singleSceneProjection)
    {
        _originalResult = new WeakReference<MarkdownDeferredSceneResult>(result);
        _metadata = metadata is null ? null : new WeakReference<ReconstructionMetadata>(metadata);
        _pendingMetadata = metadata;
        _diagnostics = diagnostics;
        _canReconstruct = metadata is not null;
        EstimatedGraphWeightBytes = estimatedGraphWeightBytes;
        MetadataWeightBytes = metadataWeightBytes;
        SingleSceneProjection = singleSceneProjection;
        ProjectionIdentity = singleSceneProjection is { } signature ? signature : null;
    }

    /// <summary>Estimated managed graph weight captured once at completion.</summary>
    internal long EstimatedGraphWeightBytes { get; }

    /// <summary>
    /// Approximate reconstruction metadata retained by this descriptor. It
    /// excludes diagnostics maintained separately for plan reporting and is not
    /// part of the completed-result graph-cache budget.
    /// </summary>
    internal long MetadataWeightBytes { get; }

    internal readonly MarkdownSceneProjectionSignature? SingleSceneProjection;
    // One boxed scalar digest is shared by the descriptor and materialized
    // binding; it contains no scene/content/native references.
    internal readonly object? ProjectionIdentity;

    internal IReadOnlyList<MarkdownDiagnostic> Diagnostics => _diagnostics;

    internal ImmutableArray<MarkdownDiagnostic> DiagnosticContribution => _diagnostics;

    internal static MarkdownCompletedSceneDescriptor Create(
        MarkdownDeferredSceneResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(result.Fragment);
        ArgumentNullException.ThrowIfNull(result.Diagnostics);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<MarkdownContent> sourceItems = result.Fragment.Items;
        long graphWeight = SceneGraphWeightEstimator.Estimate(sourceItems, result.Diagnostics, cancellationToken);
        ImmutableArray<MarkdownDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<MarkdownDiagnostic>(result.Diagnostics.Count);
        for (int index = 0; index < result.Diagnostics.Count; index++)
        {
            if ((index & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            diagnostics.Add(result.Diagnostics[index]);
        }
        ImmutableArray<MarkdownDiagnostic> diagnosticContribution = diagnostics.MoveToImmutable();

        bool canReconstruct = result.Fragment.DeferredSceneRenderer is null &&
            result.Fragment.DeferredSceneRendererLifetime is null;
        ContentNodeDescriptor[] descriptors = Array.Empty<ContentNodeDescriptor>();
        long metadataWeight = 0;
        bool hasReconstructionMetadata = canReconstruct &&
            ContentNodeDescriptor.TryCreateGraph(
                sourceItems,
                MaximumReconstructionMetadataBytes,
                cancellationToken,
                out descriptors,
                out metadataWeight);

        if (!hasReconstructionMetadata)
        {
            // Descriptors are only a cache optimization. An unusually broad
            // extension result remains publishable and may be retained by the
            // strong weighted cache, but is re-prepared after that cache drops it.
            descriptors = Array.Empty<ContentNodeDescriptor>();
            metadataWeight = 0;
        }
        ReconstructionMetadata? metadata = hasReconstructionMetadata
            ? new ReconstructionMetadata(descriptors)
            : null;

        return new MarkdownCompletedSceneDescriptor(
            result,
            metadata,
            diagnosticContribution,
            graphWeight,
            metadataWeight,
            sourceItems.Count == 1 && sourceItems[0].Kind == MarkdownContentKind.VectorScene &&
                sourceItems[0].VectorScene is not null && sourceItems[0].Children.Count == 0 &&
                !sourceItems[0].Attributes.ContainsKey(MarkdownContentAttributes.DeferredScene)
                ? MarkdownSceneProjectionSignature.Create(sourceItems[0], cancellationToken)
                : null);
    }

    /// <summary>
    /// Transfers the descriptor's temporary strong construction reference to
    /// the bounded metadata cache before the descriptor is published.
    /// </summary>
    internal object? DetachMetadataOwner() => Interlocked.Exchange(ref _pendingMetadata, null);

    /// <summary>
    /// Reuses the original immutable result if still alive, otherwise rebuilds
    /// its content tree using only live weak scene targets and copied scalars.
    /// </summary>
    internal bool TryGetOriginal(out MarkdownDeferredSceneResult? result) =>
        _originalResult.TryGetTarget(out result);

    internal bool TryRestore(
        out MarkdownDeferredSceneResult? result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryGetOriginal(out result))
            return true;

        if (!_canReconstruct || _metadata is null ||
            !_metadata.TryGetTarget(out ReconstructionMetadata? metadata))
        {
            result = null;
            return false;
        }

        if (!ContentNodeDescriptor.TryRestoreGraph(metadata.Items, cancellationToken, out MarkdownContent[] items))
        {
            result = null;
            return false;
        }

        MarkdownContentFragment fragment = items.Length == 0
            ? MarkdownContentFragment.Empty
            : new MarkdownContentFragment(new ReadOnlyCollection<MarkdownContent>(items));
        result = new MarkdownDeferredSceneResult(fragment, _diagnostics);
        return true;
    }

    private sealed class ReconstructionMetadata(ContentNodeDescriptor[] items)
    {
        internal ContentNodeDescriptor[] Items { get; } = items;
    }

    private sealed class ContentNodeDescriptor
    {
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
        private readonly ContentNodeDescriptor[] _children;
        private readonly string? _accessibilityName;
        private readonly string? _accessibilityDescription;
        private readonly string? _semanticText;

        private ContentNodeDescriptor(
            MarkdownContent content,
            ContentNodeDescriptor[] children,
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
            out ContentNodeDescriptor[] result,
            out long estimatedWeightBytes)
        {
            result = Array.Empty<ContentNodeDescriptor>();
            estimatedWeightBytes = 0;
            var metadataBudget = new DescriptorMetadataBudget(maximumWeightBytes, roots.Count);
            if (!metadataBudget.IsWithinBudget)
                return false;

            var descriptors = new Dictionary<MarkdownContent, ContentNodeDescriptor>(
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
                    var children = new ContentNodeDescriptor[node.Children.Count];
                    for (int childIndex = 0; childIndex < children.Length; childIndex++)
                    {
                        if ((childIndex & 63) == 0)
                            cancellationToken.ThrowIfCancellationRequested();
                        children[childIndex] = descriptors[node.Children[childIndex]];
                    }
                    descriptors.Add(node, new ContentNodeDescriptor(node, children, cancellationToken));
                }
            }

            result = new ContentNodeDescriptor[roots.Count];
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
            ContentNodeDescriptor[] roots,
            CancellationToken cancellationToken,
            out MarkdownContent[] restoredRoots)
        {
            var restored = new Dictionary<ContentNodeDescriptor, MarkdownContent>();
            var restoreScenes = new Dictionary<ContentNodeDescriptor, MarkdownVectorScene?>();
            var visiting = new HashSet<ContentNodeDescriptor>(ReferenceEqualityComparer.Instance);
            var pending = new Stack<(ContentNodeDescriptor Node, bool Exit)>();
            int processed = 0;

            for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ContentNodeDescriptor root = roots[rootIndex];
                pending.Push((root, false));
                while (pending.Count > 0)
                {
                    if ((processed++ & 63) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    (ContentNodeDescriptor node, bool exit) = pending.Pop();
                    if (restored.ContainsKey(node))
                        continue;

                    if (!exit)
                    {
                        if (node._originalNode.TryGetTarget(out MarkdownContent? originalNode))
                        {
                            restored.Add(node, originalNode);
                            continue;
                        }

                        if (node._scene is { } sceneReference)
                        {
                            if (!sceneReference.TryGetTarget(out MarkdownVectorScene? scene))
                            {
                                restoredRoots = Array.Empty<MarkdownContent>();
                                return false;
                            }
                            restoreScenes.Add(node, scene);
                        }

                        if (!visiting.Add(node))
                        {
                            restoredRoots = Array.Empty<MarkdownContent>();
                            return false;
                        }

                        pending.Push((node, true));
                        for (int childIndex = node._children.Length - 1; childIndex >= 0; childIndex--)
                        {
                            if ((childIndex & 63) == 0)
                                cancellationToken.ThrowIfCancellationRequested();
                            ContentNodeDescriptor child = node._children[childIndex];
                            if (!restored.ContainsKey(child))
                                pending.Push((child, false));
                        }
                        continue;
                    }

                    visiting.Remove(node);
                    var children = new MarkdownContent[node._children.Length];
                    for (int childIndex = 0; childIndex < children.Length; childIndex++)
                    {
                        if ((childIndex & 63) == 0)
                            cancellationToken.ThrowIfCancellationRequested();
                        children[childIndex] = restored[node._children[childIndex]];
                    }

                    IReadOnlyList<MarkdownContent> childList = children.Length == 0
                        ? Array.Empty<MarkdownContent>()
                        : new ReadOnlyCollection<MarkdownContent>(children);
                    restoreScenes.TryGetValue(node, out MarkdownVectorScene? restoredScene);
                    restored.Add(node, new MarkdownContent(
                        node._kind,
                        node._styleRole,
                        node._sourceSpan,
                        node._accessibilityRole,
                        node._text,
                        node._destination,
                        node._language,
                        node._factoryKey,
                        node._customKind,
                        restoredScene,
                        RestoreAttributes(node._attributes, cancellationToken),
                        childList,
                        node._accessibilityName,
                        node._accessibilityDescription,
                        node._semanticText));
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
                return EmptyAttributes.Value;

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

    private sealed class EmptyAttributes
    {
        internal static readonly IReadOnlyDictionary<string, string> Value =
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
    }

    private sealed class DescriptorMetadataBudget
    {
        private readonly long _maximumWeightBytes;
        private readonly HashSet<string> _strings = new(ReferenceEqualityComparer.Instance);
        private long _weightBytes;

        internal DescriptorMetadataBudget(long maximumWeightBytes, int rootCount)
        {
            _maximumWeightBytes = maximumWeightBytes;
            Add(104); // descriptor, weak-result handle, and root descriptor array
            Add(Multiply(rootCount, 8));
        }

        internal long WeightBytes => _weightBytes;

        internal bool IsWithinBudget => _weightBytes <= _maximumWeightBytes;

        internal bool TryAdd(MarkdownContent node, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(160);
            Add(Multiply(node.Children.Count, 8));
            Add(Multiply(node.Attributes.Count, 16));
            Add(node.Children.Count == 0 ? 0 : 24);
            Add(node.Attributes.Count == 0 ? 0 : 24);
            Add(Multiply(node.VectorScene is null ? 1 : 2, 40));
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
                _weightBytes = SaturatingAdd(
                    _weightBytes,
                    SaturatingAdd(24, Multiply(value.Length, sizeof(char))));
                if (_weightBytes <= _maximumWeightBytes)
                    _strings.Add(value);
            }
        }

        private void Add(long bytes)
        {
            _weightBytes = SaturatingAdd(_weightBytes, bytes);
        }
    }

    private sealed class SceneGraphWeightEstimator
    {
        private readonly WeightCounter _weight = new();
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
            var estimator = new SceneGraphWeightEstimator();
            estimator._weight.Add(80); // result and immutable fragment/list wrappers
            estimator._weight.Add(Multiply(items.Count, 8));
            estimator._weight.Add(24); // boxed immutable diagnostic view used by restored results
            estimator._weight.Add(Multiply(diagnostics.Count, IntPtr.Size));
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
            _weight.Add(Multiply(content.Children.Count, 8));
            _weight.Add(Multiply(content.Attributes.Count, 64));
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
            _weight.Add(Multiply(scene.Commands.Count, 8));
            _weight.Add(Multiply(scene.Semantics.Count, 20));
            _weight.Add(Multiply(scene.Links.Count, 16));
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
            _weight.Add(Multiply(command.Path.Count, 56));
            if (command.Path.Count > 0)
                _weight.Add(48); // path collection wrapper plus array header
            AddString(command.Text);
            if (command.Style is { } paintStyle && _paintStyles.Add(paintStyle))
            {
                _weight.Add(128);
                _weight.Add(Multiply(paintStyle.DashArray.Count, sizeof(float)));
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

    private sealed class WeightCounter
    {
        internal long Value { get; private set; }

        internal void Add(long bytes) => Value = SaturatingAdd(Value, bytes);

        internal void AddString(string? value, HashSet<string> seen)
        {
            if (value is not null && seen.Add(value))
                Add(SaturatingAdd(24, Multiply(value.Length, sizeof(char))));
        }
    }

    private static long Multiply(long left, long right) =>
        left <= 0 || right <= 0 ? 0 : left > long.MaxValue / right ? long.MaxValue : left * right;

    private static long SaturatingAdd(long left, long right) =>
        right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;
}
