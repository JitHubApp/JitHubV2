using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// The use count and retirement state of one opened SVG document an image box
/// renders from.
/// </summary>
internal sealed class SvgDocumentUseState(IMarkdownSvgDocument document)
{
    internal readonly IMarkdownSvgDocument Document = document;
    internal int ActiveUses;
    internal bool CandidateReserved;
    internal bool Retired;
    internal bool DisposalScheduled;
    internal ImageWorkLease? ReservedDisposalWork;
}
