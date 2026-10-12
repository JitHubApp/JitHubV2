using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg;

const int repeatedCycles = 100;
const int maximumHandleGrowth = 4;
const ulong maximumPrivateByteGrowth = 8 * 1024 * 1024;

bool expectUnavailable = args.Contains("--expect-unavailable", StringComparer.Ordinal);
string expectedWorkerPath = Path.Combine(AppContext.BaseDirectory, "MarkdownRenderer.Svg.Resvg.Worker.exe");
byte[] simpleSvg = Encoding.UTF8.GetBytes(
    "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16'><rect width='16' height='16' fill='#00ff00'/></svg>");
byte[] embeddedPng = CreatePng(2, 2, 240, 24, 40, 255);
string embeddedPngDataUri = "data:image/png;base64," + Convert.ToBase64String(embeddedPng);
byte[] complexSvg = Encoding.UTF8.GetBytes(
    "<svg xmlns='http://www.w3.org/2000/svg' width='128' height='64'>" +
    $"<image x='0' y='0' width='16' height='16' href='{embeddedPngDataUri}'/>" +
    $"<image x='16' y='0' width='16' height='16' href='{embeddedPngDataUri}'/>" +
    "<text x='120' y='52' text-anchor='start' direction='rtl' unicode-bidi='bidi-override' " +
    "font-family='sans-serif' font-size='22' fill='#101010'>שלום</text></svg>");
byte[] manyImagesSvg = CreateManyImagesSvg();
byte[] staticWebP = SmokeRasterDecodeFixture.Create();
byte[] colorManagedWebP = SmokeRasterDecodeFixture.WithMetadata(staticWebP, 2, 2,
    SmokeRasterDecodeFixture.CreateLinearRgbProfile());
byte[] streamedWebP = SmokeRasterDecodeFixture.WithMetadata(
    SmokeRasterDecodeFixture.CreateLiteralWebP(1024, 1025, patterned: true), 1024, 1025,
    SmokeRasterDecodeFixture.CreateLinearRgbProfile(version4: true), orientation: 6, hasAlpha: true);
byte[]? streamedPixelHash = null;

