using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Windows.Foundation;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Accessibility;

internal enum MarkdownSemanticRole
{
    Document,
    Group,
    Paragraph,
    Heading,
    CodeBlock,
    Link,
    List,
    ListItem,
    Table,
    TableCell,
    Image,
    Math,
    Diagram,
    Embed,
    Abbreviation,
}

internal sealed class MarkdownSemanticNode
{
    private const byte InlineBoxOwner = 1 << 0;
    private const byte ImageBoxOwner = 1 << 1;
    private const byte EmbedBoxOwner = 1 << 2;
    private const byte HostedElementBoxOwner = 1 << 3;
    private const byte VectorSceneBoxOwner = 1 << 4;
    private const byte TableBoxOwner = 1 << 5;

    private const byte CodeLanguageText = 1 << 0;
    private const byte HelpTextText = 1 << 1;
    private const byte AccessibilityNameText = 1 << 2;
    private const byte AccessibilityDescriptionText = 1 << 3;
    private const byte AutomationIdText = 1 << 4;

    private const byte NumericKindNone = 0;
    private const byte NumericKindTable = 1;
    private const byte NumericKindList = 2;
    private const byte NumericKindVector = 3;
    private const byte NumericKindMixed = 4;
    private const byte NumericRolePresent = 1 << 0;
    private const byte NumericHeader = 1 << 0;

    // Leaves need no child container. Keep a unary node's first child directly
    // in this existing reference slot, promoting to a list only when a second
    // child is added, without changing the live Children view.
    private object? _children;
    private object? _owners;
    private object? _texts;
    private NumericMetadata? _mixedNumeric;
    private MarkdownSemanticNode? _parent;
    private int _childrenVersion;

    // Compact metadata writes are single-writer construction. The three list
    // position properties retain setters for the existing builder sequence,
    // which completes before a semantic node is published to its parent.
    private byte _ownerMask;
    private byte _textMask;
    private byte _numericKind;
    private byte _numericFlags;
    private int _numeric0;
    private int _numeric1;
    private int _numeric2;
    private int _numeric3;
    private int _numeric4;
    private int _numeric5;

    public readonly MarkdownSemanticRole Role;
    public readonly BlockBox? Box;

    public MarkdownSemanticNode(MarkdownSemanticRole role, BlockBox? box = null)
    {
        Role = role;
        Box = box;
    }

    // Most semantic nodes have either no typed owner or one typed owner. Keep
    // that owner directly in a shared slot and use a small mask to retain the
    // exact typed accessor contract (notably, Box does not implicitly populate
    // any of these properties). The rare synthetic multi-owner node gets a
    // compatibility holder.
    public InlineContainerBox? InlineBox
    {
        get => _owners is OwnerMetadata values
            ? values.InlineBox
            : (_ownerMask & InlineBoxOwner) != 0 ? (InlineContainerBox?)_owners : null;
        init => SetOwner(InlineBoxOwner, value);
    }

    public ImageBox? ImageBox
    {
        get => _owners is OwnerMetadata values
            ? values.ImageBox
            : (_ownerMask & ImageBoxOwner) != 0 ? (ImageBox?)_owners : null;
        init => SetOwner(ImageBoxOwner, value);
    }

    public EmbedBox? EmbedBox
    {
        get => _owners is OwnerMetadata values
            ? values.EmbedBox
            : (_ownerMask & EmbedBoxOwner) != 0 ? (EmbedBox?)_owners : null;
        init => SetOwner(EmbedBoxOwner, value);
    }

    public DeclarativeHostedElementBox? HostedElementBox
    {
        get => _owners is OwnerMetadata values
            ? values.HostedElementBox
            : (_ownerMask & HostedElementBoxOwner) != 0 ? (DeclarativeHostedElementBox?)_owners : null;
        init => SetOwner(HostedElementBoxOwner, value);
    }

    public VectorSceneBox? VectorSceneBox
    {
        get => _owners is OwnerMetadata values
            ? values.VectorSceneBox
            : (_ownerMask & VectorSceneBoxOwner) != 0 ? (VectorSceneBox?)_owners : null;
        init => SetOwner(VectorSceneBoxOwner, value);
    }

    public int VectorSemanticIndex
    {
        get => _mixedNumeric?.VectorSemanticIndex ??
            (_numericKind == NumericKindVector ? _numeric0 : -1);
        init => SetVectorSemanticIndex(value);
    }

    public MarkdownVectorSemanticRole? VectorSemanticRole
    {
        get
        {
            if (_mixedNumeric is { } mixed)
                return mixed.HasVectorSemanticRole ? (MarkdownVectorSemanticRole)mixed.VectorSemanticRole : null;
            return _numericKind == NumericKindVector && (_numericFlags & NumericRolePresent) != 0
                ? (MarkdownVectorSemanticRole)_numeric1
                : null;
        }
        init => SetVectorSemanticRole(value);
    }

    public MarkdownVectorSemanticFlags VectorSemanticFlags
    {
        get => (MarkdownVectorSemanticFlags)(_mixedNumeric?.VectorSemanticFlags ??
            (_numericKind == NumericKindVector ? _numeric2 : 0));
        init => SetVectorSemanticFlags(value);
    }

    public TableBox? TableBox
    {
        get => _owners is OwnerMetadata values
            ? values.TableBox
            : (_ownerMask & TableBoxOwner) != 0 ? (TableBox?)_owners : null;
        init => SetOwner(TableBoxOwner, value);
    }

    public InlineRun? InlineRun { get; init; }
    public MarkdownSemanticNode? Parent
    {
        get => _parent;
        private set => _parent = value;
    }
    // A value view keeps children live across construction without making each
    // node itself an enumerable WinRT collection. Retained views continue to
    // observe children added later, and the view adds no per-node allocation.
    public ChildrenView Children => new(this);
    public int TextStart { get; set; }
    public int TextEnd { get; set; }
    public int HeadingLevel { get; init; }
    public int Row
    {
        get => _mixedNumeric?.Row ?? (_numericKind == NumericKindTable ? _numeric0 : -1);
        init => SetTableValue(0, value, -1);
    }

    public int Column
    {
        get => _mixedNumeric?.Column ?? (_numericKind == NumericKindTable ? _numeric1 : -1);
        init => SetTableValue(1, value, -1);
    }

    public int RowSpan
    {
        get => _mixedNumeric?.RowSpan ?? (_numericKind == NumericKindTable ? _numeric2 : 1);
        init => SetTableValue(2, value, 1);
    }

    public int ColumnSpan
    {
        get => _mixedNumeric?.ColumnSpan ?? (_numericKind == NumericKindTable ? _numeric3 : 1);
        init => SetTableValue(3, value, 1);
    }

    public int RowCount
    {
        get => _mixedNumeric?.RowCount ?? (_numericKind == NumericKindTable ? _numeric4 : 0);
        init => SetTableValue(4, value, 0);
    }

    public int ColumnCount
    {
        get => _mixedNumeric?.ColumnCount ?? (_numericKind == NumericKindTable ? _numeric5 : 0);
        init => SetTableValue(5, value, 0);
    }

    public int Level
    {
        get => _mixedNumeric?.Level ?? (_numericKind == NumericKindList ? _numeric0 : 0);
        set => SetListValue(0, value);
    }

    public int PositionInSet
    {
        get => _mixedNumeric?.PositionInSet ?? (_numericKind == NumericKindList ? _numeric1 : 0);
        set => SetListValue(1, value);
    }

    public int SizeOfSet
    {
        get => _mixedNumeric?.SizeOfSet ?? (_numericKind == NumericKindList ? _numeric2 : 0);
        set => SetListValue(2, value);
    }

    public bool IsHeader
    {
        get => _mixedNumeric?.IsHeader ??
            (_numericKind == NumericKindTable && (_numericFlags & NumericHeader) != 0);
        init => SetIsHeader(value);
    }

    public MarkdownAccessibilityRole AccessibilityRole { get; init; }

    public string? CodeLanguage
    {
        get => GetText(CodeLanguageText);
        init => SetText(CodeLanguageText, value);
    }

    public string? HelpText
    {
        get => GetText(HelpTextText);
        init => SetText(HelpTextText, value);
    }

    public string? AccessibilityName
    {
        get => GetText(AccessibilityNameText);
        init => SetText(AccessibilityNameText, value);
    }

    public string? AccessibilityDescription
    {
        get => GetText(AccessibilityDescriptionText);
        init => SetText(AccessibilityDescriptionText, value);
    }

    public string? AutomationId
    {
        get => GetText(AutomationIdText);
        init => SetText(AutomationIdText, value);
    }

    private void SetOwner(byte owner, object? value)
    {
        if (_owners is OwnerMetadata values)
        {
            values.Set(owner, value);
            if (value is null)
                _ownerMask = (byte)(_ownerMask & ~owner);
            else
                _ownerMask |= owner;
            return;
        }

        if (value is null)
        {
            if ((_ownerMask & owner) != 0)
            {
                _ownerMask = (byte)(_ownerMask & ~owner);
                if (_ownerMask == 0)
                    _owners = null;
            }
            return;
        }

        if (_ownerMask == 0)
        {
            _owners = value;
            _ownerMask = owner;
            return;
        }

        if (ReferenceEquals(_owners, value))
        {
            _ownerMask |= owner;
            return;
        }

        if (_ownerMask == owner)
        {
            _owners = value;
            return;
        }

        var promoted = new OwnerMetadata(_ownerMask, _owners!);
        promoted.Set(owner, value);
        _owners = promoted;
        _ownerMask |= owner;
    }

    private string? GetText(byte property)
    {
        if (_texts is TextMetadata metadata)
            return metadata.Get(property);
        return (_textMask & property) != 0 ? (string?)_texts : null;
    }

    private void SetText(byte property, string? value)
    {
        if (_texts is TextMetadata metadata)
        {
            metadata.Set(property, value);
            return;
        }

        if (value is null)
        {
            if ((_textMask & property) != 0)
            {
                _textMask = (byte)(_textMask & ~property);
                if (_textMask == 0)
                    _texts = null;
            }
            return;
        }

        if (_textMask == 0)
        {
            _texts = value;
            _textMask = property;
            return;
        }

        if (ReferenceEquals(_texts, value))
        {
            _textMask |= property;
            return;
        }

        if (_textMask == property)
        {
            _texts = value;
            return;
        }

        var promoted = new TextMetadata((string)_texts!, _textMask);
        promoted.Set(property, value);
        _texts = promoted;
        _textMask = 0;
    }

    private void SetTableValue(int slot, int value, int defaultValue)
    {
        if (_mixedNumeric is { } mixed)
        {
            mixed.SetTableValue(slot, value);
            return;
        }

        if (_numericKind != NumericKindTable)
        {
            if (value == defaultValue)
                return;
            EnsureNumericKind(NumericKindTable);
        }

        if (_mixedNumeric is { } promoted)
        {
            promoted.SetTableValue(slot, value);
            return;
        }

        SetInlineNumeric(slot, value);
    }

    private void SetListValue(int slot, int value)
    {
        if (_mixedNumeric is { } mixed)
        {
            mixed.SetListValue(slot, value);
            return;
        }

        if (_numericKind != NumericKindList)
        {
            if (value == 0)
                return;
            EnsureNumericKind(NumericKindList);
        }

        if (_mixedNumeric is { } promoted)
        {
            promoted.SetListValue(slot, value);
            return;
        }

        SetInlineNumeric(slot, value);
    }

    private void SetVectorSemanticIndex(int value)
    {
        if (_mixedNumeric is { } mixed)
        {
            mixed.VectorSemanticIndex = value;
            return;
        }

        if (_numericKind != NumericKindVector)
        {
            if (value == -1)
                return;
            EnsureNumericKind(NumericKindVector);
        }

        if (_mixedNumeric is { } promoted)
        {
            promoted.VectorSemanticIndex = value;
            return;
        }

        _numeric0 = value;
    }

