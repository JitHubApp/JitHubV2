using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Xunit;
using Xunit.Abstractions;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Diagnostic-only capability probe for the installed WIC WebP frame decoder.
/// It does not make source-transform support a renderer requirement or change
/// the shipping decode pipeline.
/// </summary>
[Collection(WicRasterMemoryQualificationCollection.CollectionName)]
public sealed partial class WicBitmapSourceTransformCapabilityTests(ITestOutputHelper output)
{
    private const string FixtureResource = "MarkdownRenderer.GitHub.Tests.Fixtures.oversized-static.webp";
    private const string FixtureSha256 = "6FEB51F257EF7FAC608FFA54E0D942EA4FE4DE11DECD165A12EA254C3886DB85";
    private const int RequestedPreviewWidth = 256;
    private const int RequestedPreviewHeight = 1025;
    private const int CoInitMultithreaded = 0;
    private const int WicTransformRotate0 = 0;

    [Trait("Requires", "WicWebpDecoder")]
    [Fact]
    [Trait("Category", "NativeMemoryQualification")]
    public async Task PinnedWebpFrame_ReportsDecoderSideScaleCapabilityWithoutRequiringIt()
    {
        byte[] source = await ReadPinnedFixtureAsync();
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        Assert.False(budget.Accepted);
        Assert.True(budget.CanRenderStaticPreview, budget.Reason);
        Assert.Equal("WEBP", budget.Format);
        Assert.Equal(1, budget.FrameCount);
        Assert.Equal(2_048, budget.Width);
        Assert.Equal(8_193, budget.Height);
        Assert.InRange(source.Length, 1, RasterImageResourceBudget.MaxInputBytes);
        Assert.InRange(budget.TotalPixels, 1, RasterImageResourceBudget.MaxDownsampleSourcePixels);

        var sizingRequest = new RasterImageSizingRequest(
            RequestedPreviewWidth,
            rasterizationScale: 1d,
            requestedWidth: null,
            requestedHeight: null,
            maximumPixels: 300_000);
        Assert.True(RasterImageDecodePlan.TryCreate(source, budget, sizingRequest, out RasterImageDecodePlan plan));
        Assert.Equal(RequestedPreviewWidth, plan.OutputWidth);
        Assert.Equal(RequestedPreviewHeight, plan.OutputHeight);
        Assert.Equal(1, plan.Orientation);
        Assert.Equal(plan.OutputWidth, plan.DecodeWidth);
        Assert.Equal(plan.OutputHeight, plan.DecodeHeight);
        Assert.InRange(plan.OutputPixels, 1, sizingRequest.MaximumPixels);

        NativeResourceRetirement retirement = NativeResourceRetirement.Shared;
        var before = new RetirementSnapshot(
            retirement.PendingRecordCount,
            retirement.PendingResourceCount,
            retirement.QuarantinedResourceCount);

        ProbeExecution execution = await Task.Run(() => ProbeAndCompare(source, budget, plan));

        var after = new RetirementSnapshot(
            retirement.PendingRecordCount,
            retirement.PendingResourceCount,
            retirement.QuarantinedResourceCount);
        output.WriteLine(
            $"WIC WebP IWICBitmapSourceTransform capability: source={budget.Width}x{budget.Height}; " +
            $"requested={plan.DecodeWidth}x{plan.DecodeHeight}; QI-HRESULT=0x{execution.Probe.QueryInterfaceHResult:X8}; " +
            $"supported={execution.Probe.InterfaceAvailable}; closest-size-HRESULT=0x{execution.Probe.ClosestSizeHResult:X8}; " +
            $"closest-size={execution.Probe.ClosestWidth}x{execution.Probe.ClosestHeight}; " +
            $"closest-format-HRESULT=0x{execution.Probe.ClosestFormatHResult:X8}; " +
            $"closest-format={execution.Probe.ClosestPixelFormat}; " +
            $"identity-transform-HRESULT=0x{execution.Probe.IdentityTransformHResult:X8}; " +
            $"identity-transform-supported={execution.Probe.IdentityTransformSupported}; " +
            $"copy-HRESULT=0x{execution.Probe.CopyPixelsHResult:X8}; " +
            $"comparison={execution.Comparison.Description}. " +
            $"No source-transform support or pixel parity is required by this diagnostic.");
        output.WriteLine(
            $"WIC capability probe retirement ledger: pending-records={before.PendingRecords}->{after.PendingRecords}; " +
            $"pending-resources={before.PendingResources}->{after.PendingResources}; " +
            $"quarantined-resources={before.QuarantinedResources}->{after.QuarantinedResources}; " +
            $"graph-retired={execution.Probe.GraphRetired}; apartment-balanced={execution.Probe.ApartmentBalanced}.");

        Assert.True(execution.Probe.GraphRetired, "The capability probe did not retire its WIC graph.");
        Assert.True(execution.Probe.ApartmentBalanced, "The capability probe did not balance its MTA initialization.");
        Assert.Equal(before.PendingRecords, after.PendingRecords);
        Assert.Equal(before.PendingResources, after.PendingResources);
        Assert.Equal(before.QuarantinedResources, after.QuarantinedResources);
    }

