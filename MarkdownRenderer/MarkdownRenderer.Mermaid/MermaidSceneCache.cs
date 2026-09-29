using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MarkdownRenderer.Mermaid;

internal readonly record struct MermaidSceneCacheKey(
    string SourceSha256,
    int SourceByteLength,
    string MermaidCompatibilityVersion,
    string MermanCommit,
    uint NativeAbiVersion,
    MermaidSceneVersion MmirVersion,
    MermaidLayoutMode Layout,
    MermaidThemeVariant Theme,
    int MaxSourceBytes,
    int MaxNodes,
    int MaxEdges,
    int MaxDepth,
    int MaxLabelBytes,
    int MaxSceneBytes,
    long MaxWorkingMemoryBytes,
    long DeadlineTicks,
    int MaxConcurrentRenders,
    int MaxOutstandingRenders,
    long MaxOutstandingSourceBytes,
    string FontCatalogFingerprint,
    string DiagnosticCultureName)
{
    private const int Utf8HashBufferBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal const string PinnedMermaidCompatibilityVersion = "11.16.1";
    internal const string PinnedMermanCommit = "ac53f21ba97e5bd8da7f849fbcdffe64695171e6";

    internal static MermaidSceneCacheKey Create(
        ReadOnlySpan<byte> sourceUtf8,
        MermaidRenderOptions options,
        MermaidFontCatalog fontCatalog,
        CultureInfo diagnosticCulture)
        => CreateCore(
            Convert.ToHexStringLower(SHA256.HashData(sourceUtf8)),
            sourceUtf8.Length,
            options,
            fontCatalog,
            diagnosticCulture);

    /// <summary>
    /// Hashes UTF-8 incrementally so cache lookup and same-key joining do not
    /// require every waiter to retain a full encoded copy before admission.
    /// </summary>
    internal static MermaidSceneCacheKey Create(
        string source,
        int sourceByteLength,
        MermaidRenderOptions options,
        MermaidFontCatalog fontCatalog,
        CultureInfo diagnosticCulture)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (sourceByteLength < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceByteLength));
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fontCatalog);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Utf8HashBufferBytes);
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Encoder encoder = StrictUtf8.GetEncoder();
            ReadOnlySpan<char> remaining = source.AsSpan();
            bool completed;
            do
            {
                encoder.Convert(
                    remaining,
                    buffer.AsSpan(0, Utf8HashBufferBytes),
                    flush: true,
                    out int charsUsed,
                    out int bytesUsed,
                    out completed);
                if (bytesUsed != 0)
                    hash.AppendData(buffer.AsSpan(0, bytesUsed));
                remaining = remaining[charsUsed..];
            }
            while (!completed);

            return CreateCore(
                Convert.ToHexStringLower(hash.GetHashAndReset()),
                sourceByteLength,
                options,
                fontCatalog,
                diagnosticCulture);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static MermaidSceneCacheKey CreateCore(
        string sourceSha256,
        int sourceByteLength,
        MermaidRenderOptions options,
        MermaidFontCatalog fontCatalog,
        CultureInfo diagnosticCulture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSha256);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fontCatalog);
        MermaidRenderBudgets budgets = options.Budgets;
        return new MermaidSceneCacheKey(
            sourceSha256,
            sourceByteLength,
            PinnedMermaidCompatibilityVersion,
            PinnedMermanCommit,
            NativeMethods.PackedAbiVersion,
            MermaidSceneVersion.Current,
            options.Layout,
            options.Theme,
            budgets.MaxSourceBytes,
            budgets.MaxNodes,
            budgets.MaxEdges,
            budgets.MaxDepth,
            budgets.MaxLabelBytes,
            budgets.MaxSceneBytes,
            budgets.MaxWorkingMemoryBytes,
            budgets.Deadline.Ticks,
            budgets.MaxConcurrentRenders,
            budgets.MaxOutstandingRenders,
            budgets.MaxOutstandingSourceBytes,
            fontCatalog.Fingerprint,
            diagnosticCulture.Name);
    }
}

