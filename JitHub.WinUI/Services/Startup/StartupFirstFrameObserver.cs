using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace JitHub.WinUI.Services.Startup;

/// <summary>
/// Reports when the first page has loaded and rendered, so the splash is
/// replaced by real content instead of an empty window.
/// </summary>
internal sealed class StartupFirstFrameObserver
{
    private const int RenderedFramesBeforeReady = 2;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    private readonly Frame _contentFrame;
    private readonly Action _ready;
    private readonly DispatcherQueueTimer _timeoutTimer;
    private FrameworkElement? _page;
    private bool _renderingHooked;
    private bool _finished;
    private int _renderedFrames;

    public StartupFirstFrameObserver(Frame contentFrame, DispatcherQueue dispatcherQueue, Action ready)
    {
        _contentFrame = contentFrame;
        _ready = ready;
        _timeoutTimer = dispatcherQueue.CreateTimer();
        _timeoutTimer.Interval = Timeout;
        _timeoutTimer.IsRepeating = false;
        _timeoutTimer.Tick += TimeoutTimer_Tick;
        _contentFrame.Navigated += ContentFrame_Navigated;
        _timeoutTimer.Start();
    }

    public void Stop()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _timeoutTimer.Stop();
        _timeoutTimer.Tick -= TimeoutTimer_Tick;
        _contentFrame.Navigated -= ContentFrame_Navigated;
        if (_page is not null)
        {
            _page.Loaded -= Page_Loaded;
            _page = null;
        }

        if (_renderingHooked)
        {
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            _renderingHooked = false;
        }
    }

    private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        if (_page is not null)
        {
            _page.Loaded -= Page_Loaded;
            _page = null;
        }

        if (e.Content is not FrameworkElement page)
        {
            return;
        }

        if (page.IsLoaded)
        {
            HookRendering();
            return;
        }

        _page = page;
        _page.Loaded += Page_Loaded;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e) => HookRendering();

    private void HookRendering()
    {
        if (_renderingHooked || _finished)
        {
            return;
        }

        _renderingHooked = true;
        CompositionTarget.Rendering += CompositionTarget_Rendering;
    }

    private void CompositionTarget_Rendering(object? sender, object e)
    {
        _renderedFrames++;
        if (_renderedFrames >= RenderedFramesBeforeReady)
        {
            Finish();
        }
    }

    private void TimeoutTimer_Tick(DispatcherQueueTimer sender, object args) => Finish();

    private void Finish()
    {
        if (_finished)
        {
            return;
        }

        Stop();
        _ready();
    }
}
