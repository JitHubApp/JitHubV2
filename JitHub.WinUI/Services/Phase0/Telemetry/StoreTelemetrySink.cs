using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
#if STORE_ENGAGEMENT_AVAILABLE
using Microsoft.Services.Store.Engagement;
#endif

namespace JitHub.Services;

public interface IStoreTelemetrySink
{
    bool IsAvailable { get; }

    string AvailabilityStatus { get; }

    void TrackEvent(string name);
}

public sealed class StoreTelemetrySink : IStoreTelemetrySink
{
    private const int DefaultQueueCapacity = 256;
    private static readonly TimeSpan DefaultDispatchInterval = TimeSpan.FromSeconds(1);

#if STORE_ENGAGEMENT_AVAILABLE
    private StoreServicesCustomEventLogger? _logger;
#endif
    private readonly object _queueGate = new();
    private readonly HashSet<string> _pendingNames = new(StringComparer.Ordinal);
    private Action<string>? _testLogger;
    private readonly Func<Action<string>?>? _testLoggerFactory;
    private readonly Channel<string>? _queue;
    private readonly Task _dispatchTask;
    private readonly TimeSpan _dispatchInterval;
    private string _availabilityStatus;
    // 0 = initialization pending, 1 = available, 2 = unavailable. The Store
    // API can perform receipt/network work in GetDefault(), so initialization
    // must remain on the bounded dispatch worker and never a UI/DI caller.
    private int _loggerInitializationState;
    private bool _acceptingEvents;
    private long _coalescedEventCount;
    private long _droppedEventCount;

    public StoreTelemetrySink()
    {
        _dispatchInterval = DefaultDispatchInterval;
#if STORE_ENGAGEMENT_AVAILABLE
        _availabilityStatus = "initializing";
        _queue = CreateQueue(DefaultQueueCapacity);
        _acceptingEvents = true;
        _dispatchTask = Task.Run(DispatchLoopAsync);
#else
        _availabilityStatus = "store_engagement_architecture_unavailable";
        _loggerInitializationState = 2;
        _dispatchTask = Task.CompletedTask;
#endif
    }

    internal StoreTelemetrySink(
        Action<string>? logger,
        TimeSpan dispatchInterval,
        int queueCapacity = DefaultQueueCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);
        if (dispatchInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(dispatchInterval));
        }

        _testLogger = logger;
        _dispatchInterval = dispatchInterval;
        _availabilityStatus = logger is null ? "store_engagement_logger_unavailable" : "available";
        _loggerInitializationState = logger is null ? 2 : 1;
        if (logger is not null)
        {
            _queue = CreateQueue(queueCapacity);
            _acceptingEvents = true;
            _dispatchTask = Task.Run(DispatchLoopAsync);
        }
        else
        {
            _dispatchTask = Task.CompletedTask;
        }
    }

    internal StoreTelemetrySink(
        Func<Action<string>?> loggerFactory,
        TimeSpan dispatchInterval,
        int queueCapacity = DefaultQueueCapacity)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);
        if (dispatchInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(dispatchInterval));
        }

        _testLoggerFactory = loggerFactory;
        _dispatchInterval = dispatchInterval;
        _availabilityStatus = "initializing";
        _queue = CreateQueue(queueCapacity);
        _acceptingEvents = true;
        _dispatchTask = Task.Run(DispatchLoopAsync);
    }

    public bool IsAvailable => Volatile.Read(ref _loggerInitializationState) != 2;

    public string AvailabilityStatus => Volatile.Read(ref _availabilityStatus);

    internal long CoalescedEventCount => Interlocked.Read(ref _coalescedEventCount);

    internal long DroppedEventCount => Interlocked.Read(ref _droppedEventCount);

    internal int PendingEventCount
    {
        get
        {
            lock (_queueGate)
            {
                return _pendingNames.Count;
            }
        }
    }

    public void TrackEvent(string name)
    {
        if (!TelemetrySanitizer.IsStoreEventAllowed(name))
        {
            return;
        }

        Channel<string>? queue = _queue;
        if (queue is null)
        {
            return;
        }

        lock (_queueGate)
        {
            if (!_acceptingEvents)
            {
                return;
            }

            if (!_pendingNames.Add(name))
            {
                Interlocked.Increment(ref _coalescedEventCount);
                return;
            }

            if (!queue.Writer.TryWrite(name))
            {
                _pendingNames.Remove(name);
                Interlocked.Increment(ref _droppedEventCount);
            }
        }
    }

    internal async Task<bool> WaitForIdleAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (PendingEventCount > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopwatch.Elapsed >= timeout)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private static Channel<string> CreateQueue(int capacity) =>
        Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    private async Task DispatchLoopAsync()
    {
        Channel<string>? queue = _queue;
        if (queue is null)
        {
            return;
        }

        bool dispatchedAny = false;
        try
        {
            while (await queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (queue.Reader.TryRead(out string? name))
                {
                    if (dispatchedAny && _dispatchInterval > TimeSpan.Zero)
                    {
                        await Task.Delay(_dispatchInterval).ConfigureAwait(false);
                    }

                    try
                    {
                        DispatchEvent(name);
                    }
                    catch
                    {
                        // The optional Store framework must remain outside product failure boundaries.
                    }
                    finally
                    {
                        lock (_queueGate)
                        {
                            _pendingNames.Remove(name);
                        }
                    }

                    dispatchedAny = true;
                }
            }
        }
        catch
        {
            // Keep faults observed; local diagnostics remain the authoritative fallback.
        }
    }

    private void DispatchEvent(string name)
    {
        EnsureLoggerInitialized();

        if (_testLogger is not null)
        {
            _testLogger(name);
            return;
        }

#if STORE_ENGAGEMENT_AVAILABLE
        _logger?.Log(name);
#endif
    }

    private void EnsureLoggerInitialized()
    {
        if (Volatile.Read(ref _loggerInitializationState) != 0)
        {
            return;
        }

        try
        {
            if (_testLoggerFactory is not null)
            {
                _testLogger = _testLoggerFactory();
                PublishInitializationResult(
                    _testLogger is not null,
                    "store_engagement_logger_unavailable");
                return;
            }

#if STORE_ENGAGEMENT_AVAILABLE
            _logger = StoreServicesCustomEventLogger.GetDefault();
            PublishInitializationResult(
                _logger is not null,
                "store_engagement_logger_unavailable");
#else
            PublishInitializationResult(
                available: false,
                "store_engagement_architecture_unavailable");
#endif
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _availabilityStatus, exception.GetType().Name);
            Volatile.Write(ref _loggerInitializationState, 2);
        }
    }

    private void PublishInitializationResult(bool available, string unavailableStatus)
    {
        Volatile.Write(
            ref _availabilityStatus,
            available ? "available" : unavailableStatus);
        Volatile.Write(ref _loggerInitializationState, available ? 1 : 2);
    }
}
