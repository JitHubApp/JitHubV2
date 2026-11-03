using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;

namespace MarkdownRenderer.Svg.Resvg;

internal readonly record struct SvgDocumentRenderSnapshot(
    SvgPreflightResult Preflight,
    MarkdownSvgOpenRequest OpenRequest);
