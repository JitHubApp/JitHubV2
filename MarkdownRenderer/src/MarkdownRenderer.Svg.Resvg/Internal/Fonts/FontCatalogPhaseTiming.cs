using System.Diagnostics;
using MarkdownRenderer.Svg.Resvg.Internal.Diagnostics;

namespace MarkdownRenderer.Svg.Resvg.Internal.Fonts;

internal readonly record struct FontCatalogPhaseTiming(
    SvgWorkerStageId StageId,
    long WallStopwatchTicks,
    long WorkerCpuTicks);
