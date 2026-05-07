using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

var options = CaptureOptions.Parse(args);
Directory.CreateDirectory(options.OutputDirectory);

if (string.Equals(options.Probe, "search-context", StringComparison.OrdinalIgnoreCase))
{
    RunSearchContextProbe(options);
    return;
}

if (string.Equals(options.Probe, "theme-switch", StringComparison.OrdinalIgnoreCase))
{
    RunThemeSwitchProbe(options);
    return;
}

if (string.Equals(options.Probe, "combo-open", StringComparison.OrdinalIgnoreCase))
{
    RunComboOpenProbe(options);
    return;
}

if (string.Equals(options.Probe, "search-select-dismiss", StringComparison.OrdinalIgnoreCase))
{
    RunSearchSelectDismissProbe(options);
    return;
}

if (string.Equals(options.Probe, "emoji-panel", StringComparison.OrdinalIgnoreCase))
{
    RunEmojiPanelProbe(options);
    return;
}

if (string.Equals(options.Probe, "segments-hover", StringComparison.OrdinalIgnoreCase))
{
    RunSegmentsHoverProbe(options);
    return;
}

if (string.Equals(options.Probe, "activity-link-hover", StringComparison.OrdinalIgnoreCase))
{
    RunActivityLinkHoverProbe(options);
    return;
}

if (string.Equals(options.Probe, "pr-timeline-link-hover", StringComparison.OrdinalIgnoreCase))
{
    RunPullRequestTimelineLinkHoverProbe(options);
    return;
}

var captures = new List<CaptureResult>();
foreach (string theme in options.Themes)
{
    foreach (CaptureTarget target in options.Targets)
    {
        KillExistingApplicationInstances(options.AppPath);
        string[] launchArguments = BuildLaunchArguments(target, theme, options.RepositoryFullName);
        Console.WriteLine($"Launching {target.Name}: {string.Join(' ', launchArguments)}");
        using var app = LaunchApplication(options.AppPath, launchArguments);
        using var automation = new UIA3Automation();

        try
        {
            var windowRetry = Retry.WhileNull(() => app.GetMainWindow(automation), timeout: TimeSpan.FromSeconds(20), interval: TimeSpan.FromMilliseconds(250));
            if (!windowRetry.Success || windowRetry.Result is null)
            {
                throw new InvalidOperationException($"Unable to find main window for target '{target.Name}'.");
            }

            Window window = windowRetry.Result;
            if (window.Patterns.Transform.IsSupported)
            {
                window.Patterns.Transform.Pattern.Resize(1600, 1000);
            }
            window.Move(80, 80);
            window.SetForeground();
            window.FocusNative();
            Thread.Sleep(GetSettleDelay(target));
            PrepareTargetForCapture(window, target);

            AutomationElement element = window;
            if (!string.IsNullOrWhiteSpace(target.AutomationId))
            {
                var elementRetry = Retry.WhileNull(
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId(target.AutomationId)),
                    timeout: TimeSpan.FromSeconds(10),
                    interval: TimeSpan.FromMilliseconds(200));
                if (elementRetry.Success && elementRetry.Result is not null)
                {
                    element = elementRetry.Result;
                    if (element.Patterns.ScrollItem.IsSupported)
                    {
                        element.Patterns.ScrollItem.Pattern.ScrollIntoView();
                        Thread.Sleep(500);
                    }
                }
                else
                {
                    throw new InvalidOperationException($"Unable to find screenshot target '{target.AutomationId}' for '{target.Name}'.");
                }
            }

            string fileName = $"{theme}-{target.Name}.png";
            string filePath = Path.Combine(options.OutputDirectory, fileName);
            AutomationElement captureTarget = string.Equals(target.Page, "design-lab", StringComparison.OrdinalIgnoreCase)
                ? window
                : element;
            using (var capture = captureTarget.Capture())
            {
                capture.Save(filePath);
            }

            if (IsAppPreviewTarget(target))
            {
                TrimAppPreviewCapture(filePath);
            }
            captures.Add(new CaptureResult(theme, target.Name, fileName));
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }
}

WriteManifest(options.OutputDirectory, captures);
Console.WriteLine($"Captured {captures.Count} screenshots to {options.OutputDirectory}");

