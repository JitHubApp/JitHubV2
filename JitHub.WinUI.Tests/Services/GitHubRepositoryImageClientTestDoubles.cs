using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

/// <summary>
/// Content builders, reader runs, admissions and streams shared by the repository image
/// client tests.
/// </summary>
internal static class GitHubRepositoryImageClientTestDoubles
{
    internal static StreamContent CreateContent(
        byte[] bytes,
        long? length,
        string? contentType = null,
        int chunkSize = int.MaxValue,
        Action? onRead = null) =>
        CreateContent(new TrackingReadStream(bytes, chunkSize, onRead: onRead), length, contentType);

    internal static StreamContent CreateContent(
        TrackingReadStream stream,
        long? length,
        string? contentType = null)
    {
        StreamContent content = new(stream);
        if (length is long declaredLength)
            content.Headers.ContentLength = declaredLength;
        if (contentType is not null)
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return content;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static ReaderRun StartTruncatedKnownLengthRead()
    {
        TrackingAdmission admission = new(16);
        TrackingReadStream stream = new([1, 2], onRead: admission.RecordRead);
        StreamContent content = CreateContent(stream, length: 4);
        Task<(byte[] Bytes, IDisposable? Lease)> task = AdmittedImageContentReader.ReadAsync(
            content,
            maximumBytes: 8,
            admission,
            CancellationToken.None);
        return new ReaderRun(task, stream, admission, content);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static ReaderRun StartChunkedAdmissionFailure(bool speculative)
    {
        TrackingAdmission admission = new(4, speculative);
        TrackingReadStream stream = new(new byte[32], chunkSize: 2, onRead: admission.RecordRead);
        StreamContent content = CreateContent(stream, length: null);
        Task<(byte[] Bytes, IDisposable? Lease)> task = AdmittedImageContentReader.ReadAsync(
            content,
            maximumBytes: 64,
            admission,
            CancellationToken.None);
        return new ReaderRun(task, stream, admission, content);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static ReaderRun StartChunkedShortRead()
    {
        TrackingAdmission admission = new(16);
        TrackingReadStream stream = new([5, 4, 3, 2, 1], chunkSize: 2, onRead: admission.RecordRead);
        StreamContent content = CreateContent(stream, length: null);
        Task<(byte[] Bytes, IDisposable? Lease)> task = AdmittedImageContentReader.ReadAsync(
            content,
            maximumBytes: 8,
            admission,
            CancellationToken.None);
        return new ReaderRun(task, stream, admission, content);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static ReaderRun StartChunkedFinalAdmissionWait(CancellationToken cancellationToken)
    {
        TrackingAdmission admission = new(64, blockSecondReservation: true);
        TrackingReadStream stream = new(new byte[40], onRead: admission.RecordRead);
        StreamContent content = CreateContent(stream, length: null);
        Task<(byte[] Bytes, IDisposable? Lease)> task = AdmittedImageContentReader.ReadAsync(
            content,
            maximumBytes: 64,
            admission,
            cancellationToken);
        return new ReaderRun(task, stream, admission, content);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static ReaderRun StartChunkedSpoolRead()
    {
        TrackingAdmission admission = new(64);
        TrackingReadStream stream = new(new byte[40], onRead: admission.RecordRead);
        StreamContent content = CreateContent(stream, length: null);
        Task<(byte[] Bytes, IDisposable? Lease)> task = AdmittedImageContentReader.ReadAsync(
            content,
            maximumBytes: 64,
            admission,
            CancellationToken.None);
        return new ReaderRun(task, stream, admission, content);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void AssertCollected(DestinationBuffer[] destinations)
    {
        foreach (DestinationBuffer destination in destinations)
        {
            for (int attempt = 0; attempt < 5 && IsAlive(destination.Buffer); attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            Assert.False(IsAlive(destination.Buffer),
                $"A {destination.RequestedLength}-byte read destination remained rooted after completion.");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool IsAlive(WeakReference<byte[]> reference) => reference.TryGetTarget(out _);

    internal sealed record ReaderRun(
        Task<(byte[] Bytes, IDisposable? Lease)> Task,
        TrackingReadStream Stream,
        TrackingAdmission Admission,
        StreamContent Content)
    {
        public DestinationBuffer[] DestinationBuffers => Stream.DestinationBuffers;
    }

    internal sealed record DestinationBuffer(WeakReference<byte[]> Buffer, int RequestedLength);

    internal sealed class TrackingAdmission(
        long maximumReservationBytes,
        bool speculative = false,
        bool blockSecondReservation = false) : IMarkdownImageSourceByteAdmission
    {
        private long _reservedBytes;
        private int _reserveCalls;
        private int _invalidReleaseCount;
        private long _lastRequestedBytes;
        private long _reservedBeforeSecondRequest;
        private bool _readStartedUnderReservation = true;
        private readonly bool _blockSecondReservation = blockSecondReservation;
        private readonly TaskCompletionSource _secondReservationStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsSpeculative { get; } = speculative;

        public long MaximumReservationBytes { get; } = maximumReservationBytes;

        public long ReservedBytes => Interlocked.Read(ref _reservedBytes);

        public int ReserveCalls => Volatile.Read(ref _reserveCalls);

        public int InvalidReleaseCount => Volatile.Read(ref _invalidReleaseCount);

        public long LastRequestedBytes => Interlocked.Read(ref _lastRequestedBytes);

        public long ReservedBeforeSecondRequest => Interlocked.Read(ref _reservedBeforeSecondRequest);

        public bool ReadStartedUnderReservation => _readStartedUnderReservation;

        public Task SecondReservationStarted => _secondReservationStarted.Task;

        public ValueTask<IDisposable> ReserveAsync(long bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes <= 0 || bytes > MaximumReservationBytes)
                throw new InvalidOperationException("The test admission received an invalid reservation request.");

            int call = Interlocked.Increment(ref _reserveCalls);
            if (call == 2)
                Interlocked.Exchange(ref _reservedBeforeSecondRequest, ReservedBytes);
            Interlocked.Exchange(ref _lastRequestedBytes, bytes);
            if (call == 2 && _blockSecondReservation)
            {
                _secondReservationStarted.TrySetResult();
                return new ValueTask<IDisposable>(WaitForCancellationAsync(cancellationToken));
            }

            Interlocked.Add(ref _reservedBytes, bytes);
            return ValueTask.FromResult<IDisposable>(new Reservation(this, bytes));
        }

        private static async Task<IDisposable> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The test reservation should only finish by cancellation.");
        }

        public void RecordRead()
        {
            if (ReservedBytes <= 0)
                _readStartedUnderReservation = false;
        }

        private void Release(long bytes)
        {
            long remaining = Interlocked.Add(ref _reservedBytes, -bytes);
            if (remaining < 0)
                Interlocked.Increment(ref _invalidReleaseCount);
        }

        private sealed class Reservation(TrackingAdmission owner, long bytes) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    owner.Release(bytes);
            }
        }
    }

    internal sealed class RequestRecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request));
        }
    }

    internal sealed class TrackingReadStream : Stream
    {
        private readonly byte[] _bytes;
        private readonly int _chunkSize;
        private readonly bool _blockReads;
        private readonly Action? _onRead;
        private readonly List<DestinationBuffer> _destinationBuffers = [];
        private int _position;
        private long _bytesRead;

        public TrackingReadStream(
            byte[]? bytes = null,
            int chunkSize = int.MaxValue,
            bool blockReads = false,
            Action? onRead = null)
        {
            _bytes = bytes ?? [];
            _chunkSize = chunkSize;
            _blockReads = blockReads;
            _onRead = onRead;
        }

        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long BytesRead => Interlocked.Read(ref _bytesRead);

        public DestinationBuffer[] DestinationBuffers => _destinationBuffers.ToArray();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _destinationBuffers.Add(new DestinationBuffer(new WeakReference<byte[]>(buffer), count));
            _onRead?.Invoke();
            int read = Copy(buffer.AsSpan(offset, count));
            Interlocked.Add(ref _bytesRead, read);
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> segment) && segment.Array is byte[] array)
                _destinationBuffers.Add(new DestinationBuffer(
                    new WeakReference<byte[]>(array), segment.Count));
            _onRead?.Invoke();
            ReadStarted.TrySetResult();
            return _blockReads
                ? new ValueTask<int>(WaitForCancellationAsync(cancellationToken))
                : ValueTask.FromResult(ReadAndCount(buffer));
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        private int ReadAndCount(Memory<byte> buffer)
        {
            int read = Copy(buffer.Span);
            Interlocked.Add(ref _bytesRead, read);
            return read;
        }

        private int Copy(Span<byte> destination)
        {
            int count = Math.Min(Math.Min(destination.Length, _chunkSize), _bytes.Length - _position);
            _bytes.AsSpan(_position, count).CopyTo(destination);
            _position += count;
            return count;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
