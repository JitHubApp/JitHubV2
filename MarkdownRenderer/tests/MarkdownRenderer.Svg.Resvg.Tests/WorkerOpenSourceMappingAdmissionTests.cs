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
}
