using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Owns the borrowed source stream, terminal WinRT decode operations, optional
/// decoded software bitmap, and process-wide decode credit for one WIC load.
/// It does not own a decoder result or promise a bound on codec-private memory.
/// </summary>
internal sealed class WicDecodeOperationOwner : IDisposable
{
    private const string RetirementPendingMessage =
        "A terminal WIC decode resource could not yet be retired.";

    private readonly NativeResourceRetirementRecord _retirementRecord;
    private IDisposable? _processDecodeSlot;
    private MemoryStream? _sourceStream;
    private IRandomAccessStream? _randomAccessStream;
    private IAsyncOperation<BitmapDecoder>? _decoderCreationOperation;
    private IAsyncOperation<SoftwareBitmap>? _softwareBitmapOperation;
    private SoftwareBitmap? _softwareBitmap;
    private int _retirementStarted;
    private int _disposed;
    private int _quarantined;
    private int _lastFailureHResult;

    private WicDecodeOperationOwner(NativeResourceRetirement retirement)
    {
        _retirementRecord = retirement.Reserve(
            resourceGroupCount: 0,
            temporarySlotCount: 1);
        _retirementRecord.SetTemporary(0, this);
    }

    internal static WicDecodeOperationOwner Create(NativeResourceRetirement? retirement = null) =>
        new(retirement ?? NativeResourceRetirement.Shared);

    internal int LastFailureHResult => Volatile.Read(ref _lastFailureHResult);
    internal bool HasSourceStreamsForTests => _sourceStream is not null || _randomAccessStream is not null;

    /// <summary>
    /// True once a native close/dispose attempt failed and ownership can no
    /// longer be queried safely. A quarantined owner deliberately retains its
    /// remaining resources and process credit without further native calls.
    /// </summary>
    internal bool IsQuarantined => Volatile.Read(ref _quarantined) != 0;

    /// <summary>
    /// Transfers a process decode lease without an unowned interval: the caller
    /// retains it if validation fails, otherwise this owner receives it before
    /// the caller's local is cleared.
    /// </summary>
    internal void AdoptProcessDecodeSlot(ref IDisposable? processDecodeSlot)
    {
        EnsureActive();
        if (processDecodeSlot is null)
            throw new ArgumentNullException(nameof(processDecodeSlot));
        if (_processDecodeSlot is not null)
            throw new InvalidOperationException("A WIC decode owner already holds a process decode slot.");

        _processDecodeSlot = processDecodeSlot;
        processDecodeSlot = null;
    }

    /// <summary>
    /// Opens and immediately adopts both borrowed-source stream wrappers.
    /// The returned adapter is borrowed by the caller and must not be disposed
    /// independently of this owner.
    /// </summary>
    internal IRandomAccessStream OpenSourceStream(byte[] sourceBytes)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(sourceBytes);
        if (_sourceStream is not null || _randomAccessStream is not null)
            throw new InvalidOperationException("A WIC decode owner already holds source streams.");

