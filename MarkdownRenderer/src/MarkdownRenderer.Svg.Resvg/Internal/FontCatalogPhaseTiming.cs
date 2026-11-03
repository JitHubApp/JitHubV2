using System.Diagnostics;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal readonly record struct FontCatalogPhaseTiming(
    SvgWorkerStageId StageId,
    long WallStopwatchTicks,
    long WorkerCpuTicks);
