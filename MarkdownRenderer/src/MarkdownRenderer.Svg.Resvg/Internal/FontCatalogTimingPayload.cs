#if MARKDOWN_LAYOUT_DIAGNOSTICS
using System.Buffers.Binary;
using System.Diagnostics;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal readonly record struct FontCatalogPhaseTiming(
    SvgWorkerStageId StageId,
    long WallStopwatchTicks,
    long WorkerCpuTicks);

/// <summary>
/// Parses the fixed-size, content-free timing payload carried in the existing
/// successful HELLO detail field. Native durations use Windows 100-ns ticks.
/// </summary>
internal static class FontCatalogTimingPayload
{
    internal const string Prefix = "font-timings-v1:";
    private const int PhaseCount = 7;
    private const int RecordBytes = 9;
    private const int PayloadBytes = PhaseCount * RecordBytes;
    private const int EncodedPayloadChars = ((PayloadBytes + 2) / 3) * 4;

    private static readonly SvgWorkerStageId[] StageIds =
    [
        SvgWorkerStageId.FontCatalogDatabaseCreate,
        SvgWorkerStageId.FontCatalogMappedFonts,
        SvgWorkerStageId.FontCatalogUserLocalFonts,
        SvgWorkerStageId.FontCatalogUserRoamingFonts,
        SvgWorkerStageId.FontCatalogFamilyDefaults,
        SvgWorkerStageId.FontCatalogTextPipelineWarmup,
        SvgWorkerStageId.FontCatalogTotal,
    ];

    internal static bool TryParse(
        string? detail,
        out FontCatalogPhaseTiming[] phases)
    {
        phases = [];
        if (detail is null ||
            detail.Length != Prefix.Length + EncodedPayloadChars ||
            !detail.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        Span<byte> payload = stackalloc byte[PayloadBytes];
        if (!Convert.TryFromBase64String(
                detail[Prefix.Length..],
                payload,
                out int bytesWritten) ||
            bytesWritten != PayloadBytes)
        {
            return false;
        }

        var parsed = new FontCatalogPhaseTiming[PhaseCount];
        for (int index = 0; index < PhaseCount; index++)
        {
            int offset = index * RecordBytes;
            byte executed = payload[offset];
            uint wallTicks100Nanoseconds = BinaryPrimitives.ReadUInt32LittleEndian(payload[(offset + 1)..]);
            uint cpuTicks100Nanoseconds = BinaryPrimitives.ReadUInt32LittleEndian(payload[(offset + 5)..]);
            if (executed > 1 ||
                (executed == 0 && (wallTicks100Nanoseconds != 0 || cpuTicks100Nanoseconds != 0)) ||
                ((index is 0 or 1 or 4 or 5 or 6) && executed != 1))
            {
                return false;
            }

            long wallStopwatchTicks = executed == 0
                ? 0
                : ConvertWindows100NanosecondTicksToStopwatchTicks(wallTicks100Nanoseconds);
            long workerCpuTicks = cpuTicks100Nanoseconds == uint.MaxValue
                ? -1
                : cpuTicks100Nanoseconds;
            parsed[index] = new FontCatalogPhaseTiming(
                StageIds[index],
                wallStopwatchTicks,
                workerCpuTicks);
        }

        phases = parsed;
        return true;
    }

    private static long ConvertWindows100NanosecondTicksToStopwatchTicks(uint ticks)
    {
        double converted = ticks * (double)Stopwatch.Frequency / TimeSpan.TicksPerSecond;
        return checked((long)Math.Round(converted, MidpointRounding.AwayFromZero));
    }
}
#endif