    private void SetVectorSemanticRole(MarkdownVectorSemanticRole? value)
    {
        if (_mixedNumeric is { } mixed)
        {
            mixed.HasVectorSemanticRole = value.HasValue;
            if (value.HasValue)
                mixed.VectorSemanticRole = (int)value.Value;
            return;
        }

        if (_numericKind != NumericKindVector)
        {
            if (!value.HasValue)
                return;
            EnsureNumericKind(NumericKindVector);
        }

        if (_mixedNumeric is { } promoted)
        {
            promoted.HasVectorSemanticRole = value.HasValue;
            if (value.HasValue)
                promoted.VectorSemanticRole = (int)value.Value;
            return;
        }

        if (value.HasValue)
        {
            _numeric1 = (int)value.Value;
            _numericFlags |= NumericRolePresent;
        }
        else
        {
            _numericFlags = (byte)(_numericFlags & ~NumericRolePresent);
        }
    }

    private void SetVectorSemanticFlags(MarkdownVectorSemanticFlags value)
    {
        if (_mixedNumeric is { } mixed)
        {
            mixed.VectorSemanticFlags = (int)value;
            return;
        }

        if (_numericKind != NumericKindVector)
        {
            if (value == MarkdownVectorSemanticFlags.None)
                return;
            EnsureNumericKind(NumericKindVector);
        }

        if (_mixedNumeric is { } promoted)
        {
            promoted.VectorSemanticFlags = (int)value;
            return;
        }

        _numeric2 = (int)value;
    }

    private void SetIsHeader(bool value)
    {
        if (_mixedNumeric is { } mixed)
        {
            mixed.IsHeader = value;
            return;
        }

        if (_numericKind != NumericKindTable)
        {
            if (!value)
                return;
            EnsureNumericKind(NumericKindTable);
        }

        if (_mixedNumeric is { } promoted)
        {
            promoted.IsHeader = value;
            return;
        }

        if (value)
            _numericFlags |= NumericHeader;
        else
            _numericFlags = (byte)(_numericFlags & ~NumericHeader);
    }

    private void EnsureNumericKind(byte kind)
    {
        if (_numericKind == kind)
            return;

        if (_numericKind == NumericKindNone)
        {
            _numericKind = kind;
            _numericFlags = 0;
            switch (kind)
            {
                case NumericKindTable:
                    _numeric0 = -1;
                    _numeric1 = -1;
                    _numeric2 = 1;
                    _numeric3 = 1;
                    _numeric4 = 0;
                    _numeric5 = 0;
                    break;
                case NumericKindList:
                    _numeric0 = 0;
                    _numeric1 = 0;
                    _numeric2 = 0;
                    break;
                case NumericKindVector:
                    _numeric0 = -1;
                    _numeric1 = 0;
                    _numeric2 = 0;
                    break;
            }
            return;
        }

        PromoteNumericToMixed();
    }

    private void PromoteNumericToMixed()
    {
        var mixed = new NumericMetadata();
        switch (_numericKind)
        {
            case NumericKindTable:
                mixed.Row = _numeric0;
                mixed.Column = _numeric1;
                mixed.RowSpan = _numeric2;
                mixed.ColumnSpan = _numeric3;
                mixed.RowCount = _numeric4;
                mixed.ColumnCount = _numeric5;
                mixed.IsHeader = (_numericFlags & NumericHeader) != 0;
                break;
            case NumericKindList:
                mixed.Level = _numeric0;
                mixed.PositionInSet = _numeric1;
                mixed.SizeOfSet = _numeric2;
                break;
            case NumericKindVector:
                mixed.VectorSemanticIndex = _numeric0;
                mixed.VectorSemanticRole = _numeric1;
                mixed.HasVectorSemanticRole = (_numericFlags & NumericRolePresent) != 0;
                mixed.VectorSemanticFlags = _numeric2;
                break;
        }

        _mixedNumeric = mixed;
        _numericKind = NumericKindMixed;
        _numericFlags = 0;
    }

    private void SetInlineNumeric(int slot, int value)
    {
        switch (slot)
        {
            case 0: _numeric0 = value; break;
            case 1: _numeric1 = value; break;
            case 2: _numeric2 = value; break;
            case 3: _numeric3 = value; break;
            case 4: _numeric4 = value; break;
            case 5: _numeric5 = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(slot));
        }
    }

    private sealed class OwnerMetadata(byte mask, object owner)
    {
        internal InlineContainerBox? InlineBox = (mask & InlineBoxOwner) != 0 ? (InlineContainerBox)owner : null;
        internal ImageBox? ImageBox = (mask & ImageBoxOwner) != 0 ? (ImageBox)owner : null;
        internal EmbedBox? EmbedBox = (mask & EmbedBoxOwner) != 0 ? (EmbedBox)owner : null;
        internal DeclarativeHostedElementBox? HostedElementBox =
            (mask & HostedElementBoxOwner) != 0 ? (DeclarativeHostedElementBox)owner : null;
        internal VectorSceneBox? VectorSceneBox = (mask & VectorSceneBoxOwner) != 0 ? (VectorSceneBox)owner : null;
        internal TableBox? TableBox = (mask & TableBoxOwner) != 0 ? (TableBox)owner : null;

        internal void Set(byte owner, object? value)
        {
            switch (owner)
            {
                case InlineBoxOwner: InlineBox = (InlineContainerBox?)value; break;
                case ImageBoxOwner: ImageBox = (ImageBox?)value; break;
                case EmbedBoxOwner: EmbedBox = (EmbedBox?)value; break;
                case HostedElementBoxOwner: HostedElementBox = (DeclarativeHostedElementBox?)value; break;
                case VectorSceneBoxOwner: VectorSceneBox = (VectorSceneBox?)value; break;
                case TableBoxOwner: TableBox = (TableBox?)value; break;
                default: throw new ArgumentOutOfRangeException(nameof(owner));
            }
        }
    }

    private sealed class TextMetadata(string text, byte mask)
    {
        internal string? CodeLanguage = (mask & CodeLanguageText) != 0 ? text : null;
        internal string? HelpText = (mask & HelpTextText) != 0 ? text : null;
        internal string? AccessibilityName = (mask & AccessibilityNameText) != 0 ? text : null;
        internal string? AccessibilityDescription = (mask & AccessibilityDescriptionText) != 0 ? text : null;
        internal string? AutomationId = (mask & AutomationIdText) != 0 ? text : null;

        internal string? Get(byte property) => property switch
        {
            CodeLanguageText => CodeLanguage,
            HelpTextText => HelpText,
            AccessibilityNameText => AccessibilityName,
            AccessibilityDescriptionText => AccessibilityDescription,
            AutomationIdText => AutomationId,
            _ => throw new ArgumentOutOfRangeException(nameof(property)),
        };

        internal void Set(byte property, string? value)
        {
            switch (property)
            {
                case CodeLanguageText: CodeLanguage = value; break;
                case HelpTextText: HelpText = value; break;
                case AccessibilityNameText: AccessibilityName = value; break;
                case AccessibilityDescriptionText: AccessibilityDescription = value; break;
                case AutomationIdText: AutomationId = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(property));
            }
        }
    }

    private sealed class NumericMetadata
    {
        internal int Row = -1;
        internal int Column = -1;
        internal int RowSpan = 1;
        internal int ColumnSpan = 1;
        internal int RowCount;
        internal int ColumnCount;
        internal int Level;
        internal int PositionInSet;
        internal int SizeOfSet;
        internal int VectorSemanticIndex = -1;
        internal int VectorSemanticRole;
        internal int VectorSemanticFlags;
        internal bool HasVectorSemanticRole;
        internal bool IsHeader;

        internal void SetTableValue(int slot, int value)
        {
            switch (slot)
            {
                case 0: Row = value; break;
                case 1: Column = value; break;
                case 2: RowSpan = value; break;
                case 3: ColumnSpan = value; break;
                case 4: RowCount = value; break;
                case 5: ColumnCount = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(slot));
            }
        }

        internal void SetListValue(int slot, int value)
        {
            switch (slot)
            {
                case 0: Level = value; break;
                case 1: PositionInSet = value; break;
                case 2: SizeOfSet = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(slot));
            }
        }
    }

    public Rect Bounds
    {
        get
        {
            if (InlineBox is not null && InlineRun is not null)
            {
                var runRect = InlineBox.GetRunRect(InlineRun.InlineIndex);
                if (runRect.Width > 0 && runRect.Height > 0)
                    return runRect;
            }

            if (VectorSceneBox is { } vector)
                return VectorSemanticIndex >= 0 ? vector.GetSemanticBounds(VectorSemanticIndex) : vector.Bounds;

            return Box?.Bounds ?? InlineBox?.Bounds ?? ImageBox?.Bounds ?? EmbedBox?.Bounds ?? HostedElementBox?.Bounds ?? default;
        }
    }

    internal int LayoutBlockIndex => VectorSceneBox?.BlockIndex ?? Box?.BlockIndex ??
        InlineBox?.BlockIndex ?? ImageBox?.BlockIndex ?? EmbedBox?.BlockIndex ??
        HostedElementBox?.BlockIndex ?? -1;

    public void Add(MarkdownSemanticNode child)
    {
        child.Parent = this;
        if (_children is null)
        {
            _children = child;
        }
        else if (_children is MarkdownSemanticNode firstChild)
        {
            var children = new List<MarkdownSemanticNode>(2)
            {
                firstChild,
                child,
            };
            _children = children;
        }
        else
        {
            ((List<MarkdownSemanticNode>)_children).Add(child);
        }

        _childrenVersion++;
    }

    private int ChildCount => _children switch
    {
        null => 0,
        MarkdownSemanticNode => 1,
        List<MarkdownSemanticNode> children => children.Count,
        _ => throw new InvalidOperationException("Invalid semantic child storage."),
    };

    private MarkdownSemanticNode GetChildAt(int index)
    {
        return _children switch
        {
            MarkdownSemanticNode child when index == 0 => child,
            List<MarkdownSemanticNode> children => children[index],
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };
    }

    internal readonly struct ChildrenView : IReadOnlyList<MarkdownSemanticNode>
    {
        private readonly MarkdownSemanticNode? _owner;

        internal ChildrenView(MarkdownSemanticNode owner)
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
            => new(_owner, _owner?._childrenVersion ?? 0);

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
                if (_owner is { } owner && _version != owner._childrenVersion)
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
                if (_owner is { } owner && _version != owner._childrenVersion)
                    throw new InvalidOperationException("The semantic child collection was modified during enumeration.");

                _index = 0;
                _current = null;
            }

            public void Dispose()
            {
            }
        }
    }
}

