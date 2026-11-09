using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace JitHub.Services;

public sealed partial class LocalDiagnosticsStore : ILocalDiagnosticsStore
{
    public const long DefaultMaxBytes = 25L * 1024L * 1024L;
    public const int DefaultQueueCapacity = 512;
    public const int DefaultAppendBatchSize = 64;
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(14);

    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly int NewLineByteCount = Utf8WithoutBom.GetByteCount(Environment.NewLine);

    private readonly string _diagnosticsPath;
    private readonly long _maxBytes;
    private readonly TimeSpan _retention;
    private readonly TimeProvider _timeProvider;
    private readonly int _queueCapacity;
    private readonly Channel<StoreOperation> _operations;
    private readonly Task _writerTask;
    private readonly object _lifecycleGate = new();
    private bool _acceptingOperations = true;
    private int _activeEnqueues;
    private TaskCompletionSource? _enqueuesDrained;
    private Task? _shutdownTask;
    private bool _initialTrimCompleted;
    private DateTimeOffset _nextRetentionTrimAt = DateTimeOffset.MinValue;
    private long _droppedEventCount;
    private long _pendingDroppedEventCount;
    private bool _overflowSignalScheduled;

    public LocalDiagnosticsStore(IAppStoragePathProvider pathProvider)
        : this(pathProvider.DiagnosticsPath, DefaultMaxBytes, DefaultRetention)
    {
    }

    internal LocalDiagnosticsStore(
        string diagnosticsPath,
        long maxBytes,
        TimeSpan retention,
        int queueCapacity = DefaultQueueCapacity,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticsPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);

        _diagnosticsPath = diagnosticsPath;
        _maxBytes = maxBytes;
        _retention = retention;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _queueCapacity = queueCapacity;

