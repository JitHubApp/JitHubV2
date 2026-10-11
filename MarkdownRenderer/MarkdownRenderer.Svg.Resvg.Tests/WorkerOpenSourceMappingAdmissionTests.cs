using System.Text;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class WorkerOpenSourceMappingAdmissionTests
{
    [Fact]
    public async Task CancellationDuringMappingCreationReleasesMappingWithoutAttaching()
    {
        using var cancellation = new CancellationTokenSource();
        SharedMemoryLease? mapping = null;
        bool attached = false;
        using SvgSourceBuffer sourceBuffer = SvgSourceBuffer.TakeOwnedArray(new byte[4]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await WorkerPool.WithSourceMappingAsync(
                (name, source) =>
                {
                    mapping = new SharedMemoryLease(name, source, 0);
                    cancellation.Cancel();
                    return mapping;
                },
                $"Local\\MarkdownRenderer.Resvg.Test.{Environment.ProcessId}.{Guid.NewGuid():N}",
                sourceBuffer,
                _ =>
                {
                    attached = true;
                    return Task.FromResult(0);
                },
                cancellation.Token));

        Assert.False(attached);
        Assert.NotNull(mapping);
        Assert.Throws<ObjectDisposedException>(() => mapping!.ReadOpenProgressPhase());
    }

    [Fact]
    public async Task PreCanceledOpenDoesNotAllocateSourceMapping()
    {
        int allocations = 0;
        var options = new ResvgMarkdownSvgRendererOptions();
        using var pool = new WorkerPool(
            options,
            TimeProvider.System,
            static () => true,
            (name, source) =>
            {
                Interlocked.Increment(ref allocations);
                return new SharedMemoryLease(name, source, outputLength: 0);
            });
        var request = CreateRequest();
        SvgPreflightResult preflight = await SvgPreflight.InspectAsync(
            request,
            options,
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pool.OpenDocumentAsync(preflight, request, cancellation.Token));

        Assert.Equal(0, Volatile.Read(ref allocations));
    }

    [Fact]
    public async Task SourceMappingIsDisposedWhenOpenTransactionCompletes()
    {
        SharedMemoryLease? mapping = null;
        using SvgSourceBuffer sourceBuffer = CreateRequest().SanitizedSvgBytes.ToArray().CopyIntoBufferForTest();
        int result = await WorkerPool.WithSourceMappingAsync(
            CreateSourceMapping,
            NewMappingName(),
            sourceBuffer,
            activeMapping =>
            {
                Assert.Equal(0, activeMapping.Memory.Length);
                return Task.FromResult(42);
            },
            CancellationToken.None);

        Assert.Equal(42, result);
        Assert.NotNull(mapping);
        Assert.Throws<ObjectDisposedException>(() => mapping!.ReadOpenProgressPhase());

        SharedMemoryLease CreateSourceMapping(string name, SvgSourceBuffer source)
        {
            var created = new SharedMemoryLease(name, source, outputLength: 0);
            mapping = created;
            return created;
        }
    }

    [Fact]
    public async Task SourceMappingIsDisposedWhenOpenTransactionThrows()
    {
        SharedMemoryLease? mapping = null;
        using SvgSourceBuffer sourceBuffer = CreateRequest().SanitizedSvgBytes.ToArray().CopyIntoBufferForTest();
        var failure = new IOException("simulated open transport failure");

        IOException thrown = await Assert.ThrowsAsync<IOException>(async () =>
            await WorkerPool.WithSourceMappingAsync(
                CreateSourceMapping,
                NewMappingName(),
                sourceBuffer,
                _ => Task.FromException<int>(failure),
                CancellationToken.None));

        Assert.Same(failure, thrown);
        Assert.NotNull(mapping);
        Assert.Throws<ObjectDisposedException>(() => mapping!.ReadOpenProgressPhase());

        SharedMemoryLease CreateSourceMapping(string name, SvgSourceBuffer source)
        {
            var created = new SharedMemoryLease(name, source, outputLength: 0);
            mapping = created;
            return created;
        }
    }

    [Fact]
    public async Task ReattachAllocatesSourceMappingOnlyForDocumentMissing()
    {
        int allocations = 0;
        int attachments = 0;
        SharedMemoryLease? mapping = null;
        using SvgSourceBuffer sourceBuffer = CreateRequest().SanitizedSvgBytes.ToArray().CopyIntoBufferForTest();

        Task<WorkerResponse> AttachAsync() => WorkerPool.WithSourceMappingAsync(
            (name, source) =>
            {
                Interlocked.Increment(ref allocations);
                mapping = new SharedMemoryLease(name, source, outputLength: 0);
                return mapping;
            },
            NewMappingName(),
            sourceBuffer,
            _ =>
            {
                Interlocked.Increment(ref attachments);
                return Task.FromResult(CreateResponse(WorkerStatus.Ok));
            },
            CancellationToken.None);

        WorkerResponse? noAttachment = await WorkerPool.AttachDocumentIfMissingAsync(
            CreateResponse(WorkerStatus.Ok),
            AttachAsync);
        Assert.Null(noAttachment);
        Assert.Equal(0, allocations);
        Assert.Equal(0, attachments);

        WorkerResponse? attachment = await WorkerPool.AttachDocumentIfMissingAsync(
            CreateResponse(WorkerStatus.DocumentMissing),
            AttachAsync);
        Assert.NotNull(attachment);
        Assert.Equal(WorkerStatus.Ok, attachment.Value.Status);
        Assert.Equal(1, allocations);
        Assert.Equal(1, attachments);
        Assert.NotNull(mapping);
        Assert.Throws<ObjectDisposedException>(() => mapping!.ReadOpenProgressPhase());
    }

    [Fact]
    public void MappingIsCreatedAfterAdmissionAndTheDocumentRetainsNoMapping()
    {
        string poolSource = File.ReadAllText(FindSourcePath("WorkerPool.cs"));
        string documentSource = File.ReadAllText(FindSourcePath("ResvgMarkdownSvgDocument.cs"));
        string openMethod = SliceMethod(
            poolSource,
            "public async ValueTask<WorkerOpenResult> OpenDocumentAsync(",
            "public Task<WorkerRenderResult> RenderDocumentAsync(");
        int admission = openMethod.IndexOf("await AcquireAsync(", StringComparison.Ordinal);
        int workerReady = openMethod.IndexOf("await EnsureWorkerAsync(", StringComparison.Ordinal);
        int mapping = openMethod.IndexOf("WithSourceMappingAsync(", StringComparison.Ordinal);
        int attachment = openMethod.IndexOf("AttachDocumentAsync(", StringComparison.Ordinal);
        Assert.True(admission >= 0 && workerReady > admission && mapping > workerReady && attachment > mapping,
            "Opening must create the source mapping only after queue admission and worker/font readiness.");
        Assert.Contains("_sourceMemoryFactory is null ? worker.ObjectSecurity : null", openMethod, StringComparison.Ordinal);

        string renderMethod = SliceMethod(
            poolSource,
            "private async ValueTask<WorkerResponse> RenderDocumentAdmittedAsync(",
            "public void ForgetDocument(");
        int missingCheck = renderMethod.IndexOf("AttachDocumentIfMissingAsync(", StringComparison.Ordinal);
        int reattachMapping = renderMethod.IndexOf("WithSourceMappingAsync(", missingCheck, StringComparison.Ordinal);
        Assert.True(missingCheck >= 0 && reattachMapping > missingCheck,
            "A render may create a reattachment mapping only inside the DocumentMissing branch.");
        Assert.Contains("_sourceMemoryFactory is null ? worker.ObjectSecurity : null", renderMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("SharedMemoryLease _sourceMemory", documentSource, StringComparison.Ordinal);
    }

    private static MarkdownSvgOpenRequest CreateRequest() => new(Encoding.UTF8.GetBytes(
        "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='8'><rect width='16' height='8'/></svg>"));

    private static WorkerResponse CreateResponse(WorkerStatus status) => new(
        status,
        16,
        8,
        2,
        0,
        0,
        0,
        0,
        MarkdownSvgPixelFormat.Rgba8Premultiplied,
        false,
        false,
        false,
        string.Empty);

    private static string NewMappingName() =>
        $"Local\\MarkdownRenderer.Resvg.Test.{Environment.ProcessId}.{Guid.NewGuid():N}";

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Source method was not found: {startMarker}");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Source method boundary was not found: {endMarker}");
        return source[start..end];
    }

    private static string FindSourcePath(string fileName)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string sourcePath = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                "MarkdownRenderer.Svg.Resvg",
                fileName == "WorkerPool.cs" ? "Internal" : string.Empty,
                fileName);
            if (File.Exists(sourcePath))
                return sourcePath;
        }

        throw new FileNotFoundException(
            $"The repository source for the SVG mapping contract was not found: {fileName}");
    }
}