static void RunSearchContextProbe(CaptureOptions options)
{
    using var app = string.IsNullOrWhiteSpace(options.AttachProcess)
        ? LaunchApplication(options.AppPath, "--page=shell", "--scenario=search-suggestions", "--theme=dark")
        : CreateProbeApplication(options);
    using var automation = new UIA3Automation();

    var windowRetry = Retry.WhileNull(() => app.GetMainWindow(automation), timeout: TimeSpan.FromSeconds(20), interval: TimeSpan.FromMilliseconds(250));
    if (!windowRetry.Success || windowRetry.Result is null)
    {
        throw new InvalidOperationException("Unable to find main window for search-context probe.");
    }

    Window window = windowRetry.Result;
    if (window.Patterns.Transform.IsSupported)
    {
        window.Patterns.Transform.Pattern.Resize(1600, 1000);
    }

    window.Move(80, 80);
    window.SetForeground();
    window.FocusNative();
    Thread.Sleep(1200);

    var searchRetry = Retry.WhileNull(
        () => window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchTextBox")),
        timeout: TimeSpan.FromSeconds(10),
        interval: TimeSpan.FromMilliseconds(200));
    if (!searchRetry.Success || searchRetry.Result is null)
    {
        throw new InvalidOperationException("Unable to find ShellSearchTextBox for search-context probe.");
    }

    AutomationElement searchBox = searchRetry.Result;
    searchBox.FocusNative();
    Thread.Sleep(200);

    var searchBounds = searchBox.BoundingRectangle;
    var clickablePoint = new System.Drawing.Point(
        (int)Math.Round(searchBounds.X + (searchBounds.Width / 2d)),
        (int)Math.Round(searchBounds.Y + (searchBounds.Height / 2d)));
    Console.WriteLine(
        $"search-context probe target: bounds=({searchBounds.X},{searchBounds.Y},{searchBounds.Width},{searchBounds.Height}) " +
        $"click=({clickablePoint.X},{clickablePoint.Y}) windowTitle={window.Title}");

    Mouse.RightClick(clickablePoint);

    var menuRetry = Retry.WhileTrue(
        () =>
        {
            AutomationElement? undo = automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Undo"));
            AutomationElement? paste = automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Paste"));
            return undo is null || paste is null;
        },
        timeout: TimeSpan.FromSeconds(2),
        interval: TimeSpan.FromMilliseconds(100));

    Thread.Sleep(900);
    AutomationElement? undoMenuItem = automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Undo"));
    AutomationElement? pasteMenuItem = automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Paste"));

    string filePath = Path.Combine(options.OutputDirectory, "probe-search-context.png");
    using var capture = window.Capture();
    capture.Save(filePath);

    bool menuStillOpen = undoMenuItem is not null && pasteMenuItem is not null;
    Console.WriteLine($"search-context probe: initialMenu={!menuRetry.Result}, menuStillOpen={menuStillOpen}, screenshot={filePath}");

    if (!menuRetry.Success || !menuStillOpen)
    {
        throw new InvalidOperationException("Search context menu did not remain open during the automation probe.");
    }

    if (string.IsNullOrWhiteSpace(options.AttachProcess))
    {
        TryClose(app);
    }
}

static void RunThemeSwitchProbe(CaptureOptions options)
{
    using var app = string.IsNullOrWhiteSpace(options.AttachProcess)
        ? LaunchApplication(options.AppPath, "--page=settings", "--theme=light")
        : CreateProbeApplication(options);
    using var automation = new UIA3Automation();

    var windowRetry = Retry.WhileNull(() => app.GetMainWindow(automation), timeout: TimeSpan.FromSeconds(20), interval: TimeSpan.FromMilliseconds(250));
    if (!windowRetry.Success || windowRetry.Result is null)
    {
        throw new InvalidOperationException("Unable to find main window for theme-switch probe.");
    }

    Window window = windowRetry.Result;
    if (window.Patterns.Transform.IsSupported)
    {
        window.Patterns.Transform.Pattern.Resize(1600, 1000);
    }

    window.Move(80, 80);
    window.SetForeground();
    window.FocusNative();
    Thread.Sleep(1200);

    var comboRetry = Retry.WhileNull(
        () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsThemeComboBox")),
        timeout: TimeSpan.FromSeconds(10),
        interval: TimeSpan.FromMilliseconds(200));
    if (!comboRetry.Success || comboRetry.Result is null)
    {
        throw new InvalidOperationException("Unable to find SettingsThemeComboBox for theme-switch probe.");
    }

    AutomationElement combo = comboRetry.Result;

    string beforePath = Path.Combine(options.OutputDirectory, "probe-theme-before.png");
    using (var beforeCapture = window.Capture())
    {
        beforeCapture.Save(beforePath);
    }

    combo.FocusNative();
    Thread.Sleep(200);

    if (combo.Patterns.ExpandCollapse.IsSupported)
    {
        combo.Patterns.ExpandCollapse.Pattern.Expand();
    }
    else
    {
        var bounds = combo.BoundingRectangle;
        Mouse.Click(new System.Drawing.Point(
            (int)Math.Round(bounds.X + (bounds.Width / 2d)),
            (int)Math.Round(bounds.Y + (bounds.Height / 2d))));
    }

    var darkRetry = Retry.WhileNull(
        () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Dark")),
        timeout: TimeSpan.FromSeconds(3),
        interval: TimeSpan.FromMilliseconds(100));
    if (!darkRetry.Success || darkRetry.Result is null)
    {
        throw new InvalidOperationException("Unable to find Dark theme option for theme-switch probe.");
    }

    darkRetry.Result.Click();
    Thread.Sleep(1200);

    string afterPath = Path.Combine(options.OutputDirectory, "probe-theme-after.png");
    using (var afterCapture = window.Capture())
    {
        afterCapture.Save(afterPath);
    }

    Console.WriteLine($"theme-switch probe: before={beforePath}, after={afterPath}");
    if (string.IsNullOrWhiteSpace(options.AttachProcess))
    {
        TryClose(app);
    }
}

