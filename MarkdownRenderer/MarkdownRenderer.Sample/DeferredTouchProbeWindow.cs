using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Layout;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

// This opt-in regression host intentionally tests the common control internals.
#pragma warning disable MR1001

namespace MarkdownRenderer.Sample;

/// <summary>
/// Real WinUI host for P1-005/006. Only the dedicated launch argument opens it.
/// Input must still arrive as real routed touch, normally injected in Sandbox.
/// The probe never synthesizes PointerInput or changes selection itself.
/// </summary>
internal sealed class DeferredTouchProbeWindow : Window
{
    private readonly MarkdownScrollView _owned = new();
    private readonly MarkdownDocumentView _document = new();
    private readonly ScrollViewer _ancestor = new();
    private readonly Grid _host = new();
    private readonly CheckBox _viewport = new() { Content = "Use ancestor-owned viewport", VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _releaseButton = new() { Content = "Arm deferred release", VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _cancelButton = new() { Content = "Arm deferred cancel", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _receipt = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    // Raw-only diagnostics are deliberately outside the reading/focus order.
    // They observe real routed input without changing selection or capture.
    private readonly TextBlock _touchContactStatus = new() { Opacity = 0, IsHitTestVisible = false, Width = 1, Height = 1 };
    private readonly TouchContactObservation _touchContactObservation;
    private readonly DispatcherQueueTimer _timer;
    private BackgroundInteractionLease? _lease;
    private LayoutSnapshot? _heldSnapshot;
    private DeferredTouchProbeState? _preContact;
    private DeferredTouchProbeState? _before;
    private DeferredTouchProbeState? _queued;
    private DeferredTouchProbeState? _after;
    private DeferredTouchProbeState? _fresh;
    private DeferredTouchProbeState? _live;
    private long _settleDeadline;
    // Bound for work resumed by the dropped terminal to drain after the fresh contact.
    private const long FreshContactSettleMilliseconds = 2_000;
    private string _phase = "idle";
    private string? _failure;
    private bool _usesAncestor;
    private bool _canceled;
    private bool _hadSelectionBeforeContact;
    private bool _workerHeldAtDrop;
    private bool _closed;
    private uint _pointerId;
    private long _gestureEpoch;
    private long _deadline;
    private int _sequence;

    internal DeferredTouchProbeWindow()
    {
        Title = "MarkdownRenderer deferred-touch probe";
        const string markdown = "# Deferred touch regression\n\nCommit target: committedword.\n\nHeld target: deferredword.\n\nFresh target: freshword.\n\nTerminal delivery must queue behind a worker-held snapshot, then retire through the real control failure callback.";
        _owned.Markdown = _document.Markdown = markdown;
        AutomationProperties.SetAutomationId(_owned, "MarkdownRenderer");
        AutomationProperties.SetAutomationId(_document, "MarkdownRenderer");
        AutomationProperties.SetAutomationId(_viewport, "DeferredTouchAncestorViewport");
        AutomationProperties.SetAutomationId(_releaseButton, "DeferredTouchArmRelease");
        AutomationProperties.SetAutomationId(_cancelButton, "DeferredTouchArmCancel");
        AutomationProperties.SetAutomationId(_receipt, "DeferredTouchReceipt");
        AutomationProperties.SetName(_receipt, "Deferred touch regression receipt");
        AutomationProperties.SetAutomationId(_ancestor, "TouchSelectionAncestorViewport");
        AutomationProperties.SetName(_ancestor, "Page-owned markdown viewport");
        _ancestor.HorizontalScrollMode = ScrollMode.Disabled;
        _ancestor.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _ancestor.VerticalScrollMode = ScrollMode.Auto;
        _ancestor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _ancestor.VerticalContentAlignment = VerticalAlignment.Top;
        _ancestor.ZoomMode = ZoomMode.Disabled;
        AutomationProperties.SetAutomationId(_touchContactStatus, "TouchContactObservation");
        AutomationProperties.SetAccessibilityView(_touchContactStatus, AccessibilityView.Raw);
        _touchContactObservation = new TouchContactObservation(_touchContactStatus);
        _viewport.Checked += (_, _) => ChangeViewportFromControl(true);
        _viewport.Unchecked += (_, _) => ChangeViewportFromControl(false);
        _releaseButton.Click += (_, _) => Arm(canceled: false);
        _cancelButton.Click += (_, _) => Arm(canceled: true);

        var root = new Grid { Style = (Style)Application.Current.Resources["SampleShellRootStyle"] };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _viewport.Margin = (Thickness)Application.Current.Resources["SamplePageHeaderMargin"];
        root.Children.Add(_viewport);
        var commands = new Grid { Margin = _viewport.Margin };
        commands.ColumnDefinitions.Add(new());
        commands.ColumnDefinitions.Add(new());
        commands.Children.Add(_releaseButton);
        Grid.SetColumn(_cancelButton, 1);
        commands.Children.Add(_cancelButton);
        Grid.SetRow(commands, 1);
        root.Children.Add(commands);
        Grid.SetRow(_host, 2);
        root.Children.Add(_host);
        Grid.SetRow(_touchContactStatus, 2);
        root.Children.Add(_touchContactStatus);
        // Receipt growth must not resize the viewport and invalidate the
        // exact snapshot/contact being observed during a deferred terminal.
        _receipt.Height = (double)Application.Current.Resources["SampleDiagnosticsMaxHeight"];
        _receipt.Margin = (Thickness)Application.Current.Resources["SampleDiagnosticsMargin"];
        Grid.SetRow(_receipt, 3);
        root.Children.Add(_receipt);
        var close = new Button { Content = "Close probe", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = _receipt.Margin };
        AutomationProperties.SetAutomationId(close, "DeferredTouchCloseProbe");
        close.Click += (_, _) => Close();
        Grid.SetRow(close, 4);
        root.Children.Add(close);
        Content = root;
        SetViewport(false);
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(25);
        _timer.Tick += OnTick;
        Closed += OnClosed;
        Publish();
    }

    private MarkdownRendererControl Renderer => _usesAncestor ? _document : _owned;

    private void ChangeViewportFromControl(bool ancestor)
    {
        if (!SetViewport(ancestor) && _viewport.IsChecked != _usesAncestor)
            _viewport.IsChecked = _usesAncestor;
    }

    private bool SetViewport(bool ancestor)
    {
        // A failed run remains observable until its real contact and worker
        // have retired. Refuse the mode change without mutating that evidence
        // or throwing out of the checkbox's routed event handler.
        if (_closed || _phase is not ("idle" or "complete" or "failed") ||
            _lease is { Completed.IsCompleted: false })
            return false;
        if (Renderer.IsLoaded)
        {
            DeferredTouchProbeState current = Renderer.CaptureDeferredTouchProbeState();
            if (current.ActivePointerId != 0 || current.QueuedCount != 0 ||
                current.CaptureCount != 0 || current.RetryTimerExists || current.SelectionContextMenuOpen)
                return false;
        }
        _touchContactObservation.Detach();
        _usesAncestor = ancestor;
        _host.Children.Clear();
        _ancestor.Content = null;
        if (ancestor)
        {
            _ancestor.Content = _document;
            _host.Children.Add(_ancestor);
        }
        else { _host.Children.Add(_owned); }
        _touchContactObservation.Attach(Renderer);
        if (_receipt.Text.Length > 0)
            Publish();
        return true;
    }

    private void Arm(bool canceled)
    {
        try
        {
            if (_closed || _phase is not ("idle" or "complete" or "failed") ||
                _lease is { Completed.IsCompleted: false })
                throw new InvalidOperationException("The previous probe is not fully released.");
            if (!Renderer.IsLoaded || Renderer.CurrentSnapshot is null)
                throw new InvalidOperationException("The real renderer is not ready.");
            DeferredTouchProbeState current = Renderer.CaptureDeferredTouchProbeState();
            if (current.ActivePointerId != 0 || current.QueuedCount != 0 || current.CaptureCount != 0 || current.RetryTimerExists)
                throw new InvalidOperationException("The probe must start without an active contact or deferred work.");
            _sequence++;
            _failure = null;
            _preContact = current;
            _before = _queued = _after = _fresh = _live = null;
            _settleDeadline = 0;
            _heldSnapshot = null;
            _lease = null;
            _pointerId = 0;
            _gestureEpoch = 0;
            _workerHeldAtDrop = false;
            _canceled = canceled;
            _hadSelectionBeforeContact = current.SelectionIsActive;
            _deadline = Environment.TickCount64 + 12_000;
            _phase = "armed";
            SetCommandsEnabled(false);
            _timer.Start();
            Publish();
        }
        catch (Exception error) { Fail(error); }
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (_closed)
            return;
        try
        {
            if (Environment.TickCount64 > _deadline)
                throw new TimeoutException("The real routed touch sequence did not reach its next phase.");
            if (_lease?.Failure is { } failure)
                throw new InvalidOperationException("The worker snapshot lease failed.", failure);
            DeferredTouchProbeState current = Renderer.CaptureDeferredTouchProbeState();
            switch (_phase)
            {
                case "armed":
                    // For a new range, wait for real Holding to commit it before
                    // contending for the snapshot. Existing ranges need only a
                    // real accepted Press. No session/selection is fabricated.
                    if (current.ActivePointerId == 0 || current.GestureEpoch <= 0 || !current.SelectionIsActive)
                        return;
                    _pointerId = current.ActivePointerId;
                    _gestureEpoch = current.GestureEpoch;
                    _heldSnapshot = Renderer.CurrentSnapshot ?? throw new InvalidOperationException("The current snapshot disappeared.");
                    _lease = new BackgroundInteractionLease(_heldSnapshot.TryBeginInteraction, _heldSnapshot.EndInteraction);
                    _phase = "acquiring";
                    break;
                case "acquiring":
                    if (_lease is not { IsHeld: true })
                        return;
                    if (current.ActivePointerId != _pointerId || current.GestureEpoch != _gestureEpoch ||
                        !ReferenceEquals(Renderer.CurrentSnapshot, _heldSnapshot) || !current.SelectionIsActive ||
                        current.MatchingTerminalCount != 0)
                        throw new InvalidOperationException("The accepted contact or snapshot changed before contention was established.");
                    _before = current;
                    _phase = "holding";
                    break;
                case "holding":
                    if (_lease is not { IsHeld: true })
                        throw new InvalidOperationException("The terminal arrived after the worker released its snapshot.");
                    if (current.MatchingTerminalCount == 0)
                    {
                        // Republish only when the deferred input changes, so a stalled
                        // terminal match is diagnosable from the receipt.
                        if (_live is null || _live.QueuedCount != current.QueuedCount ||
                            _live.ActivePointerId != current.ActivePointerId || _live.GestureEpoch != current.GestureEpoch)
                        {
                            _live = current;
                            Publish();
                        }
                        return;
                    }
                    _live = null;
                    _queued = current;
                    _workerHeldAtDrop = _lease.IsHeld;
                    _after = Renderer.FailDeferredTouchTerminalForProbe(_heldSnapshot!, _pointerId, _gestureEpoch, _canceled);
                    bool preserve = !_canceled || _hadSelectionBeforeContact;
                    var expectedRange = _canceled && _hadSelectionBeforeContact
                        ? _preContact!.SelectionRange
                        : _before!.SelectionRange;
                    if (preserve ? !_after.SelectionIsActive || _after.SelectionRange != expectedRange : _after.SelectionIsActive)
                        throw new InvalidOperationException("The production reset used the wrong selection disposition.");
                    if (_after.IsHandleDragging || (!preserve && _after.VisibleHandleCount != 0))
                        throw new InvalidOperationException("The canceled gesture left handle state behind.");
                    _lease.Release();
                    _phase = "releasing";
                    break;
                case "releasing":
                    if (_lease is not { Completed.IsCompleted: true })
                        return;
                    if (_lease.IsHeld)
                        throw new InvalidOperationException("The completed worker still owns the snapshot.");
                    _phase = "awaitingFreshContact";
                    _deadline = Environment.TickCount64 + 12_000;
                    break;
                case "awaitingFreshContact":
                    if (current.ActivePointerId == 0)
                        return;
                    if (current.GestureEpoch <= 0 || current.GestureEpoch == _gestureEpoch)
                        throw new InvalidOperationException("The next contact did not acquire a fresh gesture epoch.");
                    _fresh = current;
                    _phase = "freshContactAccepted";
                    break;
                case "freshContactAccepted":
                    if (current.ActivePointerId != 0)
                        return;
                    if (current.QueuedCount != 0 || current.CaptureCount != 0 || current.RetryTimerExists || !current.SelectionIsActive)
                    {
                        // Work the drop resumed (a rebuild, an exit queued behind
                        // it) may still drain after the contact ends. Clean state
                        // is required within a bound, and the residue is kept.
                        if (_settleDeadline == 0)
                            _settleDeadline = Environment.TickCount64 + FreshContactSettleMilliseconds;
                        _live = current;
                        if (Environment.TickCount64 < _settleDeadline)
                            return;
                        throw new InvalidOperationException(
                            "The fresh contact did not finish with a clean committed selection. State: " +
                            JsonSerializer.Serialize(current, DeferredTouchProbeJsonContext.Default.DeferredTouchProbeState));
                    }
                    _phase = "complete";
                    _timer.Stop();
                    SetCommandsEnabled(true);
                    break;
                default:
                    throw new InvalidOperationException("Unexpected probe phase.");
            }
            Publish();
        }
        catch (Exception error) { Fail(error); }
    }

    private void Fail(Exception error)
    {
        _lease?.Release();
        _failure = error.ToString();
        _phase = "failed";
        _timer?.Stop();
        SetCommandsEnabled(true);
        Publish();
    }

    private void SetCommandsEnabled(bool enabled)
        => _viewport.IsEnabled = _releaseButton.IsEnabled = _cancelButton.IsEnabled = enabled;

    private void Publish()
        => _receipt.Text = JsonSerializer.Serialize(new DeferredTouchProbeReport(
            1, _sequence, _phase, _usesAncestor ? "ancestor" : "owned", _canceled,
            _hadSelectionBeforeContact, _pointerId, _gestureEpoch, _lease?.IsHeld == true,
            _workerHeldAtDrop,
            _preContact, _before, _queued, _after, _fresh, _failure, _live), DeferredTouchProbeJsonContext.Default.DeferredTouchProbeReport);

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        // Never block the XAML dispatcher waiting for the monitor worker.
        // The worker releases on its owning thread, even if the process/test
        // abandons this window. Its hard deadline is a second safety net.
        _lease?.Release();
        _touchContactObservation.Detach();
        _ancestor.Content = null;
        _host.Children.Clear();
        _owned.Dispose();
        _document.Dispose();
    }
}

internal sealed record DeferredTouchProbeReport(
    int Version, int Sequence, string Phase, string Viewport, bool Canceled,
    bool HadSelectionBeforeContact, uint PointerId, long GestureEpoch, bool WorkerOwnsSnapshot, bool WorkerHeldAtDrop,
    DeferredTouchProbeState? PreContact, DeferredTouchProbeState? BeforeTerminal, DeferredTouchProbeState? QueuedTerminal,
    DeferredTouchProbeState? AfterDrop, DeferredTouchProbeState? FreshContact, string? Failure,
    // P1-005 diagnostics: the live renderer state while a terminal is awaited.
    DeferredTouchProbeState? Live);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeferredTouchProbeReport))]
internal partial class DeferredTouchProbeJsonContext : JsonSerializerContext;

#pragma warning restore MR1001