await using var renderer = new ResvgMarkdownSvgRenderer();
SmokeRasterFailure rasterFailures = SmokeRasterFailure.None;
SmokeRasterResult lastRasterFailure = default;
int failedRichRenders = 0;
int failedManyImageRenders = 0;
int failedStaticRasterDecodes = 0;
try
{
    await renderer.WarmUpAsync();
    uint workerProcessId = 0;
    if (!expectUnavailable)
    {
        workerProcessId = WorkerIsolationProbe.VerifyAfterWarmUp(
            Environment.ProcessId,
            expectedWorkerPath);
    }

    using (IMarkdownSvgDocument document = await renderer.OpenAsync(new MarkdownSvgOpenRequest(simpleSvg)))
    using (MarkdownSvgRaster raster = await document.RenderAsync(new MarkdownSvgRenderRequest(16, 16)))
    {
        if (expectUnavailable)
            return 20;
        ValidateSimpleRaster(raster);
    }

    // One rich warm-up render stabilizes the worker font and embedded-raster caches
    // before taking retained-resource baselines for the repeated lifecycle check.
    using (IMarkdownSvgDocument document = await renderer.OpenAsync(new MarkdownSvgOpenRequest(complexSvg)))
    using (MarkdownSvgRaster raster = await document.RenderAsync(new MarkdownSvgRenderRequest(128, 64)))
    {
        RecordRichRaster(raster);
    }
    await RenderManyImagesAsync();
    await InitializeStreamedRasterReferenceAsync();
    await DecodeStaticRasterAsync();
    await renderer.TrimCachesAsync();

    ResourceSnapshot before = WorkerIsolationProbe.ReadResourceSnapshot(workerProcessId);
    long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    GCMemoryInfo heapBefore = GC.GetGCMemoryInfo();
    int collectionsBefore = GC.CollectionCount(0);
    for (int cycle = 0; cycle < repeatedCycles; cycle++)
    {
        using (IMarkdownSvgDocument document = await renderer.OpenAsync(new MarkdownSvgOpenRequest(complexSvg)))
        using (MarkdownSvgRaster raster = await document.RenderAsync(new MarkdownSvgRenderRequest(128, 64)))
        {
            RecordRichRaster(raster);
        }
        await RenderManyImagesAsync();
        await DecodeStaticRasterAsync();

        // Trim is the eviction boundary for each complete document/raster
        // lifecycle. Await its worker response before beginning the next cycle.
        await renderer.TrimCachesAsync();
    }
    ResourceSnapshot after = WorkerIsolationProbe.ReadResourceSnapshot(workerProcessId);
    long allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
    GCMemoryInfo heapAfter = GC.GetGCMemoryInfo();
    int collectionsAfter = GC.CollectionCount(0);
    uint workerProcessIdAfterCycles = WorkerIsolationProbe.VerifyAfterWarmUp(
        Environment.ProcessId,
        expectedWorkerPath);
    if (workerProcessIdAfterCycles != workerProcessId)
        throw new InvalidOperationException("The SVG worker process changed during the bounded render/evict lifecycle run.");

    Console.WriteLine(
        $"resvg {ResvgMarkdownSvgRenderer.ResvgVersion} isolated-worker AOT smoke measurements; " +
        $"RTL+embedded-raster+182-unique-image+static-WebP+streamed-WebP+ICC lifecycle cycles={repeatedCycles}; " +
        $"host handles {before.CurrentProcessHandleCount}->{after.CurrentProcessHandleCount}; " +
        $"worker handles {before.WorkerHandleCount}->{after.WorkerHandleCount}; " +
        $"host private bytes {before.CurrentProcessPrivateBytes}->{after.CurrentProcessPrivateBytes}; " +
        $"worker private bytes {before.WorkerPrivateBytes}->{after.WorkerPrivateBytes}; " +
        $"aggregate handle growth={HandleGrowth(before, after)}; " +
        $"aggregate private-byte growth={PrivateByteGrowth(before, after)}.");
    Console.WriteLine(
        $"Managed allocation diagnostics (not a replacement for the private-memory gate): " +
        $"allocated bytes={allocatedAfter - allocatedBefore}; gen0 collections={collectionsAfter - collectionsBefore}; " +
        $"last-GC heap size {heapBefore.HeapSizeBytes}->{heapAfter.HeapSizeBytes}; " +
        $"last-GC committed bytes {heapBefore.TotalCommittedBytes}->{heapAfter.TotalCommittedBytes}.");

    // Report both independent gates, then prove that async disposal has actually
    // reaped the exact worker. A bad raster never turns a resource result green
    // and does not prevent the full 100-cycle measurement from running.
    var failures = new List<string>();
    if (failedStaticRasterDecodes != 0)
        failures.Add($"Static WebP output failed {failedStaticRasterDecodes} times.");
    if (rasterFailures != SmokeRasterFailure.None)
        failures.Add($"Rich raster failures={rasterFailures}; failed renders={failedRichRenders}; " +
            $"failed 182-image renders={failedManyImageRenders}; " +
            $"last text bounds left={lastRasterFailure.TextLeft}, right={lastRasterFailure.TextRight}.");
    try
    {
        ValidateResourceBounds(before, after, maximumHandleGrowth, maximumPrivateByteGrowth);
        Console.WriteLine("The 100-cycle retained-resource gate passed.");
    }
    catch (InvalidOperationException exception)
    {
        failures.Add(exception.Message);
    }

    await WorkerIsolationProbe.DisposeAndVerifyExitAsync(workerProcessId, renderer.DisposeAsync);
    await VerifyShutdownDuringStartupAsync(expectedWorkerPath);
    await VerifyRetainedSourceAfterWorkerRestartAsync(expectedWorkerPath);
    if (failures.Count != 0)
    {
        foreach (string failure in failures)
            Console.Error.WriteLine(failure);
        return 21;
    }
    Console.WriteLine("The isolated-worker AOT smoke passed all raster, resource and shutdown gates.");
    return 0;
}
catch (MarkdownSvgException exception) when (expectUnavailable &&
    exception.Reason is MarkdownSvgFailureReason.ArchitectureMismatch or MarkdownSvgFailureReason.WorkerFailure)
{
    Console.WriteLine("resvg unavailable fallback passed.");
    return 0;
}