static void RunComboOpenProbe(CaptureOptions options)
{
    using var app = string.IsNullOrWhiteSpace(options.AttachProcess)
        ? LaunchApplication(options.AppPath, "--page=design-lab", "--scenario=inputs", "--theme=dark")
        : CreateProbeApplication(options);
    using var automation = new UIA3Automation();

    Window window = GetReadyWindow(app, automation, "combo-open probe");
    var comboRetry = Retry.WhileNull(
        () => window.FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox))
            .FirstOrDefault(element => string.Equals(GetElementName(element), "Open", StringComparison.OrdinalIgnoreCase))
            ?? window.FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox)),
        timeout: TimeSpan.FromSeconds(5),
        interval: TimeSpan.FromMilliseconds(100));
    if (!comboRetry.Success || comboRetry.Result is null)
    {
        throw new InvalidOperationException("Unable to find ComboBox for combo-open probe.");
    }

    AutomationElement combo = comboRetry.Result;
    Console.WriteLine($"combo-open probe target: name='{GetElementName(combo)}', bounds={combo.BoundingRectangle}");
    combo.FocusNative();
    Thread.Sleep(150);
    combo.AsComboBox().Expand();

    var openedItemRetry = Retry.WhileNull(
        () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Closed")),
        timeout: TimeSpan.FromSeconds(5),
        interval: TimeSpan.FromMilliseconds(100));
    bool expanded = openedItemRetry.Success && openedItemRetry.Result is not null && IsVisible(openedItemRetry.Result);

    string filePath = Path.Combine(options.OutputDirectory, "probe-combo-open.png");
    using var capture = Capture.MainScreen(new CaptureSettings());
    capture.ToFile(filePath);

    Console.WriteLine($"combo-open probe: expanded={expanded}, screenshot={filePath}");

    if (!expanded)
    {
        throw new InvalidOperationException("ComboBox did not stay expanded for screenshot verification.");
    }

    if (string.IsNullOrWhiteSpace(options.AttachProcess))
    {
        TryClose(app);
    }
}

static void RunSearchSelectDismissProbe(CaptureOptions options)
{
    using var app = string.IsNullOrWhiteSpace(options.AttachProcess)
        ? LaunchApplication(options.AppPath, "--page=shell", "--scenario=search-suggestions", "--theme=dark")
        : CreateProbeApplication(options);
    using var automation = new UIA3Automation();

    Window window = GetReadyWindow(app, automation, "search-select-dismiss probe");
    var searchRetry = Retry.WhileNull(
        () => FindShellSearchTextBox(window),
        timeout: TimeSpan.FromSeconds(10),
        interval: TimeSpan.FromMilliseconds(200));
    if (!searchRetry.Success || searchRetry.Result is null)
    {
        throw new InvalidOperationException("Unable to find ShellSearchTextBox for search-select-dismiss probe.");
    }

    AutomationElement searchBox = searchRetry.Result;
    Console.WriteLine($"search-select-dismiss probe target: name='{GetElementName(searchBox)}', bounds={searchBox.BoundingRectangle}");
    searchBox.FocusNative();
    Thread.Sleep(200);

    var textBox = searchBox.AsTextBox();
    textBox.Text = string.Empty;
    textBox.Enter("flutter");

    var listRetry = Retry.WhileNull(
        () =>
        {
            AutomationElement? list = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchSuggestionsList"));
            return IsVisible(list) ? list : null;
        },
        timeout: TimeSpan.FromSeconds(12),
        interval: TimeSpan.FromMilliseconds(250));
    if (!listRetry.Success || listRetry.Result is null)
    {
        throw new InvalidOperationException("Search suggestions did not open for search-select-dismiss probe.");
    }

    AutomationElement suggestionsList = listRetry.Result;
    var firstItemRetry = Retry.WhileNull(
        () => suggestionsList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
        timeout: TimeSpan.FromSeconds(5),
        interval: TimeSpan.FromMilliseconds(100));
    if (!firstItemRetry.Success || firstItemRetry.Result is null)
    {
        throw new InvalidOperationException("Search suggestions opened but no selectable result was found.");
    }

    AutomationElement firstItem = firstItemRetry.Result;
    AutomationElement suggestionAction =
        firstItem.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button))
        ?? firstItem;
    Console.WriteLine(
        $"search-select-dismiss first item bounds={firstItem.BoundingRectangle}, " +
        $"actionBounds={suggestionAction.BoundingRectangle}, actionName='{GetElementName(suggestionAction)}'");
    var actionBounds = suggestionAction.BoundingRectangle;
    double dpiScale = GetDpiScale(window);
    var clickPoint = new System.Drawing.Point(
        (int)Math.Round((actionBounds.X + (actionBounds.Width / 2d)) * dpiScale),
        (int)Math.Round((actionBounds.Y + (actionBounds.Height / 2d)) * dpiScale));
    Console.WriteLine($"search-select-dismiss click={clickPoint}, dpiScale={dpiScale:0.###}");
    Mouse.Click(clickPoint);

    bool dismissed = false;
    for (int attempt = 0; attempt < 25; attempt++)
    {
        Thread.Sleep(200);
        AutomationElement? list = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchSuggestionsList"));
        if (!IsVisible(list))
        {
            dismissed = true;
            break;
        }
    }

    string filePath = Path.Combine(options.OutputDirectory, "probe-search-select-dismiss.png");
    Thread.Sleep(900);
    using (var capture = Capture.MainScreen(new CaptureSettings()))
    {
        capture.ToFile(filePath);
    }

    Console.WriteLine($"search-select-dismiss probe: dismissed={dismissed}, screenshot={filePath}");
    if (!dismissed)
    {
        throw new InvalidOperationException("Search suggestions remained visible after selecting a result.");
    }

    if (string.IsNullOrWhiteSpace(options.AttachProcess))
    {
        TryClose(app);
    }
}

