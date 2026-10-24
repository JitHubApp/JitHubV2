using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Document;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

// This window is an opt-in actual-input regression host, not a general test API.
#pragma warning disable MR1001

namespace MarkdownRenderer.Sample;

/// <summary>
/// Observes actual long-press selection and actual captured selection-handle
/// contact while applying one real control lifecycle action. No input or
/// selection is synthesized by this window.
/// </summary>
internal sealed class TouchLifecycleProbeWindow : Window
{
    private const int MaximumEvents = 96;
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan HostCoordinatorTimeout = TimeSpan.FromSeconds(150);
    private const string StartHandleId = "MarkdownSelectionStartHandle";
    private const string EndHandleId = "MarkdownSelectionEndHandle";
    // The host runner classifies this exact prefix as UNVERIFIED for capture loss only.
    private const string CaptureLossDeliveryLimit = "TOUCH-CAPTURE-LOST-NOT-ROUTED:";
    // How long an ordinary up may still follow a routed-away contact report.
    private const long RoutedAwaySettleMilliseconds = 1_000;

    private readonly MarkdownScrollView _owned = new();
    private readonly MarkdownDocumentView _document = new();
    private readonly ScrollViewer _ancestor = new();
    private readonly Grid _host = new();
    private readonly Grid _root;
    private readonly CheckBox _viewport = new()
    {
        Content = "Use ancestor-owned viewport",
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly ComboBox _casePicker = new() { Header = "Lifecycle case" };
    private readonly Button _arm = new() { Content = "Arm selected case" };
    private readonly TextBlock _instructions = new()
    {
        Text = "Arm, hold a word, and lift. Escape closes its menu. Move a handle; keep contact down until asked to lift. After unload, hold freshword. Tap blank space before rearming.",
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBox _receipt = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly Button _close = new() { Content = "Close probe" };
    // Zero-size, hit-testable element that takes the contact in the capture-loss case.
    private readonly Border _captureTarget = new();
    private readonly DispatcherQueueTimer _timer;
    private readonly List<TouchLifecycleProbeEvent> _events = new(MaximumEvents);

    private string _phase = "idle";
    private string? _case;
    private string? _failure;
    private bool _usesAncestor;
    private bool _closed;
    private bool _disposedCase;
    private bool _eventTraceTruncated;
    private int _run;
    private int _eventSequence;
    private int _actionSequence;
    private int _handlePressSequence;
    private int _handleStartSequence;
    private int _freshStartSequence;
    private bool _coordinatorInputDeadlineStarted;
    private bool _coordinatorWaitDeadlineActive;
    private long _deadline;
    private long _lastPublishTick;
    private string? _publishedPhase;
    private uint _longPressPointerId;
    private long _longPressGestureEpoch;
    private uint _handlePointerId;
    // Set by the capture-loss action; the next routed handle move steals capture.
    private bool _stealCaptureOnNextMove;
    private bool _captureMoved;
    private TouchLifecycleProbeState? _captureMoveState;
    private long _routedAwayObservedTick;
    private long _handleGestureEpoch;
    private uint _freshPointerId;
    private long _freshGestureEpoch;
    private TouchLifecycleProbeSnapshot? _baseline;
    private TouchLifecycleProbeSnapshot? _longPressSelection;
    private TouchLifecycleProbeSnapshot? _beforeAction;
    private TouchLifecycleProbeSnapshot? _afterAction;
    private TouchLifecycleProbeSnapshot? _afterTerminal;
    private TouchLifecycleProbeSnapshot? _afterUnloaded;
    private TouchLifecycleProbeSnapshot? _afterReattach;
    private TouchLifecycleProbeSnapshot? _freshSelection;
    private TouchLifecycleProbeSnapshot? _freshContextMenuOpen;
    private TouchLifecycleProbeSnapshot? _afterFreshTerminal;
    private bool _freshContextMenuObserved;
    private bool _freshMenuDismissed;
    private int _freshReleaseSequence;
    private int _freshContextMenuOpenSequence;
    private int _freshMenuDismissedSequence;
    private Button? _capturedHandle;
    private Button? _terminalObserverHandle;
    private Button? _startHandle;
    private Button? _endHandle;

    internal TouchLifecycleProbeWindow()
    {
        Title = "MarkdownRenderer touch lifecycle probe";
        const string markdown =
            "# Touch lifecycle regression\n\nLong-press target: firstword.\n\nAlternate target: secondword.\n\nFresh post-unload target: freshword.\n\nKeep the document in view while the real handle contact remains down.";
        _owned.Markdown = _document.Markdown = markdown;
        AutomationProperties.SetAutomationId(_owned, "MarkdownRenderer");
        AutomationProperties.SetAutomationId(_document, "MarkdownRenderer");
        AutomationProperties.SetAutomationId(_ancestor, "TouchSelectionAncestorViewport");
        AutomationProperties.SetName(_ancestor, "Page-owned markdown viewport");
        AutomationProperties.SetAutomationId(_viewport, "TouchLifecycleAncestorViewport");
        AutomationProperties.SetAutomationId(_casePicker, "TouchLifecycleCase");
        AutomationProperties.SetName(_casePicker, "Lifecycle case");
        AutomationProperties.SetAutomationId(_arm, "TouchLifecycleArmCase");
        AutomationProperties.SetAutomationId(_instructions, "TouchLifecycleInstructions");
        AutomationProperties.SetAutomationId(_receipt, "TouchLifecycleReceipt");
        AutomationProperties.SetName(_receipt, "Actual touch lifecycle regression receipt");
        ScrollViewer.SetVerticalScrollBarVisibility(_receipt, ScrollBarVisibility.Auto);
        AutomationProperties.SetAutomationId(_close, "TouchLifecycleCloseProbe");
        _ancestor.HorizontalScrollMode = ScrollMode.Disabled;
        _ancestor.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _ancestor.VerticalScrollMode = ScrollMode.Auto;
        _ancestor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _ancestor.VerticalContentAlignment = VerticalAlignment.Top;
        _ancestor.ZoomMode = ZoomMode.Disabled;

        _viewport.Checked += (_, _) => ChangeViewport(true);
        _viewport.Unchecked += (_, _) => ChangeViewport(false);
        _casePicker.Items.Add("Capture loss");
        _casePicker.Items.Add("Unload and reuse");
        _casePicker.Items.Add("Dispose");
        _casePicker.SelectedIndex = 0;
        _arm.Click += (_, _) => Arm(_casePicker.SelectedIndex switch
        {
            0 => "captureLoss",
            1 => "unload",
            2 => "dispose",
            _ => throw new InvalidOperationException("Choose one of the three lifecycle cases."),
        });
        _close.Click += (_, _) => Close();
        AttachLifecycle(_owned);
        AttachLifecycle(_document);

        _root = new Grid { Style = (Style)Application.Current.Resources["SampleShellRootStyle"] };
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _viewport.Margin = (Thickness)Application.Current.Resources["SamplePageHeaderMargin"];
        _root.Children.Add(_viewport);

        var commandRow = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = _viewport.Margin,
        };
        // A native case picker uses less vertical space than three full rows
        // of commands. The document and receipt share the remaining height.
        Thickness commandMargin = (Thickness)Application.Current.Resources["SamplePageHeaderMargin"];
        _casePicker.Margin = _arm.Margin = _instructions.Margin = commandMargin;
        _casePicker.HorizontalAlignment = _arm.HorizontalAlignment = HorizontalAlignment.Stretch;
        commandRow.Children.Add(_casePicker);
        commandRow.Children.Add(_arm);
        commandRow.Children.Add(_instructions);
        Grid.SetRow(commandRow, 1);
        _root.Children.Add(commandRow);

        _host.Padding = (Thickness)Application.Current.Resources["SampleRendererPadding"];
        Grid.SetRow(_host, 2);
        _root.Children.Add(_host);
        _receipt.MaxHeight = (double)Application.Current.Resources["SampleDiagnosticsMaxHeight"];
        _receipt.Margin = (Thickness)Application.Current.Resources["SampleDiagnosticsMargin"];
        Grid.SetRow(_receipt, 3);
        _root.Children.Add(_receipt);
        _close.HorizontalAlignment = HorizontalAlignment.Right;
        _close.Margin = _receipt.Margin;
        Grid.SetRow(_close, 4);
        _root.Children.Add(_close);
        AutomationProperties.SetAutomationId(_captureTarget, "TouchLifecycleCaptureTarget");
        _captureTarget.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetRow(_captureTarget, 4);
        _root.Children.Add(_captureTarget);
        Content = _root;
        _root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        _root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerMoved), true);
        _root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPointerReleased), true);
        _root.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnPointerCanceled), true);
        _root.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnPointerCaptureLost), true);
        _root.AddHandler(UIElement.HoldingEvent, new HoldingEventHandler(OnHolding), true);
        SetViewport(false);

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(25);
        _timer.Tick += OnTick;
        _root.Loaded += OnRootLoaded;
        Closed += OnClosed;
        SetCommandsEnabled(false);
        Publish();
    }

    private MarkdownRendererControl Renderer => _usesAncestor ? _document : _owned;

    private void OnRootLoaded(object sender, RoutedEventArgs args)
    {
        _root.Loaded -= OnRootLoaded;
        _timer.Start();
        Publish();
    }

    private void ChangeViewport(bool useAncestor)
    {
        if (!SetViewport(useAncestor) && _viewport.IsChecked != _usesAncestor)
            _viewport.IsChecked = _usesAncestor;
        else if (_phase == "idle")
            _timer.Start();
    }

    private bool SetViewport(bool useAncestor)
    {
        if (_closed || _phase is not ("idle" or "complete") || _disposedCase)
            return false;
        TouchLifecycleProbeState state = Renderer.CaptureTouchLifecycleProbeState();
        if (!IsTransientStateClean(state))
            return false;

        DetachRendererFromHost();
        _startHandle = _endHandle = null;
        _usesAncestor = useAncestor;
        AttachRendererToHost();
        if (_phase == "complete")
        {
            _phase = "idle";
            SetCommandsEnabled(false);
        }
        Publish();
        return true;
    }

    private void Arm(string lifecycleCase)
    {
        try
        {
            if (_closed || _phase is not ("idle" or "complete") || _disposedCase)
                throw new InvalidOperationException("The previous case has not completed its real contact and cleanup.");
            if (!Renderer.IsLoaded || Renderer.CurrentSnapshot is null)
                throw new InvalidOperationException("The active renderer is not loaded with a committed document.");
            TouchLifecycleProbeSnapshot baseline = CaptureSnapshot(Renderer);
            if (!IsTransientStateClean(baseline.Control))
                throw new InvalidOperationException("The active renderer still has a pointer, capture, timer, menu, or handle drag.");
            if (baseline.Control.SelectionIsActive)
                throw new InvalidOperationException("Tap blank document space to clear the old selection before arming.");

            _run++;
            _case = lifecycleCase;
            _failure = null;
            _disposedCase = false;
            _eventTraceTruncated = false;
            _events.Clear();
            _eventSequence = 0;
            _actionSequence = _handlePressSequence = _handleStartSequence = _freshStartSequence = 0;
            _longPressPointerId = _handlePointerId = _freshPointerId = 0;
            _stealCaptureOnNextMove = _captureMoved = false;
            _captureMoveState = null;
            _routedAwayObservedTick = 0;
            _longPressGestureEpoch = _handleGestureEpoch = _freshGestureEpoch = 0;
            _capturedHandle = null;
            _baseline = baseline;
            _longPressSelection = _beforeAction = _afterAction = _afterTerminal = null;
            _afterUnloaded = _afterReattach = _freshSelection = _freshContextMenuOpen = _afterFreshTerminal = null;
            _freshContextMenuObserved = _freshMenuDismissed = false;
            _freshReleaseSequence = _freshContextMenuOpenSequence = _freshMenuDismissedSequence = 0;
            _phase = "awaitLongPress";
            StartCoordinatorWaitDeadline();
            SetCommandsEnabled(false);
            _timer.Start();
            Publish();
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (_closed)
            return;
        try
        {
            if (_phase == "idle")
            {
                UpdateReadyStatus();
                return;
            }
            if (_phase is "complete" or "failed")
            {
                if (_phase == "failed" && Environment.TickCount64 - _lastPublishTick > 250)
                    Publish();
                return;
            }
            if (Environment.TickCount64 > _deadline)
            {
                string budget = _coordinatorWaitDeadlineActive
                    ? $"host-coordinator wait ({HostCoordinatorTimeout.TotalSeconds:0} seconds)"
                    : $"phase deadline ({PhaseTimeout.TotalSeconds:0} seconds)";
                throw new TimeoutException($"Phase '{_phase}' did not receive its required real input or lifecycle evidence within the {budget}.");
            }
            if (_failure is not null)
                throw new InvalidOperationException(_failure);
            // T1-027: the host coordinates on the published phase. A waiting
            // phase returns before the throttled publish below, so a phase
            // entered within 250 ms of the last publish must be published here.
            if (!string.Equals(_phase, _publishedPhase, StringComparison.Ordinal))
                Publish();

            MarkdownRendererControl renderer = Renderer;
            TouchLifecycleProbeSnapshot snapshot = CaptureSnapshot(renderer);
            TouchLifecycleProbeState state = snapshot.Control;
            switch (_phase)
            {
                case "awaitLongPress":
                    if (!HasRealLongPressSelection(state, _longPressPointerId, _longPressGestureEpoch))
                        return;
                    _longPressSelection = snapshot;
                    _phase = "awaitLongPressTerminal";
                    StartPhaseDeadline();
                    break;
                case "awaitLongPressTerminal":
                    if (FindPointerTerminal(_longPressPointerId, "PointerCanceled", HoldingSequence(_longPressPointerId)) is not null)
                        throw new InvalidOperationException("The long-press contact was canceled before handle selection could begin.");
                    if (FindPointerTerminal(_longPressPointerId, "PointerReleased", HoldingSequence(_longPressPointerId)) is not { } longPressRelease)
                        return;
                    if (longPressRelease.CorrelatedGestureEpoch != _longPressGestureEpoch ||
                        state.ContactPointerId != 0 || !state.SelectionIsActive ||
                        state.SelectionRange != _longPressSelection!.Control.SelectionRange)
                        throw new InvalidOperationException("The real long-press terminal did not leave its committed word range intact.");
                    _phase = "awaitHandleContact";
                    _handleStartSequence = _eventSequence;
                    StartCoordinatorWaitDeadline();
                    break;
                case "awaitHandleContact":
                    if (_handlePointerId == 0)
                        return;
                    if (!HasActualCapturedHandleContact(snapshot, _handlePointerId, _handleGestureEpoch))
                        return;
                    _capturedHandle = FindHandle(renderer, snapshot.Control.HandleCaptureTargetAutomationId!);
                    if (_capturedHandle is null)
                        throw new InvalidOperationException("The captured selection Button disappeared before the probe action.");
                    ObserveCapturedTerminal(_capturedHandle);
                    _phase = "awaitHandleMove";
                    StartPhaseDeadline();
                    break;
                case "awaitHandleMove":
                    if (!HasActualCapturedHandleContact(snapshot, _handlePointerId, _handleGestureEpoch))
                        throw new InvalidOperationException("The handle lost its actual touch owner before the action point.");
                    if (!HasRoutedContactMove(_handlePointerId))
                        return;
                    _beforeAction = snapshot;
                    _phase = "performAction";
                    break;
                case "performAction":
                    PerformLifecycleAction(renderer);
                    break;
                case "awaitCaptureLost":
                    if (FindPointerEvent(_handlePointerId, "PointerCaptureLost") is not { } captureLost ||
                        captureLost.Sequence <= _actionSequence ||
                        captureLost.SourceAutomationId != _beforeAction!.Control.HandleCaptureTargetAutomationId)
                    {
                        if (FindPointerTerminalAfter(_handlePointerId, _actionSequence) is not { } silentTerminal)
                            return;
                        // T1-022 delivery limit: WinUI keeps a replay pointer point
                        // only for mouse, so an app-initiated release of a touch
                        // capture raises no PointerCaptureLost. Verify that the
                        // capture really moved and the renderer still cleaned up,
                        // then report the limit instead of a pass.
                        if (!_captureMoved)
                            throw new InvalidOperationException("The capture-loss contact ended before a routed move could transfer its capture.");
                        if (silentTerminal.CorrelatedGestureEpoch != _handleGestureEpoch)
                            throw new InvalidOperationException("The handle contact ended under a different or unknown gesture epoch.");
                        _afterTerminal = snapshot;
                        RequireCleanAfterContactAction(snapshot, preserveSelection: true,
                            expectedSelection: _captureMoveState!);
                        throw new InvalidOperationException(CaptureLossDeliveryLimit +
                            " the handle's touch capture moved to the probe capture target during a routed in-contact move, " +
                            "WinUI routed no in-contact PointerCaptureLost for it, and the renderer kept the selection and released all contact state at the terminal.");
                    }
                    _afterAction = snapshot;
                    RequireCleanAfterContactAction(snapshot, preserveSelection: true);
                    _phase = "awaitActionTerminal";
                    StartPhaseDeadline();
                    break;
                case "awaitUnloaded":
                    if (!HasLifecycleEvent("Unloaded", renderer, _actionSequence) || !state.IsUnloaded || state.IsLoaded)
                        return;
                    _afterUnloaded = snapshot;
                    RequireCleanAfterContactAction(snapshot, preserveSelection: false, expectHandlesHidden: true);
                    _phase = "awaitActionTerminal";
                    StartPhaseDeadline();
                    break;
                case "awaitDisposed":
                    if (!HasLifecycleEvent("DisposalCompleted", renderer, _actionSequence) || !state.IsDisposed)
                        return;
                    _afterAction = snapshot;
                    _disposedCase = true;
                    RequireCleanAfterContactAction(snapshot, preserveSelection: false, expectHandlesHidden: true);
                    _phase = "awaitActionTerminal";
                    StartPhaseDeadline();
                    break;
                case "awaitActionTerminal":
                    if (FindPointerTerminalAfter(_handlePointerId, _actionSequence) is not { } terminal)
                    {
                        // T1-026: once the renderer has left the tree, Windows may
                        // route the contact away from this window. An ordinary up
                        // still wins if it arrives within the settle period.
                        if (_case is not ("unload" or "dispose") ||
                            FindRoutedAwayTerminalAfter(_handlePointerId, _actionSequence) is not { } routedAway)
                            return;
                        if (_routedAwayObservedTick == 0)
                            _routedAwayObservedTick = Environment.TickCount64;
                        if (Environment.TickCount64 - _routedAwayObservedTick < RoutedAwaySettleMilliseconds)
                            return;
                        terminal = routedAway;
                    }
                    if (terminal.CorrelatedGestureEpoch != _handleGestureEpoch)
                        throw new InvalidOperationException("The handle contact ended under a different or unknown gesture epoch.");
                    _afterTerminal = snapshot;
                    RequireCleanAfterContactAction(snapshot, preserveSelection: _case == "captureLoss",
                        expectHandlesHidden: _case is "unload" or "dispose");
                    if (_case != "unload")
                    {
                        Complete();
                        break;
                    }
                    // Old native capture lists were verified above. Reattach
                    // recreates Buttons, so future snapshots must rediscover
                    // the new visual instances rather than reuse retired ones.
                    DetachCapturedTerminalObserver();
                    _startHandle = _endHandle = _capturedHandle = null;
                    AttachRendererToHost();
                    _phase = "awaitReattached";
                    StartPhaseDeadline();
                    break;
                case "awaitReattached":
                    if (!HasLifecycleEvent("Loaded", renderer, _actionSequence) || !state.IsLoaded || state.IsUnloaded || state.IsDisposed)
                        return;
                    RequireCleanAfterContactAction(snapshot, preserveSelection: false);
                    _afterReattach = snapshot;
                    _phase = "awaitFreshLongPress";
                    _freshStartSequence = _eventSequence;
                    StartCoordinatorWaitDeadline();
                    break;
                case "awaitFreshLongPress":
                    if (!HasRealLongPressSelection(state, _freshPointerId, _freshGestureEpoch))
                        return;
                    if (state.ContactGestureEpoch == _handleGestureEpoch ||
                        state.SelectionRange == _beforeAction!.Control.SelectionRange)
                        throw new InvalidOperationException("Reattached selection did not use a new gesture epoch and a different real word range.");
                    _freshSelection = snapshot;
                    _phase = "awaitFreshTerminal";
                    StartPhaseDeadline();
                    break;
                case "awaitFreshTerminal":
                    if (FindPointerTerminal(_freshPointerId, "PointerCanceled", HoldingSequence(_freshPointerId)) is not null)
                        throw new InvalidOperationException("The fresh post-unload selection contact was canceled.");
                    if (FindPointerTerminal(_freshPointerId, "PointerReleased", HoldingSequence(_freshPointerId)) is not { } freshRelease)
                        return;
                    if (freshRelease.CorrelatedGestureEpoch != _freshGestureEpoch ||
                        state.ContactPointerId != 0 || !state.SelectionIsActive ||
                        state.SelectionRange != _freshSelection!.Control.SelectionRange)
                        throw new InvalidOperationException("The new real selection did not survive its terminal contact.");
                    _freshReleaseSequence = freshRelease.Sequence;
                    _phase = "awaitFreshMenuOpen";
                    StartPhaseDeadline();
                    if (state.IsContextMenuOpen)
                        ObserveFreshContextMenuOpen(snapshot);
                    break;
                case "awaitFreshMenuOpen":
                    if (_freshReleaseSequence == 0 || !state.IsLoaded || state.IsUnloaded || state.IsDisposed ||
                        state.ContactPointerId != 0 || !state.SelectionIsActive ||
                        state.SelectionRange != _freshSelection!.Control.SelectionRange)
                        throw new InvalidOperationException("The fresh contact/selection changed while waiting for its native context menu.");
                    if (state.IsContextMenuOpen)
                        ObserveFreshContextMenuOpen(snapshot);
                    break;
                case "awaitFreshMenuDismissal":
                    if (!_freshContextMenuObserved || _freshContextMenuOpenSequence <= _freshReleaseSequence)
                        throw new InvalidOperationException("Fresh-menu dismissal began without an ordered observed menu-open state.");
                    if (state.IsContextMenuOpen)
                        break;
                    _freshMenuDismissed = true;
                    _freshMenuDismissedSequence = NextEventSequence();
                    AddEvent(CreateFreshMenuStateEvent("FreshMenuDismissed", state));
                    _phase = "awaitFreshTerminalCleanup";
                    StartPhaseDeadline();
                    Publish();
                    break;
                case "awaitFreshTerminalCleanup":
                    if (state.IsContextMenuOpen)
                        throw new InvalidOperationException("The context menu reopened after its dismissal was observed.");
                    if (!state.IsLoaded || state.IsUnloaded || state.IsDisposed)
                        throw new InvalidOperationException("The renderer left the loaded state during fresh-terminal cleanup.");
                    if (!IsTransientStateClean(state))
                        break;
                    if (!state.SelectionIsActive || state.SelectionRange != _freshSelection!.Control.SelectionRange)
                        throw new InvalidOperationException("Fresh-menu cleanup did not preserve the committed freshword selection.");
                    if (snapshot.StartHandle?.PointerIds.Length > 0 || snapshot.EndHandle?.PointerIds.Length > 0)
                        break;
                    _afterFreshTerminal = snapshot;
                    Complete();
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected lifecycle probe phase '{_phase}'.");
            }
            if (Environment.TickCount64 - _lastPublishTick >= 250)
                Publish();
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private void PerformLifecycleAction(MarkdownRendererControl renderer)
    {
        TouchLifecycleProbeSnapshot before = CaptureSnapshot(renderer);
        if (!HasActualCapturedHandleContact(before, _handlePointerId, _handleGestureEpoch) ||
            !HasRoutedContactPress(_handlePointerId) || !HasRoutedContactMove(_handlePointerId))
        {
            throw new InvalidOperationException("The action requires routed touch press/move and exact native Button capture.");
        }
        _beforeAction = before;
        if (_handlePressSequence == 0 || _handlePressSequence >= _eventSequence)
            throw new InvalidOperationException("No independently routed native Button press precedes the lifecycle action.");
        _actionSequence = NextEventSequence();
        AddEvent(new TouchLifecycleProbeEvent(
            _actionSequence, "Action:" + _case, _handlePointerId, "ProbeAction", true, true,
            before.Control.HandleCaptureTargetAutomationId, "ProbeAction",
            before.Control.ObservedGestureEpoch, _handleGestureEpoch,
            System.Diagnostics.Stopwatch.GetTimestamp(), AutomationProperties.GetAutomationId(renderer)));

        switch (_case)
        {
            case "captureLoss":
                // Move the real touch capture from the handle Button to another
                // element, as another control taking the pointer would. The next
                // routed handle PointerMoved performs it (T1-022).
                _stealCaptureOnNextMove = true;
                _phase = "awaitCaptureLost";
                break;
            case "unload":
                DetachRendererFromHost();
                _phase = "awaitUnloaded";
                break;
            case "dispose":
                // Dispose the actual attached public control; the window and
                // receipt stay alive to observe both disposal and contact end.
                renderer.Dispose();
                _disposedCase = true;
                _phase = "awaitDisposed";
                break;
            default:
                throw new InvalidOperationException("No lifecycle action was selected.");
        }
        StartPhaseDeadline();
    }

    private bool HasRealLongPressSelection(TouchLifecycleProbeState state, uint pointerId, long gestureEpoch)
    {
        if (pointerId == 0 || gestureEpoch <= 0 || state.ContactPointerId != pointerId ||
            state.ContactGestureEpoch != gestureEpoch || !state.SelectionIsActive ||
            state.TouchState != "Selected")
            return false;

        TouchLifecycleProbeEvent? holding = _events.LastOrDefault(item =>
            item.Kind == "HoldingStarted" && item.PointerId == pointerId && item.Device == "Touch" &&
            item.Sequence > (pointerId == _freshPointerId && _freshPointerId != 0 ? _freshStartSequence : 0));
        TouchLifecycleProbeSnapshot? baseline = pointerId == _freshPointerId && _freshPointerId != 0
            ? _afterUnloaded
            : _baseline;
        DocumentRange previousRange = pointerId == _freshPointerId && _freshPointerId != 0
            ? _beforeAction!.Control.SelectionRange
            : baseline!.Control.SelectionRange;
        return holding is not null && holding.ObservedGestureEpoch == gestureEpoch &&
            state.SelectionRange != previousRange;
    }

    private bool HasActualCapturedHandleContact(
        TouchLifecycleProbeSnapshot snapshot,
        uint pointerId,
        long gestureEpoch)
    {
        TouchLifecycleProbeState state = snapshot.Control;
        if (pointerId == 0 || gestureEpoch <= 0 || state.HandlePointerId != pointerId ||
            state.HandleGestureEpoch != gestureEpoch || state.ContactPointerId != pointerId ||
            state.ContactGestureEpoch != gestureEpoch || !state.IsHandleDragging ||
            state.HandleCaptureTargetAutomationId is not (StartHandleId or EndHandleId))
            return false;

        TouchLifecycleHandleCapture? actualButton = state.HandleCaptureTargetAutomationId == StartHandleId
            ? snapshot.StartHandle
            : snapshot.EndHandle;
        return actualButton is { Found: true, PointerCaptureListAvailable: true } &&
            actualButton.PointerIds.Contains(pointerId) &&
            HasRoutedContactPress(pointerId, state.HandleCaptureTargetAutomationId);
    }

    private bool HasRoutedContactPress(uint pointerId, string? sourceId = null)
        => _events.Any(item => item.Kind == "PointerPressed" && item.PointerId == pointerId &&
            item.Device == "Touch" && item.IsInContact == true &&
            (sourceId is null || item.SourceAutomationId == sourceId));

    private bool HasRoutedContactMove(uint pointerId)
        => _events.Any(item => item.Kind == "PointerMoved" && item.PointerId == pointerId &&
            item.Device == "Touch" && item.IsInContact == true &&
            (item.SourceAutomationId is StartHandleId or EndHandleId));

    private TouchLifecycleProbeEvent? FindPointerTerminal(uint pointerId, string kind, int afterSequence)
        => _events.LastOrDefault(item => item.Sequence > afterSequence && item.PointerId == pointerId &&
            item.Kind == kind && item.Device == "Touch" && item.IsInContact == false);

    private int HoldingSequence(uint pointerId)
        => _events.LastOrDefault(item => item.PointerId == pointerId && item.Kind == "HoldingStarted")?.Sequence ?? int.MaxValue;

    private TouchLifecycleProbeEvent? FindPointerEvent(uint pointerId, string kind)
        => _events.LastOrDefault(item => item.PointerId == pointerId && item.Kind == kind);

    private bool HasLifecycleEvent(string kind, MarkdownRendererControl renderer, int afterSequence = 0)
        => _events.Any(item => item.Sequence > afterSequence && item.Kind == kind &&
            item.RendererId == AutomationProperties.GetAutomationId(renderer));

    private void RequireCleanAfterContactAction(
        TouchLifecycleProbeSnapshot snapshot,
        bool preserveSelection,
        bool expectHandlesHidden = false,
        TouchLifecycleProbeState? expectedSelection = null)
    {
        TouchLifecycleProbeState state = snapshot.Control;
        if (state.ContactPointerId != 0 || state.HandlePointerId != 0 || state.IsHandleDragging ||
            state.HandleFrameClockSubscribed || state.QueuedPointerInputCount != 0 ||
            state.DeferredPointerCaptureCount != 0 || state.PointerRetryTimerExists || state.IsContextMenuOpen)
            throw new InvalidOperationException("The lifecycle action left transient touch, capture, frame, queue, retry, or menu state behind.");
        if (snapshot.StartHandle?.PointerIds.Length > 0 || snapshot.EndHandle?.PointerIds.Length > 0)
            throw new InvalidOperationException("A native selection Button still reports capture of the retired pointer.");
        if (expectHandlesHidden && (state.IsStartHandleVisible || state.IsEndHandleVisible))
            throw new InvalidOperationException("The detached or disposed renderer retained visible selection handles.");
        TouchLifecycleProbeState reference = expectedSelection ?? _beforeAction!.Control;
        if (preserveSelection && (!state.SelectionIsActive || !reference.SelectionIsActive || state.SelectionRange != reference.SelectionRange))
            throw new InvalidOperationException("Capture loss did not preserve the committed selection range.");
    }

    private TouchLifecycleProbeSnapshot CaptureSnapshot(MarkdownRendererControl renderer)
    {
        TouchLifecycleProbeState state = renderer.CaptureTouchLifecycleProbeState();
        // Retain the actual Buttons once discovered. Dispose/unload can remove
        // them from the visual tree before we check their native capture lists.
        _startHandle ??= FindHandle(renderer, StartHandleId);
        _endHandle ??= FindHandle(renderer, EndHandleId);
        return new TouchLifecycleProbeSnapshot(
            state,
            CaptureHandle(_startHandle, StartHandleId),
            CaptureHandle(_endHandle, EndHandleId));
    }

    private static TouchLifecycleHandleCapture? CaptureHandle(Button? button, string automationId)
    {
        if (button is null)
            return null;
        IReadOnlyList<Pointer>? captures = button.PointerCaptures;
        uint[] ids = captures is null
            ? Array.Empty<uint>()
            : captures.Select(pointer => pointer.PointerId).ToArray();
        return new TouchLifecycleHandleCapture(
            automationId,
            true,
            button.Visibility.ToString(),
            // A successful getter returning null denotes an empty list. An
            // exception propagates: unknown capture state cannot prove cleanup.
            true,
            ids);
    }

    private static Button? FindHandle(DependencyObject root, string automationId)
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        int inspected = 0;
        while (pending.Count > 0 && inspected++ < 4096)
        {
            DependencyObject item = pending.Pop();
            if (item is Button button && AutomationProperties.GetAutomationId(button) == automationId)
                return button;
            try
            {
                int childCount = VisualTreeHelper.GetChildrenCount(item);
                for (int index = childCount - 1; index >= 0; index--)
                    pending.Push(VisualTreeHelper.GetChild(item, index));
            }
            catch (Exception)
            {
                return null;
            }
        }
        return null;
    }

    private static bool IsTransientStateClean(TouchLifecycleProbeState state)
        => state.ContactPointerId == 0 && state.HandlePointerId == 0 && !state.IsHandleDragging &&
           !state.HandleFrameClockSubscribed && state.QueuedPointerInputCount == 0 &&
           state.DeferredPointerCaptureCount == 0 && !state.PointerRetryTimerExists && !state.IsContextMenuOpen;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
        => RecordPointerEvent("PointerPressed", args, sender);
    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
        => RecordPointerEvent("PointerMoved", args, sender);
    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
        => RecordPointerEvent("PointerReleased", args, sender);
    private void OnPointerCanceled(object sender, PointerRoutedEventArgs args)
        => RecordPointerEvent("PointerCanceled", args, sender);
    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs args)
        => RecordPointerEvent("PointerCaptureLost", args, sender);

    private void OnHolding(object sender, HoldingRoutedEventArgs args)
    {
        if (_phase is "idle" or "complete" or "failed" ||
            args.HoldingState != HoldingState.Started || args.PointerDeviceType != PointerDeviceType.Touch)
            return;
        try
        {
            TouchLifecycleProbeState state = Renderer.CaptureTouchLifecycleProbeState();
            uint pointerId = state.ContactPointerId;
            if (!IsExpectedLongPressPointer(pointerId))
                return;
            if (pointerId == _freshPointerId && _freshGestureEpoch == 0)
                _freshGestureEpoch = state.ContactGestureEpoch;
            else if (pointerId == _longPressPointerId && _longPressGestureEpoch == 0)
                _longPressGestureEpoch = state.ContactGestureEpoch;
            AddEvent(new TouchLifecycleProbeEvent(
                NextEventSequence(), "HoldingStarted", pointerId, "Touch", true, true,
                FindAutomationId(args.OriginalSource), "Root", state.ObservedGestureEpoch,
                state.ObservedGestureEpoch,
                System.Diagnostics.Stopwatch.GetTimestamp(), AutomationProperties.GetAutomationId(Renderer)));
        }
        catch (Exception error)
        {
            _failure = "Routed holding observation failed: " + error.GetType().Name + ": " + error.Message;
        }
    }

    private void RecordPointerEvent(string kind, PointerRoutedEventArgs args, object observer)
    {
        if (_closed || _phase is "idle" or "complete" || args.Pointer.PointerDeviceType != PointerDeviceType.Touch)
            return;
        try
        {
            uint pointerId = args.Pointer.PointerId;
            if (_stealCaptureOnNextMove && kind == "PointerMoved" && pointerId == _handlePointerId &&
                args.Pointer.IsInContact)
            {
                _stealCaptureOnNextMove = false;
                // WinUI refuses CapturePointer while another element holds the
                // pointer, so the handle releases first. Inside this routed event
                // WinUI has a current pointer point and raises PointerCaptureLost
                // synchronously; from a timer the release is silent. The capture
                // target then owns the rest of the contact, so its terminal is
                // routed once, through the root.
                Button handle = _capturedHandle ?? throw new InvalidOperationException("The captured selection Button is unknown at the capture-loss move.");
                handle.ReleasePointerCapture(args.Pointer);
                if (!_captureTarget.CapturePointer(args.Pointer))
                    throw new InvalidOperationException("The probe capture target could not take the released touch contact during a routed PointerMoved.");
                _captureMoved = true;
                // Pending handle moves from before this event may already have
                // been applied after the action tick; the selection at the
                // capture move is what the retired drag must keep.
                _captureMoveState = Renderer.CaptureTouchLifecycleProbeState();
            }
            string? sourceAutomationId = FindAutomationId(args.OriginalSource);
            MarkdownRendererControl renderer = Renderer;
            bool withinRenderer = IsDescendantOf(args.OriginalSource, renderer);
            bool startsContactPhaseDeadline = false;

            if (_phase is "awaitLongPress" or "awaitLongPressTerminal")
            {
                if (_longPressPointerId == 0 && kind == "PointerPressed" && withinRenderer &&
                    sourceAutomationId is not (StartHandleId or EndHandleId))
                    _longPressPointerId = pointerId;
                if (pointerId != _longPressPointerId)
                    return;
                if (kind == "PointerPressed" && _events.Any(item => item.PointerId == pointerId &&
                    item.Kind == "PointerPressed"))
                    throw new InvalidOperationException("The long-press pointer was pressed again before routed terminal evidence arrived.");
                startsContactPhaseDeadline = _phase == "awaitLongPress" && kind == "PointerPressed" &&
                    pointerId != 0 && pointerId == _longPressPointerId;
            }
            else if (_phase is "awaitFreshLongPress" or "awaitFreshTerminal")
            {
                if (_freshPointerId == 0 && kind == "PointerPressed" && withinRenderer &&
                    sourceAutomationId is not (StartHandleId or EndHandleId))
                    _freshPointerId = pointerId;
                if (pointerId != _freshPointerId)
                    return;
                if (kind == "PointerPressed" && _events.Any(item => item.Sequence > _freshStartSequence &&
                    item.PointerId == pointerId && item.Kind == "PointerPressed"))
                    throw new InvalidOperationException("The fresh pointer was pressed again before routed terminal evidence arrived.");
                startsContactPhaseDeadline = _phase == "awaitFreshLongPress" && kind == "PointerPressed" &&
                    pointerId != 0 && pointerId == _freshPointerId;
            }
            else if (_phase is "awaitHandleContact" or "awaitHandleMove" or "performAction" or
                     "awaitCaptureLost" or "awaitUnloaded" or "awaitDisposed" or "awaitActionTerminal" or "awaitReattached")
            {
                if (_handlePointerId == 0 && kind == "PointerPressed" &&
                    (sourceAutomationId is StartHandleId or EndHandleId))
                    _handlePointerId = pointerId;
                if (pointerId != _handlePointerId)
                    return;
                if (kind == "PointerPressed" && (_actionSequence > 0 || _events.Any(item =>
                    item.Sequence > _handleStartSequence && item.PointerId == pointerId && item.Kind == "PointerPressed")))
                    throw new InvalidOperationException("The terminal pointer ID was pressed again before routed release/cancel evidence arrived.");
                startsContactPhaseDeadline = _phase == "awaitHandleContact" && kind == "PointerPressed" &&
                    pointerId != 0 && pointerId == _handlePointerId &&
                    sourceAutomationId is StartHandleId or EndHandleId;
            }
            else if (_phase == "failed")
            {
                uint activeId = _handlePointerId != 0 ? _handlePointerId : _longPressPointerId;
                if (pointerId != activeId)
                    return;
            }
            else
            {
                return;
            }

            // Repeated move frames carry little extra evidence; retain one
            // actual contact move per pointer while preserving all transitions.
            if (kind == "PointerMoved" && _events.Any(item => item.Kind == kind &&
                item.PointerId == pointerId && item.SourceAutomationId == sourceAutomationId))
                return;

            TouchLifecycleProbeState? state = CaptureEventState(renderer, pointerId);
            if (state is not null)
            {
                if (pointerId == _longPressPointerId && _longPressGestureEpoch == 0 &&
                    state.ContactPointerId == pointerId)
                    _longPressGestureEpoch = state.ContactGestureEpoch;
                if (pointerId == _freshPointerId && _freshGestureEpoch == 0 &&
                    state.ContactPointerId == pointerId)
                    _freshGestureEpoch = state.ContactGestureEpoch;
                if (pointerId == _handlePointerId && _handleGestureEpoch == 0)
                    _handleGestureEpoch = state.HandlePointerId == pointerId
                        ? state.HandleGestureEpoch
                        : state.ContactPointerId == pointerId ? state.ContactGestureEpoch : 0;
            }
            var probeEvent = new TouchLifecycleProbeEvent(
                NextEventSequence(), kind, pointerId, args.Pointer.PointerDeviceType.ToString(),
                args.Pointer.IsInContact, state is not null, sourceAutomationId,
                ReferenceEquals(observer, _root) ? "Root" : "CapturedButton", state?.ObservedGestureEpoch ?? 0,
                CorrelatedEpoch(pointerId, state?.ObservedGestureEpoch ?? 0),
                System.Diagnostics.Stopwatch.GetTimestamp(), AutomationProperties.GetAutomationId(renderer));
            AddEvent(probeEvent);
            if (kind == "PointerPressed" && pointerId == _handlePointerId)
                _handlePressSequence = probeEvent.Sequence;
            if (startsContactPhaseDeadline && !_coordinatorInputDeadlineStarted)
            {
                _coordinatorInputDeadlineStarted = true;
                StartPhaseDeadline();
            }

        }
        catch (Exception error)
        {
            _failure = "Routed pointer observation failed: " + error.GetType().Name + ": " + error.Message;
        }
    }

    private TouchLifecycleProbeState? CaptureEventState(MarkdownRendererControl renderer, uint pointerId)
    {
        try { return renderer.CaptureTouchLifecycleProbeState(pointerId); }
        catch when (_case == "dispose" && _actionSequence > 0)
        {
            // Routed input remains independent evidence after public Dispose.
            // The required post-disposal cleanup snapshot is still mandatory.
            return null;
        }
    }

    private bool IsExpectedLongPressPointer(uint pointerId)
        => pointerId != 0 && (pointerId == _longPressPointerId || pointerId == _freshPointerId);

    private bool IsDescendantOf(object? value, DependencyObject ancestor)
    {
        if (value is not DependencyObject item)
            return false;
        DependencyObject? node = item;
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor))
                return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private static string? FindAutomationId(object? value)
    {
        if (value is not DependencyObject item)
            return null;
        DependencyObject? node = item;
        while (node is not null)
        {
            string automationId = AutomationProperties.GetAutomationId(node);
            if (!string.IsNullOrEmpty(automationId))
                return automationId;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private void AttachLifecycle(MarkdownRendererControl renderer)
    {
        renderer.Loaded += OnRendererLoaded;
        renderer.Unloaded += OnRendererUnloaded;
        renderer.DisposalCompleted += OnRendererDisposalCompleted;
    }

    private void OnRendererLoaded(object sender, RoutedEventArgs args)
        => RecordLifecycle("Loaded", sender);
    private void OnRendererUnloaded(object sender, RoutedEventArgs args)
        => RecordLifecycle("Unloaded", sender);
    private void OnRendererDisposalCompleted(object? sender, EventArgs args)
        => RecordLifecycle("DisposalCompleted", sender);

    private void RecordLifecycle(string kind, object? sender)
    {
        if (_phase is "idle" or "complete" || sender is not MarkdownRendererControl renderer ||
            !ReferenceEquals(renderer, Renderer))
            return;
        try
        {
            TouchLifecycleProbeState? state = CaptureEventState(renderer, 0);
            AddEvent(new TouchLifecycleProbeEvent(
                NextEventSequence(), kind, 0, "None", null, state is not null, null, "Renderer",
                state?.ObservedGestureEpoch ?? 0, state?.ObservedGestureEpoch ?? 0,
                System.Diagnostics.Stopwatch.GetTimestamp(), AutomationProperties.GetAutomationId(renderer)));
        }
        catch (Exception error)
        {
            _failure = "Lifecycle callback observation failed: " + error.GetType().Name + ": " + error.Message;
        }
    }

    private void AddEvent(TouchLifecycleProbeEvent item)
    {
        if (_events.Count == MaximumEvents)
        {
            int expendable = _events.FindIndex(entry => entry.Kind == "PointerMoved");
            if (expendable >= 0)
            {
                _events.RemoveAt(expendable);
                _eventTraceTruncated = true;
            }
            else
            {
                _eventTraceTruncated = true;
                return;
            }
        }
        _events.Add(item);
        if (_phase == "failed")
            Publish();
    }

    private long CorrelatedEpoch(uint pointerId, long observedEpoch)
    {
        if (observedEpoch != 0)
            return observedEpoch;
        if (pointerId == _handlePointerId)
            return _handleGestureEpoch;
        if (pointerId == _freshPointerId)
            return _freshGestureEpoch;
        if (pointerId == _longPressPointerId)
            return _longPressGestureEpoch;
        return 0;
    }

    // Capture loss is lifecycle evidence, not proof that the user's finger
    // lifted. Only the actual routed, no-longer-in-contact terminal can retire it.
    // T1-026: an in-contact PointerCaptureLost with no selection-handle source,
    // observed at the window root after the renderer left the tree, is WinUI
    // reporting WM_POINTERCAPTURECHANGED or a suspended pointer: Windows sends
    // this window no further messages for that pointer. It is never the
    // handle's own capture loss, which carries the handle as its source.
    private TouchLifecycleProbeEvent? FindRoutedAwayTerminalAfter(uint pointerId, int sequence)
        => _events.LastOrDefault(item => item.Sequence > sequence && item.PointerId == pointerId &&
            item.Device == "Touch" && item.IsInContact == true && item.Kind == "PointerCaptureLost" &&
            item.Observer == "Root" && item.SourceAutomationId is not (StartHandleId or EndHandleId));

    private TouchLifecycleProbeEvent? FindPointerTerminalAfter(uint pointerId, int sequence)
        => _events.LastOrDefault(item => item.Sequence > sequence && item.PointerId == pointerId &&
            item.Device == "Touch" && item.IsInContact == false &&
            (item.Kind is "PointerReleased" or "PointerCanceled"));

    private void DetachRendererFromHost()
    {
        if (_usesAncestor)
        {
            _ancestor.Content = null;
            _host.Children.Remove(_ancestor);
        }
        else
        {
            _host.Children.Remove(_owned);
        }
    }

    private void AttachRendererToHost()
    {
        if (_usesAncestor)
        {
            _ancestor.Content = _document;
            if (!_host.Children.Contains(_ancestor))
                _host.Children.Add(_ancestor);
        }
        else if (!_host.Children.Contains(_owned))
        {
            _host.Children.Add(_owned);
        }
    }

    private void StartPhaseDeadline()
    {
        _coordinatorWaitDeadlineActive = false;
        _deadline = Environment.TickCount64 + (long)PhaseTimeout.TotalMilliseconds;
    }

    private void StartCoordinatorWaitDeadline()
    {
        _coordinatorInputDeadlineStarted = false;
        _coordinatorWaitDeadlineActive = true;
        _deadline = Environment.TickCount64 + (long)HostCoordinatorTimeout.TotalMilliseconds;
    }

    private void ObserveFreshContextMenuOpen(TouchLifecycleProbeSnapshot snapshot)
    {
        if (_freshContextMenuObserved)
            return;
        if (_freshReleaseSequence == 0 || !snapshot.Control.IsContextMenuOpen ||
            snapshot.Control.ContactPointerId != 0 || !snapshot.Control.SelectionIsActive ||
            snapshot.Control.SelectionRange != _freshSelection!.Control.SelectionRange)
            throw new InvalidOperationException("Fresh context-menu observation was not an open native menu after the committed release.");
        _freshContextMenuObserved = true;
        _freshContextMenuOpen = snapshot;
        _freshContextMenuOpenSequence = NextEventSequence();
        AddEvent(CreateFreshMenuStateEvent("FreshContextMenuObserved", snapshot.Control));
        _phase = "awaitFreshMenuDismissal";
        StartCoordinatorWaitDeadline();
        Publish();
    }

    private TouchLifecycleProbeEvent CreateFreshMenuStateEvent(string kind, TouchLifecycleProbeState state)
        => new(
            _freshMenuDismissed ? _freshMenuDismissedSequence : _freshContextMenuOpenSequence,
            kind, _freshPointerId, "ProbeState", false, true,
            "TouchLifecycleProbeWindow", "Probe", state.ObservedGestureEpoch,
            _freshGestureEpoch, System.Diagnostics.Stopwatch.GetTimestamp(),
            AutomationProperties.GetAutomationId(Renderer));

    private int NextEventSequence() => _eventSequence = _eventSequence == int.MaxValue ? 1 : _eventSequence + 1;

    private void Complete()
    {
        _phase = "complete";
        _timer.Stop();
        DetachCapturedTerminalObserver();
        _capturedHandle = null;
        if (_case == "dispose")
        {
            _disposedCase = true;
            SetCommandsEnabled(false);
        }
        else
        {
            SetCommandsEnabled(true);
        }
        Publish();
    }

    private void Fail(Exception error)
    {
        _failure = error.ToString();
        _phase = "failed";
        _timer.Stop();
        // A failed contact is deliberately not replaced or reset. The receipt
        // remains locked to the same renderer until the window is closed.
        SetCommandsEnabled(false);
        Publish();
    }

    private void SetCommandsEnabled(bool enabled)
    {
        _viewport.IsEnabled = enabled;
        _casePicker.IsEnabled = enabled;
        _arm.IsEnabled = enabled;
    }

    private void UpdateReadyStatus()
    {
        if (Renderer.IsLoaded && Renderer.CurrentSnapshot is not null)
        {
            SetCommandsEnabled(true);
            _timer.Stop();
        }
    }

    private void ObserveCapturedTerminal(Button handle)
    {
        // The root route may disappear when the actual Button is detached.
        // Keep a passive observer on that exact native capture target too.
        DetachCapturedTerminalObserver();
        _terminalObserverHandle = handle;
        handle.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPointerReleased), true);
        handle.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnPointerCanceled), true);
    }

    private void DetachCapturedTerminalObserver()
    {
        if (_terminalObserverHandle is not { } handle)
            return;
        handle.RemoveHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPointerReleased));
        handle.RemoveHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnPointerCanceled));
        _terminalObserverHandle = null;
    }

    private void Publish()
    {
        _lastPublishTick = Environment.TickCount64;
        _publishedPhase = _phase;
        var report = new TouchLifecycleProbeReport(
            2, _run, _phase, _case, _usesAncestor ? "ancestor" : "owned",
            _longPressPointerId, _longPressGestureEpoch, _handlePointerId, _handleGestureEpoch,
            _freshPointerId, _freshGestureEpoch, _eventTraceTruncated,
            _baseline, _longPressSelection, _beforeAction, _afterAction, _afterTerminal,
            _afterUnloaded, _afterReattach, _freshSelection,
            _freshContextMenuObserved, _freshContextMenuOpenSequence, _freshMenuDismissed,
            _freshMenuDismissedSequence, _freshContextMenuOpen, _afterFreshTerminal,
            _events.ToArray(), _failure);
        _receipt.Text = JsonSerializer.Serialize(report, TouchLifecycleProbeJsonContext.Default.TouchLifecycleProbeReport);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        DetachCapturedTerminalObserver();
        _root.RemoveHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed));
        _root.RemoveHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerMoved));
        _root.RemoveHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPointerReleased));
        _root.RemoveHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnPointerCanceled));
        _root.RemoveHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnPointerCaptureLost));
        _root.RemoveHandler(UIElement.HoldingEvent, new HoldingEventHandler(OnHolding));
        _owned.Loaded -= OnRendererLoaded;
        _owned.Unloaded -= OnRendererUnloaded;
        _owned.DisposalCompleted -= OnRendererDisposalCompleted;
        _document.Loaded -= OnRendererLoaded;
        _document.Unloaded -= OnRendererUnloaded;
        _document.DisposalCompleted -= OnRendererDisposalCompleted;
        _ancestor.Content = null;
        _host.Children.Clear();
        _owned.Dispose();
        _document.Dispose();
    }
}

