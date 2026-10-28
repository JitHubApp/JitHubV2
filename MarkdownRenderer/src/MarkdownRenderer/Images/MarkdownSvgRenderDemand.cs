using System;
using System.Collections.Generic;
using System.Threading;
using MarkdownRenderer.Diagnostics;

namespace MarkdownRenderer.Images;

/// <summary>
/// The visibility of one SVG load or tile, which the view raises from
/// overscan to visible as the image scrolls into view. It never drops back.
/// Renderers that queue work read <see cref="Priority"/> and use
/// <see cref="RegisterPromotion"/> to move that work ahead.
/// </summary>
/// <remarks>
/// A demand holds no source, view or renderer. Only the view creates and
/// raises demands.
/// </remarks>
public sealed class MarkdownSvgRenderDemand : IMarkdownImageActivationDemand
{
    // Host demand has one queued operation owner at a time. This defensive
    // ceiling also bounds unexpected overlapping internal subscriptions.
    internal const int MaximumPromotionRegistrations = 64;
    private readonly object _gate = new();
    private readonly Func<bool>? _queueAdmissionProbe;
    private List<PromotionRegistration>? _registrations;
    private int _activeRegistrations;
    private int _visible;

    internal MarkdownSvgRenderDemand(MarkdownSvgRenderPriority priority)
        : this(priority, null)
    {
    }

    // A rejecting-only fault seam for deterministic component tests. Normal
    // hosts always use the constructor above and the real asynchronous queue.
    internal MarkdownSvgRenderDemand(MarkdownSvgRenderPriority priority, Func<bool>? queueAdmissionProbe)
    {
        if (priority is not (MarkdownSvgRenderPriority.Visible or MarkdownSvgRenderPriority.Overscan))
            throw new ArgumentOutOfRangeException(nameof(priority));
        _visible = priority == MarkdownSvgRenderPriority.Visible ? 1 : 0;
        _queueAdmissionProbe = queueAdmissionProbe;
    }

    /// <summary>Gets the current priority: overscan, or visible once the image is on screen.</summary>
    public MarkdownSvgRenderPriority Priority => Volatile.Read(ref _visible) != 0
        ? MarkdownSvgRenderPriority.Visible : MarkdownSvgRenderPriority.Overscan;

    internal long Version => Volatile.Read(ref _visible);

    bool IMarkdownImageActivationDemand.IsVisible => Volatile.Read(ref _visible) != 0;

    IDisposable IMarkdownImageActivationDemand.RegisterVisiblePromotion(Action callback) =>
        RegisterPromotion(callback);

    internal bool PromoteVisible()
    {
        if (Volatile.Read(ref _visible) != 0)
            return false;

        List<PromotionRegistration>? registrations;
        lock (_gate)
        {
            if (_visible != 0)
                return false;
            Volatile.Write(ref _visible, 1);
            registrations = _registrations;
            _registrations = null;
        }

        // Never invoke pool, lifetime or host code inline. Promotion may be
        // requested while the caller holds a publication or worker lock.
        if (registrations is not null)
        {
            foreach (PromotionRegistration registration in registrations)
            {
                try { registration.Queue(); }
                catch (Exception)
                {
                    registration.Dispose();
                    MarkdownDiagnostics.WriteLine("[SvgDemand] Promotion scheduling failed safely.");
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Registers a callback that runs once on the thread pool when this demand
    /// becomes visible, or soon after registration if it already is.
    /// </summary>
    /// <param name="callback">
    /// The callback. It runs on a thread-pool thread, never inline, so it may
    /// be registered while holding a lock. Exceptions it throws are logged and
    /// ignored.
    /// </param>
    /// <returns>A registration; disposing it cancels a callback that has not run.</returns>
    /// <exception cref="InvalidOperationException">Too many registrations are active on this demand.</exception>
    public IDisposable RegisterPromotion(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var registration = new PromotionRegistration(this, callback);
        bool queue;
        lock (_gate)
        {
            if (_activeRegistrations >= MaximumPromotionRegistrations)
                throw new InvalidOperationException("The SVG demand subscription limit was reached.");
            queue = _visible != 0;
            if (!queue)
                (_registrations ??= new()).Add(registration);
            _activeRegistrations++;
        }
        if (queue)
        {
            try { registration.Queue(); }
            catch { registration.Dispose(); throw; }
        }
        return registration;
    }

    private void Remove(PromotionRegistration registration)
    {
        lock (_gate)
        {
            _activeRegistrations--;
            _registrations?.Remove(registration);
            if (_registrations is { Count: 0 })
                _registrations = null;
        }
    }

    // Diagnostic unit seam, not a renderer or public theming API.
    internal int PendingPromotionCount
    {
        get { lock (_gate) return _registrations?.Count ?? 0; }
    }

    private sealed class PromotionRegistration(MarkdownSvgRenderDemand owner, Action callback) : IDisposable
    {
        private MarkdownSvgRenderDemand? _owner = owner;
        private Action? _callback = callback;
        private int _queued;

        internal void Queue()
        {
            if (Interlocked.Exchange(ref _queued, 1) == 0)
            {
                MarkdownSvgRenderDemand? owner = Volatile.Read(ref _owner);
                if (owner is null)
                    return;
                if (owner._queueAdmissionProbe?.Invoke() == false ||
                    !ThreadPool.UnsafeQueueUserWorkItem(static registration => registration.Invoke(),
                        this, preferLocal: false))
                    throw new InvalidOperationException("SVG promotion could not be queued.");
            }
        }

        private void Invoke()
        {
            Action? callback = Interlocked.Exchange(ref _callback, null);
            try
            {
                callback?.Invoke();
            }
            catch (Exception)
            {
                // A bad internal subscriber must not fail-fast the app or
                // prevent another queue subscriber from observing promotion.
                MarkdownDiagnostics.WriteLine("[SvgDemand] Promotion callback failed safely.");
            }
            finally { Interlocked.Exchange(ref _owner, null)?.Remove(this); }
        }

        public void Dispose()
        {
            // A callback already claimed by Invoke may finish after Dispose;
            // subscribers therefore check exact pending-work ownership. No
            // callback/source/host alias remains on a canceled registration.
            Interlocked.Exchange(ref _callback, null);
            Interlocked.Exchange(ref _owner, null)?.Remove(this);
        }
    }
}