static void RunEmojiPanelProbe(CaptureOptions options)
{
    using var app = string.IsNullOrWhiteSpace(options.AttachProcess)
        ? LaunchApplication(options.AppPath, "--page=design-lab", "--scenario=conversation", "--theme=dark")
        : CreateProbeApplication(options);
    using var automation = new UIA3Automation();

    Window window = GetReadyWindow(app, automation, "emoji-panel probe");
    var launcherRetry = Retry.WhileNull(
        () => window.FindFirstDescendant(cf => cf.ByAutomationId("EmojiPanelLauncherButton")),
        timeout: TimeSpan.FromSeconds(10),
        interval: TimeSpan.FromMilliseconds(200));
    if (!launcherRetry.Success || launcherRetry.Result is null)
    {
        throw new InvalidOperationException("Unable to find EmojiPanelLauncherButton for emoji-panel probe.");
    }

    AutomationElement launcher = launcherRetry.Result;
    var bounds = launcher.BoundingRectangle;
    double dpiScale = GetDpiScale(window);
    var clickPoint = new System.Drawing.Point(
        (int)Math.Round((bounds.X + (bounds.Width / 2d)) * dpiScale),
        (int)Math.Round((bounds.Y + (bounds.Height / 2d)) * dpiScale));

    Console.WriteLine($"emoji-panel probe target: bounds={bounds}, click={clickPoint}, dpiScale={dpiScale:0.###}");
     if (launcher.Patterns.Invoke.IsSupported)
     {
         launcher.Patterns.Invoke.Pattern.Invoke();
     }
     else
    {
        Mouse.Click(clickPoint);
     }
     Thread.Sleep(900);

     var firstReactionRetry = Retry.WhileNull(
         () => window.FindFirstDescendant(cf => cf.ByAutomationId("EmojiReactionButton_Plus1")),
         timeout: TimeSpan.FromSeconds(5),
         interval: TimeSpan.FromMilliseconds(200));
     if (firstReactionRetry.Success && firstReactionRetry.Result is not null)
     {
         var reactionBounds = firstReactionRetry.Result.BoundingRectangle;
         Mouse.MoveTo(new System.Drawing.Point(
             (int)Math.Round((reactionBounds.X + (reactionBounds.Width / 2d)) * dpiScale),
             (int)Math.Round((reactionBounds.Y + (reactionBounds.Height / 2d)) * dpiScale)));
         Thread.Sleep(300);
     }
 
     string filePath = Path.Combine(options.OutputDirectory, "probe-emoji-panel.png");
    using (var capture = Capture.MainScreen(new CaptureSettings()))
    {
        capture.ToFile(filePath);
    }

    Console.WriteLine($"emoji-panel probe: screenshot={filePath}");
    if (string.IsNullOrWhiteSpace(options.AttachProcess))
    {
        TryClose(app);
    }
}

static void RunSegmentsHoverProbe(CaptureOptions options)
{
    using var app = string.IsNullOrWhiteSpace(options.AttachProcess)
        ? LaunchApplication(options.AppPath, "--page=design-lab", "--scenario=segments", "--theme=dark")
        : CreateProbeApplication(options);
    using var automation = new UIA3Automation();

    Window window = GetReadyWindow(app, automation, "segments-hover probe");
    var publicRetry = Retry.WhileNull(
        () => window.FindAllDescendants(cf => cf.ByText("Public"))
            .Where(element => element.BoundingRectangle.Width > 20 && element.BoundingRectangle.Height > 10)
            .OrderByDescending(element => element.BoundingRectangle.X)
            .FirstOrDefault(),
        timeout: TimeSpan.FromSeconds(10),
        interval: TimeSpan.FromMilliseconds(200));
    if (!publicRetry.Success || publicRetry.Result is null)
    {
        throw new InvalidOperationException("Unable to find Public segmented item for hover probe.");
    }

    var bounds = publicRetry.Result.BoundingRectangle;
    double dpiScale = GetDpiScale(window);
    Mouse.MoveTo(new System.Drawing.Point(
        (int)Math.Round((bounds.X + bounds.Width / 2d) * dpiScale),
        (int)Math.Round((bounds.Y + bounds.Height / 2d) * dpiScale)));
    Thread.Sleep(700);

    string filePath = Path.Combine(options.OutputDirectory, "probe-segments-hover.png");
    using (var capture = window.Capture())
    {
        capture.Save(filePath);
    }

    Console.WriteLine($"segments-hover probe: screenshot={filePath}");
    if (string.IsNullOrWhiteSpace(options.AttachProcess))
    {
        TryClose(app);
    }
}

