using System;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Hosting;

/// <summary>
/// Owns the single active asynchronous realization attempt for a virtualized
/// hosted element. Cancellation detaches the attempt immediately so a newly
/// visible element can retry without waiting for an uncooperative factory.
/// </summary>
internal sealed class HostedElementRealizationGate<TRequest>
    where TRequest : class
{
    private readonly object _gate = new();
    private Operation? _active;

    internal bool HasActive
    {
        get
        {
            lock (_gate)
                return _active is not null;
        }
    }

    internal bool TryBegin(
        TRequest request,
        CancellationToken lifetimeToken,
        out Operation operation)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            if (_active is { } active && !active.CancellationToken.IsCancellationRequested)
            {
                operation = null!;
                return false;
            }

            // The parent lifetime can cancel an operation independently of
            // CancelActive. Treat that operation as detached immediately when
            // the next visible-band pass asks to realize again; its eventual
            // completion remains stale and owns its own disposal.
            _active = null;

            operation = new Operation(request, lifetimeToken);
            _active = operation;
            return true;
        }
    }

    /// <summary>
    /// Detaches and cancels the active operation. The completing continuation
    /// retains ownership of disposal because a factory may still be using its
    /// token after observing cancellation.
    /// </summary>
    internal void CancelActive()
    {
        Operation? operation;
        lock (_gate)
        {
            operation = _active;
            _active = null;
        }

        operation?.Cancel();
    }

    /// <summary>
    /// Atomically claims a completion for attachment. Stale or cancelled
    /// operations return false without disturbing a newer active attempt.
    /// </summary>
    internal bool TryClaimCompletion(Operation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        lock (_gate)
        {
            if (!ReferenceEquals(_active, operation))
                return false;

            _active = null;
            return !operation.CancellationToken.IsCancellationRequested;
        }
    }

    internal sealed class Operation : IDisposable
    {
        private readonly object _gate = new();
        private CancellationTokenSource? _cancellation;
        private readonly CancellationToken _token;
        private CancellationTokenRegistration _lifetimeRegistration;
        private bool _hasLifetimeRegistration;
        private Task _cancellationCallbacks = Task.CompletedTask;
        private Task _retirement = Task.CompletedTask;

        internal Operation(TRequest request, CancellationToken lifetimeToken)
        {
            Request = request;
            _cancellation = new CancellationTokenSource();
            _token = _cancellation.Token;
            if (lifetimeToken.CanBeCanceled)
            {
                // Keep the child source independently owned. A linked CTS does
                // not expose the completion of callbacks started by its parent,
                // which makes safe nonblocking disposal impossible to prove.
                // This registration only initiates CancelAsync, so even a
                // synchronous parent Cancel never runs factory callbacks inline.
                _lifetimeRegistration = lifetimeToken.UnsafeRegister(
                    static state => ((Operation)state!).Cancel(),
                    this);
                _hasLifetimeRegistration = true;
            }
        }

        internal TRequest Request { get; }

        internal CancellationToken CancellationToken => _token;

        internal Task Retirement
        {
            get
            {
                lock (_gate)
                    return _retirement;
            }
        }

        internal void Cancel()
        {
            lock (_gate)
            {
                if (_cancellation is null || _token.IsCancellationRequested)
                    return;

                _cancellationCallbacks =
                    CancellationTokenSourceRetirement.RequestCancellation(_cancellation);
            }
        }

        public void Dispose()
        {
            CancellationTokenSource? cancellation;
            Task cancellationCallbacks;
            CancellationTokenRegistration lifetimeRegistration;
            bool hasLifetimeRegistration;
            lock (_gate)
            {
                cancellation = _cancellation;
                if (cancellation is null)
                    return;

                _cancellation = null;
                cancellationCallbacks = _cancellationCallbacks;
                lifetimeRegistration = _lifetimeRegistration;
                hasLifetimeRegistration = _hasLifetimeRegistration;
                _lifetimeRegistration = default;
                _hasLifetimeRegistration = false;
            }

            Task lifetimeRegistrationRetirement = hasLifetimeRegistration
                ? lifetimeRegistration.DisposeAsync().AsTask()
                : Task.CompletedTask;
            Task retirement = CancellationTokenSourceRetirement.DisposeAfter(
                cancellation,
                cancellationCallbacks,
                lifetimeRegistrationRetirement);

            lock (_gate)
                _retirement = retirement;
        }
    }
}
