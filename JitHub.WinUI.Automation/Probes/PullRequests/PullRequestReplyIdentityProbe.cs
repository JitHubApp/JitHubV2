using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.PullRequests;

/// <summary>
/// Drives the pull-request-reply-identities probe.
/// </summary>
internal static class PullRequestReplyIdentityProbe
{
    // This method stays whole because its local editor-reading closure captures the window and is called twice: first
    // to record the identityless reply editor IDs, then again after switching to the second pull request and back, so
    // the recreated containers can be compared with the first read.
    internal static void RunPullRequestReplyIdentityProbe(CaptureOptions options)
    {
        using var app = LaunchApplication(
            options.AppPath,
            "--page=repo-pulls",
            "--scenario=pr-reply-identities",
            "--theme=dark",
            $"--repo={options.RepositoryFullName}");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "pull-request reply identity probe");
            ResizeWindow(window, 1366, 900);
            WaitForElement(
                "pull request detail",
                () => FindCurrentVisibleByAutomationId(window, "RepoPullRequestsDetailTitle"),
                TimeSpan.FromSeconds(15));
            AutomationElement reviewsSection = WaitForElement(
                "pull request reviews section",
                () => FindCurrentVisibleByAutomationId(window, "RepoPullRequestsSection_Reviews"),
                TimeSpan.FromSeconds(8));
            AssertProbe(!string.IsNullOrWhiteSpace(GetElementName(reviewsSection)), "Reviews section had no accessible name.");
            AutomationElement commitsSection = WaitForElement(
                "pull request commits section",
                () => FindCurrentVisibleByAutomationId(window, "RepoPullRequestsSection_Commits"),
                TimeSpan.FromSeconds(5));
            commitsSection.Click();
            Thread.Sleep(250);
            commitsSection.Focus();
            Keyboard.Press(VirtualKeyShort.RIGHT);
            Thread.Sleep(250);
            Keyboard.Press(VirtualKeyShort.ENTER);
            Thread.Sleep(400);

            CaptureWindow(
                window,
                Path.Combine(options.OutputDirectory, "pull-request-reviews-activation.png"));

            static AutomationElement[] FindIdentitylessReplyEditors(Window currentWindow) => currentWindow
                .FindAllDescendants()
                .Where(element =>
                {
                    string id = GetAutomationId(element);
                    return
                    id.StartsWith("PullRequestReviewThread_context_", StringComparison.Ordinal) &&
                    id.EndsWith("_ReplyForm_Editor", StringComparison.Ordinal);
                })
                .ToArray();

            string[] OpenReviewsAndReadEditors()
            {
                WaitUntil(
                    "first identityless pull request reply editor",
                    () => FindIdentitylessReplyEditors(window).Length >= 1,
                    TimeSpan.FromSeconds(12));
                AutomationElement contentScrollViewer = WaitForElement(
                    "pull request content scroll viewer",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoPullRequestsContentScrollViewer")),
                    TimeSpan.FromSeconds(8));
                bool supportsScrolling = contentScrollViewer.Patterns.Scroll.IsSupported;
                bool TryScroll(ScrollAmount amount)
                {
                    if (!supportsScrolling)
                    {
                        return false;
                    }

                    try
                    {
                        contentScrollViewer.Patterns.Scroll.Pattern.Scroll(ScrollAmount.NoAmount, amount);
                        return true;
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                }

                for (int attempt = 0; attempt < 12; attempt++)
                {
                    if (!TryScroll(ScrollAmount.LargeDecrement))
                    {
                        break;
                    }

                    Thread.Sleep(75);
                }

                HashSet<string> editorIds = new(StringComparer.Ordinal);
                for (int attempt = 0; attempt < 16 && editorIds.Count < 2; attempt++)
                {
                    foreach (AutomationElement editor in FindIdentitylessReplyEditors(window))
                    {
                        string editorId = GetAutomationId(editor);
                        AssertProbe(editor.ControlType == ControlType.Edit, $"{editorId} did not expose an Edit peer.");
                        AssertProbe(!string.IsNullOrWhiteSpace(GetElementName(editor)), $"{editorId} had no accessible name.");
                        editorIds.Add(editorId);
                    }

                    if (!TryScroll(ScrollAmount.LargeIncrement))
                    {
                        break;
                    }

                    Thread.Sleep(150);
                }

                string[] editors = editorIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();
                if (editors.Length < 2)
                {
                    string[] realizedReviewIds = window
                        .FindAllDescendants()
                        .Select(element => GetAutomationId(element))
                        .Where(id =>
                            id.Contains("PullRequestReview", StringComparison.Ordinal) ||
                            id.Contains("ReplyForm", StringComparison.Ordinal))
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToArray();
                    CaptureWindow(
                        window,
                        Path.Combine(options.OutputDirectory, "pull-request-reply-identities-failure.png"));
                    throw new InvalidOperationException(
                        $"Expected two simultaneous identityless reply editors but found {editors.Length}. " +
                        $"Realized review IDs: {string.Join(", ", realizedReviewIds)}");
                }

                return editors;
            }

            string[] initialEditors = OpenReviewsAndReadEditors();
            AssertProbe(initialEditors.Length == 2, $"Expected two identityless reply forms, found {initialEditors.Length}.");
            AssertProbe(
                initialEditors.Distinct(StringComparer.Ordinal).Count() == initialEditors.Length,
                "Simultaneous null/zero-ID reply forms exposed duplicate editor identities.");

            // Navigate away and back so the review item models and their DataTemplate containers are recreated.
            AutomationElement pullRequestList = WaitForElement(
                "repository pull request list",
                () => FindCurrentVisibleByAutomationId(window, "RepoPullRequestsList"),
                TimeSpan.FromSeconds(8));
            AutomationElement[] rows = pullRequestList
                .FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .Where(IsVisible)
                .Take(2)
                .ToArray();
            AssertProbe(rows.Length == 2, "The identity probe requires two visible pull request rows.");
            string originalTitle = GetElementName(WaitForElement(
                "original pull request title",
                () => FindCurrentVisibleByAutomationId(window, "RepoPullRequestsDetailTitle"),
                TimeSpan.FromSeconds(5)));
            InvokeOrClick(rows[1]);
            WaitUntil(
                "second pull request detail",
                () => FindCurrentVisibleByAutomationId(window, "RepoPullRequestsDetailTitle") is AutomationElement detailTitle &&
                    !string.Equals(GetElementName(detailTitle), originalTitle, StringComparison.Ordinal),
                TimeSpan.FromSeconds(10));
            InvokeOrClick(rows[0]);
            WaitUntil(
                "original pull request detail after recycling",
                () => FindCurrentVisibleByAutomationId(window, "RepoPullRequestsDetailTitle") is AutomationElement detailTitle &&
                    string.Equals(GetElementName(detailTitle), originalTitle, StringComparison.Ordinal),
                TimeSpan.FromSeconds(10));

            string[] recreatedEditors = OpenReviewsAndReadEditors();
            AssertProbe(
                initialEditors.SequenceEqual(recreatedEditors, StringComparer.Ordinal),
                "Identityless reply form identities changed after item/container recreation.");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "pull-request-reply-identities.png"));
            Console.WriteLine("pull-request reply identities: simultaneous, identityless, and recreated containers verified");
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }
}
