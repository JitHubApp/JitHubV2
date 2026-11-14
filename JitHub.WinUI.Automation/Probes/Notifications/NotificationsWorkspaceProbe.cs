using System.Drawing;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Notifications;

/// <summary>
/// Drives the notifications-workspace probe.
/// </summary>
internal static class NotificationsWorkspaceProbe
{
    // This method stays whole because it drives one Notifications window through UIA name checks, search filtering,
    // All, Participating and Unread filter changes, read, follow and mute row toggles, five responsive resizes and
    // issue routing, and each step relies on the filter and row state the previous step set.
    internal static void RunNotificationsWorkspaceProbe(CaptureOptions options)
    {
        KillExistingApplicationInstances(options.AppPath);
        using var app = LaunchApplication(options.AppPath, "--page=notifications", "--theme=dark");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "notifications-workspace probe");
            ResizeWindow(window, 1366, 900);

            AutomationElement root;
            try
            {
                root = WaitForElement(
                    "NotificationsPageRoot",
                    () => FindNotificationsWorkspaceRoot(automation, window),
                    TimeSpan.FromSeconds(10));
            }
            catch
            {
                PrintVisibleAutomationIds(window, "notifications-workspace launch failure");
                CaptureWindow(window, Path.Combine(options.OutputDirectory, "notifications-launch-failure.png"));
                throw;
            }
            AssertProbe(IsVisible(root), "The Notifications workspace root was not visible.");
            AssertProbe(IsInsideWindowBounds(root, window), "The Notifications workspace root escaped the app window.");

            AutomationElement search = AssertNamedAutomationElement(window, "NotificationsSearch", ControlType.Edit);
            AssertProbe(
                string.Equals(GetElementName(search), "Search notifications", StringComparison.Ordinal),
                "NotificationsSearch did not expose its intended accessible name.");

            AutomationElement filter = WaitForElement(
                "NotificationsFilter",
                () => FindNotificationsFilterRoot(automation, window),
                TimeSpan.FromSeconds(8));
            AssertProbe(IsVisible(filter), "NotificationsFilter was not visible in the live UIA tree.");

            AutomationElement unreadFilter = AssertNotificationFilterItem(window, "NotificationsFilter_Unread", "Unread");
            AutomationElement allFilter = AssertNotificationFilterItem(window, "NotificationsFilter_All", "All");
            AutomationElement participatingFilter = AssertNotificationFilterItem(window, "NotificationsFilter_Participating", "Participating");

            AutomationElement list = AssertNamedAutomationElement(window, "NotificationsList", ControlType.List);
            AssertProbe(
                string.Equals(GetElementName(list), "GitHub notifications", StringComparison.Ordinal),
                "NotificationsList did not expose its intended accessible name.");

            AutomationElement markAllRead = AssertNamedAutomationElement(window, "NotificationsMarkAllRead", ControlType.Button);
            AssertProbe(
                string.Equals(GetElementName(markAllRead), "Mark all notifications as read", StringComparison.Ordinal),
                "NotificationsMarkAllRead did not expose its intended accessible name.");

            WaitForElement(
                "unread preview notification",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationRow_preview-issue")),
                TimeSpan.FromSeconds(10));
            AssertProbe(
                !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationRow_preview-release"))),
                "The Unread filter included the read preview notification.");

            search.AsTextBox().Text = "Polish";
            WaitForElement(
                "filtered pull request notification",
                () =>
                {
                    AutomationElement? row = window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationRow_preview-pr"));
                    return IsVisible(row) ? row : null;
                },
                TimeSpan.FromSeconds(5));
            WaitUntil(
                "non-matching notification to leave the filtered list",
                () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationRow_preview-issue"))),
                TimeSpan.FromSeconds(5));
            search.AsTextBox().Text = string.Empty;
            WaitForElement(
                "unread preview notification after clearing search",
                () =>
                {
                    AutomationElement? row = window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationRow_preview-issue"));
                    return IsVisible(row) ? row : null;
                },
                TimeSpan.FromSeconds(5));

            SelectAutomationItem(allFilter, "Notifications All filter");
            AutomationElement releaseRow = WaitForElement(
                "All filter read preview notification",
                () =>
                {
                    AutomationElement? row = window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationRow_preview-release"));
                    return IsVisible(row) ? row : null;
                },
                TimeSpan.FromSeconds(8));
            AssertProbe(IsVisible(releaseRow), "The All filter did not include the read preview notification.");

            AutomationElement readAction = WaitForNotificationActionName(
                window,
                "NotificationRead_preview_issue",
                "Mark as read");
            InvokeOrClick(readAction);
            WaitUntil(
                "mark-read action to leave the read notification",
                () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationRead_preview_issue"))),
                TimeSpan.FromSeconds(5));

