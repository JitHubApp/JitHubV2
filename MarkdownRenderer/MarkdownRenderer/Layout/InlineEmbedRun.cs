using System;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Inline run that reserves a fixed-size cell within a paragraph or heading
/// for a hosted WinUI element (e.g. a CheckBox for a GFM task list item).
/// The cell is filled by a transparent <c>U+FFFC</c> object replacement
/// character whose advance width is forced by
/// <c>CanvasTextLayout.SetCharacterSpacing</c>.
/// </summary>
internal sealed class InlineEmbedRun : InlineRun
{
    public const string PlaceholderChar = "\uFFFC";

    public float DesiredWidth { get; }
    public float DesiredHeight { get; }

    /// <summary>
    /// UI-thread factory invoked when the embed is realized. Closures may
    /// capture primitives (e.g. <c>Checked</c>) but MUST NOT capture
    /// FrameworkElements created on a different thread.
    /// </summary>
    public Func<FrameworkElement> ElementFactory { get; }

    /// <summary>
    /// Optional UI-thread hook invoked when a previously-realised inline
    /// embed is being recycled because it has scrolled outside the
    /// virtualization derealize band. Mirrors
    /// <c>IMarkdownEmbedFactory.RecycleBlock</c> for block embeds and is the
    /// only way to release resources held by closures captured by
    /// <see cref="ElementFactory"/> (event handlers, timers, native handles).
    /// </summary>
    public Action<FrameworkElement>? Recycle { get; set; }

    /// <summary>
    /// Optional UI-object-free metadata used to preserve semantic identity and
    /// state while the hosted element is outside the realization band.
    /// </summary>
    internal InlineEmbedAutomationMetadata? AutomationMetadata { get; init; }

    /// <summary>
    /// Set by <see cref="MarkdownRenderer.Layout.Boxes.InlineContainerBox"/>
    /// once the element is realized on the UI thread.
    /// </summary>
    public FrameworkElement? RealizedElement { get; set; }

    public InlineEmbedRun(float desiredWidth, float desiredHeight, Func<FrameworkElement> factory)
    {
        DesiredWidth = Math.Max(1f, desiredWidth);
        DesiredHeight = Math.Max(1f, desiredHeight);
        ElementFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        RenderedLength = 1;
    }

    public override string Text => PlaceholderChar;

    public override string AccessibleText =>
        AutomationMetadata?.CurrentName ?? PlaceholderChar;
}

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

    internal string CheckedName { get; }
    internal string UncheckedName { get; }
    internal string ReadOnlyHelpText { get; }
    internal string ToggleHelpText { get; }
    internal string AutomationId { get; }
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
