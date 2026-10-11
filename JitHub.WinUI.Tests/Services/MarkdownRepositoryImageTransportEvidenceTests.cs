using System.Text.Json;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

[Collection("Markdown renderer audit environment")]
public sealed class MarkdownRepositoryImageTransportEvidenceTests
{
    private const string PathVariable = "JITHUB_MARKDOWN_IMAGE_TRANSPORT_EVIDENCE_PATH";

    [Fact]
    public void TransportEvidenceRequiresBothExplicitAuditAndAnExplicitPath()
    {
        WithEvidence(path =>
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            Assert.Null(CreateObserver());
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(true, null);
            Environment.SetEnvironmentVariable(PathVariable, null);
            Assert.Null(CreateObserver());
            Assert.False(File.Exists(path));
        });
    }

    [Fact]
    public void SourceAndDocumentHashesCorrelateRawAndRepositoryStagesWithoutRawIdentity()
    {
        WithEvidence(path =>
        {
            Action<GitHubRawImageFetchOutcome> raw = Assert.IsType<Action<GitHubRawImageFetchOutcome>>(CreateObserver());
            Action<GitHubRawImageFetchOutcome> repository = Assert.IsType<Action<GitHubRawImageFetchOutcome>>(
                CreateObserver(MarkdownRepositoryImageTransportStage.RepositoryFetcher));
            raw(new(GitHubRawImageFetchOutcomeKind.HttpStatus, 404));
            raw(new(GitHubRawImageFetchOutcomeKind.Success)); // raw service summary is one-shot
            repository(new(GitHubRawImageFetchOutcomeKind.HttpStatus, 429));
            repository(new(GitHubRawImageFetchOutcomeKind.Success));
            string[] rows = File.ReadAllLines(path);
            Assert.Equal(3, rows.Length);
            using JsonDocument first = JsonDocument.Parse(rows[0]);
            using JsonDocument second = JsonDocument.Parse(rows[1]);
            using JsonDocument third = JsonDocument.Parse(rows[2]);
            Assert.Equal("RawImageServiceResult", first.RootElement.GetProperty("Stage").GetString());
            Assert.Equal(404, first.RootElement.GetProperty("HttpStatusCode").GetInt32());
            Assert.Equal("RepositoryFetcher", second.RootElement.GetProperty("Stage").GetString());
            Assert.Equal(1, second.RootElement.GetProperty("StageObservationOrdinal").GetInt32());
            Assert.Equal(2, third.RootElement.GetProperty("StageObservationOrdinal").GetInt32());
            Assert.Equal(second.RootElement.GetProperty("OperationId").GetInt64(), third.RootElement.GetProperty("OperationId").GetInt64());
            Assert.Equal(first.RootElement.GetProperty("Identity").GetRawText(), second.RootElement.GetProperty("Identity").GetRawText());
            Assert.DoesNotContain("screenshot", string.Join('\n', rows), StringComparison.Ordinal);
            Assert.DoesNotContain("secret", string.Join('\n', rows), StringComparison.Ordinal);
            Assert.DoesNotContain("code-server", string.Join('\n', rows), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void OldCallbacksAreRejectedAcrossLaunchReconfiguration()
    {
        WithEvidence(path =>
        {
            Action<GitHubRawImageFetchOutcome> stale = Assert.IsType<Action<GitHubRawImageFetchOutcome>>(CreateObserver());
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(true, null);
            stale(new(GitHubRawImageFetchOutcomeKind.Success));
            Assert.False(File.Exists(path));
            CreateObserver()!(new(GitHubRawImageFetchOutcomeKind.NullResult));
            Assert.Single(File.ReadAllLines(path));
        });
    }

    [Fact]
    public void TransportRecordsRemainBoundedPerDocumentAndPerFetcher()
    {
        WithEvidence(path =>
        {
            var repository = CreateObserver(MarkdownRepositoryImageTransportStage.RepositoryFetcher)!;
            for (int i = 0; i < 8; i++) repository(new(GitHubRawImageFetchOutcomeKind.HttpStatus, 503));
            Assert.Equal(3, File.ReadAllLines(path).Length);
            for (int i = 0; i < 520; i++) CreateObserver()!(new(GitHubRawImageFetchOutcomeKind.NullResult));
            string[] rows = File.ReadAllLines(path);
            Assert.Equal(513, rows.Length); // 512 outcomes + one explicit limit notice
            using JsonDocument limit = JsonDocument.Parse(rows[^1]);
            Assert.Equal("BudgetLimit", limit.RootElement.GetProperty("RecordType").GetString());
            Assert.Equal("FailureRecords", limit.RootElement.GetProperty("Budget").GetString());
        });
    }

    [Fact]
    public void EarlySuccessSamplesCannotConsumeFailureCapacityAndLimitsAreVisible()
    {
        WithEvidence(path =>
        {
            for (int i = 0; i < 80; i++) CreateObserver()!(new(GitHubRawImageFetchOutcomeKind.Success));
            CreateObserver()!(new(GitHubRawImageFetchOutcomeKind.HttpStatus, 404));
            string[] rows = File.ReadAllLines(path);
            Assert.Equal(34, rows.Length); // 32 success samples + notice + failure
            using JsonDocument notice = JsonDocument.Parse(rows[^2]);
            Assert.Equal("SuccessSample", notice.RootElement.GetProperty("Budget").GetString());
            using JsonDocument failure = JsonDocument.Parse(rows[^1]);
            Assert.Equal(404, failure.RootElement.GetProperty("HttpStatusCode").GetInt32());
        });
    }

    [Fact]
    public void DelayedCallbackKeepsItsCapturedDestinationAndIsRejectedAfterReconfiguration()
    {
        WithEvidence(path =>
        {
            string otherPath = path + ".other";
            try
            {
                var captured = CreateObserver()!;
                Environment.SetEnvironmentVariable(PathVariable, otherPath);
                captured(new(GitHubRawImageFetchOutcomeKind.Success));
                Assert.Single(File.ReadAllLines(path));
                Assert.False(File.Exists(otherPath));
                MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(true, null);
                captured(new(GitHubRawImageFetchOutcomeKind.NullResult));
                CreateObserver()!(new(GitHubRawImageFetchOutcomeKind.Success));
                Assert.Single(File.ReadAllLines(path));
                Assert.Single(File.ReadAllLines(otherPath));
            }
            finally { if (File.Exists(otherPath)) File.Delete(otherPath); }
        });
    }

    [Fact]
    public void ConcurrentOldCallbacksCannotSpendNewGenerationFailureCapacity()
    {
        WithEvidence(path =>
        {
            var old = Enumerable.Range(0, 512).Select(_ => CreateObserver()!).ToArray();
            using var start = new ManualResetEventSlim();
            Task callbacks = Task.Run(() =>
            {
                start.Wait();
                Parallel.ForEach(old, observe => observe(new(GitHubRawImageFetchOutcomeKind.NullResult)));
            });
            start.Set();
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(true, null);
            callbacks.GetAwaiter().GetResult();
            // Old rows may legitimately complete before reconfiguration. The
            // new generation must independently admit all 512 failure rows.
            int before = File.Exists(path) ? File.ReadAllLines(path).Length : 0;
            for (int i = 0; i < 512; i++) CreateObserver()!(new(GitHubRawImageFetchOutcomeKind.NullResult));
            Assert.Equal(before + 512, File.ReadAllLines(path).Length);
            foreach (var observe in old) observe(new(GitHubRawImageFetchOutcomeKind.Success));
            Assert.Equal(before + 512, File.ReadAllLines(path).Length);
        });
    }

    [Fact]
    public void ObserverAdmissionBoundsTheAggregateToFiveHundredDocuments()
    {
        WithEvidence(path =>
        {
            for (int i = 0; i < 500; i++)
                Assert.NotNull(CreateObserver(documentPath: $"docs/{i}/README.md"));
            Assert.Null(CreateObserver(documentPath: "docs/overflow/README.md"));
            Assert.Null(CreateObserver(documentPath: "docs/second-overflow/README.md"));
            Assert.NotNull(CreateObserver(documentPath: "docs/0/README.md"));
            using JsonDocument notice = JsonDocument.Parse(Assert.Single(File.ReadAllLines(path)));
            Assert.Equal("Documents", notice.RootElement.GetProperty("Budget").GetString());
            Assert.Equal(500, notice.RootElement.GetProperty("MaximumDocuments").GetInt32());
        });
    }

    [Fact]
    public void FailedEvidenceWriteCannotEscapeToTheImageFetcher()
    {
        WithEvidence(path =>
        {
            Directory.CreateDirectory(path); // a directory cannot be appended as a file
            CreateObserver()!(new(GitHubRawImageFetchOutcomeKind.Success));
            Assert.False(File.Exists(path));
            Directory.Delete(path);
        });
    }

    private static Action<GitHubRawImageFetchOutcome>? CreateObserver(
        MarkdownRepositoryImageTransportStage stage = MarkdownRepositoryImageTransportStage.RawImageServiceResult,
        string documentPath = "docs/README.md") =>
        MarkdownLifecycleAutomationBridge.CreateRepositoryImageFetchObserver(
            "./assets/screenshot-1.png",
            new MarkdownImageResolveContext(null, DocumentSource: new MarkdownDocumentSource(
                "private-context", "coder", "code-server", "main", documentPath)),
            new Uri("https://raw.githubusercontent.com/coder/code-server/main/docs/assets/screenshot-1.png?secret=token#private"),
            stage);

    private static void WithEvidence(Action<string> test)
    {
        string? previousPath = Environment.GetEnvironmentVariable(PathVariable);
        string path = Path.Combine(Path.GetTempPath(), $"jithub-image-transport-{Guid.NewGuid():N}.ndjson");
        try
        {
            Environment.SetEnvironmentVariable(PathVariable, path);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(true, null);
            test(path);
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            Environment.SetEnvironmentVariable(PathVariable, previousPath);
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(path)) Directory.Delete(path);
        }
    }
}