            AutomationElement subscriptionAction = WaitForNotificationActionNames(
                window,
                "NotificationSubscription_preview_issue",
                "Manage following",
                "Follow thread");
            InvokeOrClick(subscriptionAction);
            subscriptionAction = WaitForNotificationActionName(
                window,
                "NotificationSubscription_preview_issue",
                "Unsubscribe from thread");
            InvokeOrClick(subscriptionAction);
            _ = WaitForNotificationActionName(
                window,
                "NotificationSubscription_preview_issue",
                "Follow thread");

            AutomationElement muteAction = WaitForNotificationActionNames(
                window,
                "NotificationMute_preview_issue",
                "Manage muting",
                "Mute thread");
            InvokeOrClick(muteAction);
            muteAction = WaitForNotificationActionName(
                window,
                "NotificationMute_preview_issue",
                "Unmute thread");
            InvokeOrClick(muteAction);
            _ = WaitForNotificationActionName(
                window,
                "NotificationMute_preview_issue",
                "Mute thread");

            SelectAutomationItem(participatingFilter, "Notifications Participating filter");
            WaitForElement(
                "Participating filter preview notification",
                () =>
                {
                    AutomationElement? row = window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationRow_preview-pr"));
                    return IsVisible(row) ? row : null;
                },
                TimeSpan.FromSeconds(8));
            SelectAutomationItem(unreadFilter, "Notifications Unread filter");
            WaitUntil(
                "Unread filter to hide the read preview notification",
                () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationRow_preview-release"))),
                TimeSpan.FromSeconds(8));

            (int Width, int Height)[] sizes =
            [
                (1366, 900),
                (1180, 800),
                (900, 700),
                (760, 650),
                (640, 600)
            ];
            foreach ((int width, int height) in sizes)
            {
                Rectangle resizedBounds = ResizeWindow(window, width, height);
                string viewportLabel = GetResponsiveViewportLabel(width, height, resizedBounds);
                root = WaitForElement(
                    "NotificationsPageRoot after resize",
                    () => FindNotificationsWorkspaceRoot(automation, window),
                    TimeSpan.FromSeconds(5));
                search = WaitForElement(
                    "NotificationsSearch after resize",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationsSearch")),
                    TimeSpan.FromSeconds(5));
                filter = WaitForElement(
                    "NotificationsFilter after resize",
                    () => FindNotificationsFilterRoot(automation, window),
                    TimeSpan.FromSeconds(5));
                list = WaitForElement(
                    "NotificationsList after resize",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationsList")),
                    TimeSpan.FromSeconds(5));
                markAllRead = WaitForElement(
                    "NotificationsMarkAllRead after resize",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationsMarkAllRead")),
                    TimeSpan.FromSeconds(5));

                foreach ((AutomationElement element, string name) in new[]
                {
                    (root, "workspace root"),
                    (search, "search"),
                    (filter, "filter"),
                    (list, "notification list"),
                    (markAllRead, "mark-all-read action")
                })
                {
                    AssertProbe(IsVisible(element), $"Notifications {name} was offscreen at {viewportLabel}.");
                    AssertProbe(IsInsideWindowBounds(element, window), $"Notifications {name} was clipped at {viewportLabel}.");
                    AssertProbe(IsInsideElementBounds(element, root, 1.5), $"Notifications {name} escaped the page at {viewportLabel}.");
                }

                CaptureWindow(
                    window,
                    Path.Combine(options.OutputDirectory, $"notifications-responsive-{viewportLabel}.png"));
            }

            ResizeWindow(window, 1366, 900);
            AutomationElement issueRow = WaitForElement(
                "preview issue row for routing",
                () =>
                {
                    AutomationElement? row = window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationRow_preview-issue"));
                    return IsVisible(row) ? row : null;
                },
                TimeSpan.FromSeconds(8));
            InvokeOrClick(issueRow);
            try
            {
                WaitForElement(
                    "repository Issues destination",
                    () =>
                    {
                        AutomationElement? destination = window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesDetailTitle"));
                        return IsVisible(destination) ? destination : null;
                    },
                    TimeSpan.FromSeconds(20));
            }
            catch
            {
                PrintVisibleAutomationIds(window, "notifications-workspace routing failure");
                CaptureWindow(window, Path.Combine(options.OutputDirectory, "notifications-routing-failure.png"));
                throw;
            }