        var sourceStream = new MemoryStream(sourceBytes, writable: false);
        _sourceStream = sourceStream;
        IRandomAccessStream randomAccessStream = sourceStream.AsRandomAccessStream();
        _randomAccessStream = randomAccessStream;
        return randomAccessStream;
    }

    /// <summary>
    /// Creates and stores the projected operation before returning it. The
    /// caller must await this operation to its terminal result without a
    /// cancellation wrapper; cancellation is checked after that await.
    /// </summary>
    internal IAsyncOperation<BitmapDecoder> CreateDecoderOperation(IRandomAccessStream stream)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(stream);
        if (!ReferenceEquals(stream, _randomAccessStream))
            throw new InvalidOperationException("The WIC decoder must use this owner's source stream.");
        if (_decoderCreationOperation is not null)
            throw new InvalidOperationException("A WIC decoder operation is already registered.");

        IAsyncOperation<BitmapDecoder> operation = BitmapDecoder.CreateAsync(stream);
        RegisterDecoderCreationOperation(operation);
        return operation;
    }

    internal void RegisterDecoderCreationOperation(IAsyncOperation<BitmapDecoder> operation)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(operation);
        if (_randomAccessStream is null)
            throw new InvalidOperationException("A WIC source stream must be opened before registering a decoder operation.");
        if (_decoderCreationOperation is not null)
            throw new InvalidOperationException("A WIC decoder operation is already registered.");

        _decoderCreationOperation = operation;
    }

    /// <summary>
    /// Creates and stores the pixel decode operation before returning it. The
    /// caller awaits it directly and immediately adopts its SoftwareBitmap
    /// result before cancellation checks or any other potentially throwing work.
    /// </summary>
    internal IAsyncOperation<SoftwareBitmap> CreateSoftwareBitmapOperation(
        BitmapDecoder decoder,
        BitmapPixelFormat pixelFormat,
        BitmapAlphaMode alphaMode,
        BitmapTransform transform,
        ExifOrientationMode orientationMode,
        ColorManagementMode colorManagementMode)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(transform);
        if (_randomAccessStream is null)
            throw new InvalidOperationException("The WIC source stream must be opened before decoding pixels.");
        if (_softwareBitmapOperation is not null || _softwareBitmap is not null)
            throw new InvalidOperationException("A WIC software-bitmap operation is already registered.");

        IAsyncOperation<SoftwareBitmap> operation = decoder.GetSoftwareBitmapAsync(
            pixelFormat,
            alphaMode,
            transform,
            orientationMode,
            colorManagementMode);
        RegisterSoftwareBitmapOperation(operation);
        return operation;
    }

    internal void RegisterSoftwareBitmapOperation(IAsyncOperation<SoftwareBitmap> operation)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(operation);
        if (_randomAccessStream is null)
            throw new InvalidOperationException("The WIC source stream must be opened before registering pixel decode.");
        if (_softwareBitmapOperation is not null || _softwareBitmap is not null)
            throw new InvalidOperationException("A WIC software-bitmap operation is already registered.");

        _softwareBitmapOperation = operation;
    }

    /// <summary>Transfers a successfully awaited frame into this owner's custody.</summary>
    internal void AdoptSoftwareBitmap(SoftwareBitmap softwareBitmap)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(softwareBitmap);
        if (_softwareBitmap is not null)
            throw new InvalidOperationException("A WIC decode owner already holds a software bitmap.");
        if (_softwareBitmapOperation is null)
            throw new InvalidOperationException("The software bitmap must come from a registered WIC operation.");

        _softwareBitmap = softwareBitmap;
    }

    /// <summary>
    /// Attempts synchronous retirement after upload. Pending operations may be
    /// retried while their status is still Started or unavailable. Once a
    /// native close/dispose call itself throws, the owner is permanently
    /// quarantined because the native object may already have been closed.
    /// </summary>
    internal void Retire()
    {
        if (Interlocked.CompareExchange(ref _retirementStarted, 1, 0) != 0)
            return;

        if (TryDisposeOwnedResources())
        {
            // This reservation was installed before the factories. Publish an
            // empty record on the immediate-success path to clear its self-slot
            // explicitly; failed paths publish the live owner below.
            _retirementRecord.SetTemporary(0, null);
            _retirementRecord.Publish();
            return;
        }

        _retirementRecord.Publish();
        throw new InvalidOperationException(RetirementPendingMessage);
    }

    /// <summary>
    /// One retry quantum used by NativeResourceRetirement. Failure deliberately
    /// carries no original exception, native wrapper, source, or callback graph.
    /// </summary>
    public void Dispose()
    {
        if (Volatile.Read(ref _retirementStarted) == 0)
            throw new InvalidOperationException("A WIC decode owner cannot retire before its owner requests retirement.");
        if (Volatile.Read(ref _disposed) != 0)
            return;
        if (!TryDisposeOwnedResources())
            throw new InvalidOperationException(RetirementPendingMessage);
    }

    private bool TryDisposeOwnedResources()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return true;
        if (IsQuarantined)
            return false;

        // Preflight every pending operation before closing or disposing any
        // dependency. If Status is still Started or itself fails, preserve the
        // complete ownership set and the process credit for a later retry.
        bool operationsTerminal = true;
        if (_decoderCreationOperation is { } decoderOperation)
            operationsTerminal &= IsTerminal(decoderOperation);
        if (_softwareBitmapOperation is { } softwareBitmapOperation)
            operationsTerminal &= IsTerminal(softwareBitmapOperation);
        if (!operationsTerminal)
            return false;

        // Both operations are known terminal before either Close call. Attempt
        // each independent operation once, even if the first Close throws. A
        // throwing Close makes that operation's state unknowable: do not read
        // Status or invoke Close again, and retain all still-dependent objects.
        bool operationsClosed = true;
        operationsClosed &= TryClose(ref _softwareBitmapOperation);
        operationsClosed &= TryClose(ref _decoderCreationOperation);
        if (!operationsClosed)
        {
            Quarantine();
            return false;
        }

        // Operations are now terminal and closed, so attempt all independent
        // dependent-resource cleanup in order. If any Dispose throws, its
        // native ownership is equally ambiguous; retain the process credit and
        // never retry that object's Dispose call.
        bool nativeResourcesDisposed = true;
        nativeResourcesDisposed &= TryDispose(ref _softwareBitmap);
        bool randomAccessStreamDisposed = TryDispose(ref _randomAccessStream);
        nativeResourcesDisposed &= randomAccessStreamDisposed;
        if (_randomAccessStream is null)
            nativeResourcesDisposed &= TryDispose(ref _sourceStream);

        if (!nativeResourcesDisposed)
        {
            Quarantine();
            return false;
        }

        if (_processDecodeSlot is { } processDecodeSlot)
        {
            try
            {
                processDecodeSlot.Dispose();
                _processDecodeSlot = null;
            }
            catch (Exception failure)
            {
                RecordFailure(failure);
                // A release invariant failure is not safe to repeat: the lease
                // may have been released before the failure surfaced.
                Quarantine();
                return false;
            }
        }

        Volatile.Write(ref _disposed, 1);
        return true;
    }

    private bool IsTerminal<T>(IAsyncOperation<T> operation)
    {
        try
        {
            AsyncStatus status = ((IAsyncInfo)operation).Status;
            return status is AsyncStatus.Completed or AsyncStatus.Canceled or AsyncStatus.Error;
        }
        catch (Exception failure)
        {
            RecordFailure(failure);
            return false;
        }
    }

    private bool TryClose<T>(ref IAsyncOperation<T>? operation)
    {
        if (operation is not { } activeOperation)
            return true;

        try
        {
            ((IAsyncInfo)activeOperation).Close();
            operation = null;
            return true;
        }
        catch (Exception failure)
        {
            RecordFailure(failure);
            Quarantine();
            // Deliberately retain the reference for custody only. No further
            // property access or Close call is permitted after this failure.
            return false;
        }
    }

    private bool TryDispose<T>(ref T? resource) where T : class, IDisposable
    {
        if (resource is not { } activeResource)
            return true;

        try
        {
            activeResource.Dispose();
            resource = null;
            return true;
        }
        catch (Exception failure)
        {
            RecordFailure(failure);
            return false;
        }
    }

    private void Quarantine()
    {
        if (Interlocked.Exchange(ref _quarantined, 1) == 0)
            _retirementRecord.StopSingleSlotRetries();
    }

    private void RecordFailure(Exception failure) =>
        Volatile.Write(ref _lastFailureHResult, failure.HResult);

    private void EnsureActive()
    {
        if (Volatile.Read(ref _retirementStarted) != 0 || Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WicDecodeOperationOwner));
    }
}