void RecordRichRaster(MarkdownSvgRaster raster)
{
    SmokeRasterResult result = SmokeRasterValidation.CheckRich(raster.Pixels.Span, raster.WidthPixels, raster.HeightPixels);
    if (result.Failures == SmokeRasterFailure.None)
        return;
    rasterFailures |= result.Failures;
    lastRasterFailure = result;
    failedRichRenders++;
}

async Task RenderManyImagesAsync()
{
    using IMarkdownSvgDocument document = await renderer.OpenAsync(new MarkdownSvgOpenRequest(manyImagesSvg));
    using MarkdownSvgRaster raster = await document.RenderAsync(new MarkdownSvgRenderRequest(
        SmokeRasterValidation.ManyImagesWidth, SmokeRasterValidation.ManyImagesHeight));
    SmokeRasterFailure failure = SmokeRasterValidation.CheckManyImages(
        raster.Pixels.Span, raster.WidthPixels, raster.HeightPixels);
    if (failure == SmokeRasterFailure.None)
        return;
    rasterFailures |= failure;
    failedManyImageRenders++;
}

async Task InitializeStreamedRasterReferenceAsync()
{
    // Establish the reference through the complete-frame path before measuring
    // retained resources. Repeating a stable but incorrect row raster must not
    // qualify the AOT path simply because every lifecycle returns the same bytes.
    using MarkdownRaster reference = await renderer.DecodeAsync(new(streamedWebP,
        MarkdownRasterFormat.WebP, 137, 101, 32 * 1024 * 1024));
    streamedPixelHash = SHA256.HashData(reference.Pixels.Span);
}

async Task DecodeStaticRasterAsync()
{
    // Exercise the statically discoverable raster interface through the very
    // same restricted worker and include its mapping/decoder/ICC ownership in the
    // unchanged 100-cycle, 8-MiB and four-handle retained-resource gates.
    IMarkdownRasterDecoder decoder = renderer;
    for (int index = 0; index < 2; index++)
    {
        using MarkdownRaster raster = await decoder.DecodeAsync(new(index == 0 ? staticWebP : colorManagedWebP,
            MarkdownRasterFormat.WebP, 2, 2, 8 * 1024 * 1024));
        if (!SmokeRasterDecodeFixture.IsValid(raster))
            failedStaticRasterDecodes++;
    }

    // The lowered 8-MiB request cannot admit the complete source, ICC copy and
    // decoder frame together. Include the row path's history ring, CMS and
    // orientation scatter in every measured lifecycle, not just tiny decodes.
    using MarkdownRaster preview = await decoder.DecodeAsync(new(streamedWebP,
        MarkdownRasterFormat.WebP, 137, 101, 8 * 1024 * 1024));
    bool validPreview = preview.WidthPixels == 137 && preview.HeightPixels == 101 &&
        preview.StrideBytes == 137 * 4 && preview.LengthBytes == 137 * 101 * 4;
    for (int offset = 0; validPreview && offset < preview.LengthBytes; offset += 4)
    {
        validPreview = preview.Pixels.Span[offset + 3] == 127 &&
            preview.Pixels.Span[offset] <= 127 && preview.Pixels.Span[offset + 1] <= 127 &&
            preview.Pixels.Span[offset + 2] <= 127;
    }
    byte[] pixelHash = SHA256.HashData(preview.Pixels.Span);
    validPreview &= streamedPixelHash is not null &&
        pixelHash.AsSpan().SequenceEqual(streamedPixelHash);
    if (!validPreview)
        failedStaticRasterDecodes++;
}

