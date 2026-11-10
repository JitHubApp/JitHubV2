using MarkdownRenderer.Layout.Internal.LazyLayout;

namespace MarkdownRenderer.Controls.Internal.LazyLayout;

internal readonly struct LazyLayoutWorkResult
{
    internal readonly LazyLayoutCommit Commit;
    internal readonly (int BlockIndex, double OffsetFromTop)? ScrollAnchor;

    internal LazyLayoutWorkResult(
        LazyLayoutCommit commit,
        (int BlockIndex, double OffsetFromTop)? scrollAnchor)
    {
        Commit = commit;
        ScrollAnchor = scrollAnchor;
    }
}
