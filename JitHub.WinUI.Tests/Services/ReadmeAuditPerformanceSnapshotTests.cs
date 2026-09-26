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
              "ThemeSnapshotMilliseconds": 0.2,
              "LayoutMilliseconds": 2.5,
              "PublicationMilliseconds": 0.5,
              "CommitMilliseconds": 0.1,
              "OverlayResetMilliseconds": 0.1,
              "PlanConstructionMilliseconds": 0.1,
              "VisibleRealizationMilliseconds": 0.15,
              "EmbedRealizationMilliseconds": 0.05,
              "HighlightSchedulingMilliseconds": 0.05,
              "HighlightRetirementMilliseconds": 0.02,
              "HighlightBandSchedulingMilliseconds": 0.03,
              "AdornmentFocusMilliseconds": 0.05,
              "FinalNotificationMilliseconds": 0.05
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
        Assert.Equal(0.2, snapshot.Pipeline.ThemeSnapshotMilliseconds);
        Assert.Equal(2.5, snapshot.Pipeline.LayoutMilliseconds);
        Assert.Equal(0.5, snapshot.Pipeline.PublicationMilliseconds);
        Assert.Equal(0.1, snapshot.Pipeline.CommitMilliseconds);
        Assert.Equal(0.1, snapshot.Pipeline.OverlayResetMilliseconds);
        Assert.Equal(0.1, snapshot.Pipeline.PlanConstructionMilliseconds);
        Assert.Equal(0.15, snapshot.Pipeline.VisibleRealizationMilliseconds);
        Assert.Equal(0.05, snapshot.Pipeline.EmbedRealizationMilliseconds);
        Assert.Equal(0.05, snapshot.Pipeline.HighlightSchedulingMilliseconds);
        Assert.Equal(0.02, snapshot.Pipeline.HighlightRetirementMilliseconds);
        Assert.Equal(0.03, snapshot.Pipeline.HighlightBandSchedulingMilliseconds);
        Assert.Equal(0.05, snapshot.Pipeline.AdornmentFocusMilliseconds);
        Assert.Equal(0.05, snapshot.Pipeline.FinalNotificationMilliseconds);
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
    [InlineData("ThemeSnapshotMilliseconds")]
    [InlineData("LayoutMilliseconds")]
    [InlineData("PublicationMilliseconds")]
    [InlineData("CommitMilliseconds")]
    [InlineData("OverlayResetMilliseconds")]
    [InlineData("PlanConstructionMilliseconds")]
    [InlineData("VisibleRealizationMilliseconds")]
    [InlineData("EmbedRealizationMilliseconds")]
    [InlineData("HighlightSchedulingMilliseconds")]
    [InlineData("HighlightRetirementMilliseconds")]
    [InlineData("HighlightBandSchedulingMilliseconds")]
    [InlineData("AdornmentFocusMilliseconds")]
    [InlineData("FinalNotificationMilliseconds")]
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
    [InlineData("ThemeSnapshotMilliseconds", -1)]
    [InlineData("LayoutMilliseconds", -1)]
    [InlineData("PublicationMilliseconds", -1)]
    [InlineData("CommitMilliseconds", -1)]
    [InlineData("OverlayResetMilliseconds", -1)]
    [InlineData("PlanConstructionMilliseconds", -1)]
    [InlineData("VisibleRealizationMilliseconds", -1)]
    [InlineData("EmbedRealizationMilliseconds", -1)]
    [InlineData("HighlightSchedulingMilliseconds", -1)]
    [InlineData("HighlightRetirementMilliseconds", -1)]
    [InlineData("HighlightBandSchedulingMilliseconds", -1)]
    [InlineData("AdornmentFocusMilliseconds", -1)]
    [InlineData("FinalNotificationMilliseconds", -1)]
    public void Parse_RejectsInvalidPipelineStage(string property, double value)
    {
        JsonNode evidence = JsonNode.Parse(ValidEvidence)!;
        evidence["Performance"]!["Pipeline"]![property] = value;

        Assert.Throws<InvalidDataException>(() => Parse(evidence));
    }

    [Fact]
    public void Parse_RejectsPublicationPhasesThatDoNotAddToTheReportedTotal()
    {
        JsonNode evidence = JsonNode.Parse(ValidEvidence)!;
        evidence["Performance"]!["Pipeline"]!["PlanConstructionMilliseconds"] = 0.3;

        Assert.Throws<InvalidDataException>(() => Parse(evidence));
    }

    [Fact]
    public void Parse_RejectsThemeSnapshotLongerThanSetup()
    {
        JsonNode evidence = JsonNode.Parse(ValidEvidence)!;
        evidence["Performance"]!["Pipeline"]!["ThemeSnapshotMilliseconds"] = 0.8;

        Assert.Throws<InvalidDataException>(() => Parse(evidence));
    }

    [Fact]
    public void Parse_RejectsVisibleRealizationPhasesThatDoNotAddToTheReportedTotal()
    {
        JsonNode evidence = JsonNode.Parse(ValidEvidence)!;
        evidence["Performance"]!["Pipeline"]!["EmbedRealizationMilliseconds"] = 0.2;

        Assert.Throws<InvalidDataException>(() => Parse(evidence));
    }

    [Fact]
    public void Parse_RejectsHighlightPhasesThatDoNotAddToTheReportedTotal()
    {
        JsonNode evidence = JsonNode.Parse(ValidEvidence)!;
        evidence["Performance"]!["Pipeline"]!["HighlightRetirementMilliseconds"] = 0.04;

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