static byte[] CreateManyImagesSvg()
{
    var source = new StringBuilder("<svg xmlns='http://www.w3.org/2000/svg' width='890' height='158'>");
    for (int image = 0; image < SmokeRasterValidation.ManyImagesCount; image++)
    {
        byte[] png = CreatePng(1, 1, (byte)(image + 1), (byte)(255 - image), (byte)((image * 37) % 256), 255);
        source.Append("<image x='").Append((image % 26) * 34)
            .Append("' y='").Append((image / 26) * 22)
            .Append("' width='16' height='16' href='data:image/png;base64,")
            .Append(Convert.ToBase64String(png)).Append("'/>");
    }
    return Encoding.UTF8.GetBytes(source.Append("</svg>").ToString());
}

static async Task VerifyShutdownDuringStartupAsync(string workerPath)
{
    const int raceCycles = 8;
    for (int cycle = 0; cycle < raceCycles; cycle++)
    {
        await using var candidate = new ResvgMarkdownSvgRenderer();
        Task startup = candidate.WarmUpAsync();
        // Start disposal while the pipe handshake may still be pending. A
        // worker that completed startup first is also a valid interleaving.
        Task shutdown = candidate.DisposeAsync().AsTask();
        try { await startup; }
        catch (MarkdownSvgException exception) when (exception.Reason == MarkdownSvgFailureReason.Canceled) { }
        await shutdown;
        WorkerIsolationProbe.VerifyNoWorkerChildren(Environment.ProcessId, workerPath);
    }
    Console.WriteLine($"Verified {raceCycles} startup/shutdown races leave no SVG worker children.");
}

static async Task VerifyRetainedSourceAfterWorkerRestartAsync(string workerPath)
{
    // Run outside the measured lifecycle window: this is a source-ownership
    // and actual-worker recovery check, not additional memory-test warm-up.
    // The dense input exercises the large immutable source snapshot path.
    byte[] source = CreateManyImagesSvg();
    await using var candidate = new ResvgMarkdownSvgRenderer();
    uint replacementProcessId;
    using (IMarkdownSvgDocument document = await candidate.OpenAsync(new MarkdownSvgOpenRequest(source)))
    {
        using (MarkdownSvgRaster raster = await document.RenderAsync(new MarkdownSvgRenderRequest(890, 158)))
            ValidateManyImagesRaster(raster);

        _ = WorkerIsolationProbe.VerifyAfterWarmUp(Environment.ProcessId, workerPath);
        Array.Clear(source);
        replacementProcessId = await WorkerIsolationProbe.TerminateAndVerifyReplacementAsync(
            Environment.ProcessId,
            workerPath,
            async () =>
            {
                using MarkdownSvgRaster raster = await document.RenderAsync(new MarkdownSvgRenderRequest(890, 158));
                ValidateManyImagesRaster(raster);
            });
    }
    await WorkerIsolationProbe.DisposeAndVerifyExitAsync(replacementProcessId, candidate.DisposeAsync);
    WorkerIsolationProbe.VerifyNoWorkerChildren(Environment.ProcessId, workerPath);
    Console.WriteLine("Verified retained immutable SVG source reattaches after idle-worker termination despite caller-byte mutation, with all 182 images intact and no orphan worker.");
}

static void ValidateManyImagesRaster(MarkdownSvgRaster raster)
{
    SmokeRasterFailure failure = SmokeRasterValidation.CheckManyImages(
        raster.Pixels.Span, raster.WidthPixels, raster.HeightPixels);
    if (failure != SmokeRasterFailure.None)
        throw new InvalidOperationException($"The worker-restart source-ownership check lost embedded images: {failure}.");
}

static void ValidateSimpleRaster(MarkdownSvgRaster raster)
{
    if (raster.WidthPixels != 16 || raster.HeightPixels != 16 || raster.LengthBytes != 16 * 16 * 4)
        throw new InvalidOperationException("The simple SVG smoke render returned unexpected dimensions or storage.");

    ReadOnlySpan<byte> pixels = raster.Pixels.Span;
    int center = ((8 * 16) + 8) * 4;
    if (pixels[center] > 40 || pixels[center + 1] < 200 || pixels[center + 2] > 40 || pixels[center + 3] < 200)
        throw new InvalidOperationException("The simple SVG smoke render did not contain the expected opaque green pixel.");
}