static void RunActivityLinkHoverProbe(CaptureOptions options)
{
    using var app = string.IsNullOrWhiteSpace(options.AttachProcess)
        ? LaunchApplication(options.AppPath, "--page=design-lab", "--scenario=activities", "--theme=dark")
        : CreateProbeApplication(options);
    using var automation = new UIA3Automation();

    Window window = GetReadyWindow(app, automation, "activity-link-hover probe");
    var linkRetry = Retry.WhileNull(
        () => window.FindAllDescendants()
            .Where(element =>
                IsVisible(element)
                && (element.ControlType == ControlType.Hyperlink || element.ControlType == ControlType.Text))
            .FirstOrDefault(element =>
            {
                string name = GetElementName(element);
                return name.Contains("commits", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("JitHubApp/JitHubV2", StringComparison.OrdinalIgnoreCase);
            }),
        timeout: TimeSpan.FromSeconds(10),
        interval: TimeSpan.FromMilliseconds(200));
    if (!linkRetry.Success || linkRetry.Result is null)
    {
        throw new InvalidOperationException("Unable to find an activity inline link for hover probe.");
    }

    AutomationElement link = linkRetry.Result;
    var bounds = link.BoundingRectangle;
    double dpiScale = GetDpiScale(window);
    Mouse.MoveTo(new System.Drawing.Point(
        (int)Math.Round((bounds.X + bounds.Width / 2d) * dpiScale),
        (int)Math.Round((bounds.Y + bounds.Height / 2d) * dpiScale)));
    Thread.Sleep(700);

    string hoverPath = Path.Combine(options.OutputDirectory, "probe-activity-link-hover.png");
    using (var capture = Capture.MainScreen(new CaptureSettings()))
    {
        capture.ToFile(hoverPath);
    }

    Console.WriteLine($"activity-link-hover probe: link='{GetElementName(link)}', screenshot={hoverPath}");
    if (string.IsNullOrWhiteSpace(options.AttachProcess))
    {
        TryClose(app);
    }
}

static void RunPullRequestTimelineLinkHoverProbe(CaptureOptions options)
{
    using var app = string.IsNullOrWhiteSpace(options.AttachProcess)
        ? LaunchApplication(options.AppPath, "--page=design-lab", "--scenario=pr-timeline", "--theme=dark")
        : CreateProbeApplication(options);
    using var automation = new UIA3Automation();

    Window window = GetReadyWindow(app, automation, "pull request timeline link hover probe");
    var linkRetry = Retry.WhileNull(
        () => window.FindAllDescendants()
            .Where(element =>
                IsVisible(element)
                && (element.ControlType == ControlType.Hyperlink || element.ControlType == ControlType.Text))
            .FirstOrDefault(element =>
                GetElementName(element).Contains("9648d21", StringComparison.OrdinalIgnoreCase)),
        timeout: TimeSpan.FromSeconds(10),
        interval: TimeSpan.FromMilliseconds(200));
    if (!linkRetry.Success || linkRetry.Result is null)
    {
        throw new InvalidOperationException("Unable to find a pull request timeline inline link for hover probe.");
    }

    AutomationElement link = linkRetry.Result;
    var bounds = link.BoundingRectangle;
    double dpiScale = GetDpiScale(window);
    Mouse.MoveTo(new System.Drawing.Point(
        (int)Math.Round((bounds.X + bounds.Width / 2d) * dpiScale),
        (int)Math.Round((bounds.Y + bounds.Height / 2d) * dpiScale)));
    Thread.Sleep(700);

    string hoverPath = Path.Combine(options.OutputDirectory, "probe-pr-timeline-link-hover.png");
    using (var capture = Capture.MainScreen(new CaptureSettings()))
    {
        capture.ToFile(hoverPath);
    }

    Console.WriteLine($"pr-timeline-link-hover probe: link='{GetElementName(link)}', screenshot={hoverPath}");
    if (string.IsNullOrWhiteSpace(options.AttachProcess))
    {
        TryClose(app);
    }
}

static Window GetReadyWindow(Application app, UIA3Automation automation, string context)
{
    var windowRetry = Retry.WhileNull(() => app.GetMainWindow(automation), timeout: TimeSpan.FromSeconds(20), interval: TimeSpan.FromMilliseconds(250));
    if (!windowRetry.Success || windowRetry.Result is null)
    {
        throw new InvalidOperationException($"Unable to find main window for {context}.");
    }

    Window window = windowRetry.Result;
    if (window.Patterns.Transform.IsSupported)
    {
        window.Patterns.Transform.Pattern.Resize(1600, 1000);
    }

    window.Move(80, 80);
    window.SetForeground();
    window.FocusNative();
    Thread.Sleep(1200);
    return window;
}

static bool IsVisible(AutomationElement? element)
{
    if (element is null)
    {
        return false;
    }

    try
    {
        return !element.Properties.IsOffscreen.ValueOrDefault;
    }
    catch
    {
        return false;
    }
}

static AutomationElement? FindShellSearchTextBox(Window window)
{
    AutomationElement? byId = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchTextBox"));
    if (IsVisible(byId))
    {
        return byId;
    }

    return window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
        .Where(IsVisible)
        .OrderByDescending(element => element.BoundingRectangle.Width)
        .FirstOrDefault();
}

static string GetElementName(AutomationElement element)
{
    try
    {
        return element.Name;
    }
    catch
    {
        return string.Empty;
    }
}

static double GetDpiScale(Window window)
{
    try
    {
        IntPtr nativeWindowHandle = window.Properties.NativeWindowHandle.ValueOrDefault;
        if (nativeWindowHandle != IntPtr.Zero)
        {
            uint dpi = NativeMethods.GetDpiForWindow(nativeWindowHandle);
            if (dpi > 0)
            {
                return dpi / 96d;
            }
        }
    }
    catch
    {
    }

    return NativeMethods.GetDpiForSystem() / 96d;
}

static Application CreateProbeApplication(CaptureOptions options)
{
    if (!string.IsNullOrWhiteSpace(options.AttachProcess))
    {
        string attachValue = options.AttachProcess.Trim();
        if (int.TryParse(attachValue, out int processId))
        {
            return Application.Attach(processId);
        }

        Process? process = Process.GetProcessesByName(attachValue)
            .OrderByDescending(static process => process.MainWindowHandle != IntPtr.Zero)
            .ThenBy(static process => process.Id)
            .FirstOrDefault();

        if (process is null)
        {
            throw new InvalidOperationException($"Could not find running process '{attachValue}' to attach automation.");
        }

        return Application.Attach(process.Id);
    }

    return LaunchApplication(options.AppPath);
}

static Application LaunchApplication(string appPath, params string[] arguments)
{
    var processStartInfo = new ProcessStartInfo(appPath)
    {
        WorkingDirectory = Path.GetDirectoryName(appPath) ?? Environment.CurrentDirectory,
        UseShellExecute = false
    };

    foreach (string argument in arguments)
    {
        processStartInfo.ArgumentList.Add(argument);
    }

    AddPreviewEnvironment(processStartInfo, arguments);
    return Application.Launch(processStartInfo);
}

static void AddPreviewEnvironment(ProcessStartInfo processStartInfo, IEnumerable<string> arguments)
{
    foreach (string argument in arguments)
    {
        if (argument.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))
        {
            processStartInfo.Environment["JITHUB_PREVIEW_PAGE"] = argument[7..];
        }
        else if (argument.StartsWith("--scenario=", StringComparison.OrdinalIgnoreCase))
        {
            processStartInfo.Environment["JITHUB_PREVIEW_SCENARIO"] = argument[11..];
        }
        else if (argument.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase))
        {
            processStartInfo.Environment["JITHUB_PREVIEW_THEME"] = argument[8..];
        }
        else if (argument.StartsWith("--repo=", StringComparison.OrdinalIgnoreCase))
        {
            processStartInfo.Environment["JITHUB_PREVIEW_REPOSITORY"] = argument[7..];
        }
        else if (argument.StartsWith("--repository=", StringComparison.OrdinalIgnoreCase))
        {
            processStartInfo.Environment["JITHUB_PREVIEW_REPOSITORY"] = argument[13..];
        }
        else if (argument.StartsWith("--branch=", StringComparison.OrdinalIgnoreCase))
        {
            processStartInfo.Environment["JITHUB_PREVIEW_BRANCH"] = argument[9..];
        }
    }
}

