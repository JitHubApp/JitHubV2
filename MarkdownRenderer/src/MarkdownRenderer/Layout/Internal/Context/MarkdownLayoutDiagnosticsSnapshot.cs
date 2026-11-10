using System.Diagnostics;
using System.Threading;

namespace MarkdownRenderer.Layout.Internal.Context;

internal readonly record struct MarkdownLayoutDiagnosticsSnapshot(
    int TopLevelBlockCount,
    int CanvasTextLayoutConstructorCalls,
    double CanvasTextLayoutConstructorMilliseconds,
    double BlockConstructionMilliseconds,
    double MeasureArrangeMilliseconds,
    double SnapshotConstructionMilliseconds);
