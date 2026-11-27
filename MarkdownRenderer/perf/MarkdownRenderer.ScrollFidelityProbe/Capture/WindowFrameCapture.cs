using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace MarkdownRenderer.ScrollFidelityProbe.Capture;

/// <summary>
/// Captures every frame the compositor presents for the probe window and,
/// while recording, reduces the measured viewport of each frame to its
/// uniform row runs. Frames arrive on a capture thread with their own Win2D
/// device, so analysis never runs on the UI thread or the renderer's device.
/// </summary>
internal sealed class WindowFrameCapture : IDisposable
{
    /// <summary>Columns each row is reduced to. Rows stay at full resolution.</summary>
    private const int SampleWidth = 192;

    private readonly CanvasDevice _device = new();
    private readonly object _gate = new();
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private Windows.Graphics.SizeInt32 _poolSize;
    private CanvasRenderTarget? _target;
    private Rect _region;
    private List<FrameSample>? _recording;
    private long _framesSeen;

    internal long FramesSeen => Interlocked.Read(ref _framesSeen);

    internal void Start(nint window)
    {
        _item = CaptureItemFactory.CreateForWindow(window);
        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = false;
        TryDisableBorder(_session);
        _session.StartCapture();
    }

    /// <summary>Sets the measured viewport, in physical pixels of the captured frame.</summary>
    internal void SetRegion(Rect region)
    {
        lock (_gate)
        {
            _region = region;
            _target?.Dispose();
            _target = null;
        }
    }

    internal void BeginRecording()
    {
        lock (_gate)
            _recording = [];
    }

    internal List<FrameSample> EndRecording()
    {
        lock (_gate)
        {
            List<FrameSample> frames = _recording ?? [];
            _recording = null;
            return frames;
        }
    }

    /// <summary>The most recent analyzed frame, or null before one arrives.</summary>
    internal FrameSample? LatestFrame { get; private set; }

    public void Dispose()
    {
        _session?.Dispose();
        if (_pool is not null)
        {
            _pool.FrameArrived -= OnFrameArrived;
            _pool.Dispose();
        }

        lock (_gate)
        {
            _target?.Dispose();
            _target = null;
        }

        _device.Dispose();
    }

    private static void TryDisableBorder(GraphicsCaptureSession session)
    {
        try
        {
            session.IsBorderRequired = false;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or NotSupportedException or InvalidCastException)
        {
            // The yellow border is drawn outside the client area and is not
            // part of the measured viewport, so capture proceeds with it.
            Debug.WriteLine($"[ScrollProbe] capture border stays: {exception.GetType().Name}");
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
        if (frame is null)
            return;

        long timestamp = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _framesSeen);
        if (frame.ContentSize.Width != _poolSize.Width || frame.ContentSize.Height != _poolSize.Height)
        {
            _poolSize = frame.ContentSize;
            sender.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        }

        lock (_gate)
        {
            if (_region.Width < 1 || _region.Height < 1)
                return;
            FrameSample sample = Analyze(frame, timestamp);
            LatestFrame = sample;
            _recording?.Add(sample);
        }
    }

    private FrameSample Analyze(Direct3D11CaptureFrame frame, long timestamp)
    {
        int height = (int)_region.Height;
        _target ??= new CanvasRenderTarget(_device, SampleWidth, height, 96);
        using (CanvasBitmap bitmap = CanvasBitmap.CreateFromDirect3D11Surface(_device, frame.Surface, 96))
        using (CanvasDrawingSession session = _target.CreateDrawingSession())
        {
            session.DrawImage(
                bitmap,
                new Rect(0, 0, SampleWidth, height),
                _region,
                1f,
                CanvasImageInterpolation.HighQualityCubic);
        }

        byte[] pixels = _target.GetPixelBytes();
        return new FrameSample(timestamp, height, ViewportRowAnalyzer.Analyze(pixels, SampleWidth, height));
    }
}
