using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Elements.FocusQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Interaction.KeyboardInput;
using static JitHub.WinUI.Automation.Probes.RepoCode.RepoCodeContentSurfacesProbe;
using static JitHub.WinUI.Automation.Probes.RepoCode.RepoCodeNavigation;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;
using static JitHub.WinUI.Automation.Workspaces.AdaptiveWorkspaceDrawers;

namespace JitHub.WinUI.Automation.Probes.RepoCode;

/// <summary>
/// Drives the repo-code-responsive-workspace probe.
/// </summary>
internal static class RepoCodeResponsiveWorkspaceProbe
{
    // This method stays whole because one repo-code app instance is resized through five widths, each checking the
    // inline tree or the file-tree drawer (focus containment, file selection, light dismiss, Escape) and the compact
    // overflow, then breadcrumb-root and Back routing reuse the route path the resize loop left selected.
    internal static void RunRepoCodeResponsiveWorkspaceProbe(CaptureOptions options)
    {
        (int Width, int Height)[] sizes =
        [
            (1366, 900),
            (1180, 800),
            (900, 700),
            (760, 650),
            (640, 600)
        ];

        KillExistingApplicationInstances(options.AppPath);
        using var app = LaunchApplication(
            options.AppPath,
            "--page=repo-code",
            "--theme=dark",
            $"--repo={options.RepositoryFullName}");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "repo-code-responsive-workspace probe");
            foreach ((int width, int height) in sizes)
            {
                Rectangle resizedBounds = ResizeWindow(window, width, height);
                int actualWidth = resizedBounds.Width;
                string viewportLabel = GetResponsiveViewportLabel(width, height, resizedBounds);
                AutomationElement workspace = WaitForElement(
                    "RepoCodeAdaptiveWorkspace",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeAdaptiveWorkspace")),
                    TimeSpan.FromSeconds(14));
                AutomationElement readingHost = WaitForElement(
                    "RepoCodeReadingHost",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeReadingHost")),
                    TimeSpan.FromSeconds(14));
                AssertProbe(IsVisible(readingHost), $"repo-code: reading surface was not visible at {viewportLabel}.");

                AutomationElement? tree = window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeFileTree"));
                AutomationElement? openTree = window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeOpenFileTreeButton"));
                bool expectsDrawer = actualWidth <= 900 || IsVisible(openTree);
                if (!expectsDrawer)
                {
                    AssertProbe(IsVisible(tree), $"repo-code: file tree was not inline at {viewportLabel}.");
                    AssertProbe(!IsVisible(openTree), $"repo-code: file-tree opener was visible while the tree was inline at {viewportLabel}.");
                }
                else
                {
                    AssertProbe(!IsVisible(tree), $"repo-code: file tree remained inline at {viewportLabel}.");
                    openTree = WaitForElement(
                        "RepoCodeOpenFileTreeButton",
                        () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeOpenFileTreeButton")).FirstOrDefault(IsVisible),
                        TimeSpan.FromSeconds(8));
                    AssertProbe(
                        readingHost.BoundingRectangle.Width >= workspace.BoundingRectangle.Width - 4,
                        $"repo-code: hidden file tree still reduced the reading width at {viewportLabel}.");

                    System.Drawing.Rectangle openerBounds = openTree.BoundingRectangle;
                    OpenRepoCodeFileTreeDrawer(window, openTree);
                    AutomationElement drawer = WaitForElement(
                        "RepoCodeLeftDrawer",
                        () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeLeftDrawer")).FirstOrDefault(IsVisible),
                        TimeSpan.FromSeconds(8));
                    AutomationElement closeTree = WaitForElement(
                        "RepoCodeCloseFileTreeButton",
                        () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeCloseFileTreeButton")).FirstOrDefault(IsVisible),
                        TimeSpan.FromSeconds(8));
                    Thread.Sleep(320);
                    AssertDrawerInsideWorkspace(window, "RepoCode", leading: true);
                    AssertElementBoundsAligned(openerBounds, closeTree, "RepoCode file-tree toggle");
                    closeTree.Focus();
                    AssertDrawerKeyboardFocusContained(window, "RepoCodeLeftDrawer", "Repo Code file-tree drawer");
                    CaptureWindow(window, Path.Combine(options.OutputDirectory, $"repo-code-adaptive-{viewportLabel}-drawer.png"));

                    if (actualWidth <= 640)
                    {
                        AutomationElement file = WaitForElement(
                            "visible repository file",
                            () => drawer.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                                .FirstOrDefault(element =>
                                    IsVisible(element) &&
                                    element.Name.EndsWith(", file", StringComparison.Ordinal)),
                            TimeSpan.FromSeconds(14));
                        file.Click();
                        WaitUntil(
                            "file selection closes repository tree drawer",
                            () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeLeftDrawer"))),
                            TimeSpan.FromSeconds(6));

                        openTree = WaitForElement(
                            "RepoCodeOpenFileTreeButton after selection",
                            () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeOpenFileTreeButton")).FirstOrDefault(IsVisible),
                            TimeSpan.FromSeconds(5));
                        OpenRepoCodeFileTreeDrawer(window, openTree);
                        drawer = WaitForElement(
                            "RepoCodeLeftDrawer for light dismiss",
                            () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeLeftDrawer")).FirstOrDefault(IsVisible),
                            TimeSpan.FromSeconds(5));
                        System.Drawing.Rectangle workspaceBounds = workspace.BoundingRectangle;
                        Mouse.Click(new System.Drawing.Point(
                            workspaceBounds.Right - 24,
                            workspaceBounds.Top + Math.Max(80, workspaceBounds.Height / 2)));
                        WaitUntil(
                            "light dismiss closes repository tree drawer",
                            () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeLeftDrawer"))),
                            TimeSpan.FromSeconds(6));
                        WaitUntil(
                            "light dismiss restores focus to file-tree opener",
                            () => IsElementFocused(openTree),
                            TimeSpan.FromSeconds(5));

                        OpenRepoCodeFileTreeDrawer(window, openTree);
                        WaitForElement(
                            "RepoCodeLeftDrawer for Escape",
                            () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeLeftDrawer")).FirstOrDefault(IsVisible),
                            TimeSpan.FromSeconds(5));
                        Keyboard.Press(VirtualKeyShort.ESCAPE);
                        WaitUntil(
                            "Escape closes repository tree drawer",
                            () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeLeftDrawer"))),
                            TimeSpan.FromSeconds(6));
                        WaitUntil(
                            "Escape restores focus to file-tree opener",
                            () => IsElementFocused(openTree),
                            TimeSpan.FromSeconds(5));
                    }
                    else
                    {
                        InvokeOrClick(closeTree);
                        WaitUntil(
                            "repository tree close button dismisses drawer",
                            () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeLeftDrawer"))),
                            TimeSpan.FromSeconds(6));
                    }
                }

                AutomationElement? compactFileName = window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeCompactFileName"));
                AutomationElement? overflow = window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeFileActionsOverflowButton"));
                AutomationElement? copyPath = window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeCopyPathButton"));
                if (actualWidth <= 640)
                {
                    AssertProbe(IsVisible(compactFileName), "repo-code: compact filename was not visible at 640px.");
                    AssertProbe(IsVisible(overflow), "repo-code: named file-actions overflow was not visible at 640px.");
                    AssertProbe(!IsVisible(copyPath), "repo-code: low-priority direct actions clipped the compact breadcrumb.");
                    AssertProbe(IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeBackButton"))), "repo-code: Back was not preserved at 640px.");
                    AssertProbe(IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeForwardButton"))), "repo-code: Forward was not preserved at 640px.");

                    AutomationElement overflowButton = overflow!;
                    FocusForKeyboardActivation(window, overflowButton);
                    System.Drawing.Rectangle overflowBounds = overflowButton.BoundingRectangle;
                    AssertProbe(
                        overflowBounds.Width > 0 && overflowBounds.Height > 0,
                        "repo-code: compact file-actions overflow did not expose clickable bounds.");
                    Mouse.MoveTo(new System.Drawing.Point(
                        overflowBounds.Left + overflowBounds.Width / 2,
                        overflowBounds.Top + overflowBounds.Height / 2));
                    Mouse.Click();
                    WaitForElement(
                        "RepoCodeOverflowCopyPath",
                        () => FindElementInWindowOrDialog(window, automation, "RepoCodeOverflowCopyPath"),
                        TimeSpan.FromSeconds(5));
                    WaitForElement(
                        "RepoCodeOverflowCopyRawLink",
                        () => FindElementInWindowOrDialog(window, automation, "RepoCodeOverflowCopyRawLink"),
                        TimeSpan.FromSeconds(5));
                    AutomationElement openOnGitHub = WaitForElement(
                        "RepoCodeOverflowOpenOnGitHub",
                        () => FindElementInWindowOrDialog(window, automation, "RepoCodeOverflowOpenOnGitHub"),
                        TimeSpan.FromSeconds(5));
                    if (!IsVisible(openOnGitHub))
                    {
                        Keyboard.Press(VirtualKeyShort.END);
                        WaitUntil(
                            "RepoCodeOverflowOpenOnGitHub is keyboard-revealed",
                            () => IsVisible(openOnGitHub) || IsElementFocused(openOnGitHub),
                            TimeSpan.FromSeconds(5));
                    }
                    AssertProbe(
                        IsVisible(openOnGitHub) || IsElementFocused(openOnGitHub),
                        "repo-code: Open on GitHub overflow command was not reachable at 640px.");
                    CaptureWindowWithPopups(window, Path.Combine(options.OutputDirectory, "repo-code-adaptive-640x600-overflow.png"));
                    Keyboard.Press(VirtualKeyShort.ESCAPE);
                }
                else if (actualWidth >= 1180)
                {
                    AssertProbe(!IsVisible(compactFileName), $"repo-code: compact filename replaced the full breadcrumb at {actualWidth}px.");
                    AssertProbe(!IsVisible(overflow), $"repo-code: overflow replaced direct actions at {actualWidth}px.");
                    AssertProbe(IsVisible(copyPath), $"repo-code: direct file actions were missing at {actualWidth}px.");
                }

                CaptureWindow(window, Path.Combine(options.OutputDirectory, $"repo-code-adaptive-{viewportLabel}.png"));
            }

            ResizeWindow(window, 1366, 900);
            Thread.Sleep(350);
            string beforePath = WaitForRepoCodeRoutePath(window, TimeSpan.FromSeconds(8));
            AutomationElement rootBreadcrumb = WaitForElement(
                "repository-root breadcrumb",
                () => window.FindAllDescendants()
                    .Where(element =>
                        element.ControlType == ControlType.Button &&
                        GetAutomationId(element).StartsWith("RepoCodeBreadcrumbSegment_", StringComparison.Ordinal) &&
                        GetElementName(element).StartsWith("Open repository root ", StringComparison.Ordinal))
                    .OrderBy(element => element.BoundingRectangle.Left)
                    .FirstOrDefault(IsVisible),
                TimeSpan.FromSeconds(8));

            string rootPath = rootBreadcrumb.Properties.ItemStatus.ValueOrDefault ??
                GetElementName(rootBreadcrumb)["Open repository root ".Length..];
            if (string.Equals(beforePath, rootPath, StringComparison.Ordinal))
            {
                AutomationElement fileRoute = WaitForElement(
                    "repository file used to establish breadcrumb history",
                    () => window.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                        .FirstOrDefault(element =>
                            IsVisible(element) &&
                            element.Name.EndsWith(", file", StringComparison.Ordinal)),
                    TimeSpan.FromSeconds(12));
                InvokeOrClick(fileRoute);
                WaitUntil(
                    "file selection establishes a non-root code route",
                    () => !string.Equals(GetRepoCodeRoutePath(window), rootPath, StringComparison.Ordinal),
                    TimeSpan.FromSeconds(12));
                beforePath = WaitForRepoCodeRoutePath(window, TimeSpan.FromSeconds(8));
                rootBreadcrumb = WaitForElement(
                    "repository-root breadcrumb after file selection",
                    () => window.FindAllDescendants()
                        .Where(element =>
                            element.ControlType == ControlType.Button &&
                            GetAutomationId(element).StartsWith("RepoCodeBreadcrumbSegment_", StringComparison.Ordinal) &&
                            GetElementName(element).StartsWith("Open repository root ", StringComparison.Ordinal))
                        .OrderBy(element => element.BoundingRectangle.Left)
                        .FirstOrDefault(IsVisible),
                    TimeSpan.FromSeconds(8));
            }

            InvokeOrClick(rootBreadcrumb);
            WaitUntil(
                "breadcrumb root changes the code route",
                () => string.Equals(GetRepoCodeRoutePath(window), rootPath, StringComparison.Ordinal),
                TimeSpan.FromSeconds(6));
            AssertProbe(
                string.Equals(GetRepoCodeRoutePath(window), rootPath, StringComparison.Ordinal),
                "repo-code: breadcrumb invocation did not change the current path.");
            AutomationElement back = WaitForElement(
                "RepoCodeBackButton after breadcrumb navigation",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeBackButton")),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(back);
            WaitUntil(
                "Back restores the pre-breadcrumb file route",
                () => string.Equals(GetRepoCodeRoutePath(window), beforePath, StringComparison.Ordinal),
                TimeSpan.FromSeconds(6));

            ExerciseRepoCodeContentSurfaces(window, options);

            Console.WriteLine($"repo-code-responsive-workspace probe: {sizes.Length} responsive states, focus containment, drawer behavior, overflow access, breadcrumb routing, CSV rich/plain semantics, and SVG zoom passed; output={options.OutputDirectory}");
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }
}
