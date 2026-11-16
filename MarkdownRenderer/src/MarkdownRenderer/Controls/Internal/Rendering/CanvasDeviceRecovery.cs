using System;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.Selection.Adorner;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Layout.Internal.RenderResources;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;

namespace MarkdownRenderer.Controls.Internal.Rendering;

/// <summary>
/// Recovers from a lost Win2D device. Device resources are released at
/// once and each lost device is told so, as Win2D's manual device-loss
/// contract requires; the rebuild waits on a backoff timer that doubles
/// from 250 ms up to 5 s and resets after a region paints again.
/// </summary>
internal sealed class CanvasDeviceRecovery
{
    private const int MaximumAttempt = 6;

    private readonly ICanvasDeviceRecoveryHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly MarkdownViewSurface _surface;
    private readonly SelectionAdornerPresenter _adorner;
    private readonly DispatcherQueue _dispatcher;

    private bool _queued;
    private int _attempt;
    private DispatcherQueueTimer? _timer;

    internal CanvasDeviceRecovery(
        ICanvasDeviceRecoveryHost host,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        MarkdownViewSurface surface,
        SelectionAdornerPresenter adorner,
        DispatcherQueue dispatcher)
    {
        _host = host;
        _lifetime = lifetime;
        _published = published;
        _surface = surface;
        _adorner = adorner;
        _dispatcher = dispatcher;
    }

    /// <summary>True while a recovery rebuild waits on its backoff timer.</summary>
    internal bool IsQueued => _queued;

    internal void HandleDeviceLost(Exception exception)
        => HandleDeviceLost(exception.HResult);

    /// <summary>
    /// Releases device resources after a failure with
    /// <paramref name="failureHResult"/> and schedules the recovery rebuild.
    /// </summary>
    internal void HandleDeviceLost(int failureHResult)
    {
        // Capture every device in use before release clears the surface owner.
        CanvasDevice? layoutDevice = _published.LayoutDevice;
        CanvasDevice? surfaceDevice = _adorner.SurfaceDevice;
        CanvasDevice? canvasDevice = null;
        try { canvasDevice = _surface.Canvas?.Device; }
        catch (Exception deviceException)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] canvas device query failed: {deviceException.Message}");
        }

        MarkdownRenderResources? resources = _published.RenderResources;
        _adorner.TryReleaseSurface();
        // Snapshot bitmaps are cached under the layout (shared) device, which
        // need not be the CanvasVirtualControl device.
        ReleaseImageDeviceResources(resources, layoutDevice);
        if (!ReferenceEquals(canvasDevice, layoutDevice))
            ReleaseImageDeviceResources(resources, canvasDevice);
        // Exceptions were caught instead of reaching Win2D, so notify each
        // genuinely lost device as the manual device-loss contract requires.
        // Every other cache holding bitmaps on a lost device watches it and
        // releases them when it is notified.
        NotifyLostDevices(failureHResult, layoutDevice, surfaceDevice, canvasDevice);

        if (_queued || _lifetime.IsDisposed || _lifetime.IsUnloaded)
            return;

        _attempt = Math.Min(_attempt + 1, MaximumAttempt);
        int delayMs = Math.Min(5000, 250 << (_attempt - 1));

        MarkdownDiagnostics.WriteLine(
            "[MarkdownRendererControl] Win2D device lost while painting; " +
            $"HRESULT={GraphicsDeviceErrors.FormatHResult(failureHResult)}. " +
            $"Retrying canvas rebuild in {delayMs}ms.");

        try
        {
            var timer = _dispatcher.CreateTimer();
            timer.IsRepeating = false;
            timer.Interval = TimeSpan.FromMilliseconds(delayMs);
            timer.Tick += OnTimer;
            _timer = timer;
            _queued = true;
            timer.Start();
        }
        catch (Exception schedulingFailure)
        {
            Stop();
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] device recovery timer failed: {schedulingFailure.Message}");
            _host.RequestFullRebuild();
        }
    }

    /// <summary>A region painted: the device works again, so the backoff restarts.</summary>
    internal void NotePainted()
    {
        _attempt = 0;
        Stop();
    }

    internal void Stop()
    {
        var timer = _timer;
        _timer = null;
        _queued = false;
        if (timer is null)
            return;

        // Clear ownership first, including when the dispatcher is shutting
        // down. No background closure or retry loop retains this control.
        try { timer.Stop(); }
        catch (Exception) { }
        finally
        {
            try { timer.Tick -= OnTimer; }
            catch (Exception) { }
        }
    }

    private void OnTimer(DispatcherQueueTimer sender, object args)
    {
        // A stopped timer can already have a tick queued. It must not clear a
        // newer recovery after unload/reload or a subsequent device failure.
        if (!ReferenceEquals(sender, _timer))
            return;

        Stop();
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded)
            return;

        _host.RequestFullRebuild();
        _host.InvalidateCanvas();
        _adorner.Invalidate();
    }

    private static void ReleaseImageDeviceResources(MarkdownRenderResources? resources, CanvasDevice? device)
    {
        if (resources is null || device is null)
            return;

        try
        {
            resources.ReleaseDevice(device);
        }
        catch (Exception releaseException)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] image device-resource release failed: {releaseException.Message}");
        }
    }

    private static void NotifyLostDevices(int failureHResult, params CanvasDevice?[] devices)
    {
        for (int index = 0; index < devices.Length; index++)
        {
            CanvasDevice? device = devices[index];
            if (device is null || Array.IndexOf(devices, device) != index)
                continue;

            try
            {
                if (device.IsDeviceLost(failureHResult))
                    device.RaiseDeviceLost();
            }
            catch (Exception notifyException)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownRendererControl] device-lost notification failed: {notifyException.Message}");
            }
        }
    }
}
