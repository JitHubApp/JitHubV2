using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using Xunit;
using static JitHub.WinUI.Tests.Services.GitHubImageServiceHttpDoubles;

namespace JitHub.WinUI.Tests.Services;

/// <summary>
/// Byte admissions and the memory-pressure source shared by the <c>GitHubImageService</c>
/// test classes.
/// </summary>
internal static class GitHubImageServiceAdmissionDoubles
{
    internal sealed class MemoryPressureSource
    {
        private EventHandler<object>? _handlers;

        internal Exception? UnsubscribeFailure { get; init; }

        internal int SubscriberCount => _handlers?.GetInvocationList().Length ?? 0;

        internal void Subscribe(EventHandler<object> handler) => _handlers += handler;

        internal void SubscribeThenThrow(EventHandler<object> handler)
        {
            _handlers += handler;
            throw new InvalidOperationException("Injected partial memory-pressure subscription failure.");
        }

        internal void Unsubscribe(EventHandler<object> handler)
        {
            if (UnsubscribeFailure is { } failure)
                throw failure;
            _handlers -= handler;
        }

        internal EventHandler<object>? CaptureHandlers() => _handlers;

        internal void Raise() => _handlers?.Invoke(this, EventArgs.Empty);
    }

    internal sealed class GatedImageAdmission(int imageBytes, int concurrentImages)
        : IMarkdownImageSourceByteAdmission, IDisposable
    {
        private readonly SemaphoreSlim _slots = new(concurrentImages, concurrentImages);
        private readonly TaskCompletionSource<bool> _full =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _activeBytes;
        private long _peakBytes;

        public bool IsSpeculative => false;
        public long MaximumReservationBytes => (long)imageBytes * concurrentImages;
        internal long ActiveBytes => Interlocked.Read(ref _activeBytes);
        internal long PeakBytes => Interlocked.Read(ref _peakBytes);

        internal Task WaitUntilFullAsync() => _full.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public async ValueTask<IDisposable> ReserveAsync(
            long bytes,
            CancellationToken cancellationToken)
        {
            Assert.InRange(bytes, 1, imageBytes);
            await _slots.WaitAsync(cancellationToken);
            long active = Interlocked.Add(ref _activeBytes, bytes);
            long peak;
            while ((peak = Interlocked.Read(ref _peakBytes)) < active &&
                   Interlocked.CompareExchange(ref _peakBytes, active, peak) != peak)
            {
            }
            if (active == MaximumReservationBytes)
                _full.TrySetResult(true);
            return new GatedImageLease(this, bytes);
        }

        private void Release(long bytes)
        {
            Interlocked.Add(ref _activeBytes, -bytes);
            _slots.Release();
        }

        public void Dispose() => _slots.Dispose();

        private sealed class GatedImageLease(GatedImageAdmission owner, long bytes) : IDisposable
        {
            private GatedImageAdmission? _owner = owner;
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(bytes);
        }
    }

    internal sealed class TrackingAdmission(long maximumBytes, bool isSpeculative = false)
        : IMarkdownImageSourceByteAdmission
    {
        private long _activeBytes;
        private long _peakBytes;
        private int _reservations;

        public bool IsSpeculative => isSpeculative;
        public long MaximumReservationBytes => maximumBytes;
        internal long ActiveBytes => Interlocked.Read(ref _activeBytes);
        internal long PeakBytes => Interlocked.Read(ref _peakBytes);
        internal int Reservations => Volatile.Read(ref _reservations);

        public ValueTask<IDisposable> ReserveAsync(long bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes > maximumBytes && isSpeculative)
                throw new MarkdownImageSourceDeferredException();
            if (bytes < 1 || bytes > maximumBytes)
                throw new ArgumentOutOfRangeException(nameof(bytes));
            long active = Interlocked.Add(ref _activeBytes, bytes);
            Interlocked.Increment(ref _reservations);
            long observed;
            while ((observed = Interlocked.Read(ref _peakBytes)) < active &&
                   Interlocked.CompareExchange(ref _peakBytes, active, observed) != observed)
            {
            }
            return ValueTask.FromResult<IDisposable>(new TrackingLease(this, bytes));
        }

        private sealed class TrackingLease(TrackingAdmission owner, long bytes) : IDisposable
        {
            private TrackingAdmission? _owner = owner;
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?
                .Release(bytes);
        }

        private void Release(long bytes) => Interlocked.Add(ref _activeBytes, -bytes);
    }

    internal sealed class MutatingAdmission(Func<Task> mutate) : IMarkdownImageSourceByteAdmission
    {
        private readonly TrackingAdmission _inner = new(1024);
        private int _reservations;

        public bool IsSpeculative => false;
        public long MaximumReservationBytes => _inner.MaximumReservationBytes;
        internal int Reservations => Volatile.Read(ref _reservations);
        internal long PeakBytes => _inner.PeakBytes;
        internal long ActiveBytes => _inner.ActiveBytes;

        public async ValueTask<IDisposable> ReserveAsync(long bytes, CancellationToken cancellationToken)
        {
            IDisposable lease = await _inner.ReserveAsync(bytes, cancellationToken);
            if (Interlocked.Increment(ref _reservations) == 1)
            {
                try
                {
                    await mutate();
                }
                catch
                {
                    lease.Dispose();
                    throw;
                }
            }

            return lease;
        }
    }
}
