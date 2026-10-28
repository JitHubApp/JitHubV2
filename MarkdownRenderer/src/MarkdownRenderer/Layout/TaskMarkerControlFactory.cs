using System;
using System.Globalization;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Parsing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MarkdownRenderer.Layout;

internal static partial class TaskMarkerControlFactory
{
    private const float BaseMarkerSize = 20f;

    internal static InlineEmbedRun CreateReadOnlyRun(
        MarkdownLayoutContext context,
        bool isChecked,
        SourceSpan sourceRange)
    {
        float markerSize = GetMarkerSize(context.ThemeSnapshot, editableRequested: false);
        (Windows.UI.Color accent, Windows.UI.Color foreground) = GetColors(context.ThemeSnapshot);
        var metadata = new InlineEmbedAutomationMetadata(
            context.ResolveString(MarkdownStringKeys.TaskCompleted, MarkdownLocalizedStrings.TaskCompleted),
            context.ResolveString(MarkdownStringKeys.TaskIncomplete, MarkdownLocalizedStrings.TaskIncomplete),
            context.ResolveString(MarkdownStringKeys.TaskReadOnly, MarkdownLocalizedStrings.TaskReadOnly),
            context.ResolveString(MarkdownStringKeys.TaskToggle, MarkdownLocalizedStrings.TaskToggle),
            CreateAutomationId(sourceRange),
            isChecked);
        return new InlineEmbedRun(
            markerSize,
            markerSize,
            () => CreateReadOnly(metadata, markerSize, accent, foreground))
        {
            ElementKey = Theming.MarkdownElementKeys.ListMarker,
            SourceSpan = sourceRange,
            AutomationMetadata = metadata,
        };
    }

    internal static float GetMarkerSize(Theming.ThemeSnapshot snapshot, bool editableRequested)
    {
        float scaledGlyphSlot = Math.Max(
            BaseMarkerSize,
            BaseMarkerSize * (float)snapshot.TextScaleFactor);
        return editableRequested
            ? Math.Max(scaledGlyphSlot, (float)snapshot.MinimumInteractiveSize)
            : scaledGlyphSlot;
    }

    internal static (Windows.UI.Color Accent, Windows.UI.Color Foreground) GetColors(
        Theming.ThemeSnapshot snapshot)
        => (snapshot.SelectionHighlightColor, snapshot.SelectionForegroundColor);

    internal static string CreateAutomationId(SourceSpan sourceRange) => string.Concat(
        "MarkdownTask_",
        sourceRange.Start.ToString("X8", CultureInfo.InvariantCulture),
        "_",
        sourceRange.Length.ToString("X8", CultureInfo.InvariantCulture));

    internal static FrameworkElement CreateReadOnly(
        InlineEmbedAutomationMetadata metadata,
        float markerSize,
        Windows.UI.Color accent,
        Windows.UI.Color foreground)
        => new ReadOnlyTaskCheckBox(metadata, markerSize, accent, foreground);

    internal static void Configure(
        CheckBox checkBox,
        bool isInteractive,
        bool isChecked,
        float markerSize,
        Windows.UI.Color selectionAccent,
        Windows.UI.Color selectionForeground)
    {
        checkBox.IsChecked = isChecked;
        checkBox.IsThreeState = false;
        checkBox.IsEnabled = true;
        checkBox.IsHitTestVisible = isInteractive;
        checkBox.IsTabStop = isInteractive;
        checkBox.Width = markerSize;
        checkBox.Height = markerSize;
        checkBox.MinWidth = markerSize;
        checkBox.MinHeight = markerSize;
        checkBox.Padding = new Thickness(0);
        checkBox.Margin = new Thickness(0);
        checkBox.HorizontalContentAlignment = HorizontalAlignment.Center;
        checkBox.VerticalContentAlignment = VerticalAlignment.Center;
        checkBox.HorizontalAlignment = HorizontalAlignment.Center;
        checkBox.VerticalAlignment = VerticalAlignment.Center;

        var accentBrush = new SolidColorBrush(selectionAccent);
        var foregroundBrush = new SolidColorBrush(selectionForeground);
        checkBox.Resources["CheckBoxCheckBackgroundFillChecked"] = accentBrush;
        checkBox.Resources["CheckBoxCheckBackgroundFillCheckedPointerOver"] = accentBrush;
        checkBox.Resources["CheckBoxCheckBackgroundFillCheckedPressed"] = accentBrush;
        checkBox.Resources["CheckBoxCheckBackgroundStrokeChecked"] = accentBrush;
        checkBox.Resources["CheckBoxCheckBackgroundStrokeCheckedPointerOver"] = accentBrush;
        checkBox.Resources["CheckBoxCheckBackgroundStrokeCheckedPressed"] = accentBrush;
        checkBox.Resources["CheckBoxCheckBackgroundStrokeUncheckedPointerOver"] = accentBrush;
        checkBox.Resources["CheckBoxCheckBackgroundStrokeUncheckedPressed"] = accentBrush;
        checkBox.Resources["CheckBoxCheckGlyphForegroundChecked"] = foregroundBrush;
        checkBox.Resources["CheckBoxCheckGlyphForegroundCheckedPointerOver"] = foregroundBrush;
        checkBox.Resources["CheckBoxCheckGlyphForegroundCheckedPressed"] = foregroundBrush;
    }

    private sealed partial class ReadOnlyTaskCheckBox : CheckBox
    {
        internal ReadOnlyTaskCheckBox(
            InlineEmbedAutomationMetadata metadata,
            float markerSize,
            Windows.UI.Color accent,
            Windows.UI.Color foreground)
        {
            Configure(this, isInteractive: false, metadata.IsChecked, markerSize, accent, foreground);
            AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
            AutomationProperties.SetAutomationId(this, metadata.AutomationId);
            AutomationProperties.SetName(this, metadata.CurrentName);
            AutomationProperties.SetHelpText(this, metadata.ReadOnlyHelpText);
        }

        protected override AutomationPeer OnCreateAutomationPeer() =>
            new ReadOnlyTaskCheckBoxAutomationPeer(this);
    }

    private sealed partial class ReadOnlyTaskCheckBoxAutomationPeer(ReadOnlyTaskCheckBox owner)
        : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(CheckBox);

        protected override AutomationControlType GetAutomationControlTypeCore() =>
            AutomationControlType.CheckBox;

        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;

        protected override bool IsKeyboardFocusableCore() => false;

        protected override object GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Toggle
                ? null!
                : base.GetPatternCore(patternInterface);
    }
}