    private static ProbeExecution ProbeAndCompare(
        byte[] source,
        RasterImageBudgetResult budget,
        RasterImageDecodePlan plan)
    {
        byte[]? transformedPixels = null;
        try
        {
            ProbeSnapshot probe;
            bool graphRetired;
            bool apartmentBalanced;
            using (var graph = new WicCapabilityProbeGraph(source, budget))
            {
                try
                {
                    graph.InitializeApartment();
                    graph.InitializeDecoder();
                    probe = graph.Probe(plan);
                    transformedPixels = probe.TransformPixels;
                }
                finally
                {
                    try
                    {
                        graph.DisposeNativeGraph();
                    }
                    finally
                    {
                        graph.BalanceApartmentInitialization();
                    }
                }

                graphRetired = graph.GraphRetired;
                apartmentBalanced = graph.ApartmentBalanced;
            }

            probe = probe with
            {
                GraphRetired = graphRetired,
                ApartmentBalanced = apartmentBalanced,
            };

            if (transformedPixels is null)
                return new ProbeExecution(probe, new PixelComparison("not attempted: decoder did not return requested PBGRA pixels"));

            var credit = new ProbeDecodeCredit();
            try
            {
                using RasterDecodedPixelLease current = RasterNativeWicDecoder.Decode(
                    source, plan, credit, CancellationToken.None);
                PixelDelta delta = ComparePixels(transformedPixels, current.Pixels);
                return new ProbeExecution(probe, new PixelComparison(
                    $"current owned-native WIC output compared; differingBytes={delta.DifferingBytes:N0}/" +
                    $"{transformedPixels.Length:N0}; maxChannelDelta={delta.MaximumChannelDelta}; " +
                    $"firstDifferenceByte={delta.FirstDifferenceByte}"));
            }
            catch (Exception failure)
            {
                return new ProbeExecution(probe, new PixelComparison(
                    $"current owned-native WIC reference failed: {failure.GetType().Name}, HRESULT=0x{failure.HResult:X8}"));
            }
            finally
            {
                if (credit.DisposeCount != 1)
                {
                    throw new InvalidOperationException(
                        $"The reference decode consumed its test credit {credit.DisposeCount} times; expected exactly once.");
                }
            }
        }
        finally
        {
            if (transformedPixels is not null)
                Array.Clear(transformedPixels);
        }
    }

    private static PixelDelta ComparePixels(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
            return new PixelDelta(System.Math.Abs(left.Length - right.Length), byte.MaxValue, -1);

        int differingBytes = 0;
        int maximumDelta = 0;
        int firstDifference = -1;
        for (int index = 0; index < left.Length; index++)
        {
            int delta = System.Math.Abs(left[index] - right[index]);
            if (delta == 0)
                continue;
            if (firstDifference < 0)
                firstDifference = index;
            differingBytes++;
            maximumDelta = System.Math.Max(maximumDelta, delta);
        }
        return new PixelDelta(differingBytes, maximumDelta, firstDifference);
    }

