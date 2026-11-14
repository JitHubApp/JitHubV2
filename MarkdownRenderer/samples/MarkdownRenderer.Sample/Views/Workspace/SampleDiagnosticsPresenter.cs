using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MarkdownRenderer.Document;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Sample.Services.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace MarkdownRenderer.Sample.Views.Workspace;

/// <summary>
/// Shows the active document's renderer diagnostics, or a render failure, in
/// the workspace InfoBar and announces each new summary once to assistive
/// technology.
/// </summary>
internal sealed class SampleDiagnosticsPresenter
{
    private const int MaximumDisplayedDiagnostics = 50;
    private const string NotificationActivityId = "RendererDiagnostics";

    private readonly SampleStrings _strings;
    private readonly InfoBar _bar;
    private readonly TextBlock _message;
    private string _lastAnnouncedSummary = string.Empty;

    internal SampleDiagnosticsPresenter(SampleStrings strings)
    {
        _strings = strings;
        _message = new TextBlock
        {
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(_message, "SampleDiagnosticsMessage");

        var scroller = new ScrollViewer
        {
            Content = _message,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = (double)Application.Current.Resources["SampleDiagnosticsMaxHeight"],
        };
        AutomationProperties.SetName(
            scroller,
            strings.Get("DiagnosticsDetailsAutomationName", "Diagnostic details"));

        _bar = new InfoBar
        {
            Content = scroller,
            IsClosable = false,
            IsOpen = false,
            Margin = (Thickness)Application.Current.Resources["SampleDiagnosticsMargin"],
            Severity = InfoBarSeverity.Informational,
        };
        AutomationProperties.SetAutomationId(_bar, "SampleDiagnostics");
        AutomationProperties.SetName(
            _bar,
            strings.Get("DiagnosticsAutomationName", "Renderer diagnostics"));
        AutomationProperties.SetLiveSetting(_bar, AutomationLiveSetting.Polite);
    }

    internal InfoBar Element => _bar;

    /// <summary>
    /// Shows the diagnostics of <paramref name="document"/>, merged in source
    /// order with the deferred-scene diagnostics of <paramref name="deferredSceneSession"/>
    /// when the document renders through that session.
    /// </summary>
    internal void Show(MarkdownDocument? document, MarkdownPerformanceSession? deferredSceneSession)
    {
        IReadOnlyList<MarkdownDiagnostic> diagnostics = Collect(document, deferredSceneSession);
        if (diagnostics.Count == 0)
        {
            Clear();
            return;
        }

        int highestSeverityIndex = FindHighestSeverityIndex(diagnostics);
        MarkdownDiagnosticSeverity highestSeverity = diagnostics[highestSeverityIndex].Severity;
        string title = string.Format(
            CultureInfo.CurrentUICulture,
            _strings.Get("DiagnosticsTitleFormat", "Renderer diagnostics ({0})"),
            diagnostics.Count);
        _bar.Title = title;
        _bar.Severity = highestSeverity switch
        {
            MarkdownDiagnosticSeverity.Error => InfoBarSeverity.Error,
            MarkdownDiagnosticSeverity.Warning => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational,
        };
        string messageText = BuildMessage(diagnostics, highestSeverityIndex);
        _message.Text = messageText;
        AutomationProperties.SetName(_message, messageText);

        Open(string.Format(
            CultureInfo.CurrentUICulture,
            _strings.Get("DiagnosticsAutomationSummaryFormat", "{0}. {1}"),
            title,
            FormatLine(diagnostics[highestSeverityIndex])));
    }

    internal void ShowRenderFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string title = _strings.Get("RenderFailedTitle", "Render failed");
        _bar.Title = title;
        _bar.Severity = InfoBarSeverity.Error;
        string message = string.Format(
            CultureInfo.CurrentUICulture,
            _strings.Get("RenderFailureMessageFormat", "{0}: {1}"),
            exception.GetType().Name,
            exception.Message);
        _message.Text = message;
        AutomationProperties.SetName(_message, message);
        Open(string.Format(
            CultureInfo.CurrentUICulture,
            _strings.Get("DiagnosticsAutomationSummaryFormat", "{0}. {1}"),
            title,
            message));
    }

