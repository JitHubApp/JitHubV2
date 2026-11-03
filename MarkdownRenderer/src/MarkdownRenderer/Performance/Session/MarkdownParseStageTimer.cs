using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Reports one parse transaction's stage timings to an optional recorder.
/// With no recorder it reads no stopwatch and reports nothing.
/// </summary>
internal readonly struct MarkdownParseStageTimer
{
    private readonly Action<int, int, long>? _recorder;
    private readonly int _ownerIdentity;

    internal MarkdownParseStageTimer(Action<int, int, long>? recorder, object documentOwner)
    {
        _recorder = recorder;
        _ownerIdentity = recorder is not null
            ? RuntimeHelpers.GetHashCode(documentOwner)
            : 0;
    }

    internal long Start() =>
        _recorder is not null ? Stopwatch.GetTimestamp() : 0;

    internal void Record(MarkdownParsePipelineStage stage, long startedTimestamp)
    {
        if (_recorder is null)
            return;

        long elapsedTicks = stage == MarkdownParsePipelineStage.Begin
            ? 0
            : Stopwatch.GetTimestamp() - startedTimestamp;
        _recorder(_ownerIdentity, (int)stage, elapsedTicks);
    }
}