    private static async Task<byte[]> ReadPinnedFixtureAsync()
    {
        using Stream fixture = typeof(WicBitmapSourceTransformCapabilityTests).Assembly
            .GetManifestResourceStream(FixtureResource)
            ?? throw new InvalidOperationException($"Missing embedded fixture '{FixtureResource}'.");
        using var buffer = new MemoryStream();
        await fixture.CopyToAsync(buffer).ConfigureAwait(false);
        byte[] source = buffer.ToArray();
        Assert.Equal(FixtureSha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)));
        return source;
    }

    private readonly record struct RetirementSnapshot(int PendingRecords, long PendingResources, long QuarantinedResources);
    private sealed record ProbeExecution(ProbeSnapshot Probe, PixelComparison Comparison);
    private sealed record PixelComparison(string Description);
    private readonly record struct PixelDelta(int DifferingBytes, int MaximumChannelDelta, int FirstDifferenceByte);

    private sealed record ProbeSnapshot(
        bool InterfaceAvailable,
        int QueryInterfaceHResult,
        int ClosestSizeHResult,
        uint ClosestWidth,
        uint ClosestHeight,
        int ClosestFormatHResult,
        Guid ClosestPixelFormat,
        int IdentityTransformHResult,
        bool IdentityTransformSupported,
        int CopyPixelsHResult,
        byte[]? TransformPixels,
        bool GraphRetired,
        bool ApartmentBalanced);

    private sealed class ProbeDecodeCredit : IDisposable
    {
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed unsafe class WicCapabilityProbeGraph : IDisposable
    {
        private static readonly Guid s_transformIid = new("3b16811b-6a43-4ec9-b713-3d5a0c13b940");
        private static readonly Guid s_pbgraFormat = RasterNativeWicInterop.WicPixelFormat32bppPbgra;
        private readonly byte[] _source;
        private readonly RasterImageBudgetResult _validatedBudget;
        private readonly StrategyBasedComWrappers _wrappers = new();
        private readonly NativeResourceRetirementRecord _retirementRecord;
        private GCHandle _sourcePin;
        private nint _pendingNativePointer;
        private nint _pendingIUnknownPointer;
        private nint _pendingQueryInterfacePointer;
        private ComObject? _factoryOwner;
        private ComObject? _streamOwner;
        private ComObject? _decoderOwner;
        private ComObject? _frameOwner;
        private ComObject? _transformOwner;
        private RasterNativeWicInterop.IWICImagingFactory? _factory;
        private RasterNativeWicInterop.IWICStream? _stream;
        private RasterNativeWicInterop.IWICBitmapDecoder? _decoder;
        private RasterNativeWicInterop.IWICBitmapFrameDecode? _frame;
        private IWICBitmapSourceTransformCapability? _transform;
        private int _outstandingQueryReferences;
        private bool _apartmentInitialized;
        private bool _graphRetired;
        private bool _quarantined;
        private bool _disposed;

        internal bool GraphRetired => _graphRetired;
        internal bool ApartmentBalanced => !_apartmentInitialized;

        internal WicCapabilityProbeGraph(byte[] source, RasterImageBudgetResult validatedBudget)
        {
            _source = source;
            _validatedBudget = validatedBudget;
            _retirementRecord = NativeResourceRetirement.Shared.Reserve(
                resourceGroupCount: 0,
                temporarySlotCount: 1);
            // The record and its only slot exist before the source is pinned or
            // a WIC factory can allocate. Uncertain release is custody-only.
            _retirementRecord.SetTemporary(0, this, retryable: false);
        }

        internal void InitializeApartment()
        {
            int hresult = NativeMethods.CoInitializeEx(0, CoInitMultithreaded);
            if (hresult < 0)
                ThrowForHResult(hresult, "CoInitializeEx(COINIT_MULTITHREADED)");
            _apartmentInitialized = true;
            _sourcePin = GCHandle.Alloc(_source, GCHandleType.Pinned);
        }

        internal void InitializeDecoder()
        {
            Guid classId = RasterNativeWicInterop.ClsidWicImagingFactory2;
            Guid interfaceId = RasterNativeWicInterop.IidWicImagingFactory;
            nint nativePointer = 0;
            int hresult;
            try
            {
                hresult = NativeMethods.CoCreateInstance(
                    &classId, 0, RasterNativeWicInterop.ClsctxInprocServer, &interfaceId, &nativePointer);
            }
            finally
            {
                _pendingNativePointer = nativePointer;
            }
            if (_pendingNativePointer != 0)
                _factory = AdoptPending<RasterNativeWicInterop.IWICImagingFactory>(
                    ref _pendingNativePointer, ref _factoryOwner, isQueryReference: false);
            ThrowForHResult(hresult, "CoCreateInstance(CLSID_WICImagingFactory2)");

            RasterNativeWicInterop.IWICImagingFactory factory = Require(_factory, "factory");
            nativePointer = 0;
            try
            {
                hresult = factory.CreateStream(&nativePointer);
            }
            finally
            {
                _pendingNativePointer = nativePointer;
            }
            if (_pendingNativePointer != 0)
                _stream = AdoptPending<RasterNativeWicInterop.IWICStream>(
                    ref _pendingNativePointer, ref _streamOwner, isQueryReference: false);
            ThrowForHResult(hresult, "IWICImagingFactory.CreateStream");

            RasterNativeWicInterop.IWICStream stream = Require(_stream, "stream");
            byte* sourceBytes = (byte*)_sourcePin.AddrOfPinnedObject();
            hresult = stream.InitializeFromMemory(sourceBytes, checked((uint)_source.Length));
            ThrowForHResult(hresult, "IWICStream.InitializeFromMemory");

            QueryOwnedInterface(_streamOwner!, RasterNativeWicInterop.IidIStream);
            try
            {
                nativePointer = 0;
                try
                {
                    hresult = factory.CreateDecoderFromStream(
                        _pendingQueryInterfacePointer,
                        0,
                        RasterNativeWicInterop.DecodeMetadataCacheOnDemand,
                        &nativePointer);
                }
                finally
                {
                    _pendingNativePointer = nativePointer;
                }
                if (_pendingNativePointer != 0)
                    _decoder = AdoptPending<RasterNativeWicInterop.IWICBitmapDecoder>(
                        ref _pendingNativePointer, ref _decoderOwner, isQueryReference: false);
            }
            finally
            {
                if (_pendingQueryInterfacePointer != 0)
                    ReleaseQueryInterfacePointer(ref _pendingQueryInterfacePointer);
            }
            ThrowForHResult(hresult, "IWICImagingFactory.CreateDecoderFromStream");

            RasterNativeWicInterop.IWICBitmapDecoder decoder = Require(_decoder, "decoder");
            uint frameCount = 0;
            hresult = decoder.GetFrameCount(&frameCount);
            ThrowForHResult(hresult, "IWICBitmapDecoder.GetFrameCount");
            if (frameCount != 1)
                throw new InvalidDataException($"Expected one static WebP frame; WIC reported {frameCount}.");

            Guid containerFormat = Guid.Empty;
            hresult = decoder.GetContainerFormat(&containerFormat);
            ThrowForHResult(hresult, "IWICBitmapDecoder.GetContainerFormat");
            if (containerFormat != RasterNativeWicInterop.ContainerFormatWebp)
                throw new InvalidDataException($"WIC selected container {containerFormat}, not WebP.");

            nativePointer = 0;
            try
            {
                hresult = decoder.GetFrame(0, &nativePointer);
            }
            finally
            {
                _pendingNativePointer = nativePointer;
            }
            if (_pendingNativePointer != 0)
                _frame = AdoptPending<RasterNativeWicInterop.IWICBitmapFrameDecode>(
                    ref _pendingNativePointer, ref _frameOwner, isQueryReference: false);
            ThrowForHResult(hresult, "IWICBitmapDecoder.GetFrame(0)");

            RasterNativeWicInterop.IWICBitmapFrameDecode frame = Require(_frame, "frame");
            uint width = 0;
            uint height = 0;
            hresult = frame.GetSize(&width, &height);
            ThrowForHResult(hresult, "IWICBitmapSource.GetSize");
            if (width != _validatedBudget.Width || height != _validatedBudget.Height)
                throw new InvalidDataException("WIC frame dimensions disagree with the validated WebP header.");
        }

        internal ProbeSnapshot Probe(RasterImageDecodePlan plan)
        {
            int queryHresult = QueryTransform();
            if (_transform is null)
            {
                return new ProbeSnapshot(
                    false, queryHresult, int.MinValue, 0, 0, int.MinValue, Guid.Empty,
                    int.MinValue, false, int.MinValue, null, _graphRetired, _apartmentInitialized);
            }

            uint closestWidth = checked((uint)plan.DecodeWidth);
            uint closestHeight = checked((uint)plan.DecodeHeight);
            IWICBitmapSourceTransformCapability transform = _transform;
            int closestSizeHresult = transform.GetClosestSize(&closestWidth, &closestHeight);

            Guid closestFormat = s_pbgraFormat;
            int closestFormatHresult = transform.GetClosestPixelFormat(&closestFormat);

            int identitySupported = 0;
            int identityHresult = transform.DoesSupportTransform(WicTransformRotate0, &identitySupported);

            byte[]? pixels = null;
            int copyHresult = int.MinValue;
            bool exactRequest = closestSizeHresult >= 0 &&
                closestWidth == (uint)plan.DecodeWidth && closestHeight == (uint)plan.DecodeHeight;
            bool exactFormat = closestFormatHresult >= 0 && closestFormat == s_pbgraFormat;
            if (exactRequest && exactFormat && identityHresult >= 0 && identitySupported != 0)
            {
                int byteCount = checked(checked(plan.OutputWidth * plan.OutputHeight) * 4);
                pixels = GC.AllocateUninitializedArray<byte>(byteCount);
                Guid outputFormat = s_pbgraFormat;
                try
                {
                    fixed (byte* destination = pixels)
                    {
                        copyHresult = transform.CopyPixels(
                            sourceRect: 0,
                            checked((uint)plan.DecodeWidth),
                            checked((uint)plan.DecodeHeight),
                            &outputFormat,
                            WicTransformRotate0,
                            checked((uint)plan.OutputWidth * 4),
                            checked((uint)byteCount),
                            destination);
                    }
                }
                catch
                {
                    Array.Clear(pixels);
                    throw;
                }
                if (copyHresult < 0)
                {
                    Array.Clear(pixels);
                    pixels = null;
                }
            }

            return new ProbeSnapshot(
                true,
                queryHresult,
                closestSizeHresult,
                closestWidth,
                closestHeight,
                closestFormatHresult,
                closestFormat,
                identityHresult,
                identitySupported != 0,
                copyHresult,
                pixels,
                _graphRetired,
                _apartmentInitialized);
        }

        internal void DisposeNativeGraph()
        {
            if (_disposed || _quarantined)
                return;
            _disposed = true;

            if (_pendingNativePointer != 0 || _pendingIUnknownPointer != 0 ||
                _pendingQueryInterfacePointer != 0 || _outstandingQueryReferences != 0)
            {
                QuarantineAndThrow("The capability probe has an unadopted or outstanding native interface pointer.");
            }

            if (!TryFinalRelease(ref _transformOwner, ref _transform) ||
                !TryFinalRelease(ref _frameOwner, ref _frame) ||
                !TryFinalRelease(ref _decoderOwner, ref _decoder) ||
                !TryFinalRelease(ref _streamOwner, ref _stream) ||
                !TryFinalRelease(ref _factoryOwner, ref _factory))
            {
                QuarantineAndThrow("A WIC capability-probe interface could not be deterministically released.");
            }

            if (_sourcePin.IsAllocated)
            {
                try
                {
                    _sourcePin.Free();
                }
                catch (Exception failure)
                {
                    QuarantineAndThrow("The pinned WebP source could not be released.", failure);
                }
            }

            _graphRetired = true;
            _retirementRecord.SetTemporary(0, resource: null, retryable: false);
            _retirementRecord.Publish();
        }

        internal void BalanceApartmentInitialization()
        {
            if (!_apartmentInitialized)
                return;
            NativeMethods.CoUninitialize();
            _apartmentInitialized = false;
        }

        public void Dispose()
        {
            if (!_disposed && !_quarantined)
                DisposeNativeGraph();
        }

        private int QueryTransform()
        {
            if (_frameOwner is null)
                throw new InvalidOperationException("The WIC frame owner is unavailable.");
            bool found = ComWrappers.TryGetComInstance(_frameOwner, out _pendingIUnknownPointer);
            if (!found || _pendingIUnknownPointer == 0)
            {
                if (_pendingIUnknownPointer != 0)
                    ReleaseIUnknownPointer();
                return unchecked((int)0x80004002); // E_NOINTERFACE
            }

            int hresult;
            try
            {
                hresult = Marshal.QueryInterface(
                    _pendingIUnknownPointer,
                    in s_transformIid,
                    out _pendingQueryInterfacePointer);
            }
            finally
            {
                ReleaseIUnknownPointer();
            }

            if (hresult < 0)
            {
                if (_pendingQueryInterfacePointer != 0)
                    ReleaseQueryInterfacePointer(ref _pendingQueryInterfacePointer);
                return hresult;
            }

            if (_pendingQueryInterfacePointer == 0)
                return unchecked((int)0x80004003); // E_POINTER
            _outstandingQueryReferences++;
            _transform = AdoptPending<IWICBitmapSourceTransformCapability>(
                ref _pendingQueryInterfacePointer, ref _transformOwner, isQueryReference: true);
            return hresult;
        }

        private void QueryOwnedInterface(ComObject owner, Guid iid)
        {
            bool found = ComWrappers.TryGetComInstance(owner, out _pendingIUnknownPointer);
            if (!found || _pendingIUnknownPointer == 0)
            {
                if (_pendingIUnknownPointer != 0)
                    ReleaseIUnknownPointer();
                throw new InvalidOperationException("The unique WIC wrapper has no COM instance.");
            }

            int hresult = Marshal.QueryInterface(
                _pendingIUnknownPointer,
                in iid,
                out _pendingQueryInterfacePointer);
            if (hresult < 0)
            {
                if (_pendingQueryInterfacePointer != 0)
                    ReleaseQueryInterfacePointer(ref _pendingQueryInterfacePointer);
                ReleaseIUnknownPointer();
                ThrowForHResult(hresult, $"QueryInterface({iid})");
            }
            if (_pendingQueryInterfacePointer == 0)
            {
                ReleaseIUnknownPointer();
                throw new InvalidOperationException($"QueryInterface({iid}) returned a null pointer.");
            }

            _outstandingQueryReferences++;
            // If releasing the temporary IUnknown reference is uncertain, keep
            // the queried interface in its graph custody slot for quarantine.
            ReleaseIUnknownPointer();
        }

        private T AdoptPending<T>(ref nint pendingPointer, ref ComObject? owner, bool isQueryReference)
            where T : class
        {
            if (pendingPointer == 0)
                throw new InvalidOperationException("There is no pending WIC interface pointer to adopt.");
            if (owner is not null)
                throw new InvalidOperationException("A WIC owner slot was assigned twice.");

            // Decoder creation still owns the stream's separate QI reference.
            // Adopt the explicitly named result, never whichever custody slot
            // happens to be nonempty while two dependent interfaces are live.
            nint pointer = pendingPointer;
            ComObject wrapper = (ComObject)_wrappers.GetOrCreateObjectForComInstance(
                pointer,
                CreateObjectFlags.UniqueInstance);
            owner = wrapper;
            T typed = (T)(object)wrapper;

            // Keep the raw pointer in its custody field until Release succeeds.
            Marshal.Release(pointer);
            pendingPointer = 0;
            if (isQueryReference)
            {
                _outstandingQueryReferences--;
            }
            return typed;
        }

        private void ReleaseQueryInterfacePointer(ref nint pointer)
        {
            nint value = pointer;
            Marshal.Release(value);
            pointer = 0;
            if (_outstandingQueryReferences > 0)
                _outstandingQueryReferences--;
        }

        private void ReleaseIUnknownPointer()
        {
            if (_pendingIUnknownPointer == 0)
                return;
            nint value = _pendingIUnknownPointer;
            Marshal.Release(value);
            _pendingIUnknownPointer = 0;
        }

        private static bool TryFinalRelease<T>(ref ComObject? owner, ref T? typed)
            where T : class
        {
            if (owner is null)
            {
                typed = null;
                return true;
            }
            try
            {
                owner.FinalRelease();
                owner = null;
                typed = null;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void QuarantineAndThrow(string message, Exception? innerException = null)
        {
            _quarantined = true;
            _retirementRecord.Publish();
            throw new InvalidOperationException(message, innerException);
        }

        private static T Require<T>(T? value, string name) where T : class =>
            value ?? throw new InvalidOperationException($"The WIC {name} is unavailable.");

        private static void ThrowForHResult(int hresult, string operation)
        {
            if (hresult < 0)
                throw new COMException($"WIC operation failed: {operation}.", hresult);
        }
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
    [Guid("3b16811b-6a43-4ec9-b713-3d5a0c13b940")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe partial interface IWICBitmapSourceTransformCapability
    {
        [PreserveSig]
        int CopyPixels(
            nint sourceRect,
            uint width,
            uint height,
            Guid* destinationFormat,
            int destinationTransform,
            uint stride,
            uint bufferSize,
            byte* pixels);

        [PreserveSig] int GetClosestSize(uint* width, uint* height);
        [PreserveSig] int GetClosestPixelFormat(Guid* destinationFormat);
        [PreserveSig] int DoesSupportTransform(int destinationTransform, int* isSupported);
    }

    private static partial class NativeMethods
    {
        [LibraryImport("ole32.dll", EntryPoint = "CoInitializeEx")]
        internal static partial int CoInitializeEx(nint reserved, int coInitialize);

        [LibraryImport("ole32.dll", EntryPoint = "CoUninitialize")]
        internal static partial void CoUninitialize();

        [LibraryImport("ole32.dll", EntryPoint = "CoCreateInstance")]
        internal static unsafe partial int CoCreateInstance(
            Guid* classId,
            nint outer,
            uint classContext,
            Guid* interfaceId,
            nint* instance);
    }
}
