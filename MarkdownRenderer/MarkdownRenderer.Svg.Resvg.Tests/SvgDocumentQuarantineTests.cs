using System.Security.Cryptography;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SvgDocumentQuarantineTests
{
    private const string QuarantineMessage = "This SVG was quarantined after a worker failure.";
    private static readonly MarkdownSvgRenderRequest RenderRequest = new(16, 16);

    [Fact]
    public async Task QuarantinedOpenFailsBeforeWorkerAdmission()
    {
        var options = new ResvgMarkdownSvgRendererOptions();
        using var renderer = new ResvgMarkdownSvgRenderer(options);
        byte[] source = "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16'/>"u8.ToArray();
        var request = new MarkdownSvgOpenRequest(source);
        SvgPreflightResult preflight = await SvgPreflight.InspectAsync(
            request,
            options,
            CancellationToken.None
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            , renderer.SourceOwnership
#endif
            );
        string identity = Convert.ToHexString(preflight.Hash);
        preflight.ReleaseSource();
        renderer.QuarantineWorkerFailure(identity);

        MarkdownSvgException exception = await Assert.ThrowsAsync<MarkdownSvgException>(
            async () => await renderer.OpenAsync(request));

        Assert.Equal(MarkdownSvgFailureReason.WorkerFailure, exception.Reason);
        Assert.Equal(QuarantineMessage, exception.Message);
        Assert.Equal(0, renderer.SourceAttachmentCount);
    }

    [Fact]
    public async Task QuarantineBlocksEveryRetainedHandleAndRetryBeforeAnyMapping()
    {
        var options = new ResvgMarkdownSvgRendererOptions();
        using var renderer = new ResvgMarkdownSvgRenderer(options);
        var mappings = new MappingCalls();
        using WorkerPool workers = CreateNoWorkerPool(options, mappings);
        byte[] source = "<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray();
        string identity = GetIdentity(source);
        using ResvgMarkdownSvgDocument first = CreateDocument(renderer, workers, options, source, 1);
        using ResvgMarkdownSvgDocument second = CreateDocument(renderer, workers, options, source, 2);

        renderer.QuarantineWorkerFailure(identity);

        await AssertQuarantinedAsync(first);
        await AssertQuarantinedAsync(second);
        await AssertQuarantinedAsync(first);
        await AssertQuarantinedAsync(second);

        Assert.Equal(0, Volatile.Read(ref mappings.Source));
        Assert.Equal(0, Volatile.Read(ref mappings.Output));
        Assert.Equal(0, workers.SourceAttachmentCount);
    }

    [Fact]
    public void UnrelatedHashPassesTheAdmissionGuardWithoutWorkerOrMapping()
    {
        var options = new ResvgMarkdownSvgRendererOptions();
        using var renderer = new ResvgMarkdownSvgRenderer(options);
        var mappings = new MappingCalls();
        using WorkerPool workers = CreateNoWorkerPool(options, mappings);
        string quarantinedIdentity = GetIdentity("<svg id='quarantined'/>"u8);
        string unrelatedIdentity = GetIdentity("<svg id='unrelated'/>"u8);

        renderer.QuarantineWorkerFailure(quarantinedIdentity);
        var unrelatedDocument = new WorkerDocumentHandle(
            documentId: 17,
            preferredWorkerInstanceId: 23,
            contentAdmissionGuard: renderer.CreateContentAdmissionGuard(unrelatedIdentity),
            contentFailureCallback: renderer.CreateContentFailureCallback(unrelatedIdentity));

        unrelatedDocument.ValidateContentAdmission();
        renderer.ThrowIfQuarantined(unrelatedIdentity);
        unrelatedDocument.ReleaseContentAdmissionGuard();
        unrelatedDocument.ValidateContentAdmission();

        Assert.Equal(0, Volatile.Read(ref mappings.Source));
        Assert.Equal(0, Volatile.Read(ref mappings.Output));
        Assert.Equal(0, workers.SourceAttachmentCount);
    }

    [Fact]
    public void ContentFailureCallbackQuarantinesOnlyItsBoundHash()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        string failedIdentity = GetIdentity("<svg id='failed'/>"u8);
        string otherIdentity = GetIdentity("<svg id='other'/>"u8);
        var failedDocument = new WorkerDocumentHandle(
            31,
            contentAdmissionGuard: renderer.CreateContentAdmissionGuard(failedIdentity),
            contentFailureCallback: renderer.CreateContentFailureCallback(failedIdentity));
        var sameContentDocument = new WorkerDocumentHandle(
            32,
            contentAdmissionGuard: renderer.CreateContentAdmissionGuard(failedIdentity));
        var otherContentDocument = new WorkerDocumentHandle(
            33,
            contentAdmissionGuard: renderer.CreateContentAdmissionGuard(otherIdentity));

        failedDocument.QuarantineContent();

        MarkdownSvgException exception = Assert.Throws<MarkdownSvgException>(
            sameContentDocument.ValidateContentAdmission);
        Assert.Equal(MarkdownSvgFailureReason.WorkerFailure, exception.Reason);
        Assert.Equal(QuarantineMessage, exception.Message);
        otherContentDocument.ValidateContentAdmission();

        failedDocument.ReleaseContentAdmissionGuard();
        sameContentDocument.ReleaseContentAdmissionGuard();
        otherContentDocument.ReleaseContentAdmissionGuard();
    }

    [Fact]
    public async Task CancellationAndDisposalStillTakePrecedenceOverQuarantine()
    {
        var options = new ResvgMarkdownSvgRendererOptions();
        using var renderer = new ResvgMarkdownSvgRenderer(options);
        var mappings = new MappingCalls();
        using WorkerPool workers = CreateNoWorkerPool(options, mappings);
        byte[] source = "<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray();
        string identity = GetIdentity(source);
        using ResvgMarkdownSvgDocument canceled = CreateDocument(renderer, workers, options, source, 41);
        using ResvgMarkdownSvgDocument disposed = CreateDocument(renderer, workers, options, source, 42);
        renderer.QuarantineWorkerFailure(identity);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        MarkdownSvgException canceledException = await Assert.ThrowsAsync<MarkdownSvgException>(
            async () => await canceled.RenderAsync(RenderRequest, cancellation.Token));
        Assert.Equal(MarkdownSvgFailureReason.Canceled, canceledException.Reason);

        disposed.Dispose();
        MarkdownSvgException disposedException = await Assert.ThrowsAsync<MarkdownSvgException>(
            async () => await disposed.RenderAsync(RenderRequest));
        Assert.Equal(MarkdownSvgFailureReason.Canceled, disposedException.Reason);

        Assert.Equal(0, Volatile.Read(ref mappings.Source));
        Assert.Equal(0, Volatile.Read(ref mappings.Output));
        Assert.Equal(0, workers.SourceAttachmentCount);
    }

    private static async Task AssertQuarantinedAsync(ResvgMarkdownSvgDocument document)
    {
        MarkdownSvgException exception = await Assert.ThrowsAsync<MarkdownSvgException>(
            async () => await document.RenderAsync(RenderRequest));
        Assert.Equal(MarkdownSvgFailureReason.WorkerFailure, exception.Reason);
        Assert.Equal(QuarantineMessage, exception.Message);
    }

    private static ResvgMarkdownSvgDocument CreateDocument(
        ResvgMarkdownSvgRenderer renderer,
        WorkerPool workers,
        ResvgMarkdownSvgRendererOptions options,
        byte[] source,
        ulong documentId)
    {
        byte[] ownedSource = source.ToArray();
        var info = new MarkdownSvgDocumentInfo(16, 16);
        var preflight = new SvgPreflightResult(
            SvgSourceBuffer.TakeOwnedArray(ownedSource),
            SHA256.HashData(ownedSource),
            info,
            StructuralCost: 0,
            EmbeddedBytes: 0,
            EmbeddedPixels: 0,
            FilterPrimitives: 0);
        var request = new MarkdownSvgOpenRequest(source);
        return new ResvgMarkdownSvgDocument(
            renderer,
            workers,
            options,
            preflight,
            request,
            Convert.ToHexString(preflight.Hash),
            info,
            new WorkerDocumentHandle(documentId));
    }

    private static WorkerPool CreateNoWorkerPool(
        ResvgMarkdownSvgRendererOptions options,
        MappingCalls mappings) =>
        new(
            options,
            TimeProvider.System,
            static () => false,
            (_, _) =>
            {
                Interlocked.Increment(ref mappings.Source);
                throw new InvalidOperationException("Unexpected SVG source mapping in a quarantined render test.");
            },
            (_, _) =>
            {
                Interlocked.Increment(ref mappings.Output);
                throw new InvalidOperationException("Unexpected SVG output mapping in a quarantined render test.");
            });

    private static string GetIdentity(ReadOnlySpan<byte> source) =>
        Convert.ToHexString(SHA256.HashData(source));

    private sealed class MappingCalls
    {
        public int Source;
        public int Output;
    }
}
