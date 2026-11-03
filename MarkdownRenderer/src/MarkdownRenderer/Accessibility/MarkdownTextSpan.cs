using System;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Accessibility;

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
