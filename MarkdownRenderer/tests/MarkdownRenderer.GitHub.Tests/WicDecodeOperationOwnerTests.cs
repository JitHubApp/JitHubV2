using MarkdownRenderer.Layout;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class WicDecodeOperationOwnerTests
{
    [Theory]
    [InlineData(AsyncStatus.Completed)]
    [InlineData(AsyncStatus.Canceled)]
    [InlineData(AsyncStatus.Error)]
    public void Retire_ClosesBothTerminalOperationsAndRefundsDecodeSlot(AsyncStatus terminalStatus)
    {
        var retirement = new NativeResourceRetirement(createTimer: false);
        Fixture fixture = CreateFixture(retirement, terminalStatus);

        Assert.True(fixture.SourceStream.CanRead);
        fixture.Owner.Retire();
        fixture.Owner.Retire();

        Assert.Equal(1, fixture.DecoderOperation.CloseCalls);
        Assert.Equal(1, fixture.PixelOperation.CloseCalls);
        Assert.Equal(1, fixture.DecoderOperation.StatusReads);
        Assert.Equal(1, fixture.PixelOperation.StatusReads);
        Assert.Equal(0, fixture.DecoderOperation.CancelCalls);
        Assert.Equal(0, fixture.PixelOperation.CancelCalls);
        Assert.Equal(1, fixture.ProcessSlot.DisposeCalls);
        Assert.False(fixture.Owner.HasSourceStreamsForTests);
        Assert.Equal(0, retirement.PendingResourceCount);
        Assert.Equal(0, retirement.QuarantinedResourceCount);
    }

    [Fact]
    public void StartedOperation_RetainsStreamAndExactDecodeSlotUntilTerminalRetry()
    {
        var retirement = new NativeResourceRetirement(createTimer: false);
        Fixture fixture = CreateFixture(retirement, AsyncStatus.Started);

        InvalidOperationException pending = Assert.Throws<InvalidOperationException>(fixture.Owner.Retire);
        Assert.Equal("A terminal WIC decode resource could not yet be retired.", pending.Message);
        Assert.Null(pending.InnerException);
        Assert.False(fixture.Owner.IsQuarantined);
        Assert.True(fixture.SourceStream.CanRead);
        Assert.Equal(0, fixture.ProcessSlot.DisposeCalls);
        Assert.Equal(1, retirement.PendingResourceCount);
        Assert.Equal(0, retirement.QuarantinedResourceCount);
        Assert.Equal(0, fixture.DecoderOperation.CloseCalls);
        Assert.Equal(0, fixture.PixelOperation.CloseCalls);

        fixture.Owner.Retire(); // The first retirement request is idempotent.
        fixture.DecoderOperation.SetStatus(AsyncStatus.Completed);
        fixture.PixelOperation.SetStatus(AsyncStatus.Completed);
        Assert.True(retirement.DrainOnce());

        Assert.False(fixture.Owner.IsQuarantined);
        Assert.Equal(1, fixture.DecoderOperation.CloseCalls);
        Assert.Equal(1, fixture.PixelOperation.CloseCalls);
        Assert.Equal(1, fixture.ProcessSlot.DisposeCalls);
        Assert.False(fixture.Owner.HasSourceStreamsForTests);
        Assert.Equal(0, retirement.PendingResourceCount);
        Assert.Equal(0, retirement.QuarantinedResourceCount);
    }

    [Fact]
    public void StatusGetterFailure_RetainsCustodyAndRetriesWithoutLeakingExceptionGraph()
    {
        var retirement = new NativeResourceRetirement(createTimer: false);
        Fixture fixture = CreateFixture(retirement, AsyncStatus.Completed);
        var statusFailure = new InvalidOperationException("status access failed");
        fixture.DecoderOperation.StatusFailure = statusFailure;

        InvalidOperationException pending = Assert.Throws<InvalidOperationException>(fixture.Owner.Retire);
        Assert.Equal("A terminal WIC decode resource could not yet be retired.", pending.Message);
        Assert.Null(pending.InnerException);
        Assert.Equal(statusFailure.HResult, fixture.Owner.LastFailureHResult);
        Assert.False(fixture.Owner.IsQuarantined);
        Assert.True(fixture.SourceStream.CanRead);
        Assert.Equal(0, fixture.ProcessSlot.DisposeCalls);
        Assert.Equal(1, retirement.PendingResourceCount);
        Assert.Equal(0, retirement.QuarantinedResourceCount);
        Assert.Equal(0, fixture.DecoderOperation.CloseCalls);
        Assert.Equal(0, fixture.PixelOperation.CloseCalls);
        Assert.Equal(1, fixture.DecoderOperation.StatusReads);
        Assert.Equal(1, fixture.PixelOperation.StatusReads);

        fixture.DecoderOperation.StatusFailure = null;
        Assert.True(retirement.DrainOnce());

        Assert.Equal(2, fixture.DecoderOperation.StatusReads);
        Assert.Equal(2, fixture.PixelOperation.StatusReads);
        Assert.Equal(1, fixture.DecoderOperation.CloseCalls);
        Assert.Equal(1, fixture.PixelOperation.CloseCalls);
        Assert.Equal(1, fixture.ProcessSlot.DisposeCalls);
        Assert.False(fixture.Owner.HasSourceStreamsForTests);
        Assert.Equal(0, retirement.PendingResourceCount);
        Assert.Equal(0, retirement.QuarantinedResourceCount);
    }

    [Fact]
    public void FailedClose_AttemptsBothTerminalClosesThenQuarantinesAllRemainingCustody()
    {
        var retirement = new NativeResourceRetirement(createTimer: false);
        Fixture fixture = CreateFixture(retirement, AsyncStatus.Completed);
        var closeFailure = new InvalidOperationException("close state is unknown");
        fixture.PixelOperation.CloseFailure = closeFailure;

        InvalidOperationException pending = Assert.Throws<InvalidOperationException>(fixture.Owner.Retire);
        Assert.Equal("A terminal WIC decode resource could not yet be retired.", pending.Message);
        Assert.Null(pending.InnerException);
        Assert.Equal(closeFailure.HResult, fixture.Owner.LastFailureHResult);
        Assert.True(fixture.Owner.IsQuarantined);
        Assert.Equal(1, fixture.PixelOperation.CloseCalls);
        Assert.Equal(1, fixture.DecoderOperation.CloseCalls);
        Assert.Equal(1, fixture.PixelOperation.StatusReads);
        Assert.Equal(1, fixture.DecoderOperation.StatusReads);
        Assert.Equal(0, fixture.ProcessSlot.DisposeCalls);
        Assert.True(fixture.SourceStream.CanRead);
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.PendingResourceCount);
        Assert.Equal(1, retirement.QuarantinedResourceCount);

        Assert.False(retirement.DrainOnce());
        fixture.Owner.Retire();
        Assert.Equal(1, fixture.PixelOperation.CloseCalls);
        Assert.Equal(1, fixture.DecoderOperation.CloseCalls);
        Assert.Equal(1, fixture.PixelOperation.StatusReads);
        Assert.Equal(1, fixture.DecoderOperation.StatusReads);
        Assert.Equal(0, fixture.ProcessSlot.DisposeCalls);
        Assert.True(fixture.SourceStream.CanRead);
        Assert.Equal(1, retirement.QuarantinedResourceCount);
    }

    [Fact]
    public void CloseFailureDuringRetirementRetryQuarantinesRecordWithoutFurtherAsyncInfoAccess()
    {
        var retirement = new NativeResourceRetirement(createTimer: false);
        Fixture fixture = CreateFixture(retirement, AsyncStatus.Started);

        InvalidOperationException pending = Assert.Throws<InvalidOperationException>(fixture.Owner.Retire);
        Assert.Equal("A terminal WIC decode resource could not yet be retired.", pending.Message);
        Assert.Null(pending.InnerException);
        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(1, retirement.PendingResourceCount);
        Assert.True(fixture.SourceStream.CanRead);
        Assert.Equal(0, fixture.ProcessSlot.DisposeCalls);

        fixture.DecoderOperation.SetStatus(AsyncStatus.Completed);
        fixture.PixelOperation.SetStatus(AsyncStatus.Completed);
        var closeFailure = new InvalidOperationException("retry close state is unknown");
        fixture.PixelOperation.CloseFailure = closeFailure;

        Assert.False(retirement.DrainOnce());
        Assert.Equal(closeFailure.HResult, fixture.Owner.LastFailureHResult);
        Assert.True(fixture.Owner.IsQuarantined);
        Assert.Equal(2, fixture.DecoderOperation.StatusReads);
        Assert.Equal(2, fixture.PixelOperation.StatusReads);
        Assert.Equal(1, fixture.PixelOperation.CloseCalls);
        Assert.Equal(1, fixture.DecoderOperation.CloseCalls);
        Assert.True(fixture.Owner.HasSourceStreamsForTests);
        Assert.Equal(0, fixture.ProcessSlot.DisposeCalls);
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.PendingResourceCount);
        Assert.Equal(1, retirement.QuarantinedResourceCount);

        Assert.False(retirement.DrainOnce());
        fixture.Owner.Retire();
        Assert.Equal(2, fixture.DecoderOperation.StatusReads);
        Assert.Equal(2, fixture.PixelOperation.StatusReads);
        Assert.Equal(1, fixture.PixelOperation.CloseCalls);
        Assert.Equal(1, fixture.DecoderOperation.CloseCalls);
        Assert.Equal(0, fixture.ProcessSlot.DisposeCalls);
        Assert.Equal(1, retirement.QuarantinedResourceCount);
    }

    [Fact]
    public void DuplicateOperationRegistrationAndDecodeSlotAdoptionAreRejectedWithoutLosingOriginals()
    {
        var retirement = new NativeResourceRetirement(createTimer: false);
        var owner = WicDecodeOperationOwner.Create(retirement);
        var unownedDecoderOperation = new FakeAsyncOperation<BitmapDecoder>(AsyncStatus.Completed);
        Assert.Throws<InvalidOperationException>(
            () => owner.RegisterDecoderCreationOperation(unownedDecoderOperation));

        owner.OpenSourceStream([1, 2, 3]);
        var decoderOperation = new FakeAsyncOperation<BitmapDecoder>(AsyncStatus.Completed);
        var duplicateDecoderOperation = new FakeAsyncOperation<BitmapDecoder>(AsyncStatus.Completed);
        owner.RegisterDecoderCreationOperation(decoderOperation);
        Assert.Throws<InvalidOperationException>(
            () => owner.RegisterDecoderCreationOperation(duplicateDecoderOperation));

        var pixelOperation = new FakeAsyncOperation<SoftwareBitmap>(AsyncStatus.Completed);
        var duplicatePixelOperation = new FakeAsyncOperation<SoftwareBitmap>(AsyncStatus.Completed);
        owner.RegisterSoftwareBitmapOperation(pixelOperation);
        Assert.Throws<InvalidOperationException>(
            () => owner.RegisterSoftwareBitmapOperation(duplicatePixelOperation));

        var firstSlot = new TrackingProcessSlot();
        IDisposable? firstSlotCandidate = firstSlot;
        owner.AdoptProcessDecodeSlot(ref firstSlotCandidate);
        Assert.Null(firstSlotCandidate);

        var duplicateSlot = new TrackingProcessSlot();
        IDisposable? duplicateSlotCandidate = duplicateSlot;
        Assert.Throws<InvalidOperationException>(
            () => owner.AdoptProcessDecodeSlot(ref duplicateSlotCandidate));
        Assert.Same(duplicateSlot, duplicateSlotCandidate);
        Assert.Equal(0, duplicateSlot.DisposeCalls);

        owner.Retire();
        duplicateSlot.Dispose();
        Assert.Equal(1, firstSlot.DisposeCalls);
        Assert.Equal(1, duplicateSlot.DisposeCalls);
        Assert.Equal(1, decoderOperation.CloseCalls);
        Assert.Equal(0, unownedDecoderOperation.CloseCalls);
        Assert.Equal(0, duplicateDecoderOperation.CloseCalls);
        Assert.Equal(0, duplicatePixelOperation.CloseCalls);
        Assert.False(owner.HasSourceStreamsForTests);
        Assert.Equal(0, retirement.PendingResourceCount);
        Assert.Equal(0, retirement.QuarantinedResourceCount);
    }

    private static Fixture CreateFixture(
        NativeResourceRetirement retirement,
        AsyncStatus status)
    {
        var owner = WicDecodeOperationOwner.Create(retirement);
        IRandomAccessStream sourceStream = owner.OpenSourceStream([0x31, 0x32, 0x33]);
        var processSlot = new TrackingProcessSlot();
        IDisposable? candidate = processSlot;
        owner.AdoptProcessDecodeSlot(ref candidate);
        Assert.Null(candidate);

        var decoderOperation = new FakeAsyncOperation<BitmapDecoder>(status);
        var pixelOperation = new FakeAsyncOperation<SoftwareBitmap>(status);
        owner.RegisterDecoderCreationOperation(decoderOperation);
        owner.RegisterSoftwareBitmapOperation(pixelOperation);
        return new Fixture(owner, sourceStream, processSlot, decoderOperation, pixelOperation);
    }

    private sealed record Fixture(
        WicDecodeOperationOwner Owner,
        IRandomAccessStream SourceStream,
        TrackingProcessSlot ProcessSlot,
        FakeAsyncOperation<BitmapDecoder> DecoderOperation,
        FakeAsyncOperation<SoftwareBitmap> PixelOperation);

    private sealed class TrackingProcessSlot : IDisposable
    {
        private int _disposeCalls;

        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public void Dispose() => Interlocked.Increment(ref _disposeCalls);
    }

    private sealed class FakeAsyncOperation<TResult>(AsyncStatus initialStatus) : IAsyncOperation<TResult>
    {
        private int _statusReads;
        private int _closeCalls;
        private int _cancelCalls;
        private int _closeInvoked;
        private AsyncStatus _status = initialStatus;

        internal int StatusReads => Volatile.Read(ref _statusReads);
        internal int CloseCalls => Volatile.Read(ref _closeCalls);
        internal int CancelCalls => Volatile.Read(ref _cancelCalls);
        internal Exception? StatusFailure { get; set; }
        internal Exception? CloseFailure { get; set; }

        public uint Id => 1;

        public AsyncStatus Status
        {
            get
            {
                Interlocked.Increment(ref _statusReads);
                if (Volatile.Read(ref _closeInvoked) != 0)
                    throw new InvalidOperationException("IAsyncInfo accessed after Close was invoked.");
                if (StatusFailure is { } failure)
                    throw failure;
                return _status;
            }
        }

        public Exception ErrorCode => new InvalidOperationException("No async operation error was requested.");

        public AsyncOperationCompletedHandler<TResult> Completed { get; set; } = null!;

        public TResult GetResults() => throw new NotSupportedException("The test fake has no operation result.");

        public void Cancel() => Interlocked.Increment(ref _cancelCalls);

        public void Close()
        {
            Interlocked.Increment(ref _closeCalls);
            if (Interlocked.Exchange(ref _closeInvoked, 1) != 0)
                throw new InvalidOperationException("The fake operation cannot be accessed after Close.");
            if (CloseFailure is { } failure)
                throw failure;
        }

        internal void SetStatus(AsyncStatus status) => _status = status;
    }
}