internal readonly struct MarkdownTextSpan
{
    // A normal semantic span owns zero or one layout box; store that owner in
    // one reference slot. The private fallback object retains the exact typed
    // fields for uncommon synthetic spans that intentionally supply several.
    private readonly object? _boxOwner;

    public readonly int TextStart;
    public readonly int TextEnd;
    public readonly InlineRun? InlineRun;
    public readonly int VectorSemanticIndex;
    public readonly ElementStyle? VectorTextStyle;

    internal int LayoutBlockIndex => InlineBox?.BlockIndex ?? VectorSceneBox?.BlockIndex ??
        ImageBox?.BlockIndex ?? EmbedBox?.BlockIndex ?? HostedElementBox?.BlockIndex ?? -1;

    public InlineContainerBox? InlineBox => _boxOwner is BoxOwners boxes
        ? boxes.InlineBox
        : _boxOwner as InlineContainerBox;
    public ImageBox? ImageBox => _boxOwner is BoxOwners boxes
        ? boxes.ImageBox
        : _boxOwner as ImageBox;
    public EmbedBox? EmbedBox => _boxOwner is BoxOwners boxes
        ? boxes.EmbedBox
        : _boxOwner as EmbedBox;
    public DeclarativeHostedElementBox? HostedElementBox => _boxOwner is BoxOwners boxes
        ? boxes.HostedElementBox
        : _boxOwner as DeclarativeHostedElementBox;
    public VectorSceneBox? VectorSceneBox => _boxOwner is BoxOwners boxes
        ? boxes.VectorSceneBox
        : _boxOwner as VectorSceneBox;

    public MarkdownTextSpan(
        int TextStart,
        int TextEnd,
        InlineContainerBox? InlineBox,
        InlineRun? InlineRun,
        ImageBox? ImageBox,
        EmbedBox? EmbedBox,
        DeclarativeHostedElementBox? HostedElementBox,
        VectorSceneBox? VectorSceneBox = null,
        int VectorSemanticIndex = -1,
        ElementStyle? VectorTextStyle = null)
    {
        this.TextStart = TextStart;
        this.TextEnd = TextEnd;
        this.InlineRun = InlineRun;
        this.VectorSemanticIndex = VectorSemanticIndex;
        this.VectorTextStyle = VectorTextStyle;

        int ownerCount =
            (InlineBox is null ? 0 : 1) +
            (ImageBox is null ? 0 : 1) +
            (EmbedBox is null ? 0 : 1) +
            (HostedElementBox is null ? 0 : 1) +
            (VectorSceneBox is null ? 0 : 1);
        if (ownerCount <= 1)
        {
            object? singleOwner = InlineBox;
            singleOwner ??= ImageBox;
            singleOwner ??= EmbedBox;
            singleOwner ??= HostedElementBox;
            singleOwner ??= VectorSceneBox;
            _boxOwner = singleOwner;
        }
        else
        {
            // Preserve even unusual multi-owner spans exactly. Product builders
            // emit a single owner, so this compatibility holder is only allocated
            // for uncommon manually constructed or synthetic spans.
            _boxOwner = new BoxOwners(InlineBox, ImageBox, EmbedBox, HostedElementBox, VectorSceneBox);
        }
    }

    internal MarkdownTextSpan ClampTextRange(int textLength)
    {
        int start = Math.Clamp(TextStart, 0, textLength);
        int end = Math.Clamp(TextEnd, start, textLength);
        if (start == TextStart && end == TextEnd)
            return this;

        return new MarkdownTextSpan(
            start,
            end,
            InlineBox,
            InlineRun,
            ImageBox,
            EmbedBox,
            HostedElementBox,
            VectorSceneBox,
            VectorSemanticIndex,
            VectorTextStyle);
    }

    private sealed class BoxOwners(
        InlineContainerBox? InlineBox,
        ImageBox? ImageBox,
        EmbedBox? EmbedBox,
        DeclarativeHostedElementBox? HostedElementBox,
        VectorSceneBox? VectorSceneBox)
    {
        internal readonly InlineContainerBox? InlineBox = InlineBox;
        internal readonly ImageBox? ImageBox = ImageBox;
        internal readonly EmbedBox? EmbedBox = EmbedBox;
        internal readonly DeclarativeHostedElementBox? HostedElementBox = HostedElementBox;
        internal readonly VectorSceneBox? VectorSceneBox = VectorSceneBox;
    }
}

internal sealed class MarkdownSemanticDocument
{
    private const int SemanticRangeFingerprintBatchBytes = 4 * 1024;

    private readonly struct SemanticNodeIndexes(
        Dictionary<InlineContainerBox, MarkdownSemanticNode> ByInlineBox,
        Dictionary<int, MarkdownSemanticNode> ByInlineBlockIndex,
        Dictionary<IHorizontalOverflowBox, MarkdownSemanticNode> ByHorizontalOverflow)
    {
        public Dictionary<InlineContainerBox, MarkdownSemanticNode> ByInlineBox { get; init; } = ByInlineBox;
        public Dictionary<int, MarkdownSemanticNode> ByInlineBlockIndex { get; init; } = ByInlineBlockIndex;
        public Dictionary<IHorizontalOverflowBox, MarkdownSemanticNode> ByHorizontalOverflow { get; init; } = ByHorizontalOverflow;
    }

    public readonly MarkdownSemanticNode Root;
    public readonly string Text;
    internal readonly MarkdownAutomationTextFingerprint TextProjectionFingerprint;
    internal readonly MarkdownAutomationTextFingerprint SemanticRangeFingerprint;
    public readonly TextElementBoundaryIndex TextElementBoundaries;
    public readonly IReadOnlyList<MarkdownTextSpan> TextSpans;

    private readonly IReadOnlyDictionary<(int BlockIndex, int InlineIndex), int> _spanByPosition;
    private readonly IReadOnlyDictionary<InlineContainerBox, MarkdownSemanticNode> _nodeByInlineBox;
    private readonly IReadOnlyDictionary<int, MarkdownSemanticNode> _nodeByInlineBlockIndex;
    private readonly IReadOnlyDictionary<IHorizontalOverflowBox, MarkdownSemanticNode> _nodeByHorizontalOverflow;

    public static MarkdownSemanticDocument Empty { get; } = new(
        new MarkdownSemanticNode(MarkdownSemanticRole.Document)
        {
            TextStart = 0,
            TextEnd = 0,
        },
        string.Empty,
        Array.Empty<MarkdownTextSpan>());

    private MarkdownSemanticDocument(MarkdownSemanticNode root, string text, IReadOnlyList<MarkdownTextSpan> spans)
        : this(root, text, spans, textProjectionReuse: null)
    {
    }

    private MarkdownSemanticDocument(
        MarkdownSemanticNode root,
        string text,
        IReadOnlyList<MarkdownTextSpan> spans,
        MarkdownSemanticTextProjectionReuse? textProjectionReuse)
        : this(root, text, spans, textProjectionReuse, nodeIndexes: null)
    {
    }

    private MarkdownSemanticDocument(
        MarkdownSemanticNode root,
        string text,
        IReadOnlyList<MarkdownTextSpan> spans,
        MarkdownSemanticTextProjectionReuse? textProjectionReuse,
        SemanticNodeIndexes? nodeIndexes)
    {
        Root = root;
        Text = text;
        if (textProjectionReuse is { } reuse)
        {
            if (!ReferenceEquals(text, reuse.Text) || reuse.TextElementBoundaries.TextLength != text.Length)
                throw new ArgumentException("A reused text projection must match the semantic text exactly.", nameof(textProjectionReuse));
            TextProjectionFingerprint = reuse.TextProjectionFingerprint;
            TextElementBoundaries = reuse.TextElementBoundaries;
        }
        else
        {
            TextProjectionFingerprint = MarkdownAutomationTextFingerprint.Compute(text);
            TextElementBoundaries = new TextElementBoundaryIndex(text);
        }
        TextSpans = spans;
        int mappableSpanCount = 0;
        bool hasPreviousMappedKey = false;
        (int BlockIndex, int InlineIndex) previousMappedKey = default;
        for (int i = 0; i < spans.Count; i++)
        {
            MarkdownTextSpan span = spans[i];
            (int BlockIndex, int InlineIndex) key;
            if (span.InlineBox is { } inline && span.InlineRun is { } run)
            {
                key = (inline.BlockIndex, run.InlineIndex);
            }
            else if (span.VectorSceneBox is { } vector)
            {
                key = (vector.BlockIndex, GetVectorDocumentInlineIndex(span.VectorSemanticIndex));
            }
            else
            {
                hasPreviousMappedKey = false;
                continue;
            }

            if (!hasPreviousMappedKey || key != previousMappedKey)
            {
                mappableSpanCount++;
            }
            previousMappedKey = key;
            hasPreviousMappedKey = true;
        }

        // Standalone images and other atomic-only documents can have many
        // semantic spans but no position-index entries. Size from consecutive
        // distinct mapped keys rather than the full span count; the ordered
        // fill/TryAdd loop below still preserves first-wins behavior for every
        // duplicate, including nonconsecutive duplicates.
        var spanByPosition = new Dictionary<(int BlockIndex, int InlineIndex), int>(mappableSpanCount);
        for (int i = 0; i < spans.Count; i++)
        {
            MarkdownTextSpan span = spans[i];
            if (span.InlineBox is { } inline && span.InlineRun is { } run)
                spanByPosition.TryAdd((inline.BlockIndex, run.InlineIndex), i);
            else if (span.VectorSceneBox is { } vector)
                spanByPosition.TryAdd((vector.BlockIndex, GetVectorDocumentInlineIndex(span.VectorSemanticIndex)), i);
        }
        _spanByPosition = spanByPosition;

        if (nodeIndexes is { } indexes)
        {
            _nodeByInlineBox = indexes.ByInlineBox;
            _nodeByInlineBlockIndex = indexes.ByInlineBlockIndex;
            _nodeByHorizontalOverflow = indexes.ByHorizontalOverflow;
        }
        else
        {
            // Keep the fallback for manually-constructed semantic roots and
            // private-constructor consumers. Production Builder snapshots pass
            // indexes populated as nodes are created, avoiding this extra walk.
            var nodeByInlineBox = new Dictionary<InlineContainerBox, MarkdownSemanticNode>();
            var nodeByHorizontalOverflow = new Dictionary<IHorizontalOverflowBox, MarkdownSemanticNode>(
                ReferenceEqualityComparer.Instance);
            foreach (MarkdownSemanticNode node in EnumerateDepthFirst(root))
            {
                // Inline semantic descendants share their parent's layout box. Index
                // only the block node so its TextProvider owns the full paragraph or
                // heading range instead of whichever inline span happened to be first.
                if (node.InlineBox is { } inline &&
                    node.InlineRun is null &&
                    node.Role is MarkdownSemanticRole.Paragraph or
                        MarkdownSemanticRole.Heading or
                        MarkdownSemanticRole.CodeBlock)
                    nodeByInlineBox.TryAdd(inline, node);
                if (node.VectorSemanticIndex < 0 && node.Box is IHorizontalOverflowBox overflow)
                    nodeByHorizontalOverflow.TryAdd(overflow, node);
            }
            _nodeByInlineBox = nodeByInlineBox;
            var nodeByInlineBlockIndex = new Dictionary<int, MarkdownSemanticNode>(nodeByInlineBox.Count);
            var ambiguousInlineBlockIndices = new HashSet<int>();
            foreach (KeyValuePair<InlineContainerBox, MarkdownSemanticNode> entry in nodeByInlineBox)
            {
                int blockIndex = entry.Key.BlockIndex;
                if (!nodeByInlineBlockIndex.TryAdd(blockIndex, entry.Value))
                    ambiguousInlineBlockIndices.Add(blockIndex);
            }
            foreach (int blockIndex in ambiguousInlineBlockIndices)
                nodeByInlineBlockIndex.Remove(blockIndex);
            _nodeByInlineBlockIndex = nodeByInlineBlockIndex;
            _nodeByHorizontalOverflow = nodeByHorizontalOverflow;
        }
        SemanticRangeFingerprint = NormalizeNodeRangesAndComputeSemanticRangeFingerprint(root, spans, text.Length);
    }

    public static MarkdownSemanticDocument Build(LayoutSnapshot snapshot)
        => Build(snapshot, textProjectionReuse: null);

    internal static MarkdownSemanticDocument Build(
        LayoutSnapshot snapshot,
        MarkdownSemanticTextProjectionReuse? textProjectionReuse)
        => Build(snapshot, textProjectionReuse, indexBuilder: null);

    internal static MarkdownSemanticDocument Build(
        LayoutSnapshot snapshot,
        MarkdownSemanticTextProjectionReuse? textProjectionReuse,
        LayoutSnapshotIndexBuilder? indexBuilder)
    {
        if (textProjectionReuse is { } projection &&
            !ReferenceEquals(snapshot.SourceMap, projection.SourceMap))
            textProjectionReuse = null;

        var builder = new Builder(textProjectionReuse?.Text, indexBuilder);
        return builder.Build(snapshot, textProjectionReuse);
    }

