using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Extensions;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class VectorSemanticParentProjectionTests
{
    [Fact]
    public void ParentAttachmentSkipsOmittedRootAndChoosesNearestExposedAncestor()
    {
        MarkdownVectorSemanticItem[] semantics = new MarkdownVectorSemanticItem[]
        {
            CreateItem(1, MarkdownVectorSemanticRole.Node, parentIndex: 3),
            CreateItem(2, MarkdownVectorSemanticRole.Group, parentIndex: 5),
            CreateItem(3, MarkdownVectorSemanticRole.Group, parentIndex: 1, decorative: true),
            CreateItem(4, MarkdownVectorSemanticRole.Node, parentIndex: 2, decorative: true),
            CreateItem(5, MarkdownVectorSemanticRole.Node, parentIndex: 1),
            CreateItem(6, MarkdownVectorSemanticRole.Diagram, parentIndex: -1),
        };
        MarkdownSemanticNode?[] nodes = CreateSemanticNodes(semantics);
        var sceneNode = new MarkdownSemanticNode(MarkdownSemanticRole.Diagram);

        MarkdownSemanticDocument.AttachVectorSemanticNodes(
            semantics,
            nodes,
            sceneNode,
            CancellationToken.None);

        Assert.Equal(new[] { 1 }, ChildIndices(sceneNode));
        Assert.Equal(new[] { 0, 4 }, ChildIndices(nodes[1]!));
        Assert.Same(nodes[1], nodes[0]!.Parent);
        Assert.Same(nodes[1], nodes[4]!.Parent);
    }

    [Fact]
    public void ParentAttachmentMatchesNearestExposedLegacyWalkForSeededForests()
    {
        var random = new Random(0x51C0DE);
        for (int iteration = 0; iteration < 64; iteration++)
        {
            int count = random.Next(2, 48);
            MarkdownVectorSemanticItem[] semantics = CreatePermutedForest(random, count);
            MarkdownSemanticNode?[] actualNodes = CreateSemanticNodes(semantics);
            MarkdownSemanticNode?[] expectedNodes = CreateSemanticNodes(semantics);
            var actualRoot = new MarkdownSemanticNode(MarkdownSemanticRole.Diagram);
            var expectedRoot = new MarkdownSemanticNode(MarkdownSemanticRole.Diagram);

            MarkdownSemanticDocument.AttachVectorSemanticNodes(
                semantics,
                actualNodes,
                actualRoot,
                CancellationToken.None);
            AttachWithLegacyWalk(semantics, expectedNodes, expectedRoot);

            AssertEquivalentTree(expectedNodes, expectedRoot, actualNodes, actualRoot);
        }
    }

    [Fact]
    public void ParentAttachmentWalksDeepOmittedSpineLinearlyWithForwardReferences()
    {
        const int leafCount = 2048;
        MarkdownVectorSemanticItem[] items = CreateDecorativeSpine(leafCount);
        MarkdownSemanticNode?[] nodes = CreateSemanticNodes(items);
        var sceneNode = new MarkdownSemanticNode(MarkdownSemanticRole.Diagram);
        var countedItems = new CountingSemanticItemList(items);

        MarkdownSemanticDocument.AttachVectorSemanticNodes(
            countedItems,
            nodes,
            sceneNode,
            CancellationToken.None);

        Assert.Equal(leafCount, sceneNode.Children.Count);
        Assert.Equal(Enumerable.Range(0, leafCount), ChildIndices(sceneNode));
        Assert.InRange(countedItems.IndexerReads, 0, (items.Length * 2) + 1);
    }

    [Fact]
    public void ParentAttachmentChecksCancellationDuringLongClimbAndUnwind()
    {
        using (var alreadyCanceled = new CancellationTokenSource())
        {
            alreadyCanceled.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                MarkdownSemanticDocument.AttachVectorSemanticNodes(
                    Array.Empty<MarkdownVectorSemanticItem>(),
                    Array.Empty<MarkdownSemanticNode?>(),
                    new MarkdownSemanticNode(MarkdownSemanticRole.Diagram),
                    alreadyCanceled.Token));
        }

        MarkdownVectorSemanticItem[] items = CreateDecorativeSpine(hiddenCount: 256, leafCount: 1);
        MarkdownSemanticNode?[] nodes = CreateSemanticNodes(items);

        using (var climbCancellation = new CancellationTokenSource())
        {
            var countedItems = new CountingSemanticItemList(
                items,
                readCount =>
                {
                    if (readCount == 10)
                        climbCancellation.Cancel();
                });
            var sceneNode = new MarkdownSemanticNode(MarkdownSemanticRole.Diagram);

            Assert.Throws<OperationCanceledException>(() =>
                MarkdownSemanticDocument.AttachVectorSemanticNodes(
                    countedItems,
                    nodes,
                    sceneNode,
                    climbCancellation.Token));
            Assert.InRange(countedItems.IndexerReads, 10, 70);
            Assert.Empty(sceneNode.Children);
        }

        using (var unwindCancellation = new CancellationTokenSource())
        {
            var countedNodes = new CountingSemanticNodeList(
                nodes,
                repeatedReadCount =>
                {
                    if (repeatedReadCount == 1)
                        unwindCancellation.Cancel();
                });
            var sceneNode = new MarkdownSemanticNode(MarkdownSemanticRole.Diagram);

            Assert.Throws<OperationCanceledException>(() =>
                MarkdownSemanticDocument.AttachVectorSemanticNodes(
                    items,
                    countedNodes,
                    sceneNode,
                    unwindCancellation.Token));
            Assert.InRange(countedNodes.RepeatedIndexerReads, 1, 64);
            Assert.Empty(sceneNode.Children);
        }
    }

    [Fact]
    public void ParentAttachmentChecksCancellationBeforeAttachingARootChild()
    {
        using var cancellation = new CancellationTokenSource();
        MarkdownVectorSemanticItem[] items =
        [
            CreateItem(1, MarkdownVectorSemanticRole.Node, parentIndex: -1),
        ];
        MarkdownSemanticNode?[] nodes = CreateSemanticNodes(items);
        var countedItems = new CountingSemanticItemList(items, _ => cancellation.Cancel());
        var sceneNode = new MarkdownSemanticNode(MarkdownSemanticRole.Diagram);

        Assert.Throws<OperationCanceledException>(() =>
            MarkdownSemanticDocument.AttachVectorSemanticNodes(
                countedItems,
                nodes,
                sceneNode,
                cancellation.Token));
        Assert.Empty(sceneNode.Children);
    }

    private static MarkdownVectorSemanticItem[] CreateDecorativeSpine(int leafCount) =>
        CreateDecorativeSpine(leafCount, leafCount);

    private static MarkdownVectorSemanticItem[] CreateDecorativeSpine(int hiddenCount, int leafCount)
    {
        int hiddenStart = leafCount;
        int diagramIndex = hiddenStart + hiddenCount;
        var semantics = new MarkdownVectorSemanticItem[diagramIndex + 1];

        for (int leafIndex = 0; leafIndex < leafCount; leafIndex++)
        {
            int parentIndex = hiddenStart + (hiddenCount - 1 - (leafIndex * hiddenCount / leafCount));
            semantics[leafIndex] = CreateItem(
                (uint)leafIndex + 1,
                MarkdownVectorSemanticRole.Node,
                parentIndex);
        }

        for (int hiddenIndex = 0; hiddenIndex < hiddenCount; hiddenIndex++)
        {
            int semanticIndex = hiddenStart + hiddenIndex;
            int parentIndex = hiddenIndex == 0 ? diagramIndex : semanticIndex - 1;
            semantics[semanticIndex] = CreateItem(
                (uint)semanticIndex + 1,
                MarkdownVectorSemanticRole.Group,
                parentIndex,
                decorative: true);
        }

        semantics[diagramIndex] = CreateItem(
            (uint)diagramIndex + 1,
            MarkdownVectorSemanticRole.Diagram,
            parentIndex: -1);
        return semantics;
    }

    private static MarkdownVectorSemanticItem[] CreatePermutedForest(Random random, int count)
    {
        var parents = new int[count];
        var roles = new MarkdownVectorSemanticRole[count];
        var flags = new MarkdownVectorSemanticFlags[count];
        parents[0] = -1;
        roles[0] = MarkdownVectorSemanticRole.Diagram;

        for (int index = 1; index < count; index++)
        {
            parents[index] = random.Next(-1, index);
            roles[index] = random.Next(5) == 0
                ? MarkdownVectorSemanticRole.Diagram
                : MarkdownVectorSemanticRole.Node;
            flags[index] = random.Next(3) == 0
                ? MarkdownVectorSemanticFlags.Decorative
                : MarkdownVectorSemanticFlags.None;
        }

        int[] newToOld = Enumerable.Range(0, count).ToArray();
        for (int index = newToOld.Length - 1; index > 0; index--)
        {
            int other = random.Next(index + 1);
            (newToOld[index], newToOld[other]) = (newToOld[other], newToOld[index]);
        }

        var oldToNew = new int[count];
        for (int newIndex = 0; newIndex < count; newIndex++)
            oldToNew[newToOld[newIndex]] = newIndex;

        var semantics = new MarkdownVectorSemanticItem[count];
        for (int newIndex = 0; newIndex < count; newIndex++)
        {
            int oldIndex = newToOld[newIndex];
            int oldParent = parents[oldIndex];
            int parentIndex = oldParent < 0 ? -1 : oldToNew[oldParent];
            semantics[newIndex] = CreateItem(
                (uint)oldIndex + 1,
                roles[oldIndex],
                parentIndex,
                decorative: (flags[oldIndex] & MarkdownVectorSemanticFlags.Decorative) != 0);
        }

        return semantics;
    }

    private static MarkdownVectorSemanticItem CreateItem(
        uint sourceId,
        MarkdownVectorSemanticRole role,
        int parentIndex,
        bool decorative = false) =>
        new(
            sourceId,
            role,
            name: $"Semantic {sourceId}",
            description: null,
            parentIndex,
            new SourceSpan((int)sourceId, 1),
            new MarkdownVectorRectangle(0, 0, 0, 0),
            decorative ? MarkdownVectorSemanticFlags.Decorative : MarkdownVectorSemanticFlags.None);

    private static MarkdownSemanticNode?[] CreateSemanticNodes(
        IReadOnlyList<MarkdownVectorSemanticItem> semantics)
    {
        var nodes = new MarkdownSemanticNode?[semantics.Count];
        for (int index = 0; index < semantics.Count; index++)
        {
            MarkdownVectorSemanticItem item = semantics[index];
            if (!MarkdownVectorSemanticPolicy.IsExposed(item.Flags) ||
                (item.ParentIndex < 0 && item.Role == MarkdownVectorSemanticRole.Diagram))
            {
                continue;
            }

            nodes[index] = new MarkdownSemanticNode(MarkdownSemanticRole.Group)
            {
                VectorSemanticIndex = index,
            };
        }

        return nodes;
    }

    private static void AttachWithLegacyWalk(
        IReadOnlyList<MarkdownVectorSemanticItem> semantics,
        MarkdownSemanticNode?[] nodes,
        MarkdownSemanticNode sceneNode)
    {
        for (int index = 0; index < nodes.Length; index++)
        {
            MarkdownSemanticNode? child = nodes[index];
            if (child is null)
                continue;

            int parent = semantics[index].ParentIndex;
            while (parent >= 0 && parent < nodes.Length && nodes[parent] is null)
                parent = semantics[parent].ParentIndex;

            if (parent >= 0 && parent < nodes.Length && nodes[parent] is { } parentNode)
                parentNode.Add(child);
            else
                sceneNode.Add(child);
        }
    }

    private static void AssertEquivalentTree(
        MarkdownSemanticNode?[] expectedNodes,
        MarkdownSemanticNode expectedRoot,
        MarkdownSemanticNode?[] actualNodes,
        MarkdownSemanticNode actualRoot)
    {
        Assert.Equal(ChildIndices(expectedRoot), ChildIndices(actualRoot));
        for (int index = 0; index < expectedNodes.Length; index++)
        {
            if (expectedNodes[index] is not { } expectedNode)
                continue;

            MarkdownSemanticNode actualNode = actualNodes[index]!;
            Assert.Equal(
                expectedNode.Parent?.VectorSemanticIndex ?? -1,
                actualNode.Parent?.VectorSemanticIndex ?? -1);
            Assert.Equal(ChildIndices(expectedNode), ChildIndices(actualNode));
        }
    }

    private static int[] ChildIndices(MarkdownSemanticNode node) =>
        node.Children.Select(static child => child.VectorSemanticIndex).ToArray();

    private sealed class CountingSemanticItemList : IReadOnlyList<MarkdownVectorSemanticItem>
    {
        private readonly IReadOnlyList<MarkdownVectorSemanticItem> _items;
        private readonly Action<int>? _afterRead;

        public CountingSemanticItemList(
            IReadOnlyList<MarkdownVectorSemanticItem> items,
            Action<int>? afterRead = null)
        {
            _items = items;
            _afterRead = afterRead;
        }

        public int IndexerReads { get; private set; }

        public int Count => _items.Count;

        public MarkdownVectorSemanticItem this[int index]
        {
            get
            {
                _afterRead?.Invoke(++IndexerReads);
                return _items[index];
            }
        }

        public IEnumerator<MarkdownVectorSemanticItem> GetEnumerator() => _items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CountingSemanticNodeList : IReadOnlyList<MarkdownSemanticNode?>
    {
        private readonly IReadOnlyList<MarkdownSemanticNode?> _nodes;
        private readonly Action<int>? _afterRepeatedRead;
        private readonly bool[] _seen;

        public CountingSemanticNodeList(
            IReadOnlyList<MarkdownSemanticNode?> nodes,
            Action<int>? afterRepeatedRead = null)
        {
            _nodes = nodes;
            _afterRepeatedRead = afterRepeatedRead;
            _seen = new bool[nodes.Count];
        }

        public int RepeatedIndexerReads { get; private set; }

        public int Count => _nodes.Count;

        public MarkdownSemanticNode? this[int index]
        {
            get
            {
                if (_seen[index])
                    _afterRepeatedRead?.Invoke(++RepeatedIndexerReads);
                else
                    _seen[index] = true;
                return _nodes[index];
            }
        }

        public IEnumerator<MarkdownSemanticNode?> GetEnumerator() => _nodes.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
