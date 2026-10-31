using System.Threading.Channels;
using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class SourceByteAdmissionStormTests
{
    [Fact]
    public async Task EighteenHundredImageStormKeepsMaterializedSourceBytesBoundedAcrossDeepJumpCancellation()
    {
        long capacity = (IntPtr.Size == 4 ? 32L : 64L) * 1024 * 1024;
        long visibleReserve = capacity / 4;
        long backgroundCapacity = capacity - visibleReserve;
        const int imageCount = 1_800;
        const int documentCount = 12;
        const int imagesPerDocument = imageCount / documentCount;
        const int retainedDocumentIndex = documentCount - 1;
        const int retainedImageCount = imagesPerDocument;
        const int imageBytes = 1024 * 1024;
        int backgroundBatchSize = (int)(backgroundCapacity / imageBytes);

        var admission = new SourceByteAdmission(capacity, visibleReserve);
        var sourceBytes = new SourceByteTracker();
        var materialized = Channel.CreateUnbounded<SourceProbe>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        var owners = Enumerable.Range(0, documentCount).Select(_ => new object()).ToArray();
        var ownerCancellations = Enumerable.Range(0, documentCount)
            .Select(_ => new CancellationTokenSource())
            .ToArray();
        var storm = new List<SourceProbe>(imageCount);

        SourceProbe CreateProbe(object owner, long bytes, bool background, CancellationToken token) =>
            new(admission, sourceBytes, materialized.Writer, owner, bytes, background, token);

        var oldPrefetchOwner = new object();
        var blocker = CreateProbe(oldPrefetchOwner, backgroundCapacity, background: true, CancellationToken.None);
        SourceProbe? visibleForCleanup = null;
        try
        {
            blocker.Start();
            Assert.Same(blocker, await materialized.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

            for (int imageIndex = 0; imageIndex < imageCount; imageIndex++)
            {
                int documentIndex = imageIndex / imagesPerDocument;
                SourceProbe probe = CreateProbe(
                    owners[documentIndex],
                    imageBytes,
                    background: true,
                    ownerCancellations[documentIndex].Token);
                storm.Add(probe);
                probe.Start();
            }

            Assert.Equal(imageCount, admission.PendingRequests);
            Assert.Equal(backgroundCapacity, admission.ActiveBackgroundBytes);
            Assert.Equal(backgroundCapacity, sourceBytes.ActiveBytes);

            // A deep jump retires all but the newest document's speculative work.
            for (int documentIndex = 0; documentIndex < retainedDocumentIndex; documentIndex++)
                ownerCancellations[documentIndex].Cancel();

            await Task.WhenAll(storm
                .Where((_, imageIndex) => imageIndex / imagesPerDocument != retainedDocumentIndex)
                .Select(static probe => probe.Completion))
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(retainedImageCount, admission.PendingRequests);
            Assert.Equal(backgroundCapacity, admission.ActiveBackgroundBytes);
            Assert.Equal(backgroundCapacity, sourceBytes.ActiveBytes);

            SourceProbe visible = CreateProbe(
                new object(), capacity, background: false, CancellationToken.None);
            visibleForCleanup = visible;
            visible.Start();
            Assert.False(visible.Completion.IsCompleted);
            blocker.Release();

            // The visible request needs the full budget. No speculative image can
            // materialize until it completes, even though the 1,800-image queue is
            // under active churn from a navigation generation change.
            Assert.Same(visible, await materialized.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(capacity, admission.ActiveBytes);
            Assert.Equal(0, admission.ActiveBackgroundBytes);
            Assert.Equal(capacity, sourceBytes.ActiveBytes);
            Assert.InRange(sourceBytes.PeakActiveBytes, capacity, capacity);

            visible.Release();
            await visible.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await blocker.Completion.WaitAsync(TimeSpan.FromSeconds(5));

            SourceProbe[] retained = storm
                .Skip(retainedDocumentIndex * imagesPerDocument)
                .ToArray();
            for (int offset = 0; offset < retained.Length; offset += backgroundBatchSize)
            {
                int batchCount = global::System.Math.Min(backgroundBatchSize, retained.Length - offset);
                var batch = new SourceProbe[batchCount];
                for (int index = 0; index < batchCount; index++)
                {
                    SourceProbe acquired = await materialized.Reader.ReadAsync()
                        .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.Contains(acquired, retained);
                    batch[index] = acquired;
                }

                Assert.InRange(sourceBytes.ActiveBytes, 0, backgroundCapacity);
                Assert.InRange(admission.ActiveBytes, 0, capacity);
                Assert.InRange(admission.ActiveBackgroundBytes, 0, backgroundCapacity);

                foreach (SourceProbe probe in batch)
                    probe.Release();
                await Task.WhenAll(batch.Select(static probe => probe.Completion))
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }

            await Task.WhenAll(storm
                .Skip(retainedDocumentIndex * imagesPerDocument)
                .Select(static probe => probe.Completion))
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, admission.PendingRequests);
            Assert.Equal(0, admission.ActiveBytes);
            Assert.Equal(0, admission.ActiveBackgroundBytes);
            Assert.Equal(0, sourceBytes.ActiveBytes);
            Assert.InRange(sourceBytes.PeakActiveBytes, capacity, capacity);

        }
        finally
        {
            foreach (CancellationTokenSource cancellation in ownerCancellations)
                cancellation.Cancel();
            blocker.Release();
            visibleForCleanup?.Release();
            foreach (SourceProbe probe in storm)
                probe.Release();

            Task[] completions = storm.Select(static probe => probe.Completion)
                .Append(blocker.Completion)
                .Append(visibleForCleanup?.Completion ?? Task.CompletedTask)
                .ToArray();
            try
            {
                await Task.WhenAll(completions).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // Preserve the assertion/build failure that led to cleanup.
            }

            foreach (CancellationTokenSource cancellation in ownerCancellations)
                cancellation.Dispose();
        }
    }

    private sealed class SourceProbe(
        SourceByteAdmission admission,
        SourceByteTracker tracker,
        ChannelWriter<SourceProbe> materialized,
        object owner,
        long bytes,
        bool background,
        CancellationToken cancellationToken)
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Completion { get; private set; } = Task.CompletedTask;

        internal void Start() => Completion = RunAsync();

        internal void Release() => _release.TrySetResult();

        private async Task RunAsync()
        {
            try
            {
                using IDisposable lease = await admission.EnterAsync(
                    owner, bytes, background, cancellationToken);
                byte[] buffer = GC.AllocateUninitializedArray<byte>(checked((int)bytes));
                tracker.Add(bytes);
                try
                {
                    await materialized.WriteAsync(this);
                    await _release.Task;
                }
                finally
                {
                    GC.KeepAlive(buffer);
                    tracker.Remove(bytes);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A stale document's queued prefetch was removed without ever
                // allocating a source buffer or taking a byte reservation.
            }
        }
    }

    private sealed class SourceByteTracker
    {
        private long _activeBytes;
        private long _peakActiveBytes;

        internal long ActiveBytes => Interlocked.Read(ref _activeBytes);
        internal long PeakActiveBytes => Interlocked.Read(ref _peakActiveBytes);

        internal void Add(long bytes)
        {
            long current = Interlocked.Add(ref _activeBytes, bytes);
            long observed;
            while (current > (observed = Interlocked.Read(ref _peakActiveBytes)) &&
                   Interlocked.CompareExchange(ref _peakActiveBytes, current, observed) != observed)
            {
            }
        }

        internal void Remove(long bytes) => Interlocked.Add(ref _activeBytes, -bytes);
    }
}