static string[] BuildLaunchArguments(CaptureTarget target, string theme, string repoFullName)
{
    var arguments = new List<string>
    {
        $"--page={target.Page}",
        $"--theme={theme}"
    };

    if (!string.IsNullOrWhiteSpace(target.Scenario))
    {
        arguments.Add($"--scenario={target.Scenario}");
    }

    if (target.Page.StartsWith("repo", StringComparison.OrdinalIgnoreCase))
    {
        arguments.Add($"--repo={repoFullName}");
    }

    return arguments.ToArray();
}

static void PrepareTargetForCapture(Window window, CaptureTarget target)
{
    if (IsRepoCodeTarget(target))
    {
        ClickReadmeByCoordinates(window);
        Thread.Sleep(4000);
        return;
    }

    if (!IsPullRequestTarget(target))
    {
        return;
    }

    Thread.Sleep(5000);
}

static bool ClickReadmeByCoordinates(Window window)
{
    var windowBounds = window.BoundingRectangle;
    double dpiScale = GetDpiScale(window);
    Mouse.DoubleClick(new System.Drawing.Point(
        (int)Math.Round((windowBounds.X + 220) * dpiScale),
        (int)Math.Round((windowBounds.Y + 430) * dpiScale)));
    return true;
}

static int GetSettleDelay(CaptureTarget target)
{
    if (string.Equals(target.Page, "repo-code", StringComparison.OrdinalIgnoreCase))
    {
        return 11500;
    }

    if (IsPullRequestTarget(target))
    {
        return 11500;
    }

    if (target.Page.StartsWith("repo", StringComparison.OrdinalIgnoreCase))
    {
        return 6500;
    }

    if (string.Equals(target.Page, "home", StringComparison.OrdinalIgnoreCase))
    {
        return 2500;
    }

    return 900;
}

