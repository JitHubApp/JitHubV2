using System;
using System.Collections.Generic;
using Windows.Foundation;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Accessibility.Internal.Semantics;

/// <summary>
/// One node of the semantic document: its role, the layout boxes it
/// describes, its text range, its table, list and vector metadata, and its
/// children.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because it is the compact storage of a
/// node. Owner, text and numeric metadata each keep the common case in one
/// shared slot under a small mask and promote a rare node to a private holder,
/// and every typed accessor and setter must read and write those same slots
/// and masks. The children view lives in <see cref="MarkdownSemanticChildrenView"/>.
/// </remarks>
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
    public MarkdownSemanticChildrenView Children => new(this);
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

    /// <summary>
    /// Counts child additions, so a <see cref="MarkdownSemanticChildrenView"/>
    /// enumerator detects a change during enumeration.
    /// </summary>
    internal int ChildrenVersion => _childrenVersion;

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

    /// <summary>The live child count, read by <see cref="MarkdownSemanticChildrenView"/>.</summary>
    internal int ChildCount => _children switch
    {
        null => 0,
        MarkdownSemanticNode => 1,
        List<MarkdownSemanticNode> children => children.Count,
        _ => throw new InvalidOperationException("Invalid semantic child storage."),
    };

    /// <summary>The child at <paramref name="index"/>, read by <see cref="MarkdownSemanticChildrenView"/>.</summary>
    internal MarkdownSemanticNode GetChildAt(int index)
    {
        return _children switch
        {
            MarkdownSemanticNode child when index == 0 => child,
            List<MarkdownSemanticNode> children => children[index],
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };
    }
}