    internal static void AttachVectorSemanticNodes(
        IReadOnlyList<MarkdownVectorSemanticItem> semantics,
        IReadOnlyList<MarkdownSemanticNode?> semanticNodes,
        MarkdownSemanticNode sceneNode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int semanticCount = semantics.Count;
        if (semanticCount == 0)
            return;

        // Parent indices may point forward. Cache each item's nearest exposed
        // ancestor (including itself) while resolving parents iteratively, so
        // deep runs of omitted/decorative items are walked only once. The final
        // loop still attaches in semantic-index order, preserving sibling order.
        // MarkdownVectorScene has already validated parent indices and rejected cycles.
        const int UnresolvedAncestor = int.MinValue;
        var nearestExposedAncestors = new int[semanticCount];
        Array.Fill(nearestExposedAncestors, UnresolvedAncestor);
        var path = new int[semanticCount];

        for (int i = 0; i < semanticCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkdownSemanticNode? child = semanticNodes[i];
            if (child is null)
                continue;

            int current = semantics[i].ParentIndex;
            int pathCount = 0;
            int nearestExposedAncestor = -1;
            while ((uint)current < (uint)semanticCount)
            {
                if ((pathCount & 0x3F) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                int cachedAncestor = nearestExposedAncestors[current];
                if (cachedAncestor != UnresolvedAncestor)
                {
                    nearestExposedAncestor = cachedAncestor;
                    break;
                }

                if (semanticNodes[current] is not null)
                {
                    nearestExposedAncestor = current;
                    nearestExposedAncestors[current] = current;
                    break;
                }

                path[pathCount++] = current;
                current = semantics[current].ParentIndex;
            }

            int unwindCount = 0;
            while (pathCount > 0)
            {
                if ((unwindCount++ & 0x3F) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                int ancestorIndex = path[--pathCount];
                if (semanticNodes[ancestorIndex] is not null)
                    nearestExposedAncestor = ancestorIndex;
                nearestExposedAncestors[ancestorIndex] = nearestExposedAncestor;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if ((uint)nearestExposedAncestor < (uint)semanticCount &&
                semanticNodes[nearestExposedAncestor] is { } parentNode)
            {
                parentNode.Add(child);
            }
            else
            {
                sceneNode.Add(child);
            }
        }
    }

    private static MarkdownAutomationTextFingerprint NormalizeNodeRangesAndComputeSemanticRangeFingerprint(
        MarkdownSemanticNode root,
        IReadOnlyList<MarkdownTextSpan> spans,
        int textLength)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> record = stackalloc byte[64];
        Span<byte> batch = stackalloc byte[SemanticRangeFingerprintBatchBytes];
        int batchLength = 0;
        foreach (MarkdownSemanticNode node in EnumerateDepthFirst(root))
        {
            // The builder trims synthetic terminal separators after creating semantic
            // nodes. Keep each published range inside the final projection;
            // this is folded into the existing fingerprint traversal so it
            // does not add a second document-tree walk.
            node.TextStart = Math.Clamp(node.TextStart, 0, textLength);
            node.TextEnd = Math.Clamp(node.TextEnd, node.TextStart, textLength);
            AppendNodeRangeProjection(node, record);
            AppendFingerprintRecord(hash, batch, ref batchLength, record);
        }
        for (int index = 0; index < spans.Count; index++)
        {
            MarkdownTextSpan span = spans[index];
            record.Clear();
            record[0] = 0x53;
            BinaryPrimitives.WriteInt32LittleEndian(record[4..], span.TextStart);
            BinaryPrimitives.WriteInt32LittleEndian(record[8..], span.TextEnd);
            BinaryPrimitives.WriteInt32LittleEndian(record[12..], span.InlineBox?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[16..], span.InlineRun?.InlineIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[20..], span.ImageBox?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[24..], span.EmbedBox?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[28..], span.HostedElementBox?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[32..], span.VectorSceneBox?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[36..], span.VectorSemanticIndex);
            BinaryPrimitives.WriteInt32LittleEndian(record[40..], span.InlineRun?.SourceSpan.Start ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[44..], span.InlineRun?.SourceSpan.Length ?? -1);
            int kindFlags =
                (span.InlineBox is null ? 0 : 1) |
                (span.InlineRun is null ? 0 : 2) |
                (span.ImageBox is null ? 0 : 4) |
                (span.EmbedBox is null ? 0 : 8) |
                (span.HostedElementBox is null ? 0 : 16) |
                (span.VectorSceneBox is null ? 0 : 32);
            BinaryPrimitives.WriteInt32LittleEndian(record[48..], kindFlags);
            AppendFingerprintRecord(hash, batch, ref batchLength, record[..52]);
        }

        if (batchLength > 0)
            hash.AppendData(batch[..batchLength]);

        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return MarkdownAutomationTextFingerprint.FromSha256(textLength, digest);
    }

    private static void AppendFingerprintRecord(
        IncrementalHash hash,
        Span<byte> batch,
        ref int batchLength,
        ReadOnlySpan<byte> record)
    {
        if (batch.Length - batchLength < record.Length)
        {
            hash.AppendData(batch[..batchLength]);
            batchLength = 0;
        }

        record.CopyTo(batch[batchLength..]);
        batchLength += record.Length;
    }

    private static void AppendNodeRangeProjection(
        MarkdownSemanticNode node,
        Span<byte> record)
    {
        record.Clear();
        record[0] = 0x4E;
        BinaryPrimitives.WriteInt32LittleEndian(record[4..], (int)node.Role);
        BinaryPrimitives.WriteInt32LittleEndian(record[8..], node.TextStart);
        BinaryPrimitives.WriteInt32LittleEndian(record[12..], node.TextEnd);
        BinaryPrimitives.WriteInt32LittleEndian(record[16..], node.Box?.BlockIndex ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(record[20..], node.InlineBox?.BlockIndex ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(record[24..], node.ImageBox?.BlockIndex ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(record[28..], node.EmbedBox?.BlockIndex ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(record[32..], node.HostedElementBox?.BlockIndex ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(record[36..], node.VectorSceneBox?.BlockIndex ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(record[40..], node.InlineRun?.InlineIndex ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(record[44..], node.VectorSemanticIndex);
        BinaryPrimitives.WriteInt32LittleEndian(record[48..], node.HeadingLevel);
        BinaryPrimitives.WriteInt32LittleEndian(record[52..], node.Row);
        BinaryPrimitives.WriteInt32LittleEndian(record[56..], node.Column);
        BinaryPrimitives.WriteInt32LittleEndian(record[60..], (int)node.AccessibilityRole);
    }

    public string GetText(MarkdownSemanticNode node)
    {
        int start = Math.Clamp(node.TextStart, 0, Text.Length);
        int end = Math.Clamp(node.TextEnd, start, Text.Length);
        return Text.Substring(start, end - start).Trim();
    }

    public int TextOffsetFromDocumentPosition(DocumentPosition position)
    {
        if (_spanByPosition.TryGetValue((position.BlockIndex, position.InlineIndex), out int indexedSpanIndex))
        {
            MarkdownTextSpan indexedSpan = TextSpans[indexedSpanIndex];
            if (indexedSpan.InlineRun is { } indexedRun)
            {
                return Math.Clamp(
                    indexedSpan.TextStart + ProjectRenderedOffsetToText(
                        indexedRun,
                        position.CharacterOffset,
                        indexedSpan.TextEnd - indexedSpan.TextStart),
                    indexedSpan.TextStart,
                    indexedSpan.TextEnd);
            }

            return position.CharacterOffset <= 0 ? indexedSpan.TextStart : indexedSpan.TextEnd;
        }

        // Non-run positions (images/embeds) and deliberately unbounded
        // selection sentinels are uncommon. Preserve the compatibility
        // fallback without putting normal copy/UIA lookups on a linear path.
        int trailingInlineTextEnd = -1;
        foreach (var span in TextSpans)
        {
            if (span.InlineBox is { } icb && icb.BlockIndex == position.BlockIndex)
            {
                if (span.InlineRun is { } run)
                {
                    if (position.InlineIndex < run.InlineIndex)
                        return span.TextStart;
                    if (position.InlineIndex > run.InlineIndex)
                    {
                        trailingInlineTextEnd = span.TextEnd;
                        continue;
                    }

                    return Math.Clamp(
                        span.TextStart + ProjectRenderedOffsetToText(run, position.CharacterOffset, span.TextEnd - span.TextStart),
                        span.TextStart,
                        span.TextEnd);
                }

                return Math.Clamp(span.TextStart + icb.GetBufferCharOffset(position), span.TextStart, span.TextEnd);
            }
        }

        // A decorative image/spacer has no semantic text span of its own. If
        // it follows the final semantic run in this box, map it to the end of
        // that preceding text rather than falling through to the first span.
        if (trailingInlineTextEnd >= 0)
            return trailingInlineTextEnd;

        foreach (var span in TextSpans)
        {
            bool matchesBlockImage = span.ImageBox?.BlockIndex == position.BlockIndex;
            bool matchesBlockEmbed = span.EmbedBox?.BlockIndex == position.BlockIndex;
            bool matchesHostedElement = span.HostedElementBox?.BlockIndex == position.BlockIndex;
            if (matchesBlockImage || matchesBlockEmbed || matchesHostedElement)
            {
                // These block objects publish explicit before/after endpoints
                // through PositionFromSpanEnd, but do not use the normal
                // position index. Keep the compatibility scan endpoint-aware
                // so a complete atomic selection includes its semantic text.
                return position.CharacterOffset <= 0 ? span.TextStart : span.TextEnd;
            }

            if (span.InlineBox?.BlockIndex == position.BlockIndex ||
                (span.VectorSceneBox?.BlockIndex == position.BlockIndex &&
                 GetVectorDocumentInlineIndex(span.VectorSemanticIndex) == position.InlineIndex))
            {
                return span.TextStart;
            }
        }

        // A box containing only omitted decorative objects still has its
        // zero-length semantic block node. Use that boundary so a spacer-only
        // paragraph between other paragraphs does not map to document end.
        if (_nodeByInlineBlockIndex.TryGetValue(position.BlockIndex, out MarkdownSemanticNode? inlineNode))
            return inlineNode.TextStart;

        return position.BlockIndex <= 0 ? 0 : Text.Length;
    }

    public bool TryGetDocumentRange(int textStart, int textEnd, out DocumentRange range)
    {
        var start = PositionFromTextOffset(textStart);
        var end = PositionFromTextOffset(textEnd);
        if (start is { } s && end is { } e)
        {
            range = new DocumentRange(s, e);
            return true;
        }

        range = DocumentRange.Empty;
        return false;
    }

    public DocumentPosition? PositionFromTextOffset(int textOffset)
    {
        textOffset = Math.Clamp(textOffset, 0, Text.Length);
        if (TextSpans.Count == 0)
            return DocumentPosition.Zero;

        int spanIndex = GetTextSpanStartIndex(textOffset);
        if (spanIndex >= TextSpans.Count)
            return PositionFromSpanEnd(TextSpans[^1]);

        MarkdownTextSpan span = TextSpans[spanIndex];
        if (textOffset < span.TextStart)
        {
            return spanIndex > 0
                ? PositionFromSpanEnd(TextSpans[spanIndex - 1])
                : PositionFromSpanEnd(span) with { CharacterOffset = 0 };
        }

        if (span.InlineBox is { } icb)
        {
            if (span.InlineRun is { } run)
            {
                int accessibleOffset = Math.Clamp(textOffset - span.TextStart, 0, Math.Max(0, span.TextEnd - span.TextStart));
                int renderedOffset = ProjectTextOffsetToRendered(run, accessibleOffset, span.TextEnd - span.TextStart);
                return new DocumentPosition(icb.BlockIndex, run.InlineIndex, renderedOffset);
            }

            int bufferOffset = Math.Clamp(textOffset - span.TextStart, 0, Math.Max(0, span.TextEnd - span.TextStart));
            return icb.GetPositionFromBufferOffset(bufferOffset);
        }

        if (span.ImageBox is { } image)
            return new DocumentPosition(image.BlockIndex, 0, textOffset <= span.TextStart ? 0 : 1);

        if (span.EmbedBox is { } embed)
            return new DocumentPosition(embed.BlockIndex, 0, textOffset <= span.TextStart ? 0 : 1);

        if (span.HostedElementBox is { } hosted)
            return new DocumentPosition(hosted.BlockIndex, 0, textOffset <= span.TextStart ? 0 : 1);

        if (span.VectorSceneBox is { } vector)
            return new DocumentPosition(
                vector.BlockIndex,
                GetVectorDocumentInlineIndex(span.VectorSemanticIndex),
                textOffset <= span.TextStart ? 0 : 1);

        return DocumentPosition.Zero;
    }

    /// <summary>Finds the first text span whose end is after the requested offset.</summary>
    internal int GetTextSpanStartIndex(int textOffset)
    {
        int low = 0;
        int high = TextSpans.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (TextSpans[middle].TextEnd <= textOffset)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    public IEnumerable<Rect> GetDocumentRects(
        int textStart,
        int textEnd,
        bool expandDegenerate = false)
        => GetDocumentRects(
            textStart,
            textEnd,
            expandDegenerate,
            placementTransform: null,
            placementScope: null);

    internal IEnumerable<Rect> GetDocumentRects(
        int textStart,
        int textEnd,
        bool expandDegenerate,
        Func<int, Rect, Rect>? placementTransform,
        Func<int, LayoutPlacementTransformContext.Scope>? placementScope)
    {
        textStart = Math.Clamp(textStart, 0, Text.Length);
        textEnd = Math.Clamp(textEnd, textStart, Text.Length);

        if (expandDegenerate && textStart == textEnd && Text.Length > 0)
        {
            if (textStart < Text.Length) textEnd = textStart + 1;
            else textStart = Math.Max(0, textStart - 1);
        }

        bool isNondegenerateRange = textStart < textEnd;

        // Span ends are monotonic because the semantic builder appends text and
        // each span in the same pass. Start one code unit before the requested
        // offset so a span ending exactly at the inclusive UIA boundary (and
        // zero-length spans at that boundary) remains eligible, matching the
        // historical linear scan below.
        int firstSpan = GetTextSpanStartIndex(textStart - 1);
        for (int index = firstSpan; index < TextSpans.Count; index++)
        {
            MarkdownTextSpan span = TextSpans[index];
            if (span.TextStart > textEnd)
                break;
            if (span.TextEnd < textStart || span.TextStart > textEnd) continue;

            // Inline text preserves its historical endpoint geometry, and a
            // collapsed caret may still use an atomic object's bounds. For a
            // nonempty [start,end) selection, however, a block object whose
            // text starts exactly at the exclusive end is not selected.
            if (isNondegenerateRange && span.TextStart == textEnd && span.InlineBox is null &&
                (span.ImageBox is not null || span.EmbedBox is not null ||
                 span.HostedElementBox is not null))
            {
                continue;
            }

            int start = Math.Max(textStart, span.TextStart);
            int end = Math.Min(textEnd, span.TextEnd);
            if (end < start) continue;

            using LayoutPlacementTransformContext.Scope scope =
                placementScope?.Invoke(span.LayoutBlockIndex) ?? default;
            bool inlineCoordinatesAreLogical =
                span.InlineBox is not null && placementTransform is not null && placementScope is not null;
            foreach (var rect in GetDocumentRectsForSpan(
                         span, start, end, logicalInlineCoordinates: inlineCoordinatesAreLogical))
            {
                yield return placementTransform is null || inlineCoordinatesAreLogical
                    ? rect
                    : placementTransform(span.LayoutBlockIndex, rect);
            }
        }
    }

    public bool TryCoercePointToNearestTextRect(Point point, out Point coercedPoint)
        => TryCoercePointToNearestTextRect(point, placementTransform: null, out coercedPoint);

    internal bool TryCoercePointToNearestTextRect(
        Point point,
        Func<int, Rect, Rect>? placementTransform,
        out Point coercedPoint)
        => TryCoercePointToNearestTextRect(
            point,
            placementTransform,
            placementScope: null,
            out coercedPoint);

    internal bool TryCoercePointToNearestTextRect(
        Point point,
        Func<int, Rect, Rect>? placementTransform,
        Func<int, LayoutPlacementTransformContext.Scope>? placementScope,
        out Point coercedPoint)
    {
        bool found = AccessibilityGeometry.TryCoercePointToNearestRect(
            new AccessibilityPoint(point.X, point.Y),
            EnumerateTextRects(),
            out AccessibilityPoint accessiblePoint);
        coercedPoint = new Point(accessiblePoint.X, accessiblePoint.Y);
        return found;

        IEnumerable<AccessibilityRect> EnumerateTextRects()
        {
            foreach (var span in TextSpans)
            {
                using LayoutPlacementTransformContext.Scope scope =
                    placementScope?.Invoke(span.LayoutBlockIndex) ?? default;
                bool inlineCoordinatesAreLogical =
                    span.InlineBox is not null && placementTransform is not null && placementScope is not null;
                foreach (var rect in GetDocumentRectsForSpan(
                             span, span.TextStart, span.TextEnd,
                             logicalInlineCoordinates: inlineCoordinatesAreLogical))
                {
                    Rect mapped = placementTransform is null || inlineCoordinatesAreLogical
                        ? rect
                        : placementTransform(span.LayoutBlockIndex, rect);
                    yield return new AccessibilityRect(mapped.X, mapped.Y, mapped.Width, mapped.Height);
                }
            }
        }
    }

    private static IEnumerable<Rect> GetDocumentRectsForSpan(
        MarkdownTextSpan span,
        int start,
        int end,
        bool logicalInlineCoordinates = false)
    {
        start = Math.Clamp(start, span.TextStart, span.TextEnd);
        end = Math.Clamp(end, start, span.TextEnd);

        if (span.InlineBox is { } icb)
        {
            DocumentPosition startPos;
            DocumentPosition endPos;
            if (span.InlineRun is { } run)
            {
                int textLength = span.TextEnd - span.TextStart;
                startPos = new DocumentPosition(
                    icb.BlockIndex,
                    run.InlineIndex,
                    ProjectTextOffsetToRendered(run, start - span.TextStart, textLength));
                endPos = new DocumentPosition(
                    icb.BlockIndex,
                    run.InlineIndex,
                    ProjectTextOffsetToRendered(run, end - span.TextStart, textLength));
                if (end > start && endPos.CharacterOffset == startPos.CharacterOffset && run.RenderedLength > 0)
                    endPos = endPos with { CharacterOffset = run.RenderedLength };
            }
            else
            {
                startPos = icb.GetPositionFromBufferOffset(start - span.TextStart);
                endPos = icb.GetPositionFromBufferOffset(end - span.TextStart);
            }

            IEnumerable<Rect> inlineRects = logicalInlineCoordinates
                ? icb.GetLogicalRangeRects(new DocumentRange(startPos, endPos))
                : icb.GetRangeRects(new DocumentRange(startPos, endPos));
            foreach (var rect in inlineRects)
                yield return rect;
        }
        else if (span.ImageBox is { } image)
        {
            yield return image.Bounds;
        }
        else if (span.EmbedBox is { } embed)
        {
            yield return embed.Bounds;
        }
        else if (span.HostedElementBox is { } hosted)
        {
            yield return hosted.Bounds;
        }
        else if (span.VectorSceneBox is { } vector)
        {
            yield return span.VectorSemanticIndex >= 0
                ? vector.GetVisibleSemanticBounds(span.VectorSemanticIndex)
                : vector.VisibleContentBounds;
        }
    }

    public IEnumerable<ImageBox> GetImagesIntersectingTextRange(int textStart, int textEnd)
    {
        textStart = Math.Clamp(textStart, 0, Text.Length);
        textEnd = Math.Clamp(textEnd, textStart, Text.Length);

        for (int index = GetTextSpanStartIndex(textStart - 1); index < TextSpans.Count; index++)
        {
            MarkdownTextSpan span = TextSpans[index];
            if (span.TextStart > textEnd)
                break;
            if (span.TextEnd < textStart || span.TextStart > textEnd)
                continue;

            if (span.ImageBox is { } blockImage)
            {
                yield return blockImage;
            }
            else if (span.InlineRun is InlineImageRun inlineImage)
            {
                yield return inlineImage.Image;
            }
        }
    }

    public IEnumerable<MarkdownSemanticNode> GetNodesIntersectingTextRange(int textStart, int textEnd)
    {
        textStart = Math.Clamp(textStart, 0, Text.Length);
        textEnd = Math.Clamp(textEnd, textStart, Text.Length);
        foreach (var node in EnumerateDepthFirst(Root))
        {
            if (node == Root) continue;
            if (node.TextEnd < textStart || node.TextStart > textEnd) continue;
            if ((node.VectorSceneBox is not null && node.VectorSemanticIndex >= 0) ||
                node.Role is MarkdownSemanticRole.Link or MarkdownSemanticRole.Image or MarkdownSemanticRole.Embed or MarkdownSemanticRole.Abbreviation or
                MarkdownSemanticRole.Table or MarkdownSemanticRole.TableCell or MarkdownSemanticRole.List or MarkdownSemanticRole.ListItem)
            {
                yield return node;
            }
        }
    }

    internal MarkdownSemanticNode GetInnermostNodeContainingTextRange(
        int textStart,
        int textEnd)
    {
        textStart = Math.Clamp(textStart, 0, Text.Length);
        textEnd = Math.Clamp(textEnd, textStart, Text.Length);
        MarkdownSemanticNode enclosing = Root;
        while (true)
        {
            MarkdownSemanticNode? next = null;
            MarkdownSemanticNode.ChildrenView children = enclosing.Children;
            bool ordered = ReferenceEquals(enclosing, Root);
            int firstChild = ordered ? GetOrderedChildStartIndexCore(children, textStart) : 0;
            for (int index = firstChild; index < children.Count; index++)
            {
                MarkdownSemanticNode child = children[index];
                if (ordered && child.TextStart > textStart)
                    break;
                if (ContainsTextRange(child, textStart, textEnd, Text.Length))
                {
                    next = child;
                    break;
                }
            }

            if (next is null)
                return enclosing;
            enclosing = next;
        }
    }

    internal MarkdownSemanticNode GetEnclosingNodeForTextRange(
        int textStart,
        int textEnd,
        InlineContainerBox? exactRangeScope)
    {
        textStart = Math.Clamp(textStart, 0, Text.Length);
        textEnd = Math.Clamp(textEnd, textStart, Text.Length);
        if (exactRangeScope is not null &&
            _nodeByInlineBox.TryGetValue(exactRangeScope, out MarkdownSemanticNode? scopedNode) &&
            textStart == scopedNode.TextStart &&
            textEnd == scopedNode.TextEnd)
        {
            return scopedNode;
        }

        return GetInnermostNodeContainingTextRange(textStart, textEnd);
    }

    internal MarkdownSemanticNode GetEnclosingNodeForTextRangeByBlockIndex(
        int textStart,
        int textEnd,
        int? exactRangeScopeBlockIndex)
    {
        textStart = Math.Clamp(textStart, 0, Text.Length);
        textEnd = Math.Clamp(textEnd, textStart, Text.Length);
        if (exactRangeScopeBlockIndex is int blockIndex &&
            _nodeByInlineBlockIndex.TryGetValue(blockIndex, out MarkdownSemanticNode? scopedNode) &&
            textStart == scopedNode.TextStart &&
            textEnd == scopedNode.TextEnd)
        {
            return scopedNode;
        }

        return GetInnermostNodeContainingTextRange(textStart, textEnd);
    }

    internal bool TryGetInlineContainerNode(
        InlineContainerBox inlineBox,
        out MarkdownSemanticNode node)
    {
        ArgumentNullException.ThrowIfNull(inlineBox);
        return _nodeByInlineBox.TryGetValue(inlineBox, out node!);
    }

    internal bool TryGetHorizontalOverflowNode(
        IHorizontalOverflowBox overflow,
        out MarkdownSemanticNode node)
    {
        ArgumentNullException.ThrowIfNull(overflow);
        return _nodeByHorizontalOverflow.TryGetValue(overflow, out node!);
    }

    internal IEnumerable<MarkdownSemanticNode> GetImmediateChildrenIntersectingTextRange(
        MarkdownSemanticNode enclosing,
        int textStart,
        int textEnd)
    {
        ArgumentNullException.ThrowIfNull(enclosing);
        textStart = Math.Clamp(textStart, 0, Text.Length);
        textEnd = Math.Clamp(textEnd, textStart, Text.Length);
        if (textStart == textEnd)
            yield break;

        MarkdownSemanticNode.ChildrenView children = enclosing.Children;
        bool ordered = ReferenceEquals(enclosing, Root);
        int firstChild = ordered ? GetOrderedChildStartIndexCore(children, textStart) : 0;
        for (int index = firstChild; index < children.Count; index++)
        {
            MarkdownSemanticNode child = children[index];
            if (ordered && child.TextStart >= textEnd)
                break;
            if (child.TextEnd > textStart && child.TextStart < textEnd)
                yield return child;
        }
    }

    // Retain the original read-only-list entry point for the independent
    // logarithmic-read contract. Production passes its concrete value view to
    // the same constrained core, avoiding an interface conversion/box.
    private static int GetOrderedChildStartIndex(IReadOnlyList<MarkdownSemanticNode> children, int textStart)
        => GetOrderedChildStartIndexCore(children, textStart);

    private static int GetOrderedChildStartIndexCore<TChildren>(TChildren children, int textStart)
        where TChildren : IReadOnlyList<MarkdownSemanticNode>
    {
        // Only the document's top-level siblings are guaranteed to have ordered
        // appended ranges. Vector semantic hierarchies may reference children
        // outside a parent's own label range, so they must keep the full scan.
        int low = 0;
        int high = children.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (children[middle].TextEnd < textStart)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private static bool ContainsTextRange(
        MarkdownSemanticNode node,
        int textStart,
        int textEnd,
        int documentLength)
    {
        if (textStart != textEnd)
            return node.TextStart <= textStart && textEnd <= node.TextEnd;

        // Text offsets are half-open. A caret at a shared boundary belongs to
        // the following node; only the final node may contain document end.
        return node.TextStart <= textStart &&
               (textStart < node.TextEnd ||
                (textStart == documentLength && textStart == node.TextEnd));
    }

    public static IEnumerable<MarkdownSemanticNode> EnumerateDepthFirst(MarkdownSemanticNode node)
    {
        // A recursive iterator creates a state machine for every node and can
        // exhaust the call stack on deeply nested (hostile or generated) input.
        // Store one frame per ancestor; a very wide document root then needs
        // constant auxiliary space rather than a stack of all its children.
        var path = new List<(MarkdownSemanticNode Node, int NextChild)>(8)
        {
            (node, 0),
        };
        yield return node;
        while (path.Count != 0)
        {
            int top = path.Count - 1;
            (MarkdownSemanticNode parent, int nextChild) = path[top];
            MarkdownSemanticNode.ChildrenView children = parent.Children;
            if (nextChild >= children.Count)
            {
                path.RemoveAt(top);
                continue;
            }

            path[top] = (parent, nextChild + 1);
            MarkdownSemanticNode child = children[nextChild];
            path.Add((child, 0));
            yield return child;
        }
    }

    private static DocumentPosition PositionFromSpanEnd(MarkdownTextSpan span)
    {
        if (span.InlineBox is { } icb)
        {
            if (span.InlineRun is { } run)
                return new DocumentPosition(icb.BlockIndex, run.InlineIndex, run.RenderedLength);
            return icb.GetPositionFromBufferOffset(span.TextEnd - span.TextStart);
        }
        if (span.ImageBox is { } image)
            return new DocumentPosition(image.BlockIndex, 0, 1);
        if (span.EmbedBox is { } embed)
            return new DocumentPosition(embed.BlockIndex, 0, 1);
        if (span.HostedElementBox is { } hosted)
            return new DocumentPosition(hosted.BlockIndex, 0, 1);
        if (span.VectorSceneBox is { } vector)
            return new DocumentPosition(vector.BlockIndex, GetVectorDocumentInlineIndex(span.VectorSemanticIndex), 1);
        return DocumentPosition.Zero;
    }

    // The negative vector-semantic index is a metadata sentinel for a whole,
    // unstructured scene, not a public document coordinate. Atomic source-map
    // and selection positions for that scene use the zero-based document slot.
    private static int GetVectorDocumentInlineIndex(int vectorSemanticIndex) =>
        vectorSemanticIndex < 0 ? 0 : vectorSemanticIndex;

    private static int ProjectRenderedOffsetToText(InlineRun run, int renderedOffset, int textLength)
    {
        if (textLength <= 0 || run.RenderedLength <= 0)
            return 0;

        renderedOffset = Math.Clamp(renderedOffset, 0, run.RenderedLength);
        if (run is InlineImageRun or InlineEmbedRun or InlineVectorSceneRun or InlineSpacerRun)
            return renderedOffset <= 0 ? 0 : textLength;

        if (run.RenderedLength == textLength)
            return renderedOffset;

        return Math.Clamp((int)Math.Round(renderedOffset * (double)textLength / run.RenderedLength), 0, textLength);
    }

    private static int ProjectTextOffsetToRendered(InlineRun run, int textOffset, int textLength)
    {
        if (run.RenderedLength <= 0)
            return 0;

        textOffset = Math.Clamp(textOffset, 0, Math.Max(0, textLength));
        if (run is InlineImageRun or InlineEmbedRun or InlineVectorSceneRun or InlineSpacerRun)
            return textOffset <= 0 ? 0 : run.RenderedLength;

        if (run.RenderedLength == textLength)
            return textOffset;

        if (textLength <= 0)
            return 0;

        return Math.Clamp((int)Math.Round(textOffset * (double)run.RenderedLength / textLength), 0, run.RenderedLength);
    }

    private sealed class Builder
    {
        private readonly SemanticTextProjectionAccumulator _text;
        private readonly LayoutSnapshotIndexBuilder? _layoutIndexes;
        // Spans are now inline values rather than per-span objects. Use paged
        // growth to avoid repeatedly copying a large value array as the semantic
        // graph is built, then freeze once into the snapshot's stable span list.
        private readonly PagedEntryBuffer<MarkdownTextSpan> _spans = new();
        private readonly Dictionary<InlineContainerBox, MarkdownSemanticNode> _nodeByInlineBox = new();
        private readonly Dictionary<IHorizontalOverflowBox, MarkdownSemanticNode> _nodeByHorizontalOverflow = new(
            ReferenceEqualityComparer.Instance);
        private int _listDepth;

        public Builder(
            string? expectedTextProjection,
            LayoutSnapshotIndexBuilder? layoutIndexes)
        {
            _text = new SemanticTextProjectionAccumulator(expectedTextProjection);
            _layoutIndexes = layoutIndexes;
        }

        public MarkdownSemanticDocument Build(
            LayoutSnapshot snapshot,
            MarkdownSemanticTextProjectionReuse? textProjectionReuse)
        {
            var root = new MarkdownSemanticNode(MarkdownSemanticRole.Document)
            {
                TextStart = 0
            };
            RegisterNodeIndexes(root);

            for (int topLevelOrdinal = 0; topLevelOrdinal < snapshot.Blocks.Count; topLevelOrdinal++)
            {
                LayoutPassCancellation.GetEffectiveToken(CancellationToken.None).ThrowIfCancellationRequested();
                _layoutIndexes?.BeginTopLevel(topLevelOrdinal);
                try
                {
                    var node = BuildBlock(snapshot.Blocks[topLevelOrdinal]);
                    if (node is not null) root.Add(node);
                }
                finally
                {
                    _layoutIndexes?.EndTopLevel();
                }
            }

            int trimmedTrailingNewlines = TrimTrailingNewline();
            root.TextEnd = _text.Length;
            if (trimmedTrailingNewlines > 0)
                ClampTrailingTextSpans(_text.Length);
            string text = _text.GetText();
            MarkdownSemanticTextProjectionReuse? matchedProjection =
                textProjectionReuse is { } projection && ReferenceEquals(text, projection.Text)
                    ? projection
                    : null;
            // The graph indexes are collected in preorder during construction.
            // Build the compact integer index once at its exact final capacity;
            // growing it alongside the graph adds rehashes and oversized arrays.
            // This loop visits only unique indexed inline boxes, not the tree.
            var nodeByInlineBlockIndex = new Dictionary<int, MarkdownSemanticNode>(_nodeByInlineBox.Count);
            var ambiguousInlineBlockIndices = new HashSet<int>();
            foreach (KeyValuePair<InlineContainerBox, MarkdownSemanticNode> entry in _nodeByInlineBox)
            {
                if (!nodeByInlineBlockIndex.TryAdd(entry.Key.BlockIndex, entry.Value))
                    ambiguousInlineBlockIndices.Add(entry.Key.BlockIndex);
            }
            foreach (int blockIndex in ambiguousInlineBlockIndices)
                nodeByInlineBlockIndex.Remove(blockIndex);
            return new MarkdownSemanticDocument(
                root,
                text,
                _spans.Freeze(),
                matchedProjection,
                new SemanticNodeIndexes(
                    _nodeByInlineBox,
                    nodeByInlineBlockIndex,
                    _nodeByHorizontalOverflow));
        }

        private void RegisterNodeIndexes(MarkdownSemanticNode node)
        {
            // Match EnumerateDepthFirst's preorder/first-wins contracts. Register
            // parent wrappers before creating any semantic children.
            if (node.InlineBox is { } inline &&
                node.InlineRun is null &&
                node.Role is (MarkdownSemanticRole.Paragraph or
                    MarkdownSemanticRole.Heading or
                    MarkdownSemanticRole.CodeBlock))
            {
                _nodeByInlineBox.TryAdd(inline, node);
            }

            if (node.VectorSemanticIndex < 0 && node.Box is IHorizontalOverflowBox overflow)
                _nodeByHorizontalOverflow.TryAdd(overflow, node);
        }

        private void AddRegisteredChild(
            MarkdownSemanticNode parent,
            MarkdownSemanticNode child)
        {
            RegisterNodeIndexes(child);
            parent.Add(child);
        }

        private MarkdownSemanticNode? BuildBlock(BlockBox box)
        {
            if (_layoutIndexes is null)
                return BuildBlockCore(box);

            LayoutSnapshotIndexBuilder.Scope scope = _layoutIndexes.EnterBlock(box);
            try
            {
                return BuildBlockCore(box);
            }
            finally
            {
                _layoutIndexes.ExitBlock(box, scope);
            }
        }

        private MarkdownSemanticNode? BuildBlockCore(BlockBox box)
        {
            return box switch
            {
                InlineContainerBox inline => BuildInline(inline),
                CodeBlockBox codeBlock => BuildCodeBlock(codeBlock),
                ImageBox image => BuildImage(image),
                EmbedBox embed => BuildEmbed(embed),
                DeclarativeHostedElementBox hosted => BuildHostedElement(hosted),
                VectorSceneBox vector => BuildVectorScene(vector),
                ListItemBox listItem => BuildListItem(listItem),
                TableBox table => BuildTable(table),
                StackBox stack when IsListStack(stack) => BuildList(stack),
                StackBox stack => BuildGroup(stack),
                _ => null,
            };
        }

        private MarkdownSemanticNode BuildInline(InlineContainerBox inline)
        {
            var role = inline.ElementKey == MarkdownElementKeys.CodeBlock
                ? MarkdownSemanticRole.CodeBlock
                : IsHeading(inline.ElementKey)
                    ? MarkdownSemanticRole.Heading
                    : MarkdownSemanticRole.Paragraph;
            var node = new MarkdownSemanticNode(role, inline)
            {
                InlineBox = inline,
                TextStart = _text.Length,
                HeadingLevel = GetHeadingLevel(inline.ElementKey),
                CodeLanguage = inline.CodeLanguage,
                HelpText = inline.ElementKey == MarkdownElementKeys.CodeBlock && !string.IsNullOrWhiteSpace(inline.CodeLanguage)
                    ? inline.Context.ResolveFormattedString(
                        MarkdownStringKeys.CodeLanguageHelp,
                        MarkdownLocalizedStrings.CodeLanguageHelpFormat,
                        inline.CodeLanguage)
                    : null,
            };
            RegisterNodeIndexes(node);

            MarkdownSemanticNode? flattenedHeading = null;
            MarkdownSemanticNode? flattenedCodeBlock = null;

            for (int runIndex = 0; runIndex < inline.Runs.Count; runIndex++)
            {
                InlineRun run = inline.Runs[runIndex];
                // The layout image index includes decorative images even when
                // semantic/UIA construction intentionally omits their runs.
                _layoutIndexes?.ObserveInlineRun(inline, run);
                // Inert spacers keep a one-character private source/layout
                // mapping, but are neither selectable text nor UIA content.
                if (run is InlineSpacerRun)
                    continue;

                // A plain literal that is still an exact view over this
                // document's immutable source contributes the same semantic
                // text without first allocating a per-run substring.
                SourceSliceTextRun? sourceSlice = run is SourceSliceTextRun candidateSlice &&
                    IsExactSourceSlice(inline, run, candidateSlice)
                        ? candidateSlice
                        : null;
                string? accessibleText = sourceSlice is null
                    ? GetInlineAccessibleText(run, inline.Context)
                    : null;
                int accessibleLength = sourceSlice?.SliceLength ?? accessibleText!.Length;

                // An unlinked image with empty alternative text is decorative. It must
                // contribute neither a text span nor an element to the UIA document.
                if (run is InlineImageRun { IsLinked: false } && accessibleLength == 0)
                    continue;

                bool atomic = IsAtomicInline(run);
                if (atomic &&
                    accessibleLength > 0 &&
                    _text.Length > node.TextStart &&
                    NeedsSemanticBoundary(_text[^1], accessibleText![0]))
                {
                    AppendInlineSemanticBoundary(inline, run);
                }

                int runStart = _text.Length;
                if (sourceSlice is not null)
                    _text.AppendSlice(sourceSlice.SourceText, sourceSlice.SourceStart, sourceSlice.SliceLength);
                else
                    _text.Append(accessibleText);
                int runEnd = _text.Length;
                _spans.Add(new MarkdownTextSpan(runStart, runEnd, inline, run, null, null, null));
                int flattenedHeadingLevel = GetHeadingLevel(run.SemanticHeadingKey);
                bool isFlattenedCodeBlock =
                    inline.ElementKey != MarkdownElementKeys.CodeBlock &&
                    run.ElementKey == MarkdownElementKeys.CodeBlock;
                if (flattenedHeadingLevel > 0)
                {
                    if (flattenedHeading is null || flattenedHeading.HeadingLevel != flattenedHeadingLevel)
                    {
                        flattenedHeading = new MarkdownSemanticNode(MarkdownSemanticRole.Heading, inline)
                        {
                            InlineBox = inline,
                            InlineRun = run,
                            TextStart = runStart,
                            TextEnd = runEnd,
                            HeadingLevel = flattenedHeadingLevel,
                        };
                        AddRegisteredChild(node, flattenedHeading);
                    }
                    else
                    {
                        flattenedHeading.TextEnd = runEnd;
                    }
                }
                else
                {
                    flattenedHeading = null;
                }

                if (isFlattenedCodeBlock)
                {
                    if (flattenedCodeBlock is null)
                    {
                        flattenedCodeBlock = new MarkdownSemanticNode(MarkdownSemanticRole.CodeBlock, inline)
                        {
                            InlineBox = inline,
                            InlineRun = run,
                            TextStart = runStart,
                            TextEnd = runEnd,
                        };
                        AddRegisteredChild(node, flattenedCodeBlock);
                    }
                    else
                    {
                        flattenedCodeBlock.TextEnd = runEnd;
                    }
                }
                else
                {
                    flattenedCodeBlock = null;
                }

                MarkdownSemanticNode semanticParent = flattenedCodeBlock ?? flattenedHeading ?? node;
                if (run is LinkRun link)
                {
                    AddRegisteredChild(semanticParent, new MarkdownSemanticNode(MarkdownSemanticRole.Link, inline)
                    {
                        InlineBox = inline,
                        InlineRun = link,
                        TextStart = runStart,
                        TextEnd = runEnd,
                        HelpText = link.Url,
                    });
                }
                else if (run is InlineEmbedRun embedRun)
                {
                    AddRegisteredChild(semanticParent, new MarkdownSemanticNode(MarkdownSemanticRole.Embed, inline)
                    {
                        InlineBox = inline,
                        InlineRun = embedRun,
                        TextStart = runStart,
                        TextEnd = runEnd,
                        AccessibilityName = embedRun.AutomationMetadata?.CurrentName,
                        AccessibilityDescription = embedRun.AutomationMetadata?.ReadOnlyHelpText,
                        AutomationId = embedRun.AutomationMetadata?.AutomationId,
                    });
                }
                else if (run is InlineImageRun imageRun)
                {
                    AddRegisteredChild(semanticParent, new MarkdownSemanticNode(MarkdownSemanticRole.Image, inline)
                    {
                        InlineBox = inline,
                        ImageBox = imageRun.Image,
                        InlineRun = imageRun,
                        TextStart = runStart,
                        TextEnd = runEnd,
                        HelpText = imageRun.IsLinked
                            ? !string.IsNullOrWhiteSpace(imageRun.LinkTitle)
                                ? imageRun.LinkTitle
                                : imageRun.LinkUrl
                            : !string.IsNullOrWhiteSpace(imageRun.Title)
                                ? imageRun.Title
                            : imageRun.Url,
                    });
                }
                else if (run is InlineVectorSceneRun vectorRun)
                {
                    AddRegisteredChild(semanticParent, new MarkdownSemanticNode(MarkdownSemanticRole.Math, inline)
                    {
                        InlineBox = inline,
                        InlineRun = vectorRun,
                        TextStart = runStart,
                        TextEnd = runEnd,
                        AccessibilityRole = MarkdownAccessibilityRole.Math,
                        AccessibilityName = vectorRun.AccessibilityName,
                        AccessibilityDescription = vectorRun.AccessibilityDescription,
                        HelpText = vectorRun.IsLinked
                            ? !string.IsNullOrWhiteSpace(vectorRun.LinkTitle)
                                ? vectorRun.LinkTitle
                                : vectorRun.LinkUrl
                            : vectorRun.AccessibilityDescription,
                        AutomationId = string.Concat(
                            "MarkdownMath_",
                            vectorRun.SourceSpan.Start.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            "_",
                            vectorRun.SourceSpan.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    });
                }
                else if (run is AbbreviationRun abbreviationRun)
                {
                    AddRegisteredChild(semanticParent, new MarkdownSemanticNode(MarkdownSemanticRole.Abbreviation, inline)
                    {
                        InlineBox = inline,
                        InlineRun = abbreviationRun,
                        TextStart = runStart,
                        TextEnd = runEnd,
                        HelpText = abbreviationRun.Expansion,
                    });
                }

                if (atomic &&
                    accessibleLength > 0 &&
                    TryGetNextInlineFirstCharacter(inline, runIndex + 1, out char nextCharacter) &&
                    NeedsSemanticBoundary(accessibleText![^1], nextCharacter))
                {
                    AppendInlineSemanticBoundary(inline, run);
                }
            }
            node.TextEnd = _text.Length;

            AppendBlockSeparator();
            return node;
        }

        private static string GetInlineAccessibleText(
            InlineRun run,
            MarkdownLayoutContext context) =>
            run is InlineImageRun imageRun
                ? MarkdownImageSemanticPolicy.GetInlineAccessibleName(
                    imageRun.AltText,
                    imageRun.IsLinked,
                    imageRun.LinkTitle,
                    context.ResolveString(
                        MarkdownStringKeys.ImageName,
                        MarkdownLocalizedStrings.ImageName)) ?? string.Empty
                : run.AccessibleText;

        private static bool IsAtomicInline(InlineRun run) =>
            run is InlineImageRun or InlineEmbedRun or InlineVectorSceneRun or InlineSpacerRun;

        private static bool NeedsSemanticBoundary(char left, char right) =>
            char.IsLetterOrDigit(left) && char.IsLetterOrDigit(right);

        private static bool TryGetNextInlineFirstCharacter(
            InlineContainerBox inline,
            int startIndex,
            out char firstCharacter)
        {
            for (int index = startIndex; index < inline.Runs.Count; index++)
            {
                InlineRun run = inline.Runs[index];
                if (run is SourceSliceTextRun sourceSlice && IsExactSourceSlice(inline, run, sourceSlice))
                {
                    if (sourceSlice.SliceLength == 0)
                        continue;

                    firstCharacter = sourceSlice.SourceText[sourceSlice.SourceStart];
                    return true;
                }

                string text = GetInlineAccessibleText(run, inline.Context);
                if (text.Length > 0)
                {
                    firstCharacter = text[0];
                    return true;
                }
            }

            firstCharacter = default;
            return false;
        }

        private static bool IsExactSourceSlice(
            InlineContainerBox inline,
            InlineRun run,
            SourceSliceTextRun sourceSlice) =>
            ReferenceEquals(sourceSlice.SourceText, inline.Context.SourceMap.SourceText) &&
            sourceSlice.SourceStart == run.SourceSpan.Start &&
            sourceSlice.SliceLength == run.SourceSpan.Length;

        private void AppendInlineSemanticBoundary(InlineContainerBox inline, InlineRun run)
        {
            int start = _text.Length;
            _text.Append(' ');
            _spans.Add(new MarkdownTextSpan(start, start + 1, inline, run, null, null, null));
        }

        private MarkdownSemanticNode BuildCodeBlock(CodeBlockBox codeBlock)
        {
            var hasLanguage = !string.IsNullOrWhiteSpace(codeBlock.CodeLanguage);
            var node = new MarkdownSemanticNode(MarkdownSemanticRole.CodeBlock, codeBlock)
            {
                TextStart = _text.Length,
                CodeLanguage = hasLanguage ? codeBlock.LanguageDisplay : null,
                HelpText = hasLanguage
                    ? codeBlock.Context.ResolveFormattedString(
                        MarkdownStringKeys.CodeLanguageHelp,
                        MarkdownLocalizedStrings.CodeLanguageHelpFormat,
                        codeBlock.LanguageDisplay)
                    : null,
            };
            RegisterNodeIndexes(node);

            foreach (var chunk in codeBlock.Chunks)
            {
                LayoutSnapshotIndexBuilder.Scope scope = default;
                if (_layoutIndexes is not null)
                    scope = _layoutIndexes.EnterCodeChunk(chunk);
                try
                {
                    foreach (var run in chunk.Runs)
                    {
                        int runStart = _text.Length;
                        _text.Append(run.AccessibleText);
                        int runEnd = _text.Length;
                        _spans.Add(new MarkdownTextSpan(runStart, runEnd, chunk, run, null, null, null));
                    }
                }
                finally
                {
                    if (_layoutIndexes is not null)
                        _layoutIndexes.ExitCodeChunk(chunk, scope);
                }
            }

            node.TextEnd = _text.Length;
            AppendBlockSeparator();
            return node;
        }

        private MarkdownSemanticNode? BuildImage(ImageBox image)
        {
            if (MarkdownImageSemanticPolicy.IsDecorativeBlock(image.Alt))
                return null;

            var node = new MarkdownSemanticNode(MarkdownSemanticRole.Image, image)
            {
                ImageBox = image,
                TextStart = _text.Length,
                HelpText = image.SvgDesc,
            };
            RegisterNodeIndexes(node);

            string name = image.Alt;
            int start = _text.Length;
            _text.Append(name);
            int end = _text.Length;
            _spans.Add(new MarkdownTextSpan(start, end, null, null, image, null, null));
            node.TextEnd = _text.Length;
            AppendBlockSeparator();
            return node;
        }

        private MarkdownSemanticNode BuildEmbed(EmbedBox embed)
        {
            var node = new MarkdownSemanticNode(MarkdownSemanticRole.Embed, embed)
            {
                EmbedBox = embed,
                TextStart = _text.Length,
            };
            RegisterNodeIndexes(node);
            int start = _text.Length;
            _text.Append(InlineEmbedRun.PlaceholderChar);
            int end = _text.Length;
            _spans.Add(new MarkdownTextSpan(start, end, null, null, null, embed, null));
            node.TextEnd = _text.Length;
            AppendBlockSeparator();
            return node;
        }

        private MarkdownSemanticNode BuildVectorScene(VectorSceneBox vector)
        {
            IReadOnlyList<MarkdownVectorSemanticItem> semantics = vector.SemanticItems;
            int start = _text.Length;
            var semanticRanges = new (int Start, int End)?[semantics.Count];
            for (int i = 0; i < semantics.Count; i++)
            {
                vector.ThrowIfCancellationRequested();
                MarkdownVectorSemanticItem item = semantics[i];
                _layoutIndexes?.ObserveVectorSemantic(vector, i, item.Flags);
                if (!MarkdownVectorSemanticPolicy.IsExposed(item.Flags) ||
                    (item.ParentIndex < 0 && item.Role == MarkdownVectorSemanticRole.Diagram) ||
                    string.IsNullOrWhiteSpace(item.Name))
                {
                    continue;
                }

                semanticRanges[i] = AppendVectorText(vector, i, item.Name!, start);
            }

            if (semantics.Count == 0 &&
                !string.IsNullOrWhiteSpace(vector.SemanticText))
            {
                // Math and other unstructured vector scenes deliberately expose
                // their semantic source as one exact range. Structured scenes expose
                // each named, non-decorative child as an exact range above; Selectable
                // independently controls application selection and pointer hit testing.
                AppendVectorText(vector, -1, vector.SemanticText, start);
            }

            if (semantics.Count == 0 &&
                _text.Length == start &&
                !string.IsNullOrWhiteSpace(vector.AccessibilityName))
            {
                AppendVectorText(vector, -1, vector.AccessibilityName!, start);
            }
            int end = _text.Length;

            string? accessibilityName = vector.AccessibilityName;
            if (string.IsNullOrWhiteSpace(accessibilityName))
            {
                for (int i = 0; i < semantics.Count; i++)
                {
                    vector.ThrowIfCancellationRequested();
                    MarkdownVectorSemanticItem candidate = semantics[i];
                    if (MarkdownVectorSemanticPolicy.IsExposed(candidate.Flags) &&
                        candidate.ParentIndex < 0 &&
                        candidate.Role == MarkdownVectorSemanticRole.Diagram &&
                        !string.IsNullOrWhiteSpace(candidate.Name))
                    {
                        accessibilityName = candidate.Name;
                        break;
                    }
                }
            }

            var node = new MarkdownSemanticNode(
                vector.AccessibilityRole == MarkdownAccessibilityRole.Math
                    ? MarkdownSemanticRole.Math
                    : MarkdownSemanticRole.Diagram,
                vector)
            {
                VectorSceneBox = vector,
                TextStart = start,
                TextEnd = end,
                AccessibilityRole = vector.AccessibilityRole,
                AccessibilityName = accessibilityName,
                AccessibilityDescription = vector.AccessibilityDescription,
                AutomationId = string.Concat(
                    vector.AccessibilityRole == MarkdownAccessibilityRole.Math ? "MarkdownMath_" : "MarkdownDiagram_",
                    vector.SourceRange.Start.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "_",
                    vector.SourceRange.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            };
            RegisterNodeIndexes(node);

            var semanticNodes = new MarkdownSemanticNode?[semantics.Count];
            for (int i = 0; i < semanticNodes.Length; i++)
            {
                vector.ThrowIfCancellationRequested();
                MarkdownVectorSemanticItem item = semantics[i];
                if (!MarkdownVectorSemanticPolicy.IsExposed(item.Flags) ||
                    (item.ParentIndex < 0 && item.Role == MarkdownVectorSemanticRole.Diagram))
                {
                    continue;
                }
                MarkdownVectorLinkAction? link = vector.GetSemanticLinkAction(i);
                bool invokable = MarkdownVectorSemanticPolicy.IsInvokable(item.Flags, link is not null);
                var child = new MarkdownSemanticNode(invokable
                    ? MarkdownSemanticRole.Link
                    : MarkdownSemanticRole.Group, vector)
                {
                    VectorSceneBox = vector,
                    VectorSemanticIndex = i,
                    VectorSemanticRole = item.Role,
                    VectorSemanticFlags = item.Flags,
                    TextStart = semanticRanges[i]?.Start ?? start,
                    TextEnd = semanticRanges[i]?.End ?? start,
                    AccessibilityRole = invokable
                        ? MarkdownAccessibilityRole.Link
                        : MarkdownAccessibilityRole.Group,
                    AccessibilityName = item.Name,
                    AccessibilityDescription = item.Description,
                    HelpText = link?.Target ?? link?.Action,
                    AutomationId = string.Concat(
                        "MarkdownDiagramItem_",
                        vector.SourceRange.Start.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "_",
                        item.SourceId.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)),
                };
                RegisterNodeIndexes(child);
                semanticNodes[i] = child;
            }

            AttachVectorSemanticNodes(
                semantics,
                semanticNodes,
                node,
                vector.CancellationToken);

            AppendBlockSeparator();
            return node;
        }

        private (int Start, int End) AppendVectorText(
            VectorSceneBox vector,
            int semanticIndex,
            string value,
            int blockStart)
        {
            if (_text.Length > blockStart)
                _text.Append('\n');
            int start = _text.Length;
            _text.Append(value);
            int end = _text.Length;
            _spans.Add(new MarkdownTextSpan(
                start,
                end,
                null,
                null,
                null,
                null,
                null,
                vector,
                semanticIndex,
                vector.ResolveSemanticTextStyle(semanticIndex)));
            return (start, end);
        }

        private MarkdownSemanticNode BuildHostedElement(DeclarativeHostedElementBox hosted)
        {
            var node = new MarkdownSemanticNode(MarkdownSemanticRole.Embed, hosted)
            {
                HostedElementBox = hosted,
                TextStart = _text.Length,
                AccessibilityRole = hosted.AccessibilityRole,
                AccessibilityName = hosted.AccessibilityName,
                AccessibilityDescription = hosted.AccessibilityDescription,
                AutomationId = hosted.AutomationId,
            };
            RegisterNodeIndexes(node);
            int start = _text.Length;
            if (hosted.SemanticText is { } semanticText)
                _text.Append(semanticText);
            else
                _text.Append(InlineEmbedRun.PlaceholderChar);
            int end = _text.Length;
            _spans.Add(new MarkdownTextSpan(start, end, null, null, null, null, hosted));
            node.TextEnd = _text.Length;
            AppendBlockSeparator();
            return node;
        }

        private MarkdownSemanticNode BuildList(StackBox stack)
        {
            int level = ++_listDepth;
            var node = new MarkdownSemanticNode(MarkdownSemanticRole.List, stack)
            {
                TextStart = _text.Length,
                Level = level,
            };
            RegisterNodeIndexes(node);

            int sizeOfSet = 0;
            IReadOnlyList<BlockBox> children = stack.Children;
            for (int index = 0; index < children.Count; index++)
            {
                BlockBox child = children[index];
                if (child is ListItemBox)
                    sizeOfSet++;
            }

            int position = 0;
            try
            {
                for (int index = 0; index < children.Count; index++)
                {
                    BlockBox child = children[index];
                    var childNode = BuildBlock(child);
                    if (childNode is null)
                        continue;

                    if (childNode.Role == MarkdownSemanticRole.ListItem)
                    {
                        childNode.Level = level;
                        childNode.PositionInSet = ++position;
                        childNode.SizeOfSet = sizeOfSet;
                    }
                    node.Add(childNode);
                }
            }
            finally
            {
                _listDepth--;
            }

            node.TextEnd = _text.Length;
            return node;
        }

        private MarkdownSemanticNode BuildListItem(ListItemBox listItem)
        {
            var node = new MarkdownSemanticNode(MarkdownSemanticRole.ListItem, listItem)
            {
                TextStart = _text.Length,
            };
            RegisterNodeIndexes(node);

            if (BuildBlock(listItem.Marker) is { } marker) node.Add(marker);
            if (BuildBlock(listItem.Content) is { } content) node.Add(content);
            node.TextEnd = _text.Length;
            return node;
        }

        private MarkdownSemanticNode BuildTable(TableBox table)
        {
            var node = new MarkdownSemanticNode(MarkdownSemanticRole.Table, table)
            {
                TableBox = table,
                TextStart = _text.Length,
                RowCount = table.RowCount,
                ColumnCount = table.ColumnCount,
            };
            RegisterNodeIndexes(node);

            foreach (var cellInfo in table.GetCellInfos())
            {
                var cellNode = new MarkdownSemanticNode(MarkdownSemanticRole.TableCell, cellInfo.Box)
                {
                    InlineBox = cellInfo.Box,
                    TextStart = _text.Length,
                    Row = cellInfo.Row,
                    Column = cellInfo.Column,
                    IsHeader = cellInfo.IsHeader,
                };
                RegisterNodeIndexes(cellNode);

                if (BuildBlock(cellInfo.Box) is { } inlineNode)
                    cellNode.Add(inlineNode);
                cellNode.TextEnd = _text.Length;
                node.Add(cellNode);
            }

            node.TextEnd = _text.Length;
            return node;
        }

        private MarkdownSemanticNode BuildGroup(StackBox stack)
        {
            var node = new MarkdownSemanticNode(MarkdownSemanticRole.Group, stack)
            {
                TextStart = _text.Length,
            };
            RegisterNodeIndexes(node);

            IReadOnlyList<BlockBox> children = stack.Children;
            for (int index = 0; index < children.Count; index++)
            {
                BlockBox child = children[index];
                var childNode = BuildBlock(child);
                if (childNode is not null) node.Add(childNode);
            }

            node.TextEnd = _text.Length;
            return node;
        }

        private void AppendBlockSeparator()
        {
            if (_text.Length == 0 || _text[^1] != '\n')
                _text.Append('\n');
        }

        private int TrimTrailingNewline()
        {
            CancellationToken cancellationToken = LayoutPassCancellation.GetEffectiveToken(CancellationToken.None);
            int lastContentEnd = 0;
            int visitedSpans = 0;

            // Positive-length spans own authored content, including a final LF
            // from a code block or explicit hard break. Any trailing LF beyond
            // the last such span is a synthetic block separator. Zero-length
            // spans at that boundary are metadata and do not move the trim bound.
            for (int index = _spans.Count - 1; index >= 0; index--)
            {
                if ((visitedSpans++ & 4095) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                MarkdownTextSpan span = _spans[index];
                if (span.TextEnd > span.TextStart)
                {
                    lastContentEnd = span.TextEnd;
                    break;
                }
            }

            int trimmed = 0;
            while (_text.Length > lastContentEnd && _text.Length > 0 && _text[^1] == '\n')
            {
                if ((trimmed++ & 4095) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                _text.Length--;
            }

            return trimmed;
        }

        private void ClampTrailingTextSpans(int textLength)
        {
            // Spans are appended in document/text order, and the semantic
            // position index relies on nondecreasing TextEnd values. Only the
            // suffix affected by trimming can be out of range.
            CancellationToken cancellationToken = LayoutPassCancellation.GetEffectiveToken(CancellationToken.None);
            int visited = 0;
            for (int index = _spans.Count - 1; index >= 0; index--)
            {
                if ((visited++ & 4095) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                MarkdownTextSpan span = _spans[index];
                if (span.TextStart <= textLength && span.TextEnd <= textLength)
                    break;

                _spans[index] = span.ClampTextRange(textLength);
            }
        }

        private static bool IsListStack(StackBox stack)
        {
            IReadOnlyList<BlockBox> children = stack.Children;
            if (children.Count == 0) return false;
            for (int index = 0; index < children.Count; index++)
            {
                BlockBox child = children[index];
                if (child is not ListItemBox) return false;
            }
            return true;
        }

        private static bool IsHeading(string elementKey) =>
            GetHeadingLevel(elementKey) > 0;

        private static int GetHeadingLevel(string elementKey) => elementKey switch
        {
            MarkdownElementKeys.Heading1 => 1,
            MarkdownElementKeys.Heading2 => 2,
            MarkdownElementKeys.Heading3 => 3,
            MarkdownElementKeys.Heading4 => 4,
            MarkdownElementKeys.Heading5 => 5,
            MarkdownElementKeys.Heading6 => 6,
            _ => 0,
        };
    }
}
