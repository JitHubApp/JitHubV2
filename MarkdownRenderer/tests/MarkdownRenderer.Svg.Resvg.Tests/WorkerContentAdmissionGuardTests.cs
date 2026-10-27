using System.Runtime.CompilerServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class WorkerContentAdmissionGuardTests
{
    [Fact]
    public void ValidationInvokesGuardUntilItIsExplicitlyReleased()
    {
        int calls = 0;
        var document = new WorkerDocumentHandle(1, contentAdmissionGuard: () => calls++);

        document.ValidateContentAdmission();
        document.ValidateContentAdmission();
        Assert.Equal(2, calls);

        document.ReleaseContentAdmissionGuard();
        document.ReleaseContentAdmissionGuard();
        document.ValidateContentAdmission();

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GuardRemainsOwnedUntilOutstandingWorkDrainsThenCanBeReleased()
    {
        (WorkerDocumentHandle document, WeakReference guardOwner) = CreateGuardedDocument();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task outstandingWork = Task.Run(async () =>
        {
            document.ValidateContentAdmission();
            entered.TrySetResult();
            await allowDrain.Task.ConfigureAwait(false);
        });

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        CollectGarbage();
        Assert.True(guardOwner.IsAlive);

        // The handle does not clear the guard merely because a validation ran;
        // its owner releases only after all admitted work has drained.
        document.ValidateContentAdmission();
        Assert.True(guardOwner.IsAlive);
        allowDrain.TrySetResult();
        await outstandingWork.WaitAsync(TimeSpan.FromSeconds(5));

        document.ReleaseContentAdmissionGuard();
        document.ValidateContentAdmission();
        CollectGarbage();

        Assert.False(guardOwner.IsAlive);
        GC.KeepAlive(document);
    }

    [Fact]
    public void UnGuardedDocumentRemainsCompatibleAndReleaseIsIdempotent()
    {
        var document = new WorkerDocumentHandle(2);

        document.ValidateContentAdmission();
        document.ReleaseContentAdmissionGuard();
        document.ValidateContentAdmission();
        document.ReleaseContentAdmissionGuard();
    }

    [Fact]
    public void QuarantineInvokesTheFailureCallbackAtMostOnce()
    {
        int calls = 0;
        var document = new WorkerDocumentHandle(4, contentFailureCallback: () => calls++);

        document.QuarantineContent();
        document.QuarantineContent();

        Assert.Equal(1, calls);
    }

    [Fact]
    public void OpenFailureMarkerInvokesAndReleasesItsBorrowedCallbackOnce()
    {
        int calls = 0;
        var marker = new WorkerContentFailureMarker(() => calls++);

        marker.InvokeOnce();
        marker.InvokeOnce();
        marker.Dispose();

        Assert.Equal(1, calls);
    }

    [Fact]
    public void ReleasingDocumentGuardClearsBothBorrowedOwners()
    {
        (WorkerDocumentHandle document, WeakReference guardOwner, WeakReference failureOwner) =
            CreateDocumentWithTwoOwners();

        Assert.True(guardOwner.IsAlive);
        Assert.True(failureOwner.IsAlive);

        document.ReleaseContentAdmissionGuard();
        CollectGarbage();

        Assert.False(guardOwner.IsAlive);
        Assert.False(failureOwner.IsAlive);
        GC.KeepAlive(document);
    }

    [Fact]
    public async Task ContentFailureIsMarkedBeforeRenderAdmissionIsReleased()
    {
        bool marked = false;
        var admission = new AdmissionThatRequiresMarkedFailure(() => marked);
        var document = new WorkerDocumentHandle(5, contentFailureCallback: () => marked = true);
        WorkerResponse response = new(
            WorkerStatus.WorkerFailure,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            MarkdownSvgPixelFormat.Rgba8Premultiplied,
            false,
            false,
            false,
            "worker failure");

        using WorkerRenderResult result = await WorkerPool.WithAdmittedOutputMappingAsync(
            _ => Task.FromResult(admission),
            (_, _, _) =>
            {
                WorkerPool.QuarantineContentBeforeWorkerRelease(
                    document,
                    contentFailureCallback: null,
                    callerCanceled: false,
                    requiresFontCatalog: false,
                    requestGeneration: 0,
                    currentGeneration: 1,
                    invalidateWorker: static () => { });
                return Task.FromResult(response);
            },
            static (name, length) => new SharedMemoryLease(name, length),
            $"Local\\WorkerContentAdmissionGuardTests.{Guid.NewGuid():N}",
            outputLength: 16,
            CancellationToken.None);

        Assert.True(marked);
        Assert.Equal(WorkerStatus.WorkerFailure, result.Response.Status);
        Assert.True(admission.Released);
    }

    [Fact]
    public void FontGenerationOnlySuppressesQuarantineForCanceledOrStaleTextWork()
    {
        Assert.True(WorkerPool.ShouldQuarantineContentFailure(
            callerCanceled: false,
            requiresFontCatalog: false,
            requestGeneration: 0,
            currentGeneration: 1));
        Assert.False(WorkerPool.ShouldQuarantineContentFailure(
            callerCanceled: false,
            requiresFontCatalog: true,
            requestGeneration: 0,
            currentGeneration: 1));
        Assert.False(WorkerPool.ShouldQuarantineContentFailure(
            callerCanceled: true,
            requiresFontCatalog: false,
            requestGeneration: 0,
            currentGeneration: 1));
        Assert.True(WorkerPool.ShouldQuarantineContentFailure(
            callerCanceled: false,
            requiresFontCatalog: true,
            requestGeneration: 1,
            currentGeneration: 1));
    }

    [Fact]
    public async Task QuarantineDuringSourceMappingStopsAttachmentAndReleasesMapping()
    {
        bool quarantined = false;
        bool attached = false;
        SharedMemoryLease? mapping = null;
        using SvgSourceBuffer sourceBuffer = SvgSourceBuffer.TakeOwnedArray(new byte[4]);
        var rejection = new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure);
        var document = new WorkerDocumentHandle(7, contentAdmissionGuard: () =>
        {
            if (quarantined)
                throw rejection;
        });

        MarkdownSvgException observed = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await WorkerPool.WithSourceMappingAsync(
                (name, source) =>
                {
                    mapping = new SharedMemoryLease(name, source, outputLength: 0);
                    quarantined = true;
                    return mapping;
                },
                $"Local\\WorkerContentAdmissionGuardTests.{Guid.NewGuid():N}",
                sourceBuffer,
                _ =>
                {
                    attached = true;
                    return Task.FromResult(0);
                },
                CancellationToken.None,
                document.ValidateContentAdmission));

        Assert.Same(rejection, observed);
        Assert.False(attached);
        Assert.NotNull(mapping);
        Assert.Throws<ObjectDisposedException>(() => mapping!.ReadOpenProgressPhase());
        document.ReleaseContentAdmissionGuard();
    }

    [Fact]
    public async Task AlreadyQuarantinedAttachmentDoesNotAllocateAMapping()
    {
        bool allocated = false;
        using SvgSourceBuffer sourceBuffer = SvgSourceBuffer.TakeOwnedArray(new byte[4]);
        var rejection = new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure);
        var document = new WorkerDocumentHandle(8, contentAdmissionGuard: () => throw rejection);

        MarkdownSvgException observed = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await WorkerPool.WithSourceMappingAsync(
                (name, source) =>
                {
                    allocated = true;
                    return new SharedMemoryLease(name, source, outputLength: 0);
                },
                $"Local\\WorkerContentAdmissionGuardTests.{Guid.NewGuid():N}",
                sourceBuffer,
                _ => Task.FromResult(0),
                CancellationToken.None,
                document.ValidateContentAdmission));

        Assert.Same(rejection, observed);
        Assert.False(allocated);
        document.ReleaseContentAdmissionGuard();
    }

    [Fact]
    public void WorkerSuccessMetadataIsValidatedBeforeItCanBePublished()
    {
        WorkerResponse open = new(
            WorkerStatus.Ok,
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
        WorkerPool.ValidateOpenResponse(open);
        Assert.Throws<WorkerProtocolException>(() =>
            WorkerPool.ValidateOpenResponse(open with { IntrinsicWidth = double.NaN }));

        var request = new MarkdownSvgRenderRequest(
            10,
            12,
            new MarkdownSvgTileRegion(2, 3, 4, 3),
            MarkdownSvgPixelFormat.Bgra8Premultiplied);
        WorkerResponse raster = new(
            WorkerStatus.Ok,
            0,
            0,
            0,
            48,
            16,
            4,
            3,
            MarkdownSvgPixelFormat.Bgra8Premultiplied,
            false,
            false,
            false,
            string.Empty);
        WorkerPool.ValidateRasterResponse(raster, request, expectedOutputLength: 48);
        Assert.Throws<WorkerProtocolException>(() =>
            WorkerPool.ValidateRasterResponse(raster with { Stride = 15 }, request, expectedOutputLength: 48));
    }

    [Fact]
    public void WorkerStatusesAreValidatedAgainstTheirOperationBeforeAdmissionReturns()
    {
        WorkerPool.ValidateOpenStatus(Response(WorkerStatus.Ok), false, 0, "geometry open");
        WorkerPool.ValidateOpenStatus(Response(WorkerStatus.Unsupported), false, 0, "geometry open");
        WorkerPool.ValidateOpenStatus(Response(WorkerStatus.ResourceLimit), false, 0, "geometry open");
        WorkerPool.ValidateOpenStatus(Response(WorkerStatus.WorkerFailure), false, 0, "geometry open");
        Assert.Throws<WorkerProtocolException>(() =>
            WorkerPool.ValidateOpenStatus(Response(WorkerStatus.DocumentMissing), false, 0, "geometry open"));
        Assert.Throws<WorkerProtocolException>(() =>
            WorkerPool.ValidateOpenStatus(Response(WorkerStatus.FontCatalogPending), false, 0, "geometry open"));

        WorkerPool.ValidateRenderStatus(Response(WorkerStatus.DocumentMissing), false, 0, true, "render probe");
        Assert.Throws<WorkerProtocolException>(() =>
            WorkerPool.ValidateRenderStatus(Response(WorkerStatus.DocumentMissing), false, 0, false, "render retry"));
        Assert.Throws<WorkerProtocolException>(() =>
            WorkerPool.ValidateRenderStatus(Response(WorkerStatus.FontCatalogFailure), false, 0, true, "geometry render"));
        WorkerPool.ValidateRenderStatus(Response(WorkerStatus.FontCatalogPending), true, 1, true, "text render");
        Assert.ThrowsAny<OperationCanceledException>(() =>
            WorkerPool.ValidateTextResponse(Response(WorkerStatus.FontCatalogPending), 1, 1, "text render"));
        WorkerPool.ValidateOpenStatus(Response(WorkerStatus.FontCatalogFailure), true, 1, "text open");
        Assert.Throws<WorkerInitializationException>(() =>
            WorkerPool.ValidateTextResponse(Response(WorkerStatus.FontCatalogFailure), 1, 1, "text open"));
        Assert.Throws<WorkerProtocolException>(() =>
            WorkerPool.ValidateOpenStatus(Response(WorkerStatus.FontCatalogFailure) with { ReadyFontGeneration = 2 }, true, 1, "text open"));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WorkerDocumentHandle Document, WeakReference GuardOwner) CreateGuardedDocument()
    {
        var owner = new object();
        var weakOwner = new WeakReference(owner);
        var document = new WorkerDocumentHandle(3, contentAdmissionGuard: () => GC.KeepAlive(owner));
        return (document, weakOwner);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WorkerDocumentHandle Document, WeakReference GuardOwner, WeakReference FailureOwner)
        CreateDocumentWithTwoOwners()
    {
        var guardOwner = new object();
        var failureOwner = new object();
        var document = new WorkerDocumentHandle(
            6,
            contentAdmissionGuard: () => GC.KeepAlive(guardOwner),
            contentFailureCallback: () => GC.KeepAlive(failureOwner));
        return (document, new WeakReference(guardOwner), new WeakReference(failureOwner));
    }

    private sealed class AdmissionThatRequiresMarkedFailure(Func<bool> isMarked) : IDisposable
    {
        public bool Released { get; private set; }

        public void Dispose()
        {
            Assert.True(isMarked());
            Released = true;
        }
    }

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static WorkerResponse Response(WorkerStatus status) => new(
        status,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        MarkdownSvgPixelFormat.Rgba8Premultiplied,
        false,
        false,
        false,
        string.Empty);
}
