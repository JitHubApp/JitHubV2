namespace MarkdownRenderer.Mermaid.Internal.Rendering;

/// <summary>
/// Defers disposal of a render semaphore until every operation that could wait
/// on or release it has left. <see cref="SemaphoreSlim.Dispose()"/> is not safe to
/// race with its other members, so renderer disposal must coordinate that
/// lifetime explicitly.
/// </summary>
internal sealed class MermaidRenderLifetime : IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _renderGate;
    private readonly Action? _cleanup;
    private int _operationCount;
    private bool _disposeRequested;
    private bool _renderGateDisposed;

    public MermaidRenderLifetime(SemaphoreSlim renderGate, Action? cleanup = null)
    {
        _renderGate = renderGate ?? throw new ArgumentNullException(nameof(renderGate));
        _cleanup = cleanup;
    }

    internal bool IsRenderGateDisposed
    {
        get
        {
            lock (_sync)
            {
                return _renderGateDisposed;
            }
        }
    }

    public Lease Enter()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            checked
            {
                _operationCount++;
            }

            return new Lease(this);
        }
    }

    private Lease ForkExistingLease()
    {
        lock (_sync)
        {
            // A renderer may be disposed after a request entered but before it
            // knows whether it will become the shared producer. The existing
            // lease keeps cleanup deferred, so that admitted request may take a
            // sibling reference even after disposal has been requested.
            if (_renderGateDisposed || _operationCount <= 0)
                throw new ObjectDisposedException(nameof(MermaidRenderLifetime));
            checked
            {
                _operationCount++;
            }

            return new Lease(this);
        }
    }

    public void Dispose()
    {
        bool disposeRenderGate = false;
        lock (_sync)
        {
            if (_disposeRequested)
            {
                return;
            }

            _disposeRequested = true;
            if (_operationCount == 0)
            {
                _renderGateDisposed = true;
                disposeRenderGate = true;
            }
        }

        if (disposeRenderGate)
        {
            _renderGate.Dispose();
            _cleanup?.Invoke();
        }
    }

    private void Exit()
    {
        bool disposeRenderGate = false;
        lock (_sync)
        {
            if (_operationCount <= 0)
            {
                throw new InvalidOperationException("The Mermaid render lifetime lease is unbalanced.");
            }

            _operationCount--;
            if (_disposeRequested && _operationCount == 0)
            {
                _renderGateDisposed = true;
                disposeRenderGate = true;
            }
        }

        if (disposeRenderGate)
        {
            _renderGate.Dispose();
            _cleanup?.Invoke();
        }
    }

    internal sealed class Lease : IDisposable
    {
        private MermaidRenderLifetime? _owner;

        public Lease(MermaidRenderLifetime owner)
        {
            _owner = owner;
        }

        internal Lease Fork()
        {
            MermaidRenderLifetime? owner = Volatile.Read(ref _owner);
            ObjectDisposedException.ThrowIf(owner is null, this);
            return owner.ForkExistingLease();
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Exit();
        }
    }
}