/// <summary>
/// A renderer-local byte-budgeted scene LRU. The key deliberately retains only
/// a source digest, never the potentially large Mermaid source. In-flight work
/// is shared while each caller keeps independent cancellation semantics.
/// </summary>
internal sealed class MermaidSceneCache : IDisposable
{
    private readonly object _sync = new();
    private readonly long _budgetBytes;
    private readonly int _maxOutstandingRenders;
    private readonly long _maxOutstandingSourceBytes;
    private readonly Dictionary<MermaidSceneCacheKey, CachedScene> _scenes = [];
    private readonly Dictionary<MermaidSceneCacheKey, InFlightRender> _inFlight = [];
    private readonly LinkedList<MermaidSceneCacheKey> _lru = [];
    private long _retainedBytes;
    private int _outstandingRenderCount;
    private long _outstandingSourceBytes;
    private bool _disposed;

    internal MermaidSceneCache(
        long budgetBytes,
        int maxOutstandingRenders = int.MaxValue,
        long maxOutstandingSourceBytes = long.MaxValue)
    {
        if (budgetBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(budgetBytes));
        if (maxOutstandingRenders <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxOutstandingRenders));
        if (maxOutstandingSourceBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxOutstandingSourceBytes));
        _budgetBytes = budgetBytes;
        _maxOutstandingRenders = maxOutstandingRenders;
        _maxOutstandingSourceBytes = maxOutstandingSourceBytes;
    }

    internal int Count
    {
        get
        {
            lock (_sync)
                return _scenes.Count;
        }
    }

    internal long RetainedBytes
    {
        get
        {
            lock (_sync)
                return _retainedBytes;
        }
    }

    internal int OutstandingRenderCount
    {
        get
        {
            lock (_sync)
                return _outstandingRenderCount;
        }
    }

    internal long OutstandingSourceBytes
    {
        get
        {
            lock (_sync)
                return _outstandingSourceBytes;
        }
    }

    internal async ValueTask<MermaidRenderResult> GetOrRenderAsync(
        MermaidSceneCacheKey key,
        string originalSource,
        Func<CancellationToken, Task<MermaidRenderResult>> factory,
        Func<MermaidRenderResult> admissionRejectedResultFactory,
        Func<MermaidRenderResult> deadlineExceededResultFactory,
        long producerDeadlineTimestamp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(originalSource);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(admissionRejectedResultFactory);
        ArgumentNullException.ThrowIfNull(deadlineExceededResultFactory);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InFlightRender? pending = null;
            bool isProducer = false;
            bool admissionRejected = false;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_scenes.TryGetValue(key, out CachedScene? cached))
                {
                    Touch(cached);
                    return Success(cached.Scene, originalSource);
                }

                if (!_inFlight.TryGetValue(key, out pending))
                {
                    int sourceByteLength = key.SourceByteLength;
                    if (_outstandingRenderCount >= _maxOutstandingRenders ||
                        sourceByteLength > _maxOutstandingSourceBytes - _outstandingSourceBytes)
                    {
                        admissionRejected = true;
                    }
                    else
                    {
                        pending = new InFlightRender(sourceByteLength, producerDeadlineTimestamp);
                        _inFlight.Add(key, pending);
                        _outstandingRenderCount++;
                        _outstandingSourceBytes += sourceByteLength;
                        isProducer = true;
                    }
                }

                if (!admissionRejected)
                {
                    checked
                    {
                        pending!.WaiterCount++;
                    }
                }
            }

            if (admissionRejected)
                return admissionRejectedResultFactory();

            if (isProducer)
            {
                _ = CompleteProducerAsync(
                    key,
                    pending!,
                    factory,
                    deadlineExceededResultFactory);
            }

            MermaidRenderResult result;
            try
            {
                result = await pending!.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ReleaseWaiter(key, pending!);
            }

            // A late joiner owns a fresh request deadline. If the older shared
            // operation expires first, re-enter admission after it has released
            // its slot rather than reporting that the newer request timed out.
            if (!isProducer &&
                result.Status == MermaidRenderStatus.TimedOut &&
                pending!.DeadlineTimestamp < producerDeadlineTimestamp &&
                !MermaidDeadline.IsExpired(producerDeadlineTimestamp))
            {
                continue;
            }

            return result;
        }
    }

    public void Dispose()
    {
        InFlightRender[] pending;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _scenes.Clear();
            _lru.Clear();
            _retainedBytes = 0;
            pending = _inFlight.Values.ToArray();
            foreach (InFlightRender render in pending)
                render.Abandoned = true;
            _inFlight.Clear();
        }

        foreach (InFlightRender render in pending)
            RequestCancellation(render);
    }

    internal static long EstimateWeightBytes(MermaidScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        long bytes = 512;
        foreach (string value in scene.Strings)
            bytes += 24L + (value.Length * sizeof(char));
        bytes += scene.Styles.Count * 32L;
        bytes += scene.Geometry.Count * sizeof(float);
        bytes += scene.Commands.Count * 40L;
        bytes += scene.Semantics.Count * 32L;
        bytes += scene.Links.Count * 20L;
        bytes += scene.SourceMappings.Count * 20L;
        foreach (MermaidDiagnostic diagnostic in scene.Diagnostics)
        {
            bytes += 64L;
            bytes += (diagnostic.Code.Length + diagnostic.Message.Length) * sizeof(char);
        }
        return bytes;
    }

    private async Task CompleteProducerAsync(
        MermaidSceneCacheKey key,
        InFlightRender pending,
        Func<CancellationToken, Task<MermaidRenderResult>> factory,
        Func<MermaidRenderResult> deadlineExceededResultFactory)
    {
        MermaidRenderResult? result = null;
        Exception? error = null;
        try
        {
            result = await factory(pending.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            error = exception;
        }

        bool disposeCancellation = false;
        bool deadlineExpired = false;
        lock (_sync)
        {
            pending.Completed = true;
            _outstandingRenderCount--;
            _outstandingSourceBytes -= pending.SourceByteLength;
            if (_inFlight.TryGetValue(key, out InFlightRender? current) && ReferenceEquals(current, pending))
                _inFlight.Remove(key);
            if (MermaidDeadline.IsExpired(pending.DeadlineTimestamp))
            {
                deadlineExpired = true;
            }
            if (!deadlineExpired &&
                !_disposed &&
                !pending.Abandoned &&
                result is { Status: MermaidRenderStatus.Success, Scene: not null })
            {
                Add(key, result.Scene);
            }
            disposeCancellation = pending.WaiterCount == 0;
        }

        // Result factories may invoke host localization code. Never call them
        // while holding the cache lock.
        if (deadlineExpired)
        {
            result = deadlineExceededResultFactory();
            error = null;
        }

        if (error is null)
            pending.Completion.TrySetResult(result!);
        else
            pending.Completion.TrySetException(error);
        if (disposeCancellation)
            pending.DisposeCancellation();
    }

    private void ReleaseWaiter(MermaidSceneCacheKey key, InFlightRender pending)
    {
        bool cancel = false;
        bool dispose = false;
        lock (_sync)
        {
            if (pending.WaiterCount <= 0)
                throw new InvalidOperationException("The Mermaid scene-cache waiter count is unbalanced.");
            pending.WaiterCount--;
            if (pending.WaiterCount != 0)
                return;

            if (!pending.Completed)
            {
                pending.Abandoned = true;
                if (_inFlight.TryGetValue(key, out InFlightRender? current) && ReferenceEquals(current, pending))
                    _inFlight.Remove(key);
                cancel = true;
            }
            else
            {
                dispose = true;
            }
        }

        if (cancel)
            RequestCancellation(pending);
        else if (dispose)
            pending.DisposeCancellation();
    }

    private void Add(MermaidSceneCacheKey key, MermaidScene scene)
    {
        if (_budgetBytes == 0)
            return;
        long weight = EstimateWeightBytes(scene);
        if (weight > _budgetBytes)
            return;

        if (_scenes.TryGetValue(key, out CachedScene? existing))
        {
            _retainedBytes -= existing.Weight;
            _lru.Remove(existing.Node);
            _scenes.Remove(key);
        }
        while (_retainedBytes + weight > _budgetBytes && _lru.Last is { } tail)
        {
            MermaidSceneCacheKey evictedKey = tail.Value;
            CachedScene evicted = _scenes[evictedKey];
            _retainedBytes -= evicted.Weight;
            _scenes.Remove(evictedKey);
            _lru.RemoveLast();
        }

        LinkedListNode<MermaidSceneCacheKey> node = _lru.AddFirst(key);
        _scenes.Add(key, new CachedScene(scene, weight, node));
        _retainedBytes += weight;
    }

    private void Touch(CachedScene cached)
    {
        _lru.Remove(cached.Node);
        _lru.AddFirst(cached.Node);
    }

    private static MermaidRenderResult Success(MermaidScene scene, string originalSource) =>
        new(MermaidRenderStatus.Success, scene, scene.Diagnostics, originalSource);

    private static void RequestCancellation(InFlightRender pending)
        => pending.RequestCancellation();

    private sealed record CachedScene(
        MermaidScene Scene,
        long Weight,
        LinkedListNode<MermaidSceneCacheKey> Node);

    private sealed class InFlightRender
    {
        private readonly object _cancellationSync = new();
        private CancellationTokenSource? _cancellation = new();
        private readonly CancellationToken _cancellationToken;
        private Task _cancellationCallbacks = Task.CompletedTask;

        internal InFlightRender(int sourceByteLength, long deadlineTimestamp)
        {
            SourceByteLength = sourceByteLength;
            DeadlineTimestamp = deadlineTimestamp;
            _cancellationToken = _cancellation.Token;
        }

        internal CancellationToken CancellationToken => _cancellationToken;
        internal TaskCompletionSource<MermaidRenderResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int WaiterCount { get; set; }
        internal int SourceByteLength { get; }
        internal long DeadlineTimestamp { get; }
        internal bool Completed { get; set; }
        internal bool Abandoned { get; set; }

        internal void RequestCancellation()
        {
            lock (_cancellationSync)
            {
                if (_cancellation is null || _cancellationToken.IsCancellationRequested)
                    return;

                try
                {
                    // A last waiter may unwind synchronously on its caller's
                    // context. Never execute producer/native callbacks there.
                    _cancellationCallbacks = _cancellation.CancelAsync();
                    ObserveFault(_cancellationCallbacks);
                }
                catch (ObjectDisposedException)
                {
                    // Producer completion won the race and already retired the
                    // source; no work remains to cancel.
                    _cancellationCallbacks = Task.CompletedTask;
                }
            }
        }

        internal void DisposeCancellation()
        {
            CancellationTokenSource? cancellation;
            Task callbacks;
            lock (_cancellationSync)
            {
                cancellation = _cancellation;
                if (cancellation is null)
                    return;

                _cancellation = null;
                callbacks = _cancellationCallbacks;
            }

            Task retirement = DisposeCancellationCoreAsync(cancellation, callbacks);
            ObserveFault(retirement);
        }

        private static async Task DisposeCancellationCoreAsync(
            CancellationTokenSource cancellation,
            Task callbacks)
        {
            try
            {
                await callbacks.ConfigureAwait(false);
            }
            catch
            {
                // Cancellation callback faults are producer diagnostics and
                // must not strand the source after the producer has exited.
                _ = callbacks.Exception;
            }
            finally
            {
                cancellation.Dispose();
            }
        }

        private static void ObserveFault(Task task)
        {
            if (task.IsFaulted)
            {
                _ = task.Exception;
                return;
            }

            if (task.IsCompleted)
                return;

            _ = task.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
    }
}

internal static class MermaidDeadline
{
    internal static long Start(TimeSpan duration)
    {
        long now = Stopwatch.GetTimestamp();
        double rawTicks = Math.Ceiling(duration.TotalSeconds * Stopwatch.Frequency);
        long durationTicks = rawTicks >= long.MaxValue ? long.MaxValue : Math.Max(1, (long)rawTicks);
        return now > long.MaxValue - durationTicks ? long.MaxValue : now + durationTicks;
    }

    internal static bool IsExpired(long deadlineTimestamp) =>
        Stopwatch.GetTimestamp() >= deadlineTimestamp;

    internal static TimeSpan GetRemaining(long deadlineTimestamp)
    {
        long now = Stopwatch.GetTimestamp();
        if (deadlineTimestamp <= now)
            return TimeSpan.Zero;

        long remainingTicks = deadlineTimestamp - now;
        return TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency);
    }

    internal static CancellationTokenSource CreateCancellation(long deadlineTimestamp)
    {
        var cancellation = new CancellationTokenSource();
        TimeSpan remaining = GetRemaining(deadlineTimestamp);
        if (remaining <= TimeSpan.Zero)
            cancellation.Cancel();
        else
            cancellation.CancelAfter(remaining);
        return cancellation;
    }
}
