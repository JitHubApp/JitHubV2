using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SvgPreflightSchedulingTests
{
    [Fact]
    public void PreCanceledInspectionDoesNotAllocateAFullSourceSnapshot()
    {
        var request = new MarkdownSvgOpenRequest(new byte[1024 * 1024]);
        var options = new ResvgMarkdownSvgRendererOptions();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        // Warm the exception path before measuring this thread's allocations.
        Assert.Throws<OperationCanceledException>(() =>
        {
            _ = SvgPreflight.InspectAsync(request, options, cancellation.Token);
        });

        long before = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            _ = SvgPreflight.InspectAsync(request, options, cancellation.Token);
            Assert.Fail("Canceled preflight was admitted.");
        }
        catch (OperationCanceledException)
        {
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 64 * 1024,
            $"Canceled inspection allocated {allocated} bytes for a 1 MiB caller source.");
    }

    [Fact]
    public async Task PoolThreadRunsInspectionInlineOnTheCallingThread()
    {
        var invocation = await Task.Factory.StartNew(
            () =>
            {
                int callerThreadId = Environment.CurrentManagedThreadId;
                bool invokedBeforeReturn = false;
                Task<(int ThreadId, bool IsThreadPoolThread)> inspection =
                    SvgPreflight.RunInspectCoreAsync(
                        () =>
                        {
                            invokedBeforeReturn = true;
                            return Task.FromResult((
                                Environment.CurrentManagedThreadId,
                                Thread.CurrentThread.IsThreadPoolThread));
                        },
                        CancellationToken.None);

                return (CallerThreadId: callerThreadId, InvokedBeforeReturn: invokedBeforeReturn, Inspection: inspection);
            },
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

        Assert.True(invocation.InvokedBeforeReturn);
        (int inspectionThreadId, bool isThreadPoolThread) = await invocation.Inspection;
        Assert.Equal(invocation.CallerThreadId, inspectionThreadId);
        Assert.True(isThreadPoolThread);
    }

    [Fact]
    public async Task NonPoolCallerDispatchesInspectionToTheThreadPool()
    {
        var completion = new TaskCompletionSource<(int CallerThreadId, Task<(int ThreadId, bool IsThreadPoolThread)> Inspection)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            int callerThreadId = Environment.CurrentManagedThreadId;
            Task<(int ThreadId, bool IsThreadPoolThread)> inspection =
                SvgPreflight.RunInspectCoreAsync(
                    () => Task.FromResult((
                        Environment.CurrentManagedThreadId,
                        Thread.CurrentThread.IsThreadPoolThread)),
                    CancellationToken.None);
            completion.TrySetResult((callerThreadId, inspection));
        })
        {
            IsBackground = true,
        };

        caller.Start();
        (int callerThreadId, Task<(int ThreadId, bool IsThreadPoolThread)> inspectionTask) =
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        (int inspectionThreadId, bool isThreadPoolThread) =
            await inspectionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual(callerThreadId, inspectionThreadId);
        Assert.True(isThreadPoolThread);
    }

    [Fact]
    public async Task NonPoolCallerSnapshotsBorrowedMemoryOnTheThreadPool()
    {
        byte[] source = Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><rect width='1' height='1'/></svg>");
        byte[] original = (byte[])source.Clone();
        using var manager = new ThreadObservingMemoryManager(source);
        var inspectionStarted = new TaskCompletionSource<(
            int CallerThreadId,
            Task<SvgPreflightResult> Inspection)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            int callerThreadId = Environment.CurrentManagedThreadId;
            var request = new MarkdownSvgOpenRequest(manager.BorrowedMemory);
            Task<SvgPreflightResult> inspection = SvgPreflight.InspectAsync(
                request,
                new ResvgMarkdownSvgRendererOptions(),
                CancellationToken.None);
            inspectionStarted.TrySetResult((callerThreadId, inspection));
        })
        {
            IsBackground = true,
        };

        caller.Start();
        (int callerThreadId, Task<SvgPreflightResult> inspectionTask) =
            await inspectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        SvgPreflightResult result = await inspectionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(manager.AccessCount > 0);
        Assert.Equal(0, manager.NonPoolAccessCount);
        Assert.NotEqual(callerThreadId, manager.LastAccessThreadId);
        Assert.True(manager.LastAccessWasThreadPoolThread);
        Assert.Equal(original, source);
        Assert.Equal(original, result.Source.CopyToArrayForTest());
    }

    [Fact]
    public async Task CorrectRasterMediaTypeDoesNotRewriteTheSvgSource()
    {
        byte[] png = CreatePngHeader();
        string payload = Convert.ToBase64String(png);
        string sourceText = CreateImageSvg("image/png", payload);
        byte[] source = Encoding.UTF8.GetBytes(sourceText);
        byte[] original = (byte[])source.Clone();

        SvgPreflightResult result = await SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(source),
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);

        Assert.Equal(original, source);
        Assert.Equal(sourceText, Encoding.UTF8.GetString(result.Source.CopyToArrayForTest()));
    }

    [Fact]
    public async Task MismatchedRasterMediaTypeIsValidatedWithoutRewritingTheXml()
    {
        byte[] png = CreatePngHeader();
        string payload = Convert.ToBase64String(png);
        string sourceText =
            $"<svg xmlns='http://www.w3.org/2000/svg'><svg><image href='data:image/gif;base64,{payload}'/></svg></svg>";
        byte[] source = Encoding.UTF8.GetBytes(sourceText);
        byte[] original = (byte[])source.Clone();

        SvgPreflightResult result = await SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(source),
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);

        Assert.Equal(original, source);
        Assert.Equal(original, result.Source.CopyToArrayForTest());
        Assert.Equal(png.Length, result.EmbeddedBytes);
        Assert.Equal(1, result.EmbeddedPixels);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Utf16SvgIsNormalizedToUtf8WithAndWithoutInertExternalDoctype(
        bool bigEndian,
        bool includeDoctype)
    {
        string doctype = includeDoctype
            ? "<!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.1//EN' 'http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd'>"
            : string.Empty;
        string sourceText =
            "<?xml version='1.0' encoding='utf-16' standalone='yes'?>" + doctype +
            "<!--preserved comment--><?audit keep?><svg xmlns='http://www.w3.org/2000/svg' width='3' height='2'>" +
            "<desc>café</desc></svg>";
        Encoding encoding = new UnicodeEncoding(bigEndian, byteOrderMark: true);
        byte[] source = EncodeWithPreamble(encoding, sourceText);
        byte[] original = (byte[])source.Clone();

        SvgPreflightResult result = await SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(source),
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        string normalized = Encoding.UTF8.GetString(result.Source.CopyToArrayForTest());
        Assert.Equal(original, source);
        Assert.False(result.Source.Length >= 3 &&
                     result.Source.GetByte(0) == 0xef && result.Source.GetByte(1) == 0xbb && result.Source.GetByte(2) == 0xbf);
        Assert.Contains("encoding=\"utf-8\"", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("standalone=\"yes\"", normalized, StringComparison.Ordinal);
        Assert.Contains("<!--preserved comment-->", normalized, StringComparison.Ordinal);
        Assert.Contains("<?audit keep?>", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("<!DOCTYPE", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("café", result.Info.Description);
        Assert.Equal(3, result.Info.IntrinsicWidthDips);
        Assert.Equal(2, result.Info.IntrinsicHeightDips);
    }

    [Fact]
    public async Task DeclaredLatin1SvgIsNormalizedAndPreservesDecodedMetadata()
    {
        string sourceText =
            "<?xml version='1.0' encoding='iso-8859-1'?>" +
            "<svg xmlns='http://www.w3.org/2000/svg'><desc>café</desc></svg>";
        byte[] source = Encoding.Latin1.GetBytes(sourceText);
        byte[] original = (byte[])source.Clone();

        SvgPreflightResult result = await SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(source),
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);

        string normalized = Encoding.UTF8.GetString(result.Source.CopyToArrayForTest());
        Assert.Equal(original, source);
        Assert.Contains("encoding=\"utf-8\"", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("café", normalized, StringComparison.Ordinal);
        Assert.Equal("café", result.Info.Description);
    }

    [Fact]
    public async Task Utf8NormalizationStopsAtLoweredSourceOutputCeiling()
    {
        string sourceText =
            "<?xml version='1.0' encoding='iso-8859-1'?>" +
            "<svg xmlns='http://www.w3.org/2000/svg'><desc>" + new string('é', 128) +
            "</desc></svg>";
        byte[] source = Encoding.Latin1.GetBytes(sourceText);

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(source),
                new ResvgMarkdownSvgRendererOptions(maxSourceBytes: source.Length),
                CancellationToken.None));

        Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, failure.Reason);
    }

    [Fact]
    public async Task CanceledUtf16InspectionAndNormalizationObserveCancellation()
    {
        string sourceText = "<?xml version='1.0' encoding='utf-16'?><svg xmlns='http://www.w3.org/2000/svg'/>";
        byte[] source = EncodeWithPreamble(new UnicodeEncoding(false, true), sourceText);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(source),
                new ResvgMarkdownSvgRendererOptions(),
                cancellation.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SvgPreflight.NormalizeToUtf8SegmentsAsync(
                source,
                new ResvgMarkdownSvgRendererOptions(),
                cancellation.Token));
    }

    [Fact]
    public async Task CompletedNormalizationTaskDoesNotRetainOriginalSourceBytes()
    {
        (Task<SvgPreflight.BoundedOutputStream> task, WeakReference<byte[]> source) = StartUtf16Normalization();

        using SvgPreflight.BoundedOutputStream normalizedOutput = await task.WaitAsync(TimeSpan.FromSeconds(5));
        AssertCollected(source);
        using SvgSourceBuffer normalizedSource = normalizedOutput.DetachSourceBuffer();
        byte[] normalized = normalizedSource.CopyToArrayForTest();

        Assert.StartsWith("<?xml", Encoding.UTF8.GetString(normalized), StringComparison.Ordinal);
        GC.KeepAlive(normalized);
        GC.KeepAlive(task);
    }

    [Fact]
    public async Task FaultedNormalizationTaskDoesNotRetainOriginalSourceBytes()
    {
        (Task<SvgPreflight.BoundedOutputStream> task, WeakReference<byte[]> source) = StartFaultedUtf16Normalization();

        await Assert.ThrowsAsync<XmlException>(async () =>
            await task.WaitAsync(TimeSpan.FromSeconds(5)));

        AssertCollected(source);
        GC.KeepAlive(task);
    }

    [Fact]
    public async Task CanceledNormalizationTaskDoesNotRetainOriginalSourceBytes()
    {
        (Task<SvgPreflight.BoundedOutputStream> task, WeakReference<byte[]> source) = StartCanceledUtf16Normalization();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await task.WaitAsync(TimeSpan.FromSeconds(5)));

        AssertCollected(source);
        GC.KeepAlive(task);
    }

    [Fact]
    public async Task FailedInspectionTaskDoesNotRetainOriginalSourceBytes()
    {
        (Task<SvgPreflightResult> task, WeakReference<byte[]> source) = StartFaultedInspection();

        await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await task.WaitAsync(TimeSpan.FromSeconds(5)));

        AssertCollected(source);
        GC.KeepAlive(task);
    }

    [Fact]
    public async Task CanceledInspectionTaskDoesNotRetainOriginalSourceBytes()
    {
        (Task<SvgPreflightResult> task, WeakReference<byte[]> source) = StartCanceledInspection();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await task.WaitAsync(TimeSpan.FromSeconds(5)));

        AssertCollected(source);
        GC.KeepAlive(task);
    }

    [Fact]
    public async Task InertSvgStylesheetProcessingInstructionIsRejected()
    {
        const string sourceText =
            "<?xml-stylesheet type='text/css' href='https://example.invalid/style.css'?>" +
            "<svg xmlns='http://www.w3.org/2000/svg'/>";

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(sourceText)),
                new ResvgMarkdownSvgRendererOptions(),
                CancellationToken.None));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
    }

    [Fact]
    public async Task RootWithoutSvgNamespaceIsRejected()
    {
        const string sourceText = "<svg xmlns='urn:not-svg'/>";

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(sourceText)),
                new ResvgMarkdownSvgRendererOptions(),
                CancellationToken.None));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
    }

    [Fact]
    public async Task NestedSvgBeyondFormerPrefixLimitIsRecognizedAfterLongPrologComment()
    {
        string nestedText = "<!--" + new string('x', 600) + "--><?audit keep?>" +
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'/>";
        byte[] nested = Encoding.UTF8.GetBytes(nestedText);
        string sourceText = CreateImageSvg("image/svg+xml", Convert.ToBase64String(nested));

        SvgPreflightResult result = await SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(sourceText)),
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);

        Assert.Equal(nested.Length, result.EmbeddedBytes);
    }

    [Theory]
    [InlineData("<html xmlns='http://www.w3.org/1999/xhtml'/>")]
    [InlineData("<svg xmlns='urn:not-svg'/>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'")]
    [InlineData("<?xml-stylesheet href='https://example.invalid/style.css'?><svg xmlns='http://www.w3.org/2000/svg'/>")]
    [InlineData("<!DOCTYPE svg [<!ENTITY x 'x'>]><svg xmlns='http://www.w3.org/2000/svg'/>")]
    [InlineData("<!DOCTYPE svg SYSTEM 'https://example.invalid/svg.dtd'><svg xmlns='http://www.w3.org/2000/svg'/>")]
    public async Task NestedSvgRootIdentificationRejectsMalformedNonSvgAndActiveProlog(
        string nestedText)
    {
        string sourceText = CreateImageSvg(
            "image/svg+xml",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(nestedText)));

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(sourceText)),
                new ResvgMarkdownSvgRendererOptions(),
                CancellationToken.None));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
    }

    [Fact]
    public async Task NestedSvgWithNonUtf8EncodingIsRejectedBeforeWorkerOpen()
    {
        const string nestedText = "<?xml version='1.0' encoding='utf-16'?>" +
            "<svg xmlns='http://www.w3.org/2000/svg'/>";
        byte[] nested = EncodeWithPreamble(new UnicodeEncoding(false, true), nestedText);
        string sourceText = CreateImageSvg("image/svg+xml", Convert.ToBase64String(nested));

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(sourceText)),
                new ResvgMarkdownSvgRendererOptions(),
                CancellationToken.None));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CssDataReferencesAcceptQuotedAndUnquotedTargetsAndNormalizeMimeHeaders(
        bool quoted)
    {
        byte[] png = CreatePngHeader();
        string payload = Convert.ToBase64String(png);
        string sourceText = CreateCssImageSvg(
            quoted,
            $"data:IMAGE/PNG;BaSe64;charset=utf-8,{payload}");

        SvgPreflightResult result = await SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(sourceText)),
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);

        Assert.Equal(png.Length, result.EmbeddedBytes);
        Assert.Equal(1, result.EmbeddedPixels);
    }

    [Fact]
    public async Task CssDataReferenceEnforcesDecodedByteBudgetBeforeAcceptingPayload()
    {
        string payload = Convert.ToBase64String(CreatePngHeader());
        string sourceText = CreateCssImageSvg(
            quoted: true,
            dataUri: $"data:image/png;base64,{payload}");

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(sourceText)),
                new ResvgMarkdownSvgRendererOptions(maxEmbeddedImageBytes: 16),
                CancellationToken.None));

        Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, failure.Reason);
    }

    [Fact]
    public async Task MalformedCssDataReferenceRemainsUnsupported()
    {
        string sourceText = CreateCssImageSvg(quoted: true, dataUri: "data:image/png;base64,%%%=");

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(sourceText)),
                new ResvgMarkdownSvgRendererOptions(),
                CancellationToken.None));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
    }

    [Theory]
    [InlineData("<style>.x{fill:red}</style><script>alert(1)</script>")]
    [InlineData("<style>.x{fill:red}</style><rect onload='alert(1)'/>")]
    [InlineData("<desc>chart</desc><image href='https://example.invalid/image.png'/>")]
    public async Task ConsumedStyleOrDescriptionDoesNotSkipFollowingUnsafeNode(string content)
    {
        string sourceText = $"<svg xmlns='http://www.w3.org/2000/svg'>{content}</svg>";

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(sourceText)),
                new ResvgMarkdownSvgRendererOptions(),
                CancellationToken.None));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
    }

    [Fact]
    public async Task NodesAfterAdjacentStyleAndDescriptionContributeMetadataAndBudget()
    {
        const string sourceText =
            "<svg xmlns='http://www.w3.org/2000/svg'><style>svg{color:currentColor}</style>" +
            "<desc>accessible graph</desc><text>label</text>" +
            "<filter id='f'><feGaussianBlur stdDeviation='1'/></filter></svg>";

        SvgPreflightResult result = await SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(sourceText)),
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);

        Assert.Equal("accessible graph", result.Info.Description);
        Assert.True(result.Info.HasText);
        Assert.True(result.Info.UsesCurrentColor);
        Assert.Equal(1, result.FilterPrimitives);
        Assert.Equal(37, result.StructuralCost);
    }

    [Fact]
    public async Task FilterBudgetImmediatelyAfterConsumedDescriptionIsEnforced()
    {
        const string sourceText =
            "<svg xmlns='http://www.w3.org/2000/svg'><style>svg{color:currentColor}</style>" +
            "<desc>accessible graph</desc><text>label</text>" +
            "<filter id='f'><feGaussianBlur stdDeviation='1'/></filter></svg>";

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(sourceText)),
                new ResvgMarkdownSvgRendererOptions(maxStructuralCost: 36),
                CancellationToken.None));

        Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, failure.Reason);
    }

    [Fact]
    public async Task InertExternalDoctypeIsWhitespaceRemovedFromPrivateSnapshot()
    {
        const string sourceText =
            "<!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.1//EN' " +
            "'http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd'>" +
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'/>";
        byte[] source = Encoding.UTF8.GetBytes(sourceText);
        byte[] original = (byte[])source.Clone();

        SvgPreflightResult result = await SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(source),
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);

        Assert.Equal(original, source);
        Assert.Equal(source.Length, result.Source.Length);
        string sanitized = Encoding.UTF8.GetString(result.Source.CopyToArrayForTest());
        Assert.DoesNotContain("<!DOCTYPE", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(" ", sanitized);
        Assert.Contains("<svg", sanitized);
    }

    [Theory]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><!-- literal <!DOCTYPE text> --></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><![CDATA[<!DOCTYPE text>]]></svg>")]
    public async Task DoctypeMarkerTextOutsideThePrologDeclarationIsPreserved(string sourceText)
    {
        byte[] source = Encoding.UTF8.GetBytes(sourceText);
        byte[] original = (byte[])source.Clone();

        SvgPreflightResult result = await SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(source),
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);

        Assert.Equal(original, source);
        Assert.Equal(original, result.Source.CopyToArrayForTest());
    }

    [Fact]
    public async Task DoctypeMarkerInPrologCommentDoesNotHideTheActualUtf8BomDoctype()
    {
        const string markerComment = "<!-- literal <!DOCTYPE comment text> -->";
        const string doctype =
            "<!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.1//EN' " +
            "'http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd'>";
        string document = markerComment + doctype +
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'/>";
        byte[] documentBytes = Encoding.UTF8.GetBytes(document);
        byte[] source = new byte[Encoding.UTF8.GetPreamble().Length + documentBytes.Length];
        Encoding.UTF8.GetPreamble().CopyTo(source, 0);
        documentBytes.CopyTo(source, Encoding.UTF8.GetPreamble().Length);
        byte[] original = (byte[])source.Clone();

        SvgPreflightResult result = await SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(source),
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);

        string sanitized = Encoding.UTF8.GetString(result.Source.CopyToArrayForTest());
        Assert.Equal(original, source);
        Assert.Equal(source.Length, result.Source.Length);
        Assert.Contains(markerComment, sanitized);
        Assert.DoesNotContain(doctype, sanitized, StringComparison.Ordinal);
        Assert.Contains("<svg", sanitized);
    }

    [Fact]
    public async Task PreCanceledInspectionNeverInvokesCoreOnEitherSchedulingPath()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var poolInvocation = await Task.Factory.StartNew(
            () =>
            {
                bool invoked = false;
                Task<int> inspection = SvgPreflight.RunInspectCoreAsync(
                    () =>
                    {
                        invoked = true;
                        return Task.FromResult(1);
                    },
                    cancellation.Token);
                return (Invoked: invoked, Inspection: inspection);
            },
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

        Assert.False(poolInvocation.Invoked);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await poolInvocation.Inspection);

        var nonPoolCompletion = new TaskCompletionSource<(bool Invoked, Task<int> Inspection)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            bool invoked = false;
            Task<int> inspection = SvgPreflight.RunInspectCoreAsync(
                () =>
                {
                    invoked = true;
                    return Task.FromResult(1);
                },
                cancellation.Token);
            nonPoolCompletion.TrySetResult((invoked, inspection));
        })
        {
            IsBackground = true,
        };
        caller.Start();

        (bool invoked, Task<int> inspectionTask) =
            await nonPoolCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(invoked);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await inspectionTask);
    }

    [Fact]
    public async Task PoolThreadFastPathStillRejectsActiveSvgContent()
    {
        var options = new ResvgMarkdownSvgRendererOptions();
        var request = new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"));

        MarkdownSvgException rejection = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await Task.Run(() => SvgPreflight.InspectAsync(request, options, CancellationToken.None)));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, rejection.Reason);
    }

    private static byte[] CreatePngHeader() =>
    [
        137, 80, 78, 71, 13, 10, 26, 10,
        0, 0, 0, 13, 73, 72, 68, 82,
        0, 0, 0, 1, 0, 0, 0, 1,
    ];

    private static string CreateImageSvg(string mediaType, string payload) =>
        $"<svg xmlns='http://www.w3.org/2000/svg'><image href='data:{mediaType};base64,{payload}'/></svg>";

    private static byte[] EncodeWithPreamble(Encoding encoding, string value)
    {
        byte[] preamble = encoding.GetPreamble();
        byte[] contents = encoding.GetBytes(value);
        byte[] result = new byte[preamble.Length + contents.Length];
        preamble.CopyTo(result, 0);
        contents.CopyTo(result, preamble.Length);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<SvgPreflight.BoundedOutputStream> Task, WeakReference<byte[]> Source) StartUtf16Normalization()
    {
        string sourceText = "<?xml version='1.0' encoding='utf-16'?><svg xmlns='http://www.w3.org/2000/svg'/>";
        byte[] source = EncodeWithPreamble(new UnicodeEncoding(false, true), sourceText);
        WeakReference<byte[]> sourceReference = new(source);
        Task<SvgPreflight.BoundedOutputStream> task = SvgPreflight.NormalizeToUtf8SegmentsAsync(
            source,
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);
        return (task, sourceReference);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<SvgPreflight.BoundedOutputStream> Task, WeakReference<byte[]> Source) StartFaultedUtf16Normalization()
    {
        string sourceText = "<?xml version='1.0' encoding='utf-16'?><svg xmlns='http://www.w3.org/2000/svg'>";
        byte[] source = EncodeWithPreamble(new UnicodeEncoding(false, true), sourceText);
        WeakReference<byte[]> sourceReference = new(source);
        Task<SvgPreflight.BoundedOutputStream> task = SvgPreflight.NormalizeToUtf8SegmentsAsync(
            source,
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);
        return (task, sourceReference);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<SvgPreflight.BoundedOutputStream> Task, WeakReference<byte[]> Source) StartCanceledUtf16Normalization()
    {
        string sourceText = "<?xml version='1.0' encoding='utf-16'?><svg xmlns='http://www.w3.org/2000/svg'/>";
        byte[] source = EncodeWithPreamble(new UnicodeEncoding(false, true), sourceText);
        WeakReference<byte[]> sourceReference = new(source);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Task<SvgPreflight.BoundedOutputStream> task = SvgPreflight.NormalizeToUtf8SegmentsAsync(
            source,
            new ResvgMarkdownSvgRendererOptions(),
            cancellation.Token);
        return (task, sourceReference);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<SvgPreflightResult> Task, WeakReference<byte[]> Source) StartFaultedInspection()
    {
        byte[] source = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'");
        WeakReference<byte[]> sourceReference = new(source);
        Task<SvgPreflightResult> task = SvgPreflight.InspectCoreAsync(
            source,
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);
        return (task, sourceReference);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<SvgPreflightResult> Task, WeakReference<byte[]> Source) StartCanceledInspection()
    {
        byte[] source = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>");
        WeakReference<byte[]> sourceReference = new(source);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Task<SvgPreflightResult> task = SvgPreflight.InspectCoreAsync(
            source,
            new ResvgMarkdownSvgRendererOptions(),
            cancellation.Token);
        return (task, sourceReference);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertCollected(WeakReference<byte[]> reference)
    {
        // A TryGetTarget loop condition can keep its JIT temporary strongly
        // rooted across the very collection being tested. Collect first, with
        // no target obtained in this frame, then inspect the weak reference.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(reference.TryGetTarget(out _), "The completed normalization task retained its input byte array.");
    }

    private static string CreateCssImageSvg(bool quoted, string dataUri)
    {
        string url = quoted
            ? $"url(\"  {dataUri}  \")"
            : $"url(  {dataUri}  )";
        return $"<svg xmlns='http://www.w3.org/2000/svg'><rect style='fill:{url}'/></svg>";
    }

    private sealed class ThreadObservingMemoryManager(byte[] source) : MemoryManager<byte>
    {
        private int _accessCount;
        private int _nonPoolAccessCount;
        private int _lastAccessThreadId;
        private int _lastAccessWasThreadPoolThread;

        public int AccessCount => Volatile.Read(ref _accessCount);

        public int NonPoolAccessCount => Volatile.Read(ref _nonPoolAccessCount);

        public int LastAccessThreadId => Volatile.Read(ref _lastAccessThreadId);

        public ReadOnlyMemory<byte> BorrowedMemory => CreateMemory(source.Length);

        public bool LastAccessWasThreadPoolThread =>
            Volatile.Read(ref _lastAccessWasThreadPoolThread) != 0;

        public override Span<byte> GetSpan()
        {
            bool isThreadPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            Volatile.Write(ref _lastAccessThreadId, Environment.CurrentManagedThreadId);
            Volatile.Write(ref _lastAccessWasThreadPoolThread, isThreadPoolThread ? 1 : 0);
            Interlocked.Increment(ref _accessCount);
            if (!isThreadPoolThread)
                Interlocked.Increment(ref _nonPoolAccessCount);
            return source;
        }

        public override MemoryHandle Pin(int elementIndex = 0) =>
            throw new NotSupportedException("The test memory manager does not support pinning.");

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
