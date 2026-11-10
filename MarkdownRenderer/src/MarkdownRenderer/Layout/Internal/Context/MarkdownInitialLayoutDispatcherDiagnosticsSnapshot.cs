using System.Diagnostics;
using System.Threading;

namespace MarkdownRenderer.Layout.Internal.Context;

internal readonly record struct MarkdownInitialLayoutDispatcherDiagnosticsSnapshot(
    bool SentinelEnqueued,
    bool SentinelDispatched,
    double SentinelDelayMilliseconds,
    int HeartbeatSampleCount,
    double HeartbeatMaxGapMilliseconds);
