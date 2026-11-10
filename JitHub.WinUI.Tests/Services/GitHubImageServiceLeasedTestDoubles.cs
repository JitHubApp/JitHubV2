using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JitHub.Core.Services.Images;
using JitHub.Markdown.Images.Caching;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

/// <summary>
/// Admissions, HTTP doubles and GC helpers shared by the leased image service test classes.
/// </summary>
internal static class GitHubImageServiceLeasedTestDoubles
{
    internal static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    internal static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool IsAlive(WeakReference<byte[]> reference) => reference.TryGetTarget(out _);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool AreAnyBuffersAlive(WeakReference<byte[]>[] references)
    {
        foreach (WeakReference<byte[]> reference in references)
        {
            if (reference.TryGetTarget(out _))
                return true;
        }

        return false;
    }

    internal sealed record RejectedSvgRun(
        GitHubImageService Service,
        SpillCaptureImageResponseHandler Handler,
        TrackingAdmission Admission,
        Task<GitHubCachedImage?> Task);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static (GitHubImageSourceLease[] Handles, WeakReference<byte[]> Bytes)
        CreateDisposedHandles()
    {
        byte[] sourceBytes = new byte[4096];
        var weakBytes = new WeakReference<byte[]>(sourceBytes);
        var root = new GitHubImageSourceLease(
            new GitHubCachedImage("test", sourceBytes, "image/png", false),
            new TrackingDisposable());
        GitHubImageSourceLease retained = root.Retain();
        root.Dispose();
        retained.Dispose();
        return ([root, retained], weakBytes);
    }

    internal sealed class TrackingAdmission(long maximumReservationBytes = GitHubImageService.MaxImageBytes)
        : IMarkdownImageSourceByteAdmission
    {
        private long _activeBytes;
        private long _lastReservationBytes;
        private readonly TaskCompletionSource _released = NewSignal();

        public bool IsSpeculative => false;
        public long MaximumReservationBytes => maximumReservationBytes;
        internal long ActiveBytes => Interlocked.Read(ref _activeBytes);
        internal long LastReservationBytes => Interlocked.Read(ref _lastReservationBytes);

        public ValueTask<IDisposable> ReserveAsync(long bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes <= 0 || bytes > MaximumReservationBytes)
                throw new ArgumentOutOfRangeException(nameof(bytes));
            Interlocked.Add(ref _activeBytes, bytes);
            Interlocked.Exchange(ref _lastReservationBytes, bytes);
            return ValueTask.FromResult<IDisposable>(new Reservation(this, bytes));
        }

        internal Task WaitUntilReleasedAsync() => _released.Task;

        private sealed class Reservation(TrackingAdmission owner, long bytes) : IDisposable
        {
            private TrackingAdmission? _owner = owner;

