using System;
using System.Threading;

namespace JitHub.Services.CodeViewer;

internal sealed partial class SvgPreviewRequestGate : IDisposable
{
    private readonly object _sync = new();
    private long _generation;
    private SvgPreviewRequest? _current;
    private bool _disposed;

    public SvgPreviewRequest Begin()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _current?.Cancel();
            _current = new SvgPreviewRequest(++_generation);
            return _current;
        }
    }

    public bool IsCurrent(SvgPreviewRequest request)
    {
        lock (_sync)
        {
            return !_disposed &&
                ReferenceEquals(_current, request) &&
                !request.CancellationToken.IsCancellationRequested;
        }
    }

    public void Complete(SvgPreviewRequest request)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_current, request))
            {
                _current = null;
            }
        }

        request.Dispose();
    }

    public void CancelCurrent()
    {
        SvgPreviewRequest? request;
        lock (_sync)
        {
            request = _current;
            _current = null;
            _generation++;
        }

        request?.Cancel();
    }

    public void Dispose()
    {
        SvgPreviewRequest? request;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            request = _current;
            _current = null;
            _generation++;
        }

        request?.Cancel();
    }
}

internal sealed partial class SvgPreviewRequest : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private int _disposed;

    public SvgPreviewRequest(long generation)
    {
        Generation = generation;
    }

    public long Generation { get; }

    public CancellationToken CancellationToken => _cancellation.Token;

    public void Cancel()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _cancellation.Dispose();
        }
    }
}
