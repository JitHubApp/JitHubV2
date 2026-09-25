using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ReadmeAuditPerformanceSnapshotTests
{
    private const string ValidEvidence = """
        {
          "Performance": {
            "SourceCacheBytes": 1048576,
            "SourceCacheHits": 2,
            "ImageFetches": 3,
            "ImageFetchMilliseconds": 4,
            "ImageFetchFailures": 0,
            "ImageFetchCancellations": 0,
            "SourceCacheEvictions": 0,
            "PendingImageFetches": 1,
            "ActiveImageFetches": 2,
            "CpuPreparations": 3,
            "CpuPreparationMilliseconds": 4,
            "ScenePreparations": 5,
            "ScenePreparationMilliseconds": 6,
            "InFlightSourceBytes": 1048576,
            "PeakInFlightSourceBytes": 4194304,
            "PendingSourceByteRequests": 2,
            "Pipeline": {
              "Generation": 3,
              "SourceUtf16Bytes": 2048,
              "ParseMilliseconds": 1.5,
              "SetupMilliseconds": 0.75,
              "LayoutMilliseconds": 2.5,
              "PublicationMilliseconds": 0.5
            }
          }
        }
        """;

    [Fact]
    public void Parse_PreservesTheProductionSourceAdmissionCounters()
    {
        using JsonDocument evidence = JsonDocument.Parse(ValidEvidence);

        ReadmeAuditPerformanceSnapshot snapshot =
            ReadmeAuditPerformanceSnapshot.Parse(evidence.RootElement);

        Assert.Equal(1_048_576, snapshot.InFlightSourceBytes);
        Assert.Equal(4_194_304, snapshot.PeakInFlightSourceBytes);
        Assert.Equal(2, snapshot.PendingSourceByteRequests);
        Assert.Equal(3, snapshot.Pipeline.Generation);
        Assert.Equal(1.5, snapshot.Pipeline.ParseMilliseconds);
        Assert.Equal(0.75, snapshot.Pipeline.SetupMilliseconds);
        Assert.Equal(2.5, snapshot.Pipeline.LayoutMilliseconds);
        Assert.Equal(0.5, snapshot.Pipeline.PublicationMilliseconds);
    }

    [Theory]
    [InlineData("InFlightSourceBytes")]
    [InlineData("PeakInFlightSourceBytes")]
    [InlineData("PendingSourceByteRequests")]
    public void Parse_RejectsMissingSourceAdmissionEvidence(string property)
    {
        JsonNode evidence = JsonNode.Parse(ValidEvidence)!;
        Assert.True(evidence["Performance"]!.AsObject().Remove(property));

        Assert.Throws<InvalidDataException>(() => Parse(evidence));
    }

    [Fact]
    public void Parse_RejectsMalformedEnvelope()
    {
        using JsonDocument evidence = JsonDocument.Parse("[]");

        Assert.Throws<InvalidDataException>(() =>
            ReadmeAuditPerformanceSnapshot.Parse(evidence.RootElement));
    }

    [Theory]
    [InlineData("Generation")]
    [InlineData("SourceUtf16Bytes")]
    [InlineData("ParseMilliseconds")]
    [InlineData("SetupMilliseconds")]
    [InlineData("LayoutMilliseconds")]
    [InlineData("PublicationMilliseconds")]
    public void Parse_RejectsMissingPipelineStage(string property)
    {
        JsonNode evidence = JsonNode.Parse(ValidEvidence)!;
        Assert.True(evidence["Performance"]!["Pipeline"]!.AsObject().Remove(property));

        Assert.Throws<InvalidDataException>(() => Parse(evidence));
    }

    [Fact]
    public void Parse_RejectsMissingPipelineEvidence()
    {
        JsonNode evidence = JsonNode.Parse(ValidEvidence)!;
        Assert.True(evidence["Performance"]!.AsObject().Remove("Pipeline"));

        Assert.Throws<InvalidDataException>(() => Parse(evidence));
    }

    [Theory]
    [InlineData("Generation", 0)]
    [InlineData("SourceUtf16Bytes", -1)]
    [InlineData("ParseMilliseconds", -1)]
    [InlineData("SetupMilliseconds", -1)]
    [InlineData("LayoutMilliseconds", -1)]
    [InlineData("PublicationMilliseconds", -1)]
    public void Parse_RejectsInvalidPipelineStage(string property, double value)
    {
        JsonNode evidence = JsonNode.Parse(ValidEvidence)!;
        evidence["Performance"]!["Pipeline"]![property] = value;

        Assert.Throws<InvalidDataException>(() => Parse(evidence));
    }

    [Theory]
    [InlineData("PeakInFlightSourceBytes", 64L * 1024 * 1024 + 1)]
    [InlineData("InFlightSourceBytes", 4_194_305)]
    [InlineData("PendingSourceByteRequests", -1)]
    [InlineData("SourceCacheBytes", 64L * 1024 * 1024 + 1)]
    public void Parse_RejectsImpossibleOrOverBudgetCounters(string property, long value)
    {
        JsonNode evidence = JsonNode.Parse(ValidEvidence)!;
        evidence["Performance"]![property] = value;

        Assert.Throws<InvalidDataException>(() => Parse(evidence));
    }

    private static ReadmeAuditPerformanceSnapshot Parse(JsonNode evidence)
    {
        using JsonDocument document = JsonDocument.Parse(evidence.ToJsonString());
        return ReadmeAuditPerformanceSnapshot.Parse(document.RootElement);
    }
}