static bool IsPullRequestTarget(CaptureTarget target) =>
    string.Equals(target.Page, "repo-pulls", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(target.Page, "repo-pull-requests", StringComparison.OrdinalIgnoreCase);

static bool IsRepoCodeTarget(CaptureTarget target) =>
    string.Equals(target.Page, "repo-code", StringComparison.OrdinalIgnoreCase);

static bool IsAppPreviewTarget(CaptureTarget target) =>
    target.Page.StartsWith("repo", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(target.Page, "home", StringComparison.OrdinalIgnoreCase);

static void TrimAppPreviewCapture(string filePath)
{
    const int left = 49;
    const int top = 42;

    byte[] sourceBytes = File.ReadAllBytes(filePath);
    using var sourceStream = new MemoryStream(sourceBytes);
    using var image = new Bitmap(sourceStream);
    if (image.Width <= left || image.Height <= top)
    {
        return;
    }

    var crop = new Rectangle(left, top, image.Width - left, image.Height - top);
    using Bitmap cropped = image.Clone(crop, image.PixelFormat);
    using MemoryStream output = new();
    cropped.Save(output, ImageFormat.Png);
    File.WriteAllBytes(filePath, output.ToArray());
}

static void TryClose(Application app)
{
    try
    {
        app.Close();
        if (!app.HasExited)
        {
            app.Kill();
        }
    }
    catch
    {
        try
        {
            if (!app.HasExited)
            {
                app.Kill();
            }
        }
        catch
        {
        }
    }
}

static void KillExistingApplicationInstances(string appPath)
{
    string processName = Path.GetFileNameWithoutExtension(appPath);
    if (string.IsNullOrWhiteSpace(processName))
    {
        return;
    }

    foreach (Process process in Process.GetProcessesByName(processName))
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    Thread.Sleep(1000);
}

static void WriteManifest(string outputDirectory, IReadOnlyList<CaptureResult> captures)
{
    var html = new StringBuilder();
    html.AppendLine("<!doctype html><html><head><meta charset='utf-8'><title>WinUI captures</title><style>body{font-family:Segoe UI, sans-serif;background:#f5f1e7;color:#223127;padding:24px}h1{font-size:28px}section{margin:0 0 24px}ul{display:grid;grid-template-columns:repeat(auto-fill,minmax(280px,1fr));gap:16px;list-style:none;padding:0}li{background:#fffdfc;border:1px solid #d5cbb7;border-radius:16px;padding:12px}img{width:100%;border-radius:12px;border:1px solid #e6ddcc}small{display:block;margin-top:8px;color:#4b5e52}</style></head><body>");
    html.AppendLine("<h1>JitHub WinUI screenshot manifest</h1>");

    foreach (var themeGroup in captures.GroupBy(c => c.Theme))
    {
        html.AppendLine($"<section><h2>{themeGroup.Key}</h2><ul>");
        foreach (CaptureResult capture in themeGroup)
        {
            html.AppendLine($"<li><img src='{capture.FileName}' alt='{capture.Name}' /><small>{capture.Name}</small></li>");
        }
        html.AppendLine("</ul></section>");
    }

    html.AppendLine("</body></html>");
    File.WriteAllText(Path.Combine(outputDirectory, "index.html"), html.ToString());
}

internal sealed record CaptureResult(string Theme, string Name, string FileName);

internal sealed record CaptureTarget(string Name, string Page, string? Scenario, string? AutomationId);

internal static partial class NativeMethods
{
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);
}

internal sealed class CaptureOptions
{
    public required string AppPath { get; init; }
    public required string OutputDirectory { get; init; }
    public required IReadOnlyList<string> Themes { get; init; }
    public required IReadOnlyList<CaptureTarget> Targets { get; init; }
    public required string RepositoryFullName { get; init; }
    public string? Probe { get; init; }
    public string? AttachProcess { get; init; }

    public static CaptureOptions Parse(string[] args)
    {
        string outputDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "screenshots", "winui"));
        string? appPath = null;
        string? probe = null;
        string? attachProcess = null;
        string repoFullName = "JitHubApp/JitHubV2";
        string[] themes = ["light", "dark"];
        string[] targetNames = ["buttons", "inputs", "segments", "navigation", "settings", "repo", "conversation", "pr-timeline", "empty", "login", "settings-page"];

        foreach (string arg in args)
        {
            if (arg.StartsWith("--app=", StringComparison.OrdinalIgnoreCase))
            {
                appPath = Path.GetFullPath(arg[6..]);
            }
            else if (arg.StartsWith("--out=", StringComparison.OrdinalIgnoreCase))
            {
                outputDirectory = Path.GetFullPath(arg[6..]);
            }
            else if (arg.StartsWith("--themes=", StringComparison.OrdinalIgnoreCase))
            {
                themes = arg[9..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            else if (arg.StartsWith("--targets=", StringComparison.OrdinalIgnoreCase))
            {
                targetNames = arg[10..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            else if (arg.StartsWith("--probe=", StringComparison.OrdinalIgnoreCase))
            {
                probe = arg[8..].Trim();
            }
            else if (arg.StartsWith("--attach-process=", StringComparison.OrdinalIgnoreCase))
            {
                attachProcess = arg[17..].Trim();
            }
            else if (arg.StartsWith("--repo=", StringComparison.OrdinalIgnoreCase))
            {
                repoFullName = arg[7..].Trim();
            }
        }

        appPath ??= GuessAppPath();
        if (!File.Exists(appPath))
        {
            throw new FileNotFoundException($"Could not find JitHub.WinUI app executable at '{appPath}'. Pass --app=<path>.");
        }

        Dictionary<string, CaptureTarget> allTargets = new(StringComparer.OrdinalIgnoreCase)
        {
            ["buttons"] = new CaptureTarget("buttons", "design-lab", "buttons", "ScenarioButtons"),
            ["inputs"] = new CaptureTarget("inputs", "design-lab", "inputs", "ScenarioInputs"),
            ["segments"] = new CaptureTarget("segments", "design-lab", "segments", "ScenarioSegments"),
            ["segmented"] = new CaptureTarget("segments", "design-lab", "segments", "ScenarioSegments"),
            ["navigation"] = new CaptureTarget("navigation", "design-lab", "navigation", "ScenarioNavigation"),
            ["settings"] = new CaptureTarget("settings", "design-lab", "settings", "ScenarioSettings"),
            ["repo"] = new CaptureTarget("repo", "design-lab", "repo", "ScenarioRepoReference"),
            ["activities"] = new CaptureTarget("activities", "design-lab", "activities", "ScenarioActivities"),
            ["activity"] = new CaptureTarget("activities", "design-lab", "activities", "ScenarioActivities"),
            ["conversation"] = new CaptureTarget("conversation", "design-lab", "conversation", "ScenarioConversation"),
            ["pr-timeline"] = new CaptureTarget("pr-timeline", "design-lab", "pr-timeline", "ScenarioPullRequestTimeline"),
            ["pull-request-timeline"] = new CaptureTarget("pr-timeline", "design-lab", "pr-timeline", "ScenarioPullRequestTimeline"),
            ["timeline"] = new CaptureTarget("pr-timeline", "design-lab", "pr-timeline", "ScenarioPullRequestTimeline"),
            ["home"] = new CaptureTarget("home", "home", null, null),
            ["shell"] = new CaptureTarget("shell", "shell", null, null),
            ["repo-code"] = new CaptureTarget("repo-code", "repo-code", null, null),
            ["real-repo"] = new CaptureTarget("repo-code", "repo-code", null, null),
            ["repo-issues"] = new CaptureTarget("repo-issues", "repo-issues", null, null),
            ["repo-pulls"] = new CaptureTarget("repo-pulls", "repo-pulls", null, null),
            ["repo-pull-requests"] = new CaptureTarget("repo-pulls", "repo-pull-requests", null, null),
            ["repo-commits"] = new CaptureTarget("repo-commits", "repo-commits", null, null),
            ["empty"] = new CaptureTarget("empty", "design-lab", "empty", "ScenarioEmptyState"),
            ["login"] = new CaptureTarget("login", "login", null, null),
            ["settings-page"] = new CaptureTarget("settings-page", "settings", null, null)
        };

        return new CaptureOptions
        {
            AppPath = appPath,
            OutputDirectory = outputDirectory,
            Themes = themes,
            Targets = targetNames.Select(name => allTargets[name]).ToList(),
            RepositoryFullName = string.IsNullOrWhiteSpace(repoFullName) ? "JitHubApp/JitHubV2" : repoFullName,
            Probe = probe,
            AttachProcess = attachProcess
        };
    }

    private static string GuessAppPath()
    {
        string baseDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string[] candidates =
        [
            Path.Combine(baseDirectory, "JitHub.WinUI", "bin", "x64", "Debug", "net10.0-windows10.0.26100.0", "win-x64", "JitHub.WinUI.exe"),
            Path.Combine(baseDirectory, "JitHub.WinUI", "bin", "Debug", "net10.0-windows10.0.26100.0", "win-x64", "JitHub.WinUI.exe"),
            Path.Combine(baseDirectory, "JitHub.WinUI", "bin", "x64", "Release", "net10.0-windows10.0.26100.0", "win-x64", "publish", "JitHub.WinUI.exe")
        ];

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}
