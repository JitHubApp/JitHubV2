using System.Text;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class WorkerOutputMappingAdmissionTests
{
    [Fact]
    public async Task CancellationDuringMappingCreationReleasesMappingWithoutRendering()
    {
        using var cancellation = new CancellationTokenSource();
        SharedMemoryLease? mapping = null;
        bool rendered = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await WorkerPool.WithAdmittedOutputMappingAsync(
                _ => Task.FromResult(new TestAdmission()),
                (_, _, _) =>
                {
                    rendered = true;
                    return Task.FromResult(CreateResponse(WorkerStatus.Ok));
                },
                (name, length) =>
                {
                    mapping = new SharedMemoryLease(name, length);
                    cancellation.Cancel();
                    return mapping;
                },
                NewMappingName(),
                16 * 8 * 4,
                cancellation.Token));

        Assert.False(rendered);
        Assert.NotNull(mapping);
        Assert.Throws<ObjectDisposedException>(() => mapping!.ReadOpenProgressPhase());
    }

    [Fact]
    public async Task PreCanceledRenderDoesNotAllocateOutputMapping()
    {
        int allocations = 0;
        var options = new ResvgMarkdownSvgRendererOptions();
        using var pool = new WorkerPool(
            options,
            TimeProvider.System,
            static () => true,
            static (name, source) => new SharedMemoryLease(name, source, outputLength: 0),
            (name, length) =>
            {
                Interlocked.Increment(ref allocations);
                return new SharedMemoryLease(name, length);
            });
        var openRequest = CreateRequest();
        SvgPreflightResult preflight = await SvgPreflight.InspectAsync(
            openRequest,
            options,
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pool.RenderDocumentAsync(
                new WorkerDocumentHandle(1),
                preflight,
                openRequest,
                new MarkdownSvgRenderRequest(16, 8),
                16 * 8 * 4,
                cancellation.Token));

        Assert.Equal(0, Volatile.Read(ref allocations));
    }

    [Fact]
    public async Task OutputMappingIsCreatedOnlyAfterAdmissionCompletes()
    {
        var admissionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int allocations = 0;
        int admissionCompleted = 0;

        Task<WorkerRenderResult> render = WorkerPool.WithAdmittedOutputMappingAsync(
            async cancellationToken =>
            {
                admissionStarted.TrySetResult();
                await releaseAdmission.Task.WaitAsync(cancellationToken);
                Volatile.Write(ref admissionCompleted, 1);
                return new TestAdmission();
            },
            (admission, mapping, _) =>
            {
                Assert.NotNull(admission);
                Assert.Equal(16 * 8 * 4, mapping.Memory.Length);
                return Task.FromResult(CreateResponse(WorkerStatus.Ok));
            },
            (name, length) =>
            {
                Assert.Equal(1, Volatile.Read(ref admissionCompleted));
                Interlocked.Increment(ref allocations);
                return new SharedMemoryLease(name, length);
            },
            NewMappingName(),
            16 * 8 * 4,
            CancellationToken.None);

        await admissionStarted.Task;
        Assert.Equal(0, Volatile.Read(ref allocations));
        releaseAdmission.TrySetResult();

        using WorkerRenderResult result = await render;
        Assert.Equal(1, Volatile.Read(ref allocations));
        Assert.Equal(WorkerStatus.Ok, result.Response.Status);
    }

    [Fact]
    public async Task OutputMappingIsDisposedWhenRenderTransactionFails()
    {
        SharedMemoryLease? mapping = null;
        var failure = new IOException("simulated render transport failure");

        IOException thrown = await Assert.ThrowsAsync<IOException>(async () =>
            await WorkerPool.WithAdmittedOutputMappingAsync(
                _ => Task.FromResult(new TestAdmission()),
                (_, _, _) => Task.FromException<WorkerResponse>(failure),
                CreateOutputMapping,
                NewMappingName(),
                16 * 8 * 4,
                CancellationToken.None));

        Assert.Same(failure, thrown);
        Assert.NotNull(mapping);
        Assert.Throws<ObjectDisposedException>(() => mapping!.ReadOpenProgressPhase());

        SharedMemoryLease CreateOutputMapping(string name, int length)
        {
            var created = new SharedMemoryLease(name, length);
            mapping = created;
            return created;
        }
    }

    [Fact]
    public async Task SuccessfulRenderTransfersOutputMappingToRasterOwner()
    {
        SharedMemoryLease? mapping = null;
        WorkerRenderResult result = await WorkerPool.WithAdmittedOutputMappingAsync(
            _ => Task.FromResult(new TestAdmission()),
            (_, activeMapping, _) =>
            {
                Assert.Equal(16 * 8 * 4, activeMapping.Memory.Length);
                return Task.FromResult(CreateResponse(WorkerStatus.Ok));
            },
            CreateOutputMapping,
            NewMappingName(),
            16 * 8 * 4,
            CancellationToken.None);

        SharedMemoryLease transferred = result.TransferOutputMemory();
        result.Dispose();
        Assert.Same(mapping, transferred);
        Assert.Equal(16 * 8 * 4, transferred.Memory.Length);

        ((IDisposable)transferred).Dispose();
        Assert.Throws<ObjectDisposedException>(() => transferred.ReadOpenProgressPhase());

        SharedMemoryLease CreateOutputMapping(string name, int length)
        {
            var created = new SharedMemoryLease(name, length);
            mapping = created;
            return created;
        }
    }

    [Fact]
    public void ProductionCreatesOutputMappingOnlyAfterQueueWorkerAndFontAdmission()
    {
        string source = File.ReadAllText(FindWorkerPoolSourcePath());
        string renderMethod = SliceMethod(
            source,
            "public Task<WorkerRenderResult> RenderDocumentAsync(",
            "private async Task<RenderAdmission> AdmitRenderAsync(");
        string admissionMethod = SliceMethod(
            source,
            "private async Task<RenderAdmission> AdmitRenderAsync(",
            "private async ValueTask<WorkerResponse> RenderDocumentAdmittedAsync(");
        string allocationHelper = SliceMethod(
            source,
            "internal static async Task<WorkerRenderResult> WithAdmittedOutputMappingAsync<",
            "internal static async Task<WorkerResponse?> AttachDocumentIfMissingAsync(");

        Assert.Contains("WithAdmittedOutputMappingAsync(", renderMethod, StringComparison.Ordinal);
        Assert.Contains("_outputMemoryFactory is null", renderMethod, StringComparison.Ordinal);
        Assert.Contains("admission.Worker.ObjectSecurity", renderMethod, StringComparison.Ordinal);
        int queueAdmission = admissionMethod.IndexOf("await AcquireAsync(", StringComparison.Ordinal);
        int workerStart = admissionMethod.IndexOf("await EnsureWorkerAsync(", StringComparison.Ordinal);
        int fontReady = admissionMethod.IndexOf("await EnsureFontCatalogReadyAsync(", StringComparison.Ordinal);
        int admissionReturn = admissionMethod.IndexOf("return admission;", StringComparison.Ordinal);
        Assert.True(queueAdmission >= 0 && workerStart > queueAdmission && admissionReturn > workerStart);
        Assert.True(fontReady < 0 || admissionReturn > fontReady);

        int waitForAdmission = allocationHelper.IndexOf("await admitAsync(", StringComparison.Ordinal);
        int factoryMapAllocation = allocationHelper.IndexOf(
            "outputMemoryFactory!(outputMemoryName, outputLength)",
            StringComparison.Ordinal);
        int workerContextMapAllocation = allocationHelper.IndexOf("new SharedMemoryLease(", StringComparison.Ordinal);
        Assert.True(waitForAdmission >= 0 && factoryMapAllocation > waitForAdmission &&
            workerContextMapAllocation > waitForAdmission);
    }

    private static MarkdownSvgOpenRequest CreateRequest() => new(Encoding.UTF8.GetBytes(
        "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='8'><rect width='16' height='8'/></svg>"));

    private static WorkerResponse CreateResponse(WorkerStatus status) => new(
        status,
        16,
        8,
        64,
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

    private static string FindWorkerPoolSourcePath()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string sourcePath = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                "MarkdownRenderer.Svg.Resvg",
                "Internal",
                "WorkerPool.cs");
            if (File.Exists(sourcePath))
                return sourcePath;
        }

        throw new FileNotFoundException("The repository WorkerPool source was not found.");
    }

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Source method was not found: {startMarker}");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Source method boundary was not found: {endMarker}");
        return source[start..end];
    }

    private sealed class TestAdmission : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