    /// <summary>
    /// Hides the diagnostics, announcing that they cleared if a summary was
    /// announced while they were open.
    /// </summary>
    internal void Clear()
    {
        bool hadAnnouncedDiagnostics = _bar.IsOpen &&
            !string.IsNullOrEmpty(_lastAnnouncedSummary);
        if (hadAnnouncedDiagnostics)
        {
            string notification = _strings.Get(
                "DiagnosticsClearedNotification",
                "No renderer diagnostics.");
            AutomationPeer? peer =
                FrameworkElementAutomationPeer.FromElement(_bar) ??
                FrameworkElementAutomationPeer.CreatePeerForElement(_bar);
            peer?.RaiseNotificationEvent(
                AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.MostRecent,
                notification,
                NotificationActivityId);
        }

        _bar.IsOpen = false;
        _bar.Title = string.Empty;
        _message.Text = string.Empty;
        _lastAnnouncedSummary = string.Empty;
        AutomationProperties.SetName(_message, string.Empty);
        AutomationProperties.SetName(
            _bar,
            _strings.Get("DiagnosticsAutomationName", "Renderer diagnostics"));
    }

    private static IReadOnlyList<MarkdownDiagnostic> Collect(
        MarkdownDocument? document,
        MarkdownPerformanceSession? deferredSceneSession)
    {
        IReadOnlyList<MarkdownDiagnostic> diagnostics =
            document?.Diagnostics ?? Array.Empty<MarkdownDiagnostic>();
        if (document is null || deferredSceneSession is null)
            return diagnostics;

        IReadOnlyList<MarkdownDiagnostic> deferred =
            deferredSceneSession.GetDeferredSceneDiagnostics(document);
        if (deferred.Count == 0)
            return diagnostics;

        var combined = new List<MarkdownDiagnostic>(diagnostics.Count + deferred.Count);
        combined.AddRange(diagnostics);
        combined.AddRange(deferred);
        combined.Sort(static (left, right) =>
            left.SourceSpan.Start.CompareTo(right.SourceSpan.Start));
        return combined;
    }

    // The first diagnostic of the highest severity summarizes the set.
    private static int FindHighestSeverityIndex(IReadOnlyList<MarkdownDiagnostic> diagnostics)
    {
        MarkdownDiagnosticSeverity highestSeverity = MarkdownDiagnosticSeverity.Information;
        int highestSeverityIndex = 0;
        for (int index = 0; index < diagnostics.Count; index++)
        {
            if (diagnostics[index].Severity > highestSeverity)
            {
                highestSeverity = diagnostics[index].Severity;
                highestSeverityIndex = index;
            }
        }

        return highestSeverityIndex;
    }

    // Lists up to MaximumDisplayedDiagnostics lines. When the summarizing
    // diagnostic would be cut off, it takes the last displayed line.
    private string BuildMessage(IReadOnlyList<MarkdownDiagnostic> diagnostics, int highestSeverityIndex)
    {
        var message = new StringBuilder();
        int displayedCount = System.Math.Min(diagnostics.Count, MaximumDisplayedDiagnostics);
        for (int index = 0; index < displayedCount; index++)
        {
            int diagnosticIndex = diagnostics.Count > MaximumDisplayedDiagnostics &&
                                  highestSeverityIndex >= MaximumDisplayedDiagnostics &&
                                  index == displayedCount - 1
                ? highestSeverityIndex
                : index;
            if (index > 0)
                message.AppendLine();
            message.Append(FormatLine(diagnostics[diagnosticIndex]));
        }

        if (diagnostics.Count > displayedCount)
        {
            message.AppendLine()
                .Append(string.Format(
                    CultureInfo.CurrentUICulture,
                    _strings.Get("DiagnosticsRemainingFormat", "Additional diagnostics not shown: {0}"),
                    diagnostics.Count - displayedCount));
        }

        return message.ToString();
    }

    private void Open(string accessibleSummary)
    {
        AutomationProperties.SetName(_bar, accessibleSummary);
        _bar.IsOpen = true;
        if (string.Equals(accessibleSummary, _lastAnnouncedSummary, StringComparison.Ordinal))
            return;

        _lastAnnouncedSummary = accessibleSummary;
        AutomationPeer? peer =
            FrameworkElementAutomationPeer.FromElement(_bar) ??
            FrameworkElementAutomationPeer.CreatePeerForElement(_bar);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private string FormatLine(MarkdownDiagnostic diagnostic) => string.Format(
        CultureInfo.CurrentUICulture,
        _strings.Get("DiagnosticLineFormat", "{0} {1} [{2}..{3}): {4}"),
        GetSeverityLabel(diagnostic.Severity),
        diagnostic.Code,
        diagnostic.SourceSpan.Start.ToString(CultureInfo.InvariantCulture),
        diagnostic.SourceSpan.End.ToString(CultureInfo.InvariantCulture),
        diagnostic.Message);

    private string GetSeverityLabel(MarkdownDiagnosticSeverity severity) => severity switch
    {
        MarkdownDiagnosticSeverity.Error => _strings.Get("DiagnosticsError", "Error"),
        MarkdownDiagnosticSeverity.Warning => _strings.Get("DiagnosticsWarning", "Warning"),
        _ => _strings.Get("DiagnosticsInformation", "Information"),
    };
}
