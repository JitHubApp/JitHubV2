using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Views.Controls.App;

internal enum SlideDrawerEdge
{
    Left,
    Right
}

internal sealed class SlideDrawerAnimator
{
    private const double DefaultDurationMilliseconds = 220;

    private readonly TranslateTransform _transform;
    private readonly SlideDrawerEdge _edge;
    private readonly Func<double> _getExtent;
    private readonly DispatcherTimer _timer = new();
    private readonly double _durationMilliseconds;
    private DateTimeOffset _startedAt;
    private double _from;
    private double _to;
    private double _offset;
    private Action? _completed;

    public SlideDrawerAnimator(
        TranslateTransform transform,
        SlideDrawerEdge edge,
        Func<double> getExtent,
        double durationMilliseconds = DefaultDurationMilliseconds)
    {
        _transform = transform;
        _edge = edge;
        _getExtent = getExtent;
        _durationMilliseconds = durationMilliseconds;
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += OnTick;
        _offset = ClosedOffset;
        ApplyOffset(_offset);
    }

    public bool IsOpen { get; private set; }

    public bool IsAnimating => _timer.IsEnabled;

    public void SetOpen(bool open, bool animate = true, Action? completed = null)
    {
        _timer.Stop();
        _completed = completed;
        IsOpen = open;

        double target = open ? 0 : ClosedOffset;
        if (!animate)
        {
            Complete(target);
            return;
        }

        _from = _offset;
        _to = target;
        if (Math.Abs(_from - _to) < 0.5)
        {
            Complete(_to);
            return;
        }

        _startedAt = DateTimeOffset.UtcNow;
        _timer.Start();
    }

    public void SyncToCurrentState()
    {
        if (_timer.IsEnabled)
        {
            return;
        }

        ApplyOffset(IsOpen ? 0 : ClosedOffset);
    }

    public void Stop()
    {
        _timer.Stop();
        _completed = null;
    }

    private double ClosedOffset
    {
        get
        {
            double extent = _getExtent();
            return _edge == SlideDrawerEdge.Left ? -extent : extent;
        }
    }

    private void OnTick(object? sender, object e)
    {
        double elapsed = (DateTimeOffset.UtcNow - _startedAt).TotalMilliseconds;
        double progress = Math.Clamp(elapsed / _durationMilliseconds, 0, 1);
        double easedProgress = 1 - Math.Pow(1 - progress, 3);
        double offset = _from + ((_to - _from) * easedProgress);
        ApplyOffset(offset);

        if (progress >= 1)
        {
            _timer.Stop();
            Complete(_to);
        }
    }

    private void Complete(double finalOffset)
    {
        ApplyOffset(finalOffset);
        Action? completed = _completed;
        _completed = null;
        completed?.Invoke();
    }

    private void ApplyOffset(double offset)
    {
        double extent = _getExtent();
        _offset = _edge == SlideDrawerEdge.Left
            ? Math.Clamp(offset, -extent, 0)
            : Math.Clamp(offset, 0, extent);
        _transform.X = _offset;
    }
}