static void ValidateResourceBounds(
    ResourceSnapshot before,
    ResourceSnapshot after,
    uint maximumHandleGrowth,
    ulong maximumPrivateByteGrowth)
{
    uint handleGrowth = HandleGrowth(before, after);
    ulong privateByteGrowth = PrivateByteGrowth(before, after);

    if (handleGrowth > maximumHandleGrowth)
    {
        throw new InvalidOperationException(
            $"Repeated SVG lifecycles leaked handles beyond the aggregate bound: host " +
            $"{before.CurrentProcessHandleCount}->{after.CurrentProcessHandleCount}, worker " +
            $"{before.WorkerHandleCount}->{after.WorkerHandleCount}, aggregate growth +{handleGrowth}, " +
            $"allowed +{maximumHandleGrowth}.");
    }

    if (privateByteGrowth > maximumPrivateByteGrowth)
    {
        throw new InvalidOperationException(
            $"Repeated SVG lifecycles exceeded the aggregate retained-private-memory bound: host " +
            $"+{Growth(before.CurrentProcessPrivateBytes, after.CurrentProcessPrivateBytes)} bytes, worker " +
            $"+{Growth(before.WorkerPrivateBytes, after.WorkerPrivateBytes)} bytes, " +
            $"aggregate +{privateByteGrowth} bytes, allowed +{maximumPrivateByteGrowth} bytes.");
    }
}

static uint HandleGrowth(ResourceSnapshot before, ResourceSnapshot after)
{
    uint start = checked(before.CurrentProcessHandleCount + before.WorkerHandleCount);
    uint end = checked(after.CurrentProcessHandleCount + after.WorkerHandleCount);
    return end > start ? end - start : 0;
}

static ulong PrivateByteGrowth(ResourceSnapshot before, ResourceSnapshot after)
{
    ulong start = checked(before.CurrentProcessPrivateBytes + before.WorkerPrivateBytes);
    ulong end = checked(after.CurrentProcessPrivateBytes + after.WorkerPrivateBytes);
    return Growth(start, end);
}

static ulong Growth(ulong start, ulong end) => end > start ? end - start : 0;

static byte[] CreatePng(int width, int height, byte red, byte green, byte blue, byte alpha)
{
    if (width <= 0 || height <= 0 || width > 16 || height > 16)
        throw new ArgumentOutOfRangeException(nameof(width));

    byte[] scanlines = new byte[checked(height * (1 + width * 4))];
    for (int y = 0; y < height; y++)
    {
        int row = y * (1 + width * 4);
        scanlines[row] = 0; // PNG filter: None.
        for (int x = 0; x < width; x++)
        {
            int pixel = row + 1 + x * 4;
            scanlines[pixel] = red;
            scanlines[pixel + 1] = green;
            scanlines[pixel + 2] = blue;
            scanlines[pixel + 3] = alpha;
        }
    }

    using var compressed = new MemoryStream();
    using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        zlib.Write(scanlines);

    using var png = new MemoryStream();
    png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
    Span<byte> header = stackalloc byte[13];
    header.Clear();
    BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)width));
    BinaryPrimitives.WriteUInt32BigEndian(header[4..], checked((uint)height));
    header[8] = 8; // bit depth
    header[9] = 6; // RGBA
    WritePngChunk(png, "IHDR"u8, header);
    WritePngChunk(png, "IDAT"u8, compressed.ToArray());
    WritePngChunk(png, "IEND"u8, ReadOnlySpan<byte>.Empty);
    return png.ToArray();
}

static void WritePngChunk(Stream destination, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
{
    Span<byte> integer = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(integer, checked((uint)payload.Length));
    destination.Write(integer);
    destination.Write(type);
    destination.Write(payload);

    uint crc = 0xFFFF_FFFF;
    foreach (byte value in type)
        crc = UpdateCrc32(crc, value);
    foreach (byte value in payload)
        crc = UpdateCrc32(crc, value);
    BinaryPrimitives.WriteUInt32BigEndian(integer, ~crc);
    destination.Write(integer);
}

static uint UpdateCrc32(uint crc, byte value)
{
    crc ^= value;
    for (int bit = 0; bit < 8; bit++)
        crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB8_8320 : crc >> 1;
    return crc;
}