        string? directoryPath = Path.GetDirectoryName(_diagnosticsPath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        _operations = Channel.CreateBounded<StoreOperation>(new BoundedChannelOptions(queueCapacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _writerTask = ProcessOperationsAsync();
    }

    public long DroppedEventCount
    {
        get
        {
            lock (_lifecycleGate)
            {
                return _droppedEventCount;
            }
        }
    }

    public bool TryAppend(LocalDiagnosticEvent entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        bool scheduleOverflowSignal = false;
        lock (_lifecycleGate)
        {
            if (!_acceptingOperations)
            {
                return false;
            }

            if (_operations.Writer.TryWrite(StoreOperation.CreateUnobservedAppend(entry)))
            {
                return true;
            }

            _droppedEventCount++;
            _pendingDroppedEventCount++;
            if (!_overflowSignalScheduled)
            {
                _overflowSignalScheduled = true;
                _activeEnqueues++;
                scheduleOverflowSignal = true;
            }
        }

        if (scheduleOverflowSignal)
        {
            _ = EnqueueOverflowSignalAsync();
        }

        return false;
    }

    public Task AppendAsync(LocalDiagnosticEvent entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return EnqueueAsync(StoreOperationKind.Append, entry, cancellationToken);
    }

    public Task<IReadOnlyList<LocalDiagnosticEvent>> ReadAsync(CancellationToken cancellationToken = default) =>
        EnqueueAsync<IReadOnlyList<LocalDiagnosticEvent>>(StoreOperationKind.Read, entry: null, cancellationToken);

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        EnqueueAsync(StoreOperationKind.Clear, entry: null, cancellationToken);

    public Task<long> GetSizeAsync(CancellationToken cancellationToken = default) =>
        EnqueueAsync<long>(StoreOperationKind.GetSize, entry: null, cancellationToken);

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        Task? shutdownTask;
        lock (_lifecycleGate)
        {
            shutdownTask = _shutdownTask;
        }

        if (shutdownTask is not null)
        {
            await shutdownTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await EnqueueAsync(StoreOperationKind.Flush, entry: null, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            lock (_lifecycleGate)
            {
                shutdownTask = _shutdownTask;
            }

            if (shutdownTask is null)
            {
                throw;
            }

            await shutdownTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        Task shutdownTask;
        lock (_lifecycleGate)
        {
            if (_shutdownTask is null)
            {
                _acceptingOperations = false;
                if (_activeEnqueues > 0)
                {
                    _enqueuesDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                _shutdownTask = CompleteShutdownAsync(_enqueuesDrained?.Task ?? Task.CompletedTask);
            }

            shutdownTask = _shutdownTask;
        }

        // Cancellation only releases this caller. The shared drain remains alive and can be
        // awaited again by disposal or a later shutdown request.
        await shutdownTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() =>
        await ShutdownAsync().ConfigureAwait(false);

    internal static IReadOnlyList<string> SelectRetainedLines(
        IEnumerable<string> lines,
        DateTimeOffset cutoff,
        long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        Queue<RetainedLine> retained = new();
        long retainedBytes = 0;

        foreach (string line in lines)
        {
            if (!TryReadRetainedLine(line, cutoff, maxBytes, out RetainedLine candidate))
            {
                continue;
            }

            retained.Enqueue(candidate);
            retainedBytes += candidate.ByteCount;
            while (retainedBytes > maxBytes)
            {
                retainedBytes -= retained.Dequeue().ByteCount;
            }
        }

        return retained.Select(static item => item.Text).ToArray();
    }

    private async Task EnqueueAsync(
        StoreOperationKind kind,
        LocalDiagnosticEvent? entry,
        CancellationToken cancellationToken)
    {
        _ = await EnqueueCoreAsync(kind, entry, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> EnqueueAsync<T>(
        StoreOperationKind kind,
        LocalDiagnosticEvent? entry,
        CancellationToken cancellationToken)
    {
        object? result = await EnqueueCoreAsync(kind, entry, cancellationToken).ConfigureAwait(false);
        return result is T typedResult
            ? typedResult
            : throw new InvalidOperationException($"Diagnostics operation '{kind}' returned an unexpected result.");
    }

    private async Task<object?> EnqueueCoreAsync(
        StoreOperationKind kind,
        LocalDiagnosticEvent? entry,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeginEnqueue();

        StoreOperation operation = StoreOperation.Create(kind, entry, cancellationToken);
        try
        {
            await _operations.Writer.WriteAsync(operation, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new ObjectDisposedException(nameof(LocalDiagnosticsStore));
        }
        finally
        {
            EndEnqueue();
        }

        return await operation.Completion!.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteShutdownAsync(Task enqueuesDrainedTask)
    {
        await enqueuesDrainedTask.ConfigureAwait(false);
        _operations.Writer.TryComplete();
        await _writerTask.ConfigureAwait(false);
    }

    private async Task ProcessOperationsAsync()
    {
        StoreOperation? pendingOperation = null;
        while (pendingOperation is not null || await _operations.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            StoreOperation operation;
            if (pendingOperation is not null)
            {
                operation = pendingOperation;
                pendingOperation = null;
            }
            else if (!_operations.Reader.TryRead(out operation!))
            {
                continue;
            }

            if (operation.Kind == StoreOperationKind.Append)
            {
                List<StoreOperation> batch = new(DefaultAppendBatchSize) { operation };
                while (batch.Count < DefaultAppendBatchSize && _operations.Reader.TryRead(out StoreOperation? next))
                {
                    if (next.Kind != StoreOperationKind.Append)
                    {
                        pendingOperation = next;
                        break;
                    }

                    batch.Add(next);
                }

                await ExecuteAppendBatchAsync(batch).ConfigureAwait(false);
                continue;
            }

            if (operation.Kind == StoreOperationKind.OverflowSignal)
            {
                await ExecuteOverflowSignalAsync().ConfigureAwait(false);
                continue;
            }

            await ExecuteOperationAsync(operation).ConfigureAwait(false);
        }
    }

    private async Task EnqueueOverflowSignalAsync()
    {
        try
        {
            await _operations.Writer.WriteAsync(StoreOperation.CreateOverflowSignal(), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            lock (_lifecycleGate)
            {
                _overflowSignalScheduled = false;
            }
        }
        finally
        {
            EndEnqueue();
        }
    }

    private async Task ExecuteOverflowSignalAsync()
    {
        long dropped;
        lock (_lifecycleGate)
        {
            dropped = _pendingDroppedEventCount;
            _pendingDroppedEventCount = 0;
            _overflowSignalScheduled = false;
        }

        if (dropped <= 0)
        {
            return;
        }

        LocalDiagnosticEvent overflow = new(
            _timeProvider.GetUtcNow(),
            "event",
            "diagnostics.queue.overflow",
            new Dictionary<string, string>
            {
                ["dropped_count"] = dropped.ToString(CultureInfo.InvariantCulture),
                ["queue_capacity"] = _queueCapacity.ToString(CultureInfo.InvariantCulture)
            });
        await ExecuteAppendBatchAsync(
            (StoreOperation[])[StoreOperation.CreateUnobservedAppend(overflow)]).ConfigureAwait(false);
    }

    private async Task ExecuteAppendBatchAsync(IReadOnlyList<StoreOperation> operations)
    {
        List<StoreOperation> activeOperations = [];
        foreach (StoreOperation operation in operations)
        {
            if (operation.CancellationToken.IsCancellationRequested)
            {
                operation.Completion?.TrySetCanceled(operation.CancellationToken);
            }
            else
            {
                activeOperations.Add(operation);
            }
        }

        if (activeOperations.Count == 0)
        {
            return;
        }

        try
        {
            await EnsureRetentionTrimAsync(force: false).ConfigureAwait(false);
            DateTimeOffset cutoff = _timeProvider.GetUtcNow().Subtract(_retention);
            StringBuilder contents = new();
            foreach (StoreOperation operation in activeOperations)
            {
                if (operation.Entry!.Timestamp >= cutoff)
                {
                    contents.Append(JsonSerializer.Serialize(
                        operation.Entry,
                        LocalDiagnosticsJsonContext.Default.DiagnosticEvent));
                    contents.Append(Environment.NewLine);
                }
            }

            if (contents.Length > 0)
            {
                await File.AppendAllTextAsync(
                    _diagnosticsPath,
                    contents.ToString(),
                    Utf8WithoutBom).ConfigureAwait(false);
            }

            if (GetSizeCore() > _maxBytes)
            {
                await TrimAsync().ConfigureAwait(false);
            }

            foreach (StoreOperation operation in activeOperations)
            {
                operation.Completion?.TrySetResult(null);
            }
        }
        catch (Exception exception)
        {
            // A failed batch is reported to observed callers but never terminates the writer.
            foreach (StoreOperation operation in activeOperations)
            {
                operation.Completion?.TrySetException(exception);
            }
        }
    }

    private async Task ExecuteOperationAsync(StoreOperation operation)
    {
        if (operation.CancellationToken.IsCancellationRequested)
        {
            operation.Completion?.TrySetCanceled(operation.CancellationToken);
            return;
        }

        try
        {
            object? result = operation.Kind switch
            {
                StoreOperationKind.Read => await ReadCoreAsync().ConfigureAwait(false),
                StoreOperationKind.Clear => ExecuteClear(),
                StoreOperationKind.GetSize => await GetSizeAfterRetentionAsync().ConfigureAwait(false),
                StoreOperationKind.Flush => null,
                _ => throw new InvalidOperationException($"Unsupported diagnostics operation '{operation.Kind}'.")
            };
            operation.Completion?.TrySetResult(result);
        }
        catch (Exception exception)
        {
            // One failed file operation must not terminate the shared writer.
            operation.Completion?.TrySetException(exception);
        }
    }

    private async Task<IReadOnlyList<LocalDiagnosticEvent>> ReadCoreAsync()
    {
        await EnsureRetentionTrimAsync(force: true).ConfigureAwait(false);
        if (!File.Exists(_diagnosticsPath))
        {
            return Array.Empty<LocalDiagnosticEvent>();
        }

        List<LocalDiagnosticEvent> events = [];
        foreach (string line in File.ReadLines(_diagnosticsPath, Utf8WithoutBom))
        {
            if (TryDeserialize(line, out LocalDiagnosticEvent? item) && item is not null)
            {
                events.Add(item);
            }
        }

        return events;
    }

    private object? ExecuteClear()
    {
        if (File.Exists(_diagnosticsPath))
        {
            File.Delete(_diagnosticsPath);
        }

        _initialTrimCompleted = true;
        _nextRetentionTrimAt = _timeProvider.GetUtcNow() + RetentionTrimInterval();
        return null;
    }

    private async Task<object?> GetSizeAfterRetentionAsync()
    {
        await EnsureRetentionTrimAsync(force: true).ConfigureAwait(false);
        return GetSizeCore();
    }

    private long GetSizeCore() =>
        File.Exists(_diagnosticsPath)
            ? new FileInfo(_diagnosticsPath).Length
            : 0;

    private async Task EnsureInitialTrimAsync()
    {
        if (_initialTrimCompleted)
        {
            return;
        }

        await TrimAsync().ConfigureAwait(false);
        _initialTrimCompleted = true;
    }

    private async Task EnsureRetentionTrimAsync(bool force)
    {
        await EnsureInitialTrimAsync().ConfigureAwait(false);
        if (force || _timeProvider.GetUtcNow() >= _nextRetentionTrimAt)
        {
            await TrimAsync().ConfigureAwait(false);
        }
    }

    private TimeSpan RetentionTrimInterval()
    {
        TimeSpan quarterRetention = TimeSpan.FromTicks(Math.Max(1, _retention.Ticks / 4));
        return quarterRetention <= TimeSpan.FromHours(1)
            ? quarterRetention
            : TimeSpan.FromHours(1);
    }

    private async Task TrimAsync()
    {
        _nextRetentionTrimAt = _timeProvider.GetUtcNow() + RetentionTrimInterval();
        if (!File.Exists(_diagnosticsPath))
        {
            return;
        }

        DateTimeOffset cutoff = _timeProvider.GetUtcNow().Subtract(_retention);
        IReadOnlyList<string> retained = SelectRetainedLines(
            File.ReadLines(_diagnosticsPath, Utf8WithoutBom),
            cutoff,
            _maxBytes);
        string temporaryPath = _diagnosticsPath + ".trim-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                useAsync: true))
            await using (StreamWriter writer = new(stream, Utf8WithoutBom))
            {
                foreach (string line in retained)
                {
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                }
            }

            File.Move(temporaryPath, _diagnosticsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool TryReadRetainedLine(
        string line,
        DateTimeOffset cutoff,
        long maxBytes,
        out RetainedLine retainedLine)
    {
        retainedLine = default;
        if (!TryDeserialize(line, out LocalDiagnosticEvent? item) ||
            item is null ||
            item.Timestamp < cutoff)
        {
            return false;
        }

        long byteCount = Utf8WithoutBom.GetByteCount(line) + NewLineByteCount;
        if (byteCount > maxBytes)
        {
            return false;
        }

        retainedLine = new RetainedLine(line, byteCount);
        return true;
    }

    private static bool TryDeserialize(string line, out LocalDiagnosticEvent? item)
    {
        item = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            item = JsonSerializer.Deserialize(
                line,
                LocalDiagnosticsJsonContext.Default.DiagnosticEvent);
            return item is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void BeginEnqueue()
    {
        lock (_lifecycleGate)
        {
            if (!_acceptingOperations)
            {
                throw new ObjectDisposedException(nameof(LocalDiagnosticsStore));
            }

            _activeEnqueues++;
        }
    }

    private void EndEnqueue()
    {
        TaskCompletionSource? drained = null;
        lock (_lifecycleGate)
        {
            _activeEnqueues--;
            if (!_acceptingOperations && _activeEnqueues == 0)
            {
                drained = _enqueuesDrained;
            }
        }

        drained?.TrySetResult();
    }

    private enum StoreOperationKind
    {
        Append,
        Read,
        Clear,
        GetSize,
        Flush,
        OverflowSignal
    }

    private readonly record struct RetainedLine(string Text, long ByteCount);

    private sealed record StoreOperation(
        StoreOperationKind Kind,
        LocalDiagnosticEvent? Entry,
        CancellationToken CancellationToken,
        TaskCompletionSource<object?>? Completion)
    {
        public static StoreOperation Create(
            StoreOperationKind kind,
            LocalDiagnosticEvent? entry,
            CancellationToken cancellationToken) =>
            new(
                kind,
                entry,
                cancellationToken,
                new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously));

        public static StoreOperation CreateUnobservedAppend(LocalDiagnosticEvent entry) =>
            new(StoreOperationKind.Append, entry, CancellationToken.None, Completion: null);

        public static StoreOperation CreateOverflowSignal() =>
            new(StoreOperationKind.OverflowSignal, Entry: null, CancellationToken.None, Completion: null);
    }
}

[JsonSerializable(typeof(LocalDiagnosticEvent), TypeInfoPropertyName = "DiagnosticEvent")]
internal sealed partial class LocalDiagnosticsJsonContext : JsonSerializerContext
{
}
