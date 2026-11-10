using System.Buffers;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Images;

/// <summary>
/// SVG provider for the source-lease tests: it gates the first and second open, the render
/// and the document disposal on test-controlled signals, and counts the provider-owned
/// source snapshot bytes it still retains.
/// </summary>
internal sealed class SourceLeaseGatedSvgRenderer : IMarkdownSvgRenderer
{
    private readonly bool _blockDocumentDispose;
    private int _prepareSourceCount;
    private int _openCount;
    private int _documentDisposeCount;
    private int _retainedDocumentSourceBytes;

    private readonly bool _blockRender;
    private readonly bool _distinctPreparation;
    private int _renderCount;

    internal SourceLeaseGatedSvgRenderer(bool blockDocumentDispose = false, bool blockRender = false,
        bool distinctPreparation = false)
    {
        _blockDocumentDispose = blockDocumentDispose;
        _blockRender = blockRender;
        _distinctPreparation = distinctPreparation;
    }

    internal TaskCompletionSource RenderStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource RenderAwaitRegistered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource RenderCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ReleaseRender { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int RenderCount => Volatile.Read(ref _renderCount);

    internal TaskCompletionSource FirstOpenStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource FirstOpenCanceled { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource FirstOpenReturned { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource FirstOpenContinuationReturned { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource SecondOpenStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ReleaseFirstOpen { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ReleaseSecondOpen { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource DocumentDisposeStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource DocumentDisposeCompleted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource ReleaseDocumentDispose { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    internal int PrepareSourceCount => Volatile.Read(ref _prepareSourceCount);
    internal int OpenCount => Volatile.Read(ref _openCount);
    internal int DocumentDisposeCount => Volatile.Read(ref _documentDisposeCount);
    internal int RetainedDocumentSourceBytes => Volatile.Read(ref _retainedDocumentSourceBytes);

    public long CacheGeneration => 0;

    public MarkdownSvgSourcePreparation PrepareSource(
        byte[] source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _prepareSourceCount);
        return MarkdownSvgSourcePreparation.Admit(_distinctPreparation ? (byte[])source.Clone() : source);
    }

    public ValueTask<IMarkdownSvgDocument> OpenAsync(
        MarkdownSvgOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        int open = Interlocked.Increment(ref _openCount);
        CancellationTokenRegistration registration = cancellationToken.Register(
            () => FirstOpenCanceled.TrySetResult());
        TaskCompletionSource release = open == 1 ? ReleaseFirstOpen : ReleaseSecondOpen;
        if (open == 1)
            FirstOpenStarted.TrySetResult();
        else
            SecondOpenStarted.TrySetResult();

        // The provider deliberately ignores cancellation until its actual
        // OpenAsync transaction returns; it may still be reading request bytes.
        var pending = new PendingOpenValueTaskSource(
            this, request, release.Task, registration, open);
        return pending.AsValueTask();
    }

    private sealed class PendingOpenValueTaskSource :
        System.Threading.Tasks.Sources.IValueTaskSource<IMarkdownSvgDocument>
    {
        private readonly SourceLeaseGatedSvgRenderer _owner;
        private System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<IMarkdownSvgDocument> _core;
        private MarkdownSvgOpenRequest? _request;
        private readonly Task _release;
        private readonly TaskCompletionSource _consumerRegistered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registration;
        private readonly int _open;

        internal PendingOpenValueTaskSource(
            SourceLeaseGatedSvgRenderer owner,
            MarkdownSvgOpenRequest request,
            Task release,
            CancellationTokenRegistration registration,
            int open)
        {
            _owner = owner;
            _request = request;
            _release = release;
            _registration = registration;
            _open = open;
            _core = new System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<IMarkdownSvgDocument>
            {
                RunContinuationsAsynchronously = true,
            };
            _ = CompleteAfterReleaseAsync();
        }

        internal ValueTask<IMarkdownSvgDocument> AsValueTask() => new(this, _core.Version);

        public System.Threading.Tasks.Sources.ValueTaskSourceStatus GetStatus(short token) =>
            _core.GetStatus(token);

        public IMarkdownSvgDocument GetResult(short token) => _core.GetResult(token);

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            System.Threading.Tasks.Sources.ValueTaskSourceOnCompletedFlags flags)
        {
            var registration = new ContinuationRegistration(this, continuation, state);
            _core.OnCompleted(
                static callbackState =>
                {
                    var callback = (ContinuationRegistration)callbackState!;
                    try
                    {
                        callback.Continuation(callback.State);
                    }
                    finally
                    {
                        if (callback.Owner._open == 1)
                            callback.Owner._owner.FirstOpenContinuationReturned.TrySetResult();
                    }
                },
                registration,
                token,
                flags);
            _consumerRegistered.TrySetResult();
        }

        private async Task CompleteAfterReleaseAsync()
        {
            try
            {
                await _release.ConfigureAwait(false);
                // ReleaseFirstOpen can race the caller's first await. Keep
                // the provider completion pending until the source-backed
                // ValueTask has a registered consumer callback, so the
                // callback-return barrier below cannot be skipped.
                await _consumerRegistered.Task.ConfigureAwait(false);
                MarkdownSvgOpenRequest? request = _request;
                if (request is null)
                    throw new InvalidOperationException("The pending SVG open lost its request.");

                byte[] providerSnapshot = request.SanitizedSvgBytes.ToArray();
                // The provider owns only its independent copy after this
                // point. Clear both the field and this local before waking
                // any consumer continuation.
                _request = null;
                request = null;
                Interlocked.Add(ref _owner._retainedDocumentSourceBytes, providerSnapshot.Length);
                var document = new GatedSvgDocument(_owner, providerSnapshot);
                if (_open == 1)
                    _owner.FirstOpenReturned.TrySetResult();
                _core.SetResult(document);
            }
            catch (Exception exception)
            {
                _request = null;
                _core.SetException(exception);
            }
            finally
            {
                _registration.Dispose();
                _registration = default;
            }
        }

        private sealed record ContinuationRegistration(
            PendingOpenValueTaskSource Owner,
            Action<object?> Continuation,
            object? State);
    }

    internal void ReleaseAllOpens()
    {
        ReleaseFirstOpen.TrySetResult();
        ReleaseSecondOpen.TrySetResult();
        ReleaseRender.TrySetResult();
        ReleaseDocumentDisposals();
    }

    internal void ReleaseDocumentDisposals() => ReleaseDocumentDispose.TrySetResult();

    private void RecordDocumentDisposal(int retainedBytes)
    {
        Interlocked.Add(ref _retainedDocumentSourceBytes, -retainedBytes);
        Interlocked.Increment(ref _documentDisposeCount);
        DocumentDisposeCompleted.TrySetResult();
    }

    private sealed class GatedSvgDocument(
        SourceLeaseGatedSvgRenderer owner,
        byte[] sourceSnapshot) : IMarkdownSvgDocument
    {
        private byte[]? _sourceSnapshot = sourceSnapshot;

        public MarkdownSvgDocumentInfo Info { get; } = new(
            IntrinsicWidthDips: 24,
            IntrinsicHeightDips: 12,
            IntrinsicAspectRatio: 2);

        public ValueTask<MarkdownSvgRaster> RenderAsync(
            MarkdownSvgRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref owner._renderCount);
            owner.RenderStarted.TrySetResult();
            if (owner._blockRender)
            {
                var pending = new PendingRenderValueTaskSource(owner, request, cancellationToken);
                return pending.AsValueTask();
            }

            using CancellationTokenRegistration registration = cancellationToken.Register(
                () => owner.RenderCanceled.TrySetResult());
            cancellationToken.ThrowIfCancellationRequested();
            int width = request.TileRegion?.Width ?? request.TargetWidthPixels;
            int height = request.TileRegion?.Height ?? request.TargetHeightPixels;
            int length = checked(width * height * 4);
            IMemoryOwner<byte> memory = MemoryPool<byte>.Shared.Rent(length);
            bool ownershipTransferred = false;
            try
            {
                memory.Memory.Span[..length].Clear();
                var raster = new MarkdownSvgRaster(
                    memory,
                    length,
                    width,
                    height,
                    checked(width * 4),
                    request.PixelFormat);
                ownershipTransferred = true;
                return ValueTask.FromResult(raster);
            }
            finally
            {
                if (!ownershipTransferred)
                    memory.Dispose();
            }
        }

        private sealed class PendingRenderValueTaskSource :
            System.Threading.Tasks.Sources.IValueTaskSource<MarkdownSvgRaster>
        {
            private readonly SourceLeaseGatedSvgRenderer _owner;
            private System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<MarkdownSvgRaster> _core;
            private readonly CancellationTokenRegistration _registration;

            internal PendingRenderValueTaskSource(
                SourceLeaseGatedSvgRenderer owner,
                MarkdownSvgRenderRequest request,
                CancellationToken cancellationToken)
            {
                _owner = owner;
                _core = new System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<MarkdownSvgRaster>
                {
                    RunContinuationsAsynchronously = true,
                };
                _registration = cancellationToken.Register(
                    static state => ((SourceLeaseGatedSvgRenderer)state!).RenderCanceled.TrySetResult(),
                    owner);
                _ = CompleteAfterReleaseAsync(request, cancellationToken);
            }

            internal ValueTask<MarkdownSvgRaster> AsValueTask() => new(this, _core.Version);

            public System.Threading.Tasks.Sources.ValueTaskSourceStatus GetStatus(short token) =>
                _core.GetStatus(token);

            public MarkdownSvgRaster GetResult(short token) => _core.GetResult(token);

            public void OnCompleted(
                Action<object?> continuation,
                object? state,
                short token,
                System.Threading.Tasks.Sources.ValueTaskSourceOnCompletedFlags flags)
            {
                _core.OnCompleted(continuation, state, token, flags);
                _owner.RenderAwaitRegistered.TrySetResult();
            }

            private async Task CompleteAfterReleaseAsync(
                MarkdownSvgRenderRequest request,
                CancellationToken cancellationToken)
            {
                try
                {
                    await _owner.ReleaseRender.Task.ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    int width = request.TileRegion?.Width ?? request.TargetWidthPixels;
                    int height = request.TileRegion?.Height ?? request.TargetHeightPixels;
                    int length = checked(width * height * 4);
                    IMemoryOwner<byte> memory = MemoryPool<byte>.Shared.Rent(length);
                    bool ownershipTransferred = false;
                    try
                    {
                        memory.Memory.Span[..length].Clear();
                        var raster = new MarkdownSvgRaster(
                            memory,
                            length,
                            width,
                            height,
                            checked(width * 4),
                            request.PixelFormat);
                        ownershipTransferred = true;
                        _core.SetResult(raster);
                    }
                    finally
                    {
                        if (!ownershipTransferred)
                            memory.Dispose();
                    }
                }
                catch (Exception exception)
                {
                    _core.SetException(exception);
                }
                finally
                {
                    _registration.Dispose();
                }
            }
        }

        public void Dispose()
        {
            owner.DocumentDisposeStarted.TrySetResult();
            if (owner._blockDocumentDispose)
                owner.ReleaseDocumentDispose.Task.GetAwaiter().GetResult();

            int retainedBytes = Interlocked.Exchange(ref _sourceSnapshot, null)?.Length ?? 0;
            owner.RecordDocumentDisposal(retainedBytes);
        }
    }
}
