using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace JitHub.Core.Services.Telemetry;

public interface IStoreTelemetrySink
{
    bool IsAvailable { get; }

    string AvailabilityStatus { get; }

    void TrackEvent(string name);
}

/// <summary>
/// Forwards allow-listed event names to the Microsoft Store engagement logger
/// on a paced background queue. The app owns the optional Store Engagement
/// framework: it resolves the native logger and hands this sink its
/// <c>Log</c> method together with the availability it observed.
/// </summary>
public sealed class StoreTelemetrySink : IStoreTelemetrySink
{
    private const int DefaultQueueCapacity = 256;
    private static readonly TimeSpan DefaultDispatchInterval = TimeSpan.FromSeconds(1);

    private readonly object _queueGate = new();
    private readonly HashSet<string> _pendingNames = new(StringComparer.Ordinal);
    private readonly Action<string>? _log;
    private readonly Channel<string>? _queue;
    private readonly Task _dispatchTask;
    private readonly TimeSpan _dispatchInterval;
    private readonly string _availabilityStatus;
    private bool _acceptingEvents;
    private long _coalescedEventCount;
    private long _droppedEventCount;

    /// <summary>
    /// Creates a sink that dispatches through <paramref name="log"/>, or that
    /// drops every event when no logger is available.
    /// </summary>
    /// <param name="log">The native logger's log method, or <see langword="null"/> when there is none.</param>
    /// <param name="availabilityStatus">Why the logger is or is not available, as reported by diagnostics.</param>
    public StoreTelemetrySink(Action<string>? log, string availabilityStatus)
        : this(log, availabilityStatus, DefaultDispatchInterval, DefaultQueueCapacity)
    {
    }

    internal StoreTelemetrySink(
        Action<string>? logger,
        TimeSpan dispatchInterval,
        int queueCapacity = DefaultQueueCapacity)
        : this(
            logger,
            logger is null ? "store_engagement_logger_unavailable" : "available",
            dispatchInterval,
            queueCapacity)
    {
    }

    private StoreTelemetrySink(
        Action<string>? log,
        string availabilityStatus,
        TimeSpan dispatchInterval,
        int queueCapacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(availabilityStatus);
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);
        if (dispatchInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(dispatchInterval));
        }

        _log = log;
        _dispatchInterval = dispatchInterval;
        _availabilityStatus = availabilityStatus;
        if (log is not null)
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

    private bool HasLogger => _log is not null;

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

    private void DispatchEvent(string name) => _log?.Invoke(name);
}