            public void Dispose()
            {
                TrackingAdmission? current = Interlocked.Exchange(ref _owner, null);
                if (current is null)
                    return;

                Interlocked.Add(ref current._activeBytes, -bytes);
                current._released.TrySetResult();
            }
        }
    }

    internal sealed class BlockingFinalGrantAdmission(long maximumReservationBytes)
        : IMarkdownImageSourceByteAdmission
    {
        private readonly TaskCompletionSource _releaseFinalGrant = NewSignal();
        private long _activeBytes;
        private long _finalGrantBytes;
        private long _lastGrantedBytes;
        private int _reserveCount;

        internal TaskCompletionSource FinalGrantRequested { get; } = NewSignal();
        internal long ActiveBytes => Interlocked.Read(ref _activeBytes);
        internal long FinalGrantBytes => Interlocked.Read(ref _finalGrantBytes);
        internal long LastGrantedBytes => Interlocked.Read(ref _lastGrantedBytes);

        public bool IsSpeculative => false;
        public long MaximumReservationBytes => maximumReservationBytes;

        internal TaskCompletionSource ReleaseFinalGrant => _releaseFinalGrant;

        public ValueTask<IDisposable> ReserveAsync(long bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes <= 0 || bytes > MaximumReservationBytes)
                throw new ArgumentOutOfRangeException(nameof(bytes));

            int request = Interlocked.Increment(ref _reserveCount);
            if (request == 1)
            {
                Interlocked.Add(ref _activeBytes, bytes);
                Interlocked.Exchange(ref _lastGrantedBytes, bytes);
                return ValueTask.FromResult<IDisposable>(new Reservation(this, bytes));
            }

            if (request != 2)
                throw new InvalidOperationException("The spill test expected exactly one final admission request.");

            Interlocked.Exchange(ref _finalGrantBytes, bytes);
            FinalGrantRequested.TrySetResult();
            return new ValueTask<IDisposable>(GrantFinalAsync(bytes, cancellationToken));
        }

        private async Task<IDisposable> GrantFinalAsync(long bytes, CancellationToken cancellationToken)
        {
            await _releaseFinalGrant.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _activeBytes, bytes);
            Interlocked.Exchange(ref _lastGrantedBytes, bytes);
            return new Reservation(this, bytes);
        }

        private sealed class Reservation(BlockingFinalGrantAdmission owner, long bytes) : IDisposable
        {
            private BlockingFinalGrantAdmission? _owner = owner;

            public void Dispose()
            {
                BlockingFinalGrantAdmission? current = Interlocked.Exchange(ref _owner, null);
                if (current is not null)
                    Interlocked.Add(ref current._activeBytes, -bytes);
            }
        }
    }

    internal sealed class TrackingWorkAdmission :
        IMarkdownImageSourceByteAdmission,
        IMarkdownImageSourceWorkTracker
    {
        private readonly TrackingAdmission _bytes = new();
        private readonly object _gate = new();
        private readonly List<Task> _trackedWork = [];
        private bool _trackingClosed;
        private int _trackWorkCount;

        public bool IsSpeculative => _bytes.IsSpeculative;
        public long MaximumReservationBytes => _bytes.MaximumReservationBytes;
        internal long ActiveBytes => _bytes.ActiveBytes;
        internal int TrackWorkCount => Volatile.Read(ref _trackWorkCount);

        internal Task[] TrackedWork
        {
            get
            {
                lock (_gate)
                    return [.. _trackedWork];
            }
        }

        public ValueTask<IDisposable> ReserveAsync(long bytes, CancellationToken cancellationToken) =>
            _bytes.ReserveAsync(bytes, cancellationToken);

        public void TrackWork(Task completion)
        {
            ArgumentNullException.ThrowIfNull(completion);
            Interlocked.Increment(ref _trackWorkCount);
            lock (_gate)
            {
                if (_trackingClosed)
                    throw new InvalidOperationException("The test work-tracking scope is closed.");
                _trackedWork.Add(completion);
            }
        }

        internal void CloseTracking()
        {
            lock (_gate)
                _trackingClosed = true;
        }
    }

    internal sealed class TrackingDisposable : IDisposable
    {
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    internal sealed class ImageResponseHandler(byte[] bytes) : HttpMessageHandler
    {
        private int _requestCount;
        internal int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _requestCount);
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    internal sealed class SpillCaptureImageResponseHandler(
        byte[] bytes,
        string contentType = "image/png",
        bool knownLength = false) : HttpMessageHandler
    {
        internal SpillCaptureImageContent? Content { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = new SpillCaptureImageContent(bytes, knownLength);
            Content = content;
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    internal sealed class SpillCaptureImageContent(byte[] bytes, bool knownLength = false) : HttpContent
    {
        internal SpillCaptureImageStream Stream { get; } = new(bytes);

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(bytes, 0, bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = knownLength ? bytes.Length : 0;
            return knownLength;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(Stream);
    }

    internal sealed class SpillCaptureImageStream(byte[] bytes) : Stream
    {
        private readonly List<WeakReference<byte[]>> _destinationBuffers = [];
        private int _position;

        internal WeakReference<byte[]>[] CapturedBuffers => [.. _destinationBuffers];

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RememberDestination(destination);
            int read = Math.Min(destination.Length, bytes.Length - _position);
            bytes.AsMemory(_position, read).CopyTo(destination);
            _position += read;
            return ValueTask.FromResult(read);
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void RememberDestination(Memory<byte> destination)
        {
            if (!MemoryMarshal.TryGetArray(
                    (ReadOnlyMemory<byte>)destination,
                    out ArraySegment<byte> segment) ||
                segment.Array is not { } array)
            {
                return;
            }

            foreach (WeakReference<byte[]> existingReference in _destinationBuffers)
            {
                if (existingReference.TryGetTarget(out byte[]? existing) &&
                    ReferenceEquals(existing, array))
                {
                    return;
                }
            }

            _destinationBuffers.Add(new WeakReference<byte[]>(array));
        }
    }

    internal sealed class UnknownLengthImageResponseHandler(byte[] bytes, Action onFirstRead) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new UnknownLengthChunkedImageContent(bytes, onFirstRead)
            });
        }
    }

    internal sealed class UnknownLengthChunkedImageContent(byte[] bytes, Action onFirstRead) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(bytes, 0, bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new ChunkedReadStream(bytes, onFirstRead));
    }

    internal sealed class ChunkedReadStream(byte[] bytes, Action onFirstRead) : Stream
    {
        private int _position;
        private int _readObserved;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ObserveRead();
            int read = Math.Min(Math.Min(count, 3), bytes.Length - _position);
            bytes.AsSpan(_position, read).CopyTo(buffer.AsSpan(offset, read));
            _position += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveRead();
            int read = Math.Min(Math.Min(buffer.Length, 3), bytes.Length - _position);
            bytes.AsMemory(_position, read).CopyTo(buffer);
            _position += read;
            return ValueTask.FromResult(read);
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void ObserveRead()
        {
            if (Interlocked.Exchange(ref _readObserved, 1) == 0)
                onFirstRead();
        }
    }

    internal sealed class RevalidationHandler(byte[] bytes, HttpStatusCode refreshStatus) : HttpMessageHandler
    {
        private int _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _requestCount) == 1)
            {
                var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }

            return Task.FromResult(new HttpResponseMessage(refreshStatus));
        }
    }
}
