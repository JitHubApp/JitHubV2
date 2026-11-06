using System;
using System.Collections;
using System.Collections.Generic;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// A live value view of a <see cref="MarkdownSemanticNode"/>'s children. A
/// retained view observes children added later, and the view adds no
/// per-node allocation.
/// </summary>
internal readonly struct MarkdownSemanticChildrenView : IReadOnlyList<MarkdownSemanticNode>
{
    private readonly MarkdownSemanticNode? _owner;

    internal MarkdownSemanticChildrenView(MarkdownSemanticNode owner)
    {
        _owner = owner;
    }

    internal MarkdownSemanticNode? Owner => _owner;

    public int Count => _owner?.ChildCount ?? 0;

    public MarkdownSemanticNode this[int index]
    {
        get
        {
            if (_owner is { } owner)
                return owner.GetChildAt(index);

            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    // Foreach over the concrete Children property uses this value-returning
    // pattern method and does not box the enumerator. Interface callers
    // retain IReadOnlyList compatibility through the explicit adapters.
    public ChildrenEnumerator GetEnumerator()
        => new(_owner, _owner?.ChildrenVersion ?? 0);

    IEnumerator<MarkdownSemanticNode> IEnumerable<MarkdownSemanticNode>.GetEnumerator()
        => Count == 0
            ? ((IEnumerable<MarkdownSemanticNode>)Array.Empty<MarkdownSemanticNode>()).GetEnumerator()
            : GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator()
        => Count == 0
            ? ((IEnumerable<MarkdownSemanticNode>)Array.Empty<MarkdownSemanticNode>()).GetEnumerator()
            : GetEnumerator();

    public struct ChildrenEnumerator : IEnumerator<MarkdownSemanticNode>
    {
        private readonly MarkdownSemanticNode? _owner;
        private readonly int _version;
        private int _index;
        private MarkdownSemanticNode? _current;

        internal ChildrenEnumerator(MarkdownSemanticNode? owner, int version)
        {
            _owner = owner;
            _version = version;
            _index = 0;
            _current = null;
        }

        public MarkdownSemanticNode Current => _current!;

        object IEnumerator.Current
        {
            get
            {
                return _index <= 0
                    ? throw new InvalidOperationException("The enumerator is not positioned on a child.")
                    : Current;
            }
        }

        public bool MoveNext()
        {
            if (_owner is { } owner && _version != owner.ChildrenVersion)
                throw new InvalidOperationException("The semantic child collection was modified during enumeration.");

            int count = _owner?.ChildCount ?? 0;
            if ((uint)_index < (uint)count)
            {
                _current = _owner!.GetChildAt(_index++);
                return true;
            }

            // Keep exhaustion independent of the owner's live child count:
            // appending later must not make non-generic Current readable.
            _index = -1;
            _current = null;
            return false;
        }

        public void Reset()
        {
            if (_owner is { } owner && _version != owner.ChildrenVersion)
                throw new InvalidOperationException("The semantic child collection was modified during enumeration.");

            _index = 0;
            _current = null;
        }

        public void Dispose()
        {
        }
    }
}
