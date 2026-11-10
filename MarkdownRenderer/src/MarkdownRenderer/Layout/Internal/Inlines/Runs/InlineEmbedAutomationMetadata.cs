using System;

namespace MarkdownRenderer.Layout.Internal.Inlines.Runs;

/// <summary>Semantic state shared by virtualized inline hosted elements and UIA.</summary>
internal sealed class InlineEmbedAutomationMetadata
{
    private readonly Func<bool, bool>? _canSetState;
    private readonly Func<bool, bool>? _trySetState;

    internal InlineEmbedAutomationMetadata(
        string checkedName,
        string uncheckedName,
        string readOnlyHelpText,
        string toggleHelpText,
        string automationId,
        bool isChecked,
        Func<bool, bool>? canSetState = null,
        Func<bool, bool>? trySetState = null)
    {
        CheckedName = checkedName;
        UncheckedName = uncheckedName;
        ReadOnlyHelpText = readOnlyHelpText;
        ToggleHelpText = toggleHelpText;
        AutomationId = automationId;
        IsChecked = isChecked;
        _canSetState = canSetState;
        _trySetState = trySetState;
    }

    internal readonly string CheckedName;
    internal readonly string UncheckedName;
    internal readonly string ReadOnlyHelpText;
    internal readonly string ToggleHelpText;
    internal readonly string AutomationId;
    internal bool IsChecked { get; private set; }
    internal string CurrentName => IsChecked ? CheckedName : UncheckedName;
    internal bool CanToggle => CanSetState(!IsChecked);
    internal string CurrentHelpText => CanToggle ? ToggleHelpText : ReadOnlyHelpText;

    internal bool CanSetState(bool requestedState)
    {
        if (_trySetState is null)
            return false;

        try { return _canSetState?.Invoke(requestedState) ?? true; }
        catch { return false; }
    }

    internal bool TrySetState(bool requestedState)
    {
        if (_trySetState is null)
            return false;

        try
        {
            if (!_trySetState.Invoke(requestedState))
                return false;
            IsChecked = requestedState;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
