#if MARKDOWN_LAYOUT_DIAGNOSTICS
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class FontCatalogTimingPayloadTests
{
    [Fact]
    public void ParsesFixedPhaseOrderAndConvertsWallTicksForEvent13()
    {
        (byte Executed, uint WallTicks, uint CpuTicks)[] records =
        [
            (1, 100, 10),
            (1, 200, 20),
            (0, 0, 0),
            (1, 400, uint.MaxValue),
            (1, 500, 50),
            (1, 600, 60),
            (1, 700, 70),
        ];
        string detail = BuildDetail(records);

        Assert.True(FontCatalogTimingPayload.TryParse(detail, out FontCatalogPhaseTiming[] phases));
        Assert.Equal(7, phases.Length);
        for (int index = 0; index < phases.Length; index++)
        {
            Assert.Equal(110 + index, (int)phases[index].StageId);
            long expectedWallTicks = records[index].Executed == 0
                ? 0
                : (long)Math.Round(
                    records[index].WallTicks * (double)Stopwatch.Frequency / TimeSpan.TicksPerSecond,
                    MidpointRounding.AwayFromZero);
            Assert.Equal(expectedWallTicks, phases[index].WallStopwatchTicks);
            long expectedCpuTicks = records[index].CpuTicks == uint.MaxValue
                ? -1
                : records[index].CpuTicks;
            Assert.Equal(expectedCpuTicks, phases[index].WorkerCpuTicks);
        }

        Assert.StartsWith(FontCatalogTimingPayload.Prefix, detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ProtocolV9AcceptsTheBoundedSuccessfulHelloDetailAndPublishedGeneration()
    {
        string detail = BuildDetail(ValidRecords());
        byte[] detailBytes = Encoding.UTF8.GetBytes(detail);
        Assert.InRange(detailBytes.Length, 1, 127);

        byte[] frame = new byte[WorkerProtocol.ResponseSize];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, WorkerProtocol.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), WorkerProtocol.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), (ushort)WorkerStatus.Ok);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(8), 41);
        frame[72] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(76), 7);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(74), checked((ushort)detailBytes.Length));
        detailBytes.CopyTo(frame.AsSpan(80));

        WorkerResponse decoded = WorkerProtocol.DecodeResponse(frame, 41, 0, 0);

        Assert.Equal(WorkerProtocol.Version, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4)));
        Assert.Equal(detail, decoded.Detail);
        Assert.Equal(7, decoded.ReadyFontGeneration);
        Assert.True(FontCatalogTimingPayload.TryParse(decoded.Detail, out FontCatalogPhaseTiming[] phases));
        Assert.Equal(7, phases.Length);

        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(74), 128);
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.DecodeResponse(frame, 41, 0, 0));
    }

    [Fact]
    public void RejectsMalformedOrInconsistentPayloadsWithoutThrowing()
    {
        Assert.False(FontCatalogTimingPayload.TryParse(null, out _));
        Assert.False(FontCatalogTimingPayload.TryParse("font-timings-v0:AAAA", out _));
        Assert.False(FontCatalogTimingPayload.TryParse(FontCatalogTimingPayload.Prefix + "not-base64!", out _));

        (byte Executed, uint WallTicks, uint CpuTicks)[] records = ValidRecords();
        records[1] = (2, 100, 10);
        Assert.False(FontCatalogTimingPayload.TryParse(BuildDetail(records), out _));

        records = ValidRecords();
        records[0] = (0, 0, 0);
        Assert.False(FontCatalogTimingPayload.TryParse(BuildDetail(records), out _));

        records = ValidRecords();
        records[2] = (0, 1, 0);
        Assert.False(FontCatalogTimingPayload.TryParse(BuildDetail(records), out _));

        records = ValidRecords();
        Assert.False(FontCatalogTimingPayload.TryParse(
            BuildDetail(records)[..^1],
            out _));
    }

    private static (byte Executed, uint WallTicks, uint CpuTicks)[] ValidRecords() =>
    [
        (1, 1, 1),
        (1, 2, 2),
        (1, 3, 3),
        (1, 4, 4),
        (1, 5, 5),
        (1, 6, 6),
        (1, 7, 7),
    ];

    private static string BuildDetail((byte Executed, uint WallTicks, uint CpuTicks)[] records)
    {
        byte[] payload = new byte[7 * 9];
        for (int index = 0; index < records.Length; index++)
        {
            int offset = index * 9;
            payload[offset] = records[index].Executed;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset + 1), records[index].WallTicks);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset + 5), records[index].CpuTicks);
        }

        return FontCatalogTimingPayload.Prefix + Convert.ToBase64String(payload);
    }
}
#endif