            Console.WriteLine(
                $"notifications-workspace probe: UIA, filters, search, row actions, routing, and {sizes.Length} responsive captures passed; output={options.OutputDirectory}");
        }
        finally
        {
            TryClose(app);
        }
    }

    private static AutomationElement AssertNotificationFilterItem(Window window, string automationId, string expectedName)
    {
        AutomationElement item = WaitForElement(
            automationId,
            () => window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)),
            TimeSpan.FromSeconds(8));
        AssertProbe(IsVisible(item), $"{automationId} was not visible in the live UIA tree.");
        AssertProbe(
            string.Equals(GetElementName(item), expectedName, StringComparison.Ordinal),
            $"{automationId} did not expose the accessible name '{expectedName}'.");
        return item;
    }

    private static AutomationElement WaitForNotificationActionName(
        Window window,
        string automationId,
        string expectedName)
    {
        return WaitForElement(
            $"{automationId} to become {expectedName}",
            () =>
            {
                AutomationElement? action = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
                return IsVisible(action)
                    && action!.Properties.IsEnabled.ValueOrDefault
                    && string.Equals(GetElementName(action), expectedName, StringComparison.Ordinal)
                    ? action
                    : null;
            },
            TimeSpan.FromSeconds(8));
    }

    private static AutomationElement WaitForNotificationActionNames(
        Window window,
        string automationId,
        params string[] expectedNames)
    {
        return WaitForElement(
            $"{automationId} to become one of: {string.Join(", ", expectedNames)}",
            () =>
            {
                AutomationElement? action = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
                return IsVisible(action)
                    && action!.Properties.IsEnabled.ValueOrDefault
                    && expectedNames.Contains(GetElementName(action), StringComparer.Ordinal)
                    ? action
                    : null;
            },
            TimeSpan.FromSeconds(8));
    }

    private static AutomationElement? FindRawDescendantByAutomationId(
        UIA3Automation automation,
        AutomationElement root,
        string automationId)
    {
        ITreeWalker walker = automation.TreeWalkerFactory.GetRawViewWalker();
        var pending = new Queue<AutomationElement>();
        AutomationElement? firstChild = walker.GetFirstChild(root);
        if (firstChild is not null)
        {
            pending.Enqueue(firstChild);
        }

        int visited = 0;
        while (pending.Count > 0 && visited++ < 5000)
        {
            AutomationElement current = pending.Dequeue();
            if (string.Equals(GetAutomationId(current), automationId, StringComparison.Ordinal))
            {
                return current;
            }

            AutomationElement? child = walker.GetFirstChild(current);
            if (child is not null)
            {
                pending.Enqueue(child);
            }

            AutomationElement? sibling = walker.GetNextSibling(current);
            if (sibling is not null)
            {
                pending.Enqueue(sibling);
            }
        }

        return null;
    }

    private static AutomationElement? FindNotificationsWorkspaceRoot(UIA3Automation automation, Window window)
    {
        AutomationElement? exactRoot = FindRawDescendantByAutomationId(automation, window, "NotificationsPageRoot");
        if (exactRoot is not null)
        {
            return exactRoot;
        }

        AutomationElement? candidate = window.FindFirstDescendant(
            cf => cf.ByAutomationId("NotificationsMarkAllRead"));
        if (candidate is null)
        {
            return null;
        }

        ITreeWalker walker = automation.TreeWalkerFactory.GetControlViewWalker();
        for (int depth = 0; candidate is not null && depth < 16; depth++)
        {
            bool ownsSearch = candidate.FindFirstDescendant(cf => cf.ByAutomationId("NotificationsSearch")) is not null;
            bool ownsFilter = candidate.FindFirstDescendant(cf => cf.ByAutomationId("NotificationsFilter")) is not null
                || candidate.FindFirstDescendant(cf => cf.ByAutomationId("NotificationsFilter_Unread")) is not null;
            bool ownsList = candidate.FindFirstDescendant(cf => cf.ByAutomationId("NotificationsList")) is not null;
            if (ownsSearch && ownsFilter && ownsList)
            {
                Console.WriteLine(
                    "NotificationsPageRoot has no WinUI runtime provider; verified the smallest UIA workspace ancestor containing its unique page contract.");
                return candidate;
            }

            candidate = walker.GetParent(candidate);
        }

        return null;
    }

    private static AutomationElement? FindNotificationsFilterRoot(UIA3Automation automation, Window window)
    {
        AutomationElement? exactFilter = FindRawDescendantByAutomationId(automation, window, "NotificationsFilter");
        if (exactFilter is not null)
        {
            return exactFilter;
        }

        AutomationElement? candidate = window.FindFirstDescendant(
            cf => cf.ByAutomationId("NotificationsFilter_Unread"));
        if (candidate is null)
        {
            return null;
        }

        ITreeWalker walker = automation.TreeWalkerFactory.GetControlViewWalker();
        for (int depth = 0; candidate is not null && depth < 8; depth++)
        {
            bool ownsUnread = string.Equals(GetAutomationId(candidate), "NotificationsFilter_Unread", StringComparison.Ordinal)
                || candidate.FindFirstDescendant(cf => cf.ByAutomationId("NotificationsFilter_Unread")) is not null;
            bool ownsAll = candidate.FindFirstDescendant(cf => cf.ByAutomationId("NotificationsFilter_All")) is not null;
            bool ownsParticipating = candidate.FindFirstDescendant(cf => cf.ByAutomationId("NotificationsFilter_Participating")) is not null;
            if (ownsUnread && ownsAll && ownsParticipating)
            {
                Console.WriteLine(
                    "NotificationsFilter has no WinUI runtime provider; verified the smallest UIA group containing all three filter items.");
                return candidate;
            }

            candidate = walker.GetParent(candidate);
        }

        return null;
    }
}
