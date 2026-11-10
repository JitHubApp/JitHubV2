using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// The inline run under a precise pointer and the inline container that
/// holds it.
/// </summary>
/// <remarks>
/// Click routing, link hover painting and the abbreviation tooltip read this
/// state. It points into the current layout snapshot, so the owner clears it
/// whenever that snapshot is replaced.
/// </remarks>
internal sealed class PointerHoverState
{
    /// <summary>The hovered run, or null when the pointer is over no run.</summary>
    public InlineRun? Run { get; private set; }

    /// <summary>The container that holds <see cref="Run"/>.</summary>
    public InlineContainerBox? Box { get; private set; }

    /// <summary>True when a link run is hovered inside a known container.</summary>
    public bool HasLink => Run is LinkRun && Box is not null;

    /// <summary>The hovered link and its container, when there is one.</summary>
    public bool TryGetLink(out LinkRun link, out InlineContainerBox box)
    {
        if (Run is LinkRun hoveredLink && Box is { } hoveredBox)
        {
            link = hoveredLink;
            box = hoveredBox;
            return true;
        }

        link = null!;
        box = null!;
        return false;
    }

    public void Set(InlineRun? run, InlineContainerBox? box)
    {
        Run = run;
        Box = box;
    }

    public void Clear()
    {
        Run = null;
        Box = null;
    }
}
