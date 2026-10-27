using System.Buffers.Binary;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class WorkerFontProtocolTests
{
    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void VersionTenRejectsOldFontReadinessFrames(ushort oldVersion)
    {
        Assert.Equal(10, WorkerProtocol.Version);
        byte[] frame = ValidResponse();
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), oldVersion);
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.DecodeResponse(frame, 31, 41, 51));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(uint.MaxValue)]
    public void PublishedGenerationOccupiesOnlyTheNewFixedField(uint generation)
    {
        byte[] frame = ValidResponse();
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(76), generation);
        WorkerResponse response = WorkerProtocol.DecodeResponse(frame, 31, 41, 51);
        Assert.Equal((long)generation, response.ReadyFontGeneration);
        Assert.Equal(WorkerProtocol.ResponseSize, frame.Length);
        Assert.Equal(string.Empty, response.Detail);

        frame[80] = 1;
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.DecodeResponse(frame, 31, 41, 51));
    }

    [Fact]
    public void ZeroFormatSuccessIsAcceptedOnlyForAnEmptyFontCatalogAcknowledgement()
    {
        byte[] frame = ValidResponse();
        frame[72] = 0;
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.DecodeResponse(frame, 31, 41, 51));
        Assert.Throws<WorkerProtocolException>(() =>
            WorkerProtocol.DecodeResponse(frame, 31, 41, 51, WorkerOperation.Render));
        WorkerResponse response = WorkerProtocol.DecodeResponse(frame, 31, 41, 51, WorkerOperation.FontCatalog);
        WindowsWorkerProcess.ValidateFontCatalogMappingAcknowledgement(response);

        frame[64] = 1;
        Assert.Throws<WorkerProtocolException>(() =>
            WorkerProtocol.DecodeResponse(frame, 31, 41, 51, WorkerOperation.FontCatalog));
        frame[64] = 0;
        frame[73] = 1;
        Assert.Throws<WorkerProtocolException>(() =>
            WorkerProtocol.DecodeResponse(frame, 31, 41, 51, WorkerOperation.FontCatalog));
    }

    [Fact]
    public void FontCatalogAcknowledgementRejectsRasterMetadataAndUnsupportedStatuses()
    {
        WorkerResponse response = WorkerProtocol.DecodeResponse(ValidResponse(), 31, 41, 51);
        WindowsWorkerProcess.ValidateFontCatalogMappingAcknowledgement(response);
        Assert.Throws<WorkerProtocolException>(() =>
            WindowsWorkerProcess.ValidateFontCatalogMappingAcknowledgement(response with { IntrinsicWidth = double.NaN }));
        Assert.Throws<WorkerProtocolException>(() =>
            WindowsWorkerProcess.ValidateFontCatalogMappingAcknowledgement(response with { OutputLength = 1 }));
        Assert.Throws<WorkerProtocolException>(() =>
            WindowsWorkerProcess.ValidateFontCatalogMappingAcknowledgement(response with { Status = WorkerStatus.FontCatalogPending }));
        WindowsWorkerProcess.ValidateFontCatalogMappingAcknowledgement(response with
        {
            Status = WorkerStatus.FontCatalogPending,
            Detail = "font-catalog-busy",
        });
    }

    private static byte[] ValidResponse()
    {
        byte[] frame = new byte[WorkerProtocol.ResponseSize];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, WorkerProtocol.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), WorkerProtocol.Version);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(8), 31);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(16), 41);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(24), 51);
        frame[72] = 1;
        return frame;
    }
}
