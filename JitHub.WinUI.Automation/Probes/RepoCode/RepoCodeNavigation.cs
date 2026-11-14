using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Interaction.KeyboardInput;

namespace JitHub.WinUI.Automation.Probes.RepoCode;

/// <summary>
/// Moves through the repository code page: the file tree drawer, fixture files, route paths and item status.
/// </summary>
internal static class RepoCodeNavigation
{
    internal static void SelectRepoCodeFixtureFile(Window window, string path, string accessibleName)
    {
        AutomationElement? file = window.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
            .FirstOrDefault(element =>
                IsVisible(element) &&
                string.Equals(element.Properties.ItemStatus.ValueOrDefault, $"path:{path}", StringComparison.Ordinal) &&
                string.Equals(GetElementName(element), accessibleName, StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            AutomationElement opener = WaitForElement(
                "RepoCode file-tree opener for fixture selection",
                () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeOpenFileTreeButton"))
                    .FirstOrDefault(IsVisible),
                TimeSpan.FromSeconds(5));
            OpenRepoCodeFileTreeDrawer(window, opener);
            AutomationElement drawer = WaitForElement(
                "RepoCode fixture-selection drawer",
                () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeLeftDrawer"))
                    .FirstOrDefault(IsVisible),
                TimeSpan.FromSeconds(5));
            file = WaitForElement(
                $"repository fixture {path}",
                () => drawer.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                    .FirstOrDefault(element =>
                        IsVisible(element) &&
                        string.Equals(element.Properties.ItemStatus.ValueOrDefault, $"path:{path}", StringComparison.Ordinal) &&
                        string.Equals(GetElementName(element), accessibleName, StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(10));
        }

        SelectAutomationItem(file, $"repository fixture {path}");
        WaitUntil(
            $"repository fixture route {path}",
            () => IsRepoCodeFixtureRouteVisible(window, path),
            TimeSpan.FromSeconds(12));
    }

    private static bool IsRepoCodeFixtureRouteVisible(Window window, string path)
    {
        if (string.Equals(GetRepoCodeRoutePath(window), path, StringComparison.Ordinal))
        {
            return true;
        }

        AutomationElement? transitionPath = FindCurrentVisibleByAutomationId(window, "RepoCodeTransitionPath");
        if (transitionPath is not null &&
            string.Equals(GetElementName(transitionPath), path, StringComparison.Ordinal))
        {
            return true;
        }

        AutomationElement? compactFileName = FindCurrentVisibleByAutomationId(window, "RepoCodeCompactFileName");
        return compactFileName is not null && string.Equals(
            GetElementName(compactFileName),
            Path.GetFileName(path),
            StringComparison.Ordinal);
    }

    internal static AutomationElement FindMandatoryRepoCodeSourceFile(Window window)
    {
        AutomationElement tree = WaitForElement(
            "ready RepoCodeFileTree",
            () =>
            {
                AutomationElement? candidate = window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeFileTree"));
                return candidate is not null &&
                    (candidate.Properties.ItemStatus.ValueOrDefault ?? string.Empty)
                        .StartsWith("ready", StringComparison.Ordinal)
                        ? candidate
                        : null;
            },
            TimeSpan.FromSeconds(15));

        if (!string.Equals(
                tree.Properties.ItemStatus.ValueOrDefault,
                "ready:path:src/App.cs",
                StringComparison.Ordinal))
        {
            AutomationElement sourceDirectory = WaitForElement(
                "deterministic src fixture directory",
                () => window.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                    .FirstOrDefault(element =>
                        IsVisible(element) &&
                        string.Equals(
                            element.Properties.ItemStatus.ValueOrDefault,
                            "path:src",
                            StringComparison.Ordinal)),
                TimeSpan.FromSeconds(8));
            ExpandRepoCodeTreeItem(sourceDirectory);
            WaitUntil(
                "deterministic src/App.cs fixture is loaded",
                () => string.Equals(
                    tree.Properties.ItemStatus.ValueOrDefault,
                    "ready:path:src/App.cs",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(15));
        }

        bool appFileVisible = window.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
            .Any(element =>
                IsVisible(element) &&
                string.Equals(element.Properties.ItemStatus.ValueOrDefault, "path:src/App.cs", StringComparison.Ordinal));
        if (!appFileVisible)
        {
            AutomationElement sourceDirectory = WaitForElement(
                "src fixture directory containing App.cs",
                () => window.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                    .FirstOrDefault(element =>
                        IsVisible(element) &&
                        string.Equals(element.Properties.ItemStatus.ValueOrDefault, "path:src", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(5));
            ExpandRepoCodeTreeItem(sourceDirectory);
        }

        return WaitForElement(
            "deterministic App.cs source fixture",
            () => window.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                .FirstOrDefault(element =>
                    IsVisible(element) &&
                    string.Equals(
                        element.Properties.ItemStatus.ValueOrDefault,
                        "path:src/App.cs",
                        StringComparison.Ordinal) &&
                    string.Equals(GetElementName(element), "App.cs, file", StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(15));
    }

    private static void ExpandRepoCodeTreeItem(AutomationElement treeItem)
    {
        if (treeItem.Patterns.ExpandCollapse.IsSupported)
        {
            treeItem.Patterns.ExpandCollapse.Pattern.Expand();
            return;
        }

        var bounds = treeItem.BoundingRectangle;
        Mouse.DoubleClick(new System.Drawing.Point(
            bounds.Left + Math.Max(12, bounds.Width / 2),
            bounds.Top + Math.Max(8, bounds.Height / 2)));
    }

    internal static AutomationElement WaitForElementWithItemStatus(
        Window window,
        string automationId,
        string expectedStatus,
        TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        do
        {
            AutomationElement? element = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            if (element is not null &&
                string.Equals(element.Properties.ItemStatus.ValueOrDefault, expectedStatus, StringComparison.Ordinal))
            {
                return element;
            }

            Thread.Sleep(5);
        }
        while (stopwatch.Elapsed <= timeout);

        AutomationElement[] observed = window.FindAllDescendants(cf => cf.ByAutomationId(automationId));
        string observedStatuses = observed.Length == 0
            ? "element absent"
            : string.Join(
                ", ",
                observed.Select(element =>
                    $"status='{element.Properties.ItemStatus.ValueOrDefault ?? string.Empty}', " +
                    $"name='{GetElementName(element)}', visible={IsVisible(element)}"));
        throw new InvalidOperationException(
            $"Timed out waiting for {automationId} item status '{expectedStatus}' within {timeout.TotalMilliseconds:F0} ms; " +
            $"observed: {observedStatuses}.");
    }

    internal static string WaitForRepoCodeRoutePath(Window window, TimeSpan timeout)
    {
        string? routePath = null;
        bool found = WaitUntilAvailable(
            () => !string.IsNullOrEmpty(routePath = GetRepoCodeRoutePath(window)),
            timeout);
        AssertProbe(found, "repo-code: current breadcrumb route did not become available.");
        return routePath!;
    }

    internal static string? GetRepoCodeRoutePath(Window window)
        => window.FindAllDescendants()
            .Where(element =>
                IsVisible(element) &&
                element.ControlType == ControlType.Button &&
                GetAutomationId(element).StartsWith("RepoCodeBreadcrumbSegment_", StringComparison.Ordinal))
            .OrderBy(element => element.BoundingRectangle.Left)
            .Select(element => element.Properties.ItemStatus.ValueOrDefault)
            .LastOrDefault(path => !string.IsNullOrEmpty(path));

    internal static void OpenRepoCodeFileTreeDrawer(Window window, AutomationElement opener)
    {
        InvokeOrClick(opener);
        bool opened = WaitUntilAvailable(
            () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeLeftDrawer")).Any(IsVisible),
            TimeSpan.FromSeconds(1));
        if (!opened)
        {
            FocusForKeyboardActivation(window, opener);
            Keyboard.Press(VirtualKeyShort.SPACE);
            opened = WaitUntilAvailable(
                () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeLeftDrawer")).Any(IsVisible),
                TimeSpan.FromSeconds(1));
        }
        if (!opened)
        {
            Keyboard.Press(VirtualKeyShort.ENTER);
        }
    }
}
