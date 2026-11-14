using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Svg.Resvg.Internal.Preflight;

namespace MarkdownRenderer.Svg.Resvg.Internal.Documents;

internal readonly record struct SvgDocumentRenderSnapshot(
    SvgPreflightResult Preflight,
    MarkdownSvgOpenRequest OpenRequest);
