using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Gfm.Renderers;

/// <summary>
/// The interactive task-list marker: a WinUI <see cref="CheckBox"/> whose toggles
/// commit through the task command set and roll back when the command is refused.
/// </summary>
/// <remarks>
/// <c>partial</c> is required by the CsWinRT AOT generator (CsWinRT1028), which
/// emits the WinRT vtable for a WinRT-derived class that adds an interface.
/// </remarks>
internal sealed partial class EditableTaskCheckBox : CheckBox, IDisposable
{
    private readonly InlineEmbedAutomationMetadata _automationMetadata;
    private readonly Action? _availabilityChanged;
    private readonly TaskToggleCommandSet _taskCommands;
    private readonly IDisposable _commandSubscription;
    private bool _committedState;
    private bool _isDisposed;
    private bool _suppress;

    public EditableTaskCheckBox(
        InlineEmbedAutomationMetadata automationMetadata,
        TaskToggleCommandSet taskCommands,
        Action? availabilityChanged,
        float markerSize,
        Windows.UI.Color selectionAccent,
        Windows.UI.Color selectionForeground)
    {
        _automationMetadata = automationMetadata;
        _availabilityChanged = availabilityChanged;
        _taskCommands = taskCommands;
        _committedState = automationMetadata.IsChecked;

        TaskMarkerControlFactory.Configure(
            this,
            isInteractive: true,
            _committedState,
            markerSize,
            selectionAccent,
            selectionForeground);
        AutomationProperties.SetAutomationId(this, automationMetadata.AutomationId);
        AutomationProperties.SetHelpText(this, automationMetadata.ToggleHelpText);
        UpdateAutomationName(_committedState);

        Checked += OnToggleStateChanged;
        Unchecked += OnToggleStateChanged;
        _commandSubscription = _taskCommands.Subscribe(OnCommandCanExecuteChanged);
        RefreshAvailability();
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        Checked -= OnToggleStateChanged;
        Unchecked -= OnToggleStateChanged;
        _commandSubscription.Dispose();
        Command = null;
    }

    private void OnCommandCanExecuteChanged(object? sender, EventArgs e)
    {
        if (_isDisposed)
            return;

        if (DispatcherQueue.HasThreadAccess)
        {
            RefreshAvailability();
            return;
        }

        _ = DispatcherQueue.TryEnqueue(RefreshAvailability);
    }

    private void OnToggleStateChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress)
            return;

        bool requestedState = IsChecked == true;
        if (!_automationMetadata.TrySetState(requestedState))
        {
            RestoreCommittedState();
            return;
        }

        _committedState = _automationMetadata.IsChecked;
        UpdateAutomationName(_committedState);
        RefreshAvailability();
    }

    private void RestoreCommittedState()
    {
        _suppress = true;
        IsChecked = _committedState;
        _suppress = false;
        UpdateAutomationName(_committedState);
    }

    private void RefreshAvailability()
    {
        if (_isDisposed)
            return;

        bool canToggle = _taskCommands.CanSetState(!_committedState);
        bool availabilityChanged = IsEnabled != canToggle;
        IsEnabled = canToggle;
        AutomationProperties.SetHelpText(
            this,
            canToggle
                ? _automationMetadata.ToggleHelpText
                : _automationMetadata.ReadOnlyHelpText);
        if (availabilityChanged)
            _availabilityChanged?.Invoke();
    }

    private void UpdateAutomationName(bool isChecked)
        => AutomationProperties.SetName(
            this,
            isChecked
                ? _automationMetadata.CheckedName
                : _automationMetadata.UncheckedName);
}
