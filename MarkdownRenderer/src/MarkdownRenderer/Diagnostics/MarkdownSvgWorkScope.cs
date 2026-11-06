using System;
using System.Threading;

namespace MarkdownRenderer.Diagnostics;

/// <summary>
/// Restores the prior ambient SVG work ID after async provider and worker
/// operations complete. AsyncLocal carries the ID across Task.Run/await without
/// adding content identity to provider requests or the worker protocol.
/// </summary>
internal readonly struct MarkdownSvgWorkScope : IDisposable
{
    private readonly long _previousWorkId;
    private readonly long _workId;

    internal MarkdownSvgWorkScope(long previousWorkId, long workId)
    {
        _previousWorkId = previousWorkId;
        _workId = workId;
    }

    internal long WorkId => _workId;

    public void Dispose()
    {
        if (_workId != 0 && MarkdownSvgStageTrace.CurrentWorkId == _workId)
            MarkdownSvgStageTrace.SetCurrentWorkId(_previousWorkId);
    }
}
