using System.Text.Json;
using System.Text.Json.Serialization;
using JitHub.WinUI.Automation;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ReadmeAuditTimingRatioTests
{
    [Theory]
    [InlineData(100, 50, 2)]
    [InlineData(50, 100, 0.5)]
    [InlineData(110, 100, 1.1)]
    public void ValidRatiosPreserveTheMeasuredDivision(double native, double reference, double expected)
        => Assert.Equal(expected, ReadmeAuditTimingRatio.Calculate(native, reference));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidInputsRemainMissingAndStrictlySerializable(double invalid)
    {
        Assert.Null(ReadmeAuditTimingRatio.Calculate(invalid, 100));
        double? missing = ReadmeAuditTimingRatio.Calculate(100, invalid);
        Assert.Null(missing);
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(
            new TimingRatioEvidence { Ratio = missing }, TimingRatioTestJsonContext.Default.TimingRatioEvidence));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("Ratio").ValueKind);
    }

    [Fact]
    public void OverflowAndUnderflowRemainMissingInsteadOfPassingAsInfinityOrZero()
    {
        Assert.Null(ReadmeAuditTimingRatio.Calculate(double.MaxValue, double.Epsilon));
        Assert.Null(ReadmeAuditTimingRatio.Calculate(double.Epsilon, double.MaxValue));
    }

    [Fact]
    public void OfflineMissingLiveTimingDoesNotEraseSameByteFailureEvidence()
    {
        var comparison = new TimingRatioEvidence
        {
            NativeToBrowserFirstRenderRatio = ReadmeAuditTimingRatio.Calculate(250, 0),
            NativeToBrowserFullPageRatio = ReadmeAuditTimingRatio.Calculate(1000, 0),
            NativeToSameByteFirstRenderRatio = ReadmeAuditTimingRatio.Calculate(250, 100),
            NativeToSameByteFullPageRatio = ReadmeAuditTimingRatio.Calculate(1000, 500),
        };
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(
            comparison, TimingRatioTestJsonContext.Default.TimingRatioEvidence));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("NativeToBrowserFirstRenderRatio").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("NativeToBrowserFullPageRatio").ValueKind);
        Assert.Equal(2.5, document.RootElement.GetProperty("NativeToSameByteFirstRenderRatio").GetDouble());
        Assert.Equal(2, document.RootElement.GetProperty("NativeToSameByteFullPageRatio").GetDouble());
    }
}

internal sealed class TimingRatioEvidence
{
    public double? Ratio { get; init; }
    public double? NativeToBrowserFirstRenderRatio { get; init; }
    public double? NativeToBrowserFullPageRatio { get; init; }
    public double? NativeToSameByteFirstRenderRatio { get; init; }
    public double? NativeToSameByteFullPageRatio { get; init; }
}

[JsonSerializable(typeof(TimingRatioEvidence))]
internal partial class TimingRatioTestJsonContext : JsonSerializerContext;