internal sealed record TouchLifecycleProbeSnapshot(
    TouchLifecycleProbeState Control,
    TouchLifecycleHandleCapture? StartHandle,
    TouchLifecycleHandleCapture? EndHandle);

internal sealed record TouchLifecycleHandleCapture(
    string AutomationId,
    bool Found,
    string Visibility,
    bool PointerCaptureListAvailable,
    uint[] PointerIds);

internal sealed record TouchLifecycleProbeEvent(
    int Sequence,
    string Kind,
    uint PointerId,
    string Device,
    bool? IsInContact,
    bool InternalStateAvailable,
    string? SourceAutomationId,
    string Observer,
    long ObservedGestureEpoch,
    long CorrelatedGestureEpoch,
    long StopwatchTimestamp,
    string RendererId);

internal sealed record TouchLifecycleProbeReport(
    int Version,
    int Run,
    string Phase,
    string? LifecycleCase,
    string Viewport,
    uint LongPressPointerId,
    long LongPressGestureEpoch,
    uint HandlePointerId,
    long HandleGestureEpoch,
    uint FreshPointerId,
    long FreshGestureEpoch,
    bool EventTraceTruncated,
    TouchLifecycleProbeSnapshot? Baseline,
    TouchLifecycleProbeSnapshot? LongPressSelection,
    TouchLifecycleProbeSnapshot? BeforeAction,
    TouchLifecycleProbeSnapshot? AfterAction,
    TouchLifecycleProbeSnapshot? AfterTerminal,
    TouchLifecycleProbeSnapshot? AfterUnloaded,
    TouchLifecycleProbeSnapshot? AfterReattach,
    TouchLifecycleProbeSnapshot? FreshSelection,
    bool FreshContextMenuObserved,
    int FreshContextMenuOpenSequence,
    bool FreshMenuDismissed,
    int FreshMenuDismissedSequence,
    TouchLifecycleProbeSnapshot? FreshContextMenuOpen,
    TouchLifecycleProbeSnapshot? AfterFreshTerminal,
    TouchLifecycleProbeEvent[] Events,
    string? Failure);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TouchLifecycleProbeReport))]
[JsonSerializable(typeof(DocumentRange))]
[JsonSerializable(typeof(DocumentPosition))]
internal partial class TouchLifecycleProbeJsonContext : JsonSerializerContext;

#pragma warning restore MR1001
