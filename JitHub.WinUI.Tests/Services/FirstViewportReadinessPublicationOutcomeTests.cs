using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services.Markdown;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

[CollectionDefinition("First viewport readiness publication outcome writer", DisableParallelization = true)]
public sealed class FirstViewportReadinessPublicationOutcomeCollection
{
}

[Collection("First viewport readiness publication outcome writer")]
public sealed class FirstViewportReadinessPublicationOutcomeTests
{
    private const int ProcessId = 4312;
    private const string Host = "MarkdownHost_PublicationOutcomeTests";

    [Fact]
    public async Task ReplacedSameGenerationPendingWriteIsSuppressedAndNewestWriteIsPublished()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "progress.ndjson");
        var activeDrain = NewSignal();
        var identity = CreateIdentity(generation: 11);
        Task<bool>? active = null;
        Task<bool>? replaced = null;
        Task<bool>? newest = null;

        try
        {
            active = FirstViewportImagesReadyEvidenceWriter.QueueWriteAfterCurrent(
                activeDrain.Task,
                static () => true);
            replaced = FirstViewportImagesReadyEvidenceWriter.QueueWriteAfterCurrent(
                Task.CompletedTask,
                static () => true,
                publicationIdentity: identity,
                publicationProgressPath: path);
            newest = FirstViewportImagesReadyEvidenceWriter.QueueWriteAfterCurrent(
                Task.CompletedTask,
                static () => true,
                publicationIdentity: identity,
                publicationProgressPath: path);

            Assert.False(await replaced.WaitAsync(TimeSpan.FromSeconds(5)));
            activeDrain.SetResult(true);
            Assert.True(await active.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(await newest.WaitAsync(TimeSpan.FromSeconds(5)));
            await DrainProgressWritesAsync();

            var outcomes = ReadOutcomes(path);
            Assert.Collection(
                outcomes,
                suppressed =>
                {
                    Assert.Equal(11, suppressed.Generation);
                    Assert.Equal("suppressed", suppressed.Outcome);
                },
                published =>
                {
                    Assert.Equal(11, published.Generation);
                    Assert.Equal("published", published.Outcome);
                });
        }
        finally
        {
            if (!activeDrain.Task.IsCompleted)
                activeDrain.TrySetResult(true);
            if (active is not null)
                await active.WaitAsync(TimeSpan.FromSeconds(5));
            if (replaced is not null)
                await replaced.WaitAsync(TimeSpan.FromSeconds(5));
            if (newest is not null)
                await newest.WaitAsync(TimeSpan.FromSeconds(5));
            await DrainProgressWritesAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ActiveWriteFailureDoesNotEraseSameGenerationPendingSuccess()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "progress.ndjson");
        var activeDrain = NewSignal();
        var identity = CreateIdentity(generation: 12);
        Task<bool>? active = null;
        Task<bool>? pending = null;

        try
        {
            active = FirstViewportImagesReadyEvidenceWriter.QueueWriteAfterCurrent(
                activeDrain.Task,
                static () => false,
                publicationIdentity: identity,
                publicationProgressPath: path);
            pending = FirstViewportImagesReadyEvidenceWriter.QueueWriteAfterCurrent(
                Task.CompletedTask,
                static () => true,
                publicationIdentity: identity,
                publicationProgressPath: path);

            activeDrain.SetResult(true);
            Assert.False(await active.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            await DrainProgressWritesAsync();

            var outcomes = ReadOutcomes(path);
            Assert.Collection(
                outcomes,
                failed =>
                {
                    Assert.Equal(12, failed.Generation);
                    Assert.Equal("write-failed", failed.Outcome);
                },
                published =>
                {
                    Assert.Equal(12, published.Generation);
                    Assert.Equal("published", published.Outcome);
                });
        }
        finally
        {
            if (!activeDrain.Task.IsCompleted)
                activeDrain.TrySetResult(true);
            if (active is not null)
                await active.WaitAsync(TimeSpan.FromSeconds(5));
            if (pending is not null)
                await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await DrainProgressWritesAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CanceledOldGenerationIsRecordedBeforeNewGenerationPublication()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "progress.ndjson");
        var activeDrain = NewSignal();
        using var oldGeneration = new CancellationTokenSource();
        Task<bool>? oldWrite = null;
        Task<bool>? newWrite = null;

        try
        {
            oldWrite = FirstViewportImagesReadyEvidenceWriter.QueueWriteAfterCurrent(
                activeDrain.Task,
                static () => true,
                oldGeneration.Token,
                CreateIdentity(generation: 20),
                path);
            newWrite = FirstViewportImagesReadyEvidenceWriter.QueueWriteAfterCurrent(
                Task.CompletedTask,
                static () => true,
                publicationIdentity: CreateIdentity(generation: 21),
                publicationProgressPath: path);

            oldGeneration.Cancel();
            activeDrain.SetResult(true);

            Assert.False(await oldWrite.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(await newWrite.WaitAsync(TimeSpan.FromSeconds(5)));
            await DrainProgressWritesAsync();

            var outcomes = ReadOutcomes(path);
            Assert.Collection(
                outcomes,
                canceled =>
                {
                    Assert.Equal(20, canceled.Generation);
                    Assert.Equal("canceled", canceled.Outcome);
                },
                published =>
                {
                    Assert.Equal(21, published.Generation);
                    Assert.Equal("published", published.Outcome);
                });
        }
        finally
        {
            if (!activeDrain.Task.IsCompleted)
                activeDrain.TrySetResult(true);
            if (oldWrite is not null)
                await oldWrite.WaitAsync(TimeSpan.FromSeconds(5));
            if (newWrite is not null)
                await newWrite.WaitAsync(TimeSpan.FromSeconds(5));
            await DrainProgressWritesAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationAfterFalseWriteDecisionDoesNotReclassifyWriteFailure()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "progress.ndjson");
        using var generationCancellation = new CancellationTokenSource();
        Task<bool>? write = null;

        try
        {
            write = FirstViewportImagesReadyEvidenceWriter.QueueWriteAfterCurrent(
                Task.CompletedTask,
                () =>
                {
                    generationCancellation.Cancel();
                    return false;
                },
                generationCancellation.Token,
                CreateIdentity(generation: 25),
                path);

            Assert.False(await write.WaitAsync(TimeSpan.FromSeconds(5)));
            await DrainProgressWritesAsync();

            var outcome = Assert.Single(ReadOutcomes(path));
            Assert.Equal(25, outcome.Generation);
            Assert.Equal("write-failed", outcome.Outcome);
        }
        finally
        {
            if (write is not null)
                await write.WaitAsync(TimeSpan.FromSeconds(5));
            await DrainProgressWritesAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FaultedSignalWriterRecordsOnlyFixedOutcomeFields()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "progress.ndjson");
        Task<bool>? write = null;

        try
        {
            write = FirstViewportImagesReadyEvidenceWriter.QueueWriteAfterCurrent(
                Task.CompletedTask,
                static () => throw new InvalidOperationException("PRIVATE_README_OR_TOKEN_MUST_NOT_BE_SERIALIZED"),
                publicationIdentity: CreateIdentity(generation: 30),
                publicationProgressPath: path);

            Assert.False(await write.WaitAsync(TimeSpan.FromSeconds(5)));
            await DrainProgressWritesAsync();

            string line = Assert.Single(File.ReadAllLines(PublicationPath(path)));
            Assert.Contains("\"RecordKind\":\"readiness-publication-outcome\"", line, StringComparison.Ordinal);
            Assert.Contains("\"Outcome\":\"faulted\"", line, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE_README_OR_TOKEN_MUST_NOT_BE_SERIALIZED", line, StringComparison.Ordinal);
            Assert.DoesNotContain("Exception", line, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (write is not null)
                await write.WaitAsync(TimeSpan.FromSeconds(5));
            await DrainProgressWritesAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidAlreadyCanceledSignalRequestRecordsCancellationWithoutPublishing()
    {
        string directory = CreateTemporaryDirectory();
        string progressPath = Path.Combine(directory, "progress.ndjson");
        string signalPath = Path.Combine(directory, "ready.json");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            Task<bool>? publication = FirstViewportImagesReadyEvidenceWriter.TryQueueWrite(
                signalPath, true, ProcessId, Host, 31, 31, 1, 0, 0, 600,
                true, false, null, DateTimeOffset.UtcNow, 100, 1000,
                cancellation.Token, CreateIdentity(31), progressPath);
            Assert.Null(publication);
            await DrainProgressWritesAsync();
            Assert.False(File.Exists(signalPath));
            var outcome = Assert.Single(ReadOutcomes(progressPath));
            Assert.Equal((31L, "canceled"), outcome);
        }
        finally
        {
            await DrainProgressWritesAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PublicationOutcomeDrainIsSeparateFromReadinessProgressDrain()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "progress.ndjson");

        try
        {
            await DrainProgressWritesAsync();
            Assert.Null(FirstViewportImagesReadyEvidenceWriter.QueuedProgressWrite);
            Assert.True(FirstViewportImagesReadyEvidenceWriter.TryQueuePublicationOutcome(
                path,
                auditEnabled: true,
                CreateOutcome(35, "published")));

            Assert.Null(FirstViewportImagesReadyEvidenceWriter.QueuedProgressWrite);
            await DrainProgressWritesAsync();
            Assert.Single(ReadOutcomes(path));
        }
        finally
        {
            await DrainProgressWritesAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PopulatedSameByteSourceIdentityIsSerializedAsHashesAndBoundedMetadata()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "progress.ndjson");
        const long generation = 40;
        var source = new SameByteReadmeSourceAdmission(
            ProcessId,
            Host,
            generation,
            RawByteSize: 3353,
            RawSha256: new string('a', 64),
            DecodedTextUtf8ByteSize: 3353,
            DecodedTextSha256: new string('b', 64),
            ReadmeGitBlobSha1: new string('c', 40),
            StopwatchTicks: 100,
            StopwatchFrequency: 1_000_000L,
            SourceOwner: "owner",
            SourceRepository: "repository",
            SourceRef: new string('d', 40),
            SourcePath: "docs/README.md");
        var identity = CreateIdentity(generation, source);
        Task<bool>? write = null;

        try
        {
            DateTimeOffset publicationStartedAt = DateTimeOffset.UtcNow;
            write = FirstViewportImagesReadyEvidenceWriter.QueueWriteAfterCurrent(
                Task.CompletedTask,
                static () => true,
                publicationIdentity: identity,
                publicationProgressPath: path);

            Assert.True(await write.WaitAsync(TimeSpan.FromSeconds(5)));
            await DrainProgressWritesAsync();

            string line = Assert.Single(File.ReadAllLines(PublicationPath(path)));
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement row = document.RootElement;
            Assert.Equal("readiness-publication-outcome", row.GetProperty("RecordKind").GetString());
            Assert.Equal(1, row.GetProperty("SchemaVersion").GetInt32());
            Assert.Equal(ProcessId, row.GetProperty("ProcessId").GetInt32());
            Assert.Equal(Host, row.GetProperty("Host").GetString());
            Assert.Equal(generation, row.GetProperty("Generation").GetInt64());
            Assert.Equal(generation, row.GetProperty("ViewportPaintGeneration").GetInt64());
            Assert.Equal("published", row.GetProperty("Outcome").GetString());
            Assert.InRange(row.GetProperty("Timestamp").GetDateTimeOffset(), publicationStartedAt, DateTimeOffset.UtcNow);
            JsonElement admitted = row.GetProperty("SameByteReadmeSourceAdmission");
            Assert.Equal(ProcessId, admitted.GetProperty("ProcessId").GetInt32());
            Assert.Equal(Host, admitted.GetProperty("Host").GetString());
            Assert.Equal(generation, admitted.GetProperty("Generation").GetInt64());
            Assert.Equal(3353, admitted.GetProperty("RawByteSize").GetInt32());
            Assert.Equal(new string('a', 64), admitted.GetProperty("RawSha256").GetString());
            Assert.Equal(3353, admitted.GetProperty("DecodedTextUtf8ByteSize").GetInt32());
            Assert.Equal(new string('b', 64), admitted.GetProperty("DecodedTextSha256").GetString());
            Assert.Equal(new string('c', 40), admitted.GetProperty("ReadmeGitBlobSha1").GetString());
            Assert.Equal(100, admitted.GetProperty("StopwatchTicks").GetInt64());
            Assert.Equal(1_000_000L, admitted.GetProperty("StopwatchFrequency").GetInt64());
            Assert.Equal("owner", admitted.GetProperty("SourceOwner").GetString());
            Assert.Equal("repository", admitted.GetProperty("SourceRepository").GetString());
            Assert.Equal(new string('d', 40), admitted.GetProperty("SourceRef").GetString());
            Assert.Equal("docs/README.md", admitted.GetProperty("SourcePath").GetString());
            Assert.DoesNotContain("README CONTENT", line, StringComparison.Ordinal);
            Assert.DoesNotContain("Authorization", line, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (write is not null)
                await write.WaitAsync(TimeSpan.FromSeconds(5));
            await DrainProgressWritesAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AcceptedConcurrentOutcomeRowsSurviveProgressDrainFinalizationRace()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "progress.ndjson");
        const int requestCount = 96;
        var start = NewSignal();

        try
        {
            Task<(long Generation, bool Accepted)>[] requests = Enumerable.Range(1, requestCount)
                .Select(value => Task.Run(async () =>
                {
                    await start.Task.ConfigureAwait(false);
                    long generation = 1000 + value;
                    bool accepted = FirstViewportImagesReadyEvidenceWriter.TryQueuePublicationOutcome(
                        path,
                        auditEnabled: true,
                        CreateOutcome(generation, "published"));
                    return (generation, accepted);
                }))
                .ToArray();

            start.SetResult(true);
            (long Generation, bool Accepted)[] results = await Task.WhenAll(requests)
                .WaitAsync(TimeSpan.FromSeconds(10));
            await DrainProgressWritesAsync();

            long[] acceptedGenerations = results
                .Where(result => result.Accepted)
                .Select(result => result.Generation)
                .OrderBy(generation => generation)
                .ToArray();
            Assert.NotEmpty(acceptedGenerations);
            Assert.Equal(requestCount, results.Length);

            var persisted = ReadOutcomes(path);
            Assert.Equal(acceptedGenerations, persisted.Select(row => row.Generation).OrderBy(value => value).ToArray());
            Assert.All(persisted, row => Assert.Equal("published", row.Outcome));
            Assert.Equal(persisted.Length, persisted.Select(row => row.Generation).Distinct().Count());
        }
        finally
        {
            if (!start.Task.IsCompleted)
                start.TrySetResult(true);
            await DrainProgressWritesAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LastSummaryCombinesLatestProgressAndPublicationWithoutEchoingMalformedTokens()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "progress.ndjson");
        const string progressRows =
            "{\"Stage\":\"polling\",\"Reason\":\"waiting-for-paint\",\"Generation\":6}\n" +
            "{\"Stage\":\"polling\",\"Reason\":\"visible-images-loading\",\"Generation\":7}\n";

        try
        {
            File.WriteAllText(path, progressRows, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Assert.True(FirstViewportImagesReadyEvidenceWriter.TryQueuePublicationOutcome(
                path,
                auditEnabled: true,
                CreateOutcome(7, "published")));
            Assert.True(FirstViewportImagesReadyEvidenceWriter.TryQueuePublicationOutcome(
                path,
                auditEnabled: true,
                CreateOutcome(7, "suppressed")));
            await DrainProgressWritesAsync();

            string diskSummary = NativeReadinessProgressDiagnostic.ReadLastSummary(path);
            Assert.Contains("readinessProgress stage=polling reason=visible-images-loading generation=7", diskSummary, StringComparison.Ordinal);
            Assert.Contains("readinessPublication outcome=suppressed processId=4312 generation=7 viewportPaintGeneration=7", diskSummary, StringComparison.Ordinal);

            Assert.False(FirstViewportImagesReadyEvidenceWriter.TryQueuePublicationOutcome(
                path,
                auditEnabled: true,
                CreateOutcome(7, "published") with { Outcome = "published\nPRIVATE_TOKEN" }));

            byte[] progressBytes = File.ReadAllBytes(path);
            byte[] publicationBytes = File.ReadAllBytes(PublicationPath(path));
            byte[] validRows = new byte[progressBytes.Length + publicationBytes.Length];
            Buffer.BlockCopy(progressBytes, 0, validRows, 0, progressBytes.Length);
            Buffer.BlockCopy(publicationBytes, 0, validRows, progressBytes.Length, publicationBytes.Length);
            const string invalidOutcome = "{\"SchemaVersion\":1,\"RecordKind\":\"readiness-publication-outcome\"," +
                "\"ProcessId\":4312,\"Host\":\"MarkdownHost_PublicationOutcomeTests\"," +
                "\"Generation\":7,\"ViewportPaintGeneration\":7," +
                "\"Outcome\":\"published\\nPRIVATE_TOKEN\"}";
            byte[] suffix = Encoding.UTF8.GetBytes(invalidOutcome + "\n{\"RecordKind\":");
            byte[] tail = new byte[validRows.Length + suffix.Length];
            Buffer.BlockCopy(validRows, 0, tail, 0, validRows.Length);
            Buffer.BlockCopy(suffix, 0, tail, validRows.Length, suffix.Length);

            string summary = NativeReadinessProgressDiagnostic.FormatLastSummary(
                tail,
                startsAtFileBeginning: true);

            Assert.Contains("readinessProgress stage=polling reason=visible-images-loading generation=7", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("waiting-for-paint", summary, StringComparison.Ordinal);
            Assert.Contains("readinessPublication outcome=suppressed processId=4312 generation=7 viewportPaintGeneration=7", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE_TOKEN", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("MarkdownHost_PublicationOutcomeTests", summary, StringComparison.Ordinal);
        }
        finally
        {
            await DrainProgressWritesAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidDiagnosticPathIsRejectedAndSummarizedAsUnavailable()
    {
        Assert.False(FirstViewportImagesReadyEvidenceWriter.TryQueuePublicationOutcome(
            null,
            auditEnabled: true,
            CreateOutcome(50, "published")));
        Assert.False(FirstViewportImagesReadyEvidenceWriter.TryQueuePublicationOutcome(
            string.Empty,
            auditEnabled: true,
            CreateOutcome(50, "published")));
        Assert.Equal(
            "readinessProgress=unavailable",
            NativeReadinessProgressDiagnostic.ReadLastSummary(string.Empty));
        Assert.Equal(
            "readinessProgress=unavailable",
            NativeReadinessProgressDiagnostic.ReadLastSummary("invalid\0diagnostic-path"));
        await DrainProgressWritesAsync();
    }

    [Fact]
    public void OutcomeSnapshotFailureCannotTurnOptionalDiagnosticsIntoAnAuditFailure()
    {
        string directory = CreateTemporaryDirectory();
        string source = Path.Combine(directory, "outcomes.ndjson");
        string destination = Path.Combine(directory, "preserved.ndjson");
        try
        {
            Assert.False(NativeReadinessProgressDiagnostic.TryPreservePublicationOutcomes(source, destination));
            Assert.False(File.Exists(destination));
            const string row = "{\"Outcome\":\"published\"}\n";
            File.WriteAllText(source, row, new UTF8Encoding(false));
            Assert.True(NativeReadinessProgressDiagnostic.TryPreservePublicationOutcomes(source, destination));
            Assert.Equal(row, File.ReadAllText(destination));
            Assert.False(NativeReadinessProgressDiagnostic.TryPreservePublicationOutcomes(
                source, "invalid\0diagnostic-destination"));
            // A real IO failure exhausts the strict snapshot retry, but remains
            // diagnostic-only. An existing file cannot be a destination folder.
            Assert.False(NativeReadinessProgressDiagnostic.TryPreservePublicationOutcomes(
                source, Path.Combine(source, "cannot-copy.ndjson")));
            Assert.Equal(row, File.ReadAllText(source));
            Assert.Equal(row, File.ReadAllText(destination));
            Assert.Empty(Directory.GetFiles(directory, "*.snapshot-*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static FirstViewportImagesReadyPublicationIdentity CreateIdentity(
        long generation,
        SameByteReadmeSourceAdmission? source = null) =>
        new(ProcessId, Host, generation, generation, source);

    private static FirstViewportImagesReadyPublicationOutcomeRow CreateOutcome(
        long generation,
        string outcome,
        SameByteReadmeSourceAdmission? source = null) =>
        new(1, "readiness-publication-outcome", ProcessId, Host,
            generation, generation, source, outcome, DateTimeOffset.UnixEpoch.AddSeconds(generation));

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task DrainProgressWritesAsync()
    {
        Task? progressDrain = FirstViewportImagesReadyEvidenceWriter.QueuedProgressWrite;
        Task? publicationDrain = FirstViewportImagesReadyEvidenceWriter.QueuedPublicationWrite;
        if (progressDrain is not null)
            await progressDrain.WaitAsync(TimeSpan.FromSeconds(10));
        if (publicationDrain is not null)
            await publicationDrain.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static (long Generation, string Outcome)[] ReadOutcomes(string progressPath) =>
        File.ReadAllLines(PublicationPath(progressPath))
            .Select(line =>
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement row = document.RootElement;
                Assert.Equal("readiness-publication-outcome", row.GetProperty("RecordKind").GetString());
                return (row.GetProperty("Generation").GetInt64(), row.GetProperty("Outcome").GetString()!);
            })
            .Select(row => (row.Item1, row.Item2))
            .ToArray();

    private static string PublicationPath(string progressPath) =>
        FirstViewportImagesReadyEvidenceWriter.GetPublicationOutcomePath(progressPath);

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"readiness-publication-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
