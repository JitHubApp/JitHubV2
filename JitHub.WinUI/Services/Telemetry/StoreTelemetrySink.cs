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
    private readonly StoreServicesCustomEventLogger? _logger;
#endif
    private readonly object _queueGate = new();
    private readonly HashSet<string> _pendingNames = new(StringComparer.Ordinal);
    private readonly Action<string>? _testLogger;
    private readonly Channel<string>? _queue;
    private readonly Task _dispatchTask;
    private readonly TimeSpan _dispatchInterval;
    private readonly string _availabilityStatus;
    private bool _acceptingEvents;
    private long _coalescedEventCount;
    private long _droppedEventCount;

    public StoreTelemetrySink()
    {
        _dispatchInterval = DefaultDispatchInterval;
#if STORE_ENGAGEMENT_AVAILABLE
        try
        {
            _logger = StoreServicesCustomEventLogger.GetDefault();
            _availabilityStatus = _logger is null
                ? "store_engagement_logger_unavailable"
                : "available";
        }
        catch (Exception exception)
        {
            _logger = null;
            _availabilityStatus = exception.GetType().Name;
        }
#else
        _availabilityStatus = "store_engagement_architecture_unavailable";
#endif

        if (HasLogger)
        {
            _queue = CreateQueue(DefaultQueueCapacity);
            _acceptingEvents = true;
            _dispatchTask = Task.Run(DispatchLoopAsync);
        }
        else
        {
            _dispatchTask = Task.CompletedTask;
        }
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

    public bool IsAvailable => HasLogger;

    public string AvailabilityStatus => _availabilityStatus;

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

    private bool HasLogger
    {
        get
        {
#if STORE_ENGAGEMENT_AVAILABLE
            return _logger is not null || _testLogger is not null;
#else
            return _testLogger is not null;
#endif
        }
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
        if (_testLogger is not null)
        {
            _testLogger(name);
            return;
        }

#if STORE_ENGAGEMENT_AVAILABLE
        _logger?.Log(name);
#endif
    }
}
