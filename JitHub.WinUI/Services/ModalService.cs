using System;
using System.Windows.Input;
using JitHub.Models.NavArgs;
using Microsoft.UI.Xaml;

namespace JitHub.Services;

public class ModalService
{
    public event EventHandler? DismissalStateChanged;

    private readonly object _initializationGate = new();
    private readonly ModalOperationGate _operationGate = new();
    private readonly DialogPresentationCoordinator _presentationCoordinator;
    private ICommand? _open;
    private ICommand? _close;
    private bool _initialized;
    private ModalSession? _currentSession;

    public ModalService(DialogPresentationCoordinator presentationCoordinator)
    {
        _presentationCoordinator = presentationCoordinator ??
            throw new ArgumentNullException(nameof(presentationCoordinator));
    }

    public void Init(ICommand open, ICommand close)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(close);

        ICommand? previousClose = null;
        ModalSession? previousSession = null;
        lock (_initializationGate)
        {
            if (_currentSession is not null)
            {
                previousSession = _currentSession;
                previousClose = _close;
                _currentSession = null;
            }

            _operationGate.Reset();
            _open = open;
            _close = close;
            _initialized = true;
        }

        if (previousSession is null)
        {
            return;
        }

        try
        {
            if (previousClose?.CanExecute(null) == true)
            {
                previousClose.Execute(null);
            }
        }
        finally
        {
            previousSession.MarkClosed();
            _presentationCoordinator.Complete(previousSession.PresentationLease);
            NotifyDismissalStateChanged();
        }
    }

    public void Open(string title, FrameworkElement element)
    {
        Open(title, element, false);
    }

    public void Open(string title, FrameworkElement element, bool useHeader)
    {
        _ = TryOpenSession(title, element, useHeader, callback: null);
    }

    public void Open(FrameworkElement element)
    {
        _ = TryOpenSession(string.Empty, element, useHeader: true, callback: null);
    }

    public void Open(string title, FrameworkElement element, ICommand callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _ = TryOpenSession(title, element, useHeader: false, callback);
    }

    public ModalSession? TryOpenSession(
        string title,
        FrameworkElement element,
        bool useHeader = false,
        ICommand? callback = null) =>
        TryOpenSessionCore(title, element, useHeader, callback);

    public void Close()
    {
        _ = TryClose(expectedSession: null);
    }

    public bool TryClose(ModalSession? expectedSession)
    {
        ICommand? close;
        ModalSession? session;
        lock (_initializationGate)
        {
            if (!_initialized || _close?.CanExecute(null) != true)
            {
                return false;
            }

            session = _currentSession;
            if (session is null ||
                (expectedSession is not null && !ReferenceEquals(expectedSession, session)) ||
                !session.TryBeginClose())
            {
                return false;
            }
            close = _close;
        }

        if (!_operationGate.TryComplete(session.OperationLease, out ICommand? callback))
        {
            session.RestoreAfterCloseFailure();
            return false;
        }

        try
        {
            close.Execute(null);
        }
        catch
        {
            _operationGate.RestoreAfterCloseFailure(session.OperationLease, callback);
            session.RestoreAfterCloseFailure();
            throw;
        }

        lock (_initializationGate)
        {
            if (ReferenceEquals(_currentSession, session))
            {
                _currentSession = null;
            }
        }
        session.MarkClosed();
        _presentationCoordinator.Complete(session.PresentationLease);
        NotifyDismissalStateChanged();

        if (callback?.CanExecute(null) == true)
        {
            callback.Execute(null);
        }

        return true;
    }

    internal bool IsOpen
    {
        get
        {
            return _operationGate.IsOpen;
        }
    }

    public bool CanDismiss
    {
        get
        {
            lock (_initializationGate)
            {
                return _currentSession is { IsActive: true, IsMutationInProgress: false };
            }
        }
    }

    internal void NotifyDismissalStateChanged() =>
        DismissalStateChanged?.Invoke(this, EventArgs.Empty);

    private ModalSession? TryOpenSessionCore(
        string title,
        FrameworkElement element,
        bool useHeader,
        ICommand? callback)
    {
        ArgumentNullException.ThrowIfNull(element);

        var arg = new ModalArg
        {
            Title = title,
            Content = element,
            UseHeader = useHeader
        };
        ICommand? open;
        ICommand? close;
        lock (_initializationGate)
        {
            if (!_initialized || _open?.CanExecute(arg) != true)
            {
                return null;
            }

            open = _open;
            close = _close;
        }

        if (!_presentationCoordinator.TryBegin(DialogPresentationKind.ShellOverlay, out long presentationLease))
        {
            return null;
        }

        if (!_operationGate.TryBegin(callback, out long operationLease))
        {
            _presentationCoordinator.Complete(presentationLease);
            return null;
        }

        ModalSession? openedSession = null;
        try
        {
            open.Execute(arg);
            ModalSession session = new(this, operationLease, presentationLease);
            openedSession = session;
            lock (_initializationGate)
            {
                _currentSession = session;
            }

            if (element is IModalSessionAware aware)
            {
                aware.AttachModalSession(session);
            }

            NotifyDismissalStateChanged();
            return session;
        }
        catch
        {
            openedSession?.MarkClosed();
            lock (_initializationGate)
            {
                _currentSession = null;
            }
            _operationGate.Abort(operationLease);
            _presentationCoordinator.Complete(presentationLease);
            try
            {
                if (close?.CanExecute(null) == true)
                {
                    close.Execute(null);
                }
            }
            catch (Exception cleanupException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Shell dialog host cleanup failed after an open error: {cleanupException}");
            }
            throw;
        }
    }
}
