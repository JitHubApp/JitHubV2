using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

internal static class RepositoryLibraryProbe
{
    private static readonly (int Width, int Height)[] Viewports =
    [
        (1366, 900),
        (1180, 800),
        (900, 700),
        (760, 650),
        (640, 600)
    ];

    public static void Run(CaptureOptions options)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        string dataRoot = Path.Combine(
            Path.GetTempPath(),
            "JitHub.WinUI.Automation",
            $"repository-library-{Environment.ProcessId}");
        if (Directory.Exists(dataRoot))
        {
            Directory.Delete(dataRoot, recursive: true);
        }

        Directory.CreateDirectory(dataRoot);
        ProcessStartInfo startInfo = new(options.AppPath)
        {
            WorkingDirectory = Path.GetDirectoryName(options.AppPath) ?? Environment.CurrentDirectory,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--page=repositories");
        startInfo.ArgumentList.Add("--scenario=repository-library");
        startInfo.ArgumentList.Add("--theme=dark");
        startInfo.Environment["JITHUB_AUTOMATION_DATA_ROOT"] = dataRoot;
        startInfo.Environment["JITHUB_PREVIEW_PAGE"] = "repositories";
        startInfo.Environment["JITHUB_PREVIEW_SCENARIO"] = "repository-library";
        startInfo.Environment["JITHUB_PREVIEW_THEME"] = "dark";

        using Application app = Application.Launch(startInfo);
        using UIA3Automation automation = new();
        try
        {
            Window window = WaitForWindow(app, automation);
            ScreenshotTarget screenshotTarget = CreateScreenshotTarget(window);
            AutomationElement root = WaitFor(
                "repositories workspace",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoManagePageRoot")));
            Resize(window, 1180, 800);
            window.SetForeground();
            Thread.Sleep(250);
            Assert(root.IsEnabled, "Repositories workspace is disabled.");
            AutomationElement search = WaitFor(
                "repository search",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepositoryLibrarySearch")));
            AutomationElement filter = WaitFor(
                "repository filter",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepositoryLibraryFilter")));
            AutomationElement sort = WaitFor(
                "repository sort",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepositoryLibrarySort")));
            AutomationElement repositoryCount = WaitFor(
                "repository preview scope",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepositoryLibraryCount")));
            AutomationElement firstRow = WaitFor(
                "first repository row",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepositoryLibraryRow_900000")));

            Assert(GetName(firstRow).Contains("JitHubApp/JitHubV2", StringComparison.Ordinal),
                "Repository row does not expose its visible owner/name.");
            string selectedFilter = GetSelectedComboLabel(filter);
            Assert(string.Equals(selectedFilter, "All", StringComparison.OrdinalIgnoreCase),
                $"Repository filter did not initialize to All. Actual selection: '{selectedFilter}'.");
            string selectedSort = GetSelectedComboLabel(sort);
            Assert(selectedSort.Contains("Last updated", StringComparison.OrdinalIgnoreCase),
                $"Repository sort did not initialize to Last updated. Actual selection: '{selectedSort}'.");
            Assert(GetName(repositoryCount).Contains("preview", StringComparison.OrdinalIgnoreCase),
                "Deterministic repository fixtures are not explicitly labeled as preview data.");

            MovePointerAwayFromRows(window);
            Thread.Sleep(250);
            firstRow = WaitFor(
                "visible first repository row",
                () =>
                {
                    AutomationElement? candidate = window.FindFirstDescendant(
                        cf => cf.ByAutomationId("RepositoryLibraryRow_900000"));
                    return candidate is not null && candidate.BoundingRectangle.Width > 0 && candidate.BoundingRectangle.Height > 0
                        ? candidate
                        : null;
                });
            Rectangle hoveredRowBounds = firstRow.BoundingRectangle;
            string beforeHoverPath = Path.Combine(options.OutputDirectory, "repository-library-before-hover.png");
            string rowHoverPath = Path.Combine(options.OutputDirectory, "repository-library-row-hover.png");
            CaptureScreenshot(screenshotTarget, beforeHoverPath);
            MovePointer(firstRow);
            Thread.Sleep(350);
            CaptureScreenshot(screenshotTarget, rowHoverPath);
            Assert(
                ScreenshotRegionDiffers(beforeHoverPath, rowHoverPath, screenshotTarget, hoveredRowBounds),
                "Repository row hover did not produce a visible state change.");

            TextBox searchBox = search.AsTextBox();
            searchBox.Text = "repository-120";
            WaitFor(
                "filtered repository row",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepositoryLibraryRow_900119")));
            Assert(window.FindFirstDescendant(cf => cf.ByAutomationId("RepositoryLibraryRow_900118")) is null,
                "Repository search left nonmatching rows realized.");
            searchBox.Text = "JitHubV2";
            WaitFor("target repository row", () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepositoryLibraryRow_900000")));
            searchBox.Text = string.Empty;
            WaitFor("anchored target repository row", () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepositoryLibraryRow_900000")));

            filter.AsComboBox().Select("Private");
            Assert(GetSelectedComboLabel(filter).Contains("Private", StringComparison.OrdinalIgnoreCase),
                "Private repository filter did not select.");
            filter.AsComboBox().Select("All");
            sort.AsComboBox().Select("Name");
            Assert(GetSelectedComboLabel(sort).Contains("Name", StringComparison.OrdinalIgnoreCase),
                "Repository sort did not select Name.");
            sort.AsComboBox().Select("Last updated");

            AutomationElement selectionMode = WaitFor(
                "selection mode",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepositoryLibrarySelectionMode")));
            Invoke(selectionMode);
            firstRow = WaitFor(
                "repository row in selection mode",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepositoryLibraryRow_900000")));
            Assert(firstRow.Patterns.SelectionItem.IsSupported,
                "Repository row does not expose native selection state in bulk selection mode.");
            if (firstRow.Patterns.ScrollItem.IsSupported)
            {
                firstRow.Patterns.ScrollItem.Pattern.ScrollIntoView();
                Thread.Sleep(150);
                firstRow = WaitFor(
                    "visible repository row in selection mode",
                    () =>
                    {
                        AutomationElement? candidate = window.FindFirstDescendant(
                            cf => cf.ByAutomationId("RepositoryLibraryRow_900000"));
                        if (candidate is null)
                        {
                            return null;
                        }

                        Rectangle bounds = candidate.BoundingRectangle;
                        Rectangle windowBounds = window.BoundingRectangle;
                        return bounds.Width > 0 &&
                               bounds.Height > 0 &&
                               bounds.Top >= windowBounds.Top &&
                               bounds.Bottom <= windowBounds.Bottom
                            ? candidate
                            : null;
                    });
            }

            MovePointerAwayFromRows(window);
            Thread.Sleep(250);
            Rectangle selectionRowBounds = firstRow.BoundingRectangle;
            string selectionOffPath = Path.Combine(options.OutputDirectory, "repository-library-selection-off.png");
            string selectionOnPath = Path.Combine(options.OutputDirectory, "repository-library-selection-on.png");
            string selectionClearedPath = Path.Combine(options.OutputDirectory, "repository-library-selection-cleared.png");
            CaptureScreenshot(screenshotTarget, selectionOffPath);
            AutomationElement selection = WaitFor(
                "native repository selection checkbox",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepositoryLibrarySelect_900000")));
            Assert(selection.Patterns.Toggle.IsSupported, "Repository selection is not exposed as a native toggle checkbox.");
            selection.Patterns.Toggle.Pattern.Toggle();
            WaitFor(
                "selected repository count",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepositoryLibrarySelectionCount")) is { } count &&
                      GetName(count).Contains("1 selected", StringComparison.Ordinal)
                    ? count
                    : null);
            WaitUntil(
                "native repository row selected state",
                () => firstRow.Patterns.SelectionItem.Pattern.IsSelected.Value,
                TimeSpan.FromSeconds(3));
            MovePointerAwayFromRows(window);
            Thread.Sleep(250);
            CaptureScreenshot(screenshotTarget, selectionOnPath);
            Assert(
                ScreenshotRegionDiffers(selectionOffPath, selectionOnPath, screenshotTarget, selectionRowBounds),
                "Selecting the repository checkbox did not produce a matching full-width selected row state.");
            AutomationElement clearSelection = WaitFor(
                "clear repository selection",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepositoryLibraryClearSelection")));
            Invoke(clearSelection);
            Assert(selection.Patterns.Toggle.Pattern.ToggleState.Value == FlaUI.Core.Definitions.ToggleState.Off,
                "Clear selection did not reset the native checkbox.");
            WaitUntil(
                "native repository row selection cleared",
                () => !firstRow.Patterns.SelectionItem.Pattern.IsSelected.Value,
                TimeSpan.FromSeconds(3));
            MovePointerAwayFromRows(window);
            Thread.Sleep(250);
            CaptureScreenshot(screenshotTarget, selectionClearedPath);
            Assert(
                !ScreenshotRegionDiffers(selectionOffPath, selectionClearedPath, screenshotTarget, selectionRowBounds),
                "Clearing checkbox selection left a misleading selected-row visual behind.");
            Invoke(selectionMode);

            firstRow = WaitFor(
                "first repository row after selection",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepositoryLibraryRow_900000")));
            firstRow.FocusNative();
            using (Keyboard.Pressing(VirtualKeyShort.LSHIFT))
            {
                Keyboard.Press(VirtualKeyShort.F10);
            }

            AutomationElement contextCopy = WaitFor(
                "keyboard repository context menu",
                () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("RepositoryLibraryContextCopy")));
            Assert(GetName(contextCopy).Contains("Copy repository link", StringComparison.Ordinal),
                "Repository context command has the wrong accessible name.");
            Keyboard.Press(VirtualKeyShort.ESCAPE);

            firstRow = WaitFor(
                "first repository row before pointer context menu",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepositoryLibraryRow_900000")));
            RightClickVisible(window, firstRow);
            AutomationElement contextDelete = WaitFor(
                "permission-aware delete command",
                () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("RepositoryLibraryContextDelete")));
            Invoke(contextDelete);
            WaitFor(
                "repository delete confirmation",
                () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("RepositoryDeleteConfirmation")));
            AutomationElement cancelDelete = WaitFor(
                "delete confirmation close button",
                () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByName("Cancel")));
            Invoke(cancelDelete);
            WaitUntil(
                "delete confirmation dismissed",
                () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("RepositoryDeleteConfirmation")) is null,
                TimeSpan.FromSeconds(5));

            foreach ((int width, int height) in Viewports)
            {
                Resize(window, width, height);
                Thread.Sleep(450);
                search = WaitFor("responsive search", () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepositoryLibrarySearch")));
                filter = WaitFor("responsive filter", () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepositoryLibraryFilter")));
                sort = WaitFor("responsive sort", () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepositoryLibrarySort")));
                AssertInside(window, search, $"search at {width}x{height}");
                AssertInside(window, filter, $"filter at {width}x{height}");
                AssertInside(window, sort, $"sort at {width}x{height}");
                CaptureScreenshot(screenshotTarget, Path.Combine(options.OutputDirectory, $"repository-library-{width}x{height}.png"));
            }

            Resize(window, 1180, 800);
            firstRow = WaitFor(
                "repository row before navigation",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepositoryLibraryRow_900000")));
            firstRow.Click();
            WaitFor(
                "repository detail destination",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailIdentity")));

            PressCtrlK();
            AutomationElement searchBoxAfterNavigation = WaitFor(
                "command search after repository navigation",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchTextBox")));
            searchBoxAfterNavigation.AsTextBox().Text = string.Empty;
            searchBoxAfterNavigation.AsTextBox().Enter("all repositories");
            AutomationElement allRepositories = WaitFor(
                "All Repositories command",
                () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("All Repositories")));
            Invoke(allRepositories);
            WaitFor(
                "reactivated cached repository library",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepositoryLibraryList")));
            WaitFor(
                "repository row after cached-page reactivation",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepositoryLibraryRow_900000")));

            Console.WriteLine("repository-library probe: responsive, search, filters, sort, hover, selection fidelity, context, permissions, navigation, and cached reactivation passed.");
        }
        finally
        {
            if (!app.HasExited)
            {
                app.Close();
            }
        }
    }

    private static Window WaitForWindow(Application app, UIA3Automation automation)
    {
        var retry = Retry.WhileNull(
            () => app.GetMainWindow(automation),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromMilliseconds(100));
        return retry.Success && retry.Result is not null
            ? retry.Result
            : throw new InvalidOperationException("JitHub main window did not appear.");
    }

    private static AutomationElement WaitFor(string description, Func<AutomationElement?> factory)
    {
        var retry = Retry.WhileNull(
            factory,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(100),
            ignoreException: true);
        return retry.Success && retry.Result is not null
            ? retry.Result
            : throw new InvalidOperationException($"Timed out waiting for {description}.");
    }

    private static void WaitUntil(string description, Func<bool> condition, TimeSpan timeout)
    {
        RetryResult<bool> retry = Retry.WhileFalse(
            condition,
            timeout,
            TimeSpan.FromMilliseconds(100),
            ignoreException: true);
        if (!retry.Success)
        {
            throw new InvalidOperationException($"Timed out waiting for {description}.");
        }
    }

    private static void Invoke(AutomationElement element)
    {
        if (element.Patterns.Invoke.IsSupported)
        {
            element.Patterns.Invoke.Pattern.Invoke();
        }
        else
        {
            element.Click();
        }
    }

    private static void Resize(Window window, int width, int height)
    {
        if (!window.Patterns.Transform.IsSupported)
        {
            throw new InvalidOperationException("JitHub window does not support UIA resize.");
        }

        window.Patterns.Transform.Pattern.Resize(width, height);
        window.Move(30, 30);
    }

    private static void MovePointer(AutomationElement element)
    {
        Rectangle bounds = element.BoundingRectangle;
        Point target = new(bounds.X + Math.Min(96, bounds.Width / 2), bounds.Y + Math.Min(24, bounds.Height / 2));
        SendMouseMove(target);
    }

    private static void MovePointerAwayFromRows(Window window)
    {
        Rectangle bounds = window.BoundingRectangle;
        Mouse.MoveTo(new Point(bounds.X + Math.Min(300, Math.Max(180, bounds.Width / 4)), bounds.Y + 18));
    }

    private static void RightClickVisible(Window window, AutomationElement element)
    {
        window.SetForeground();
        if (element.Patterns.ScrollItem.IsSupported)
        {
            element.Patterns.ScrollItem.Pattern.ScrollIntoView();
            Thread.Sleep(100);
        }

        element.FocusNative();
        Rectangle bounds = element.BoundingRectangle;
        Assert(bounds.Width > 0 && bounds.Height > 0, "Repository row has no visible pointer target.");
        Mouse.RightClick(new Point(bounds.X + bounds.Width / 2, bounds.Y + Math.Min(24, bounds.Height / 2)));
    }

    private static void AssertInside(Window window, AutomationElement element, string description)
    {
        Rectangle outer = window.BoundingRectangle;
        Rectangle inner = element.BoundingRectangle;
        Assert(inner.Width > 0 && inner.Height > 0, $"{description} has empty bounds.");
        Assert(inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom,
            $"{description} is outside the app window: {inner} vs {outer}.");
    }

    private static string GetName(AutomationElement element) => element.Properties.Name.ValueOrDefault ?? string.Empty;

    private static string GetSelectedComboLabel(AutomationElement element)
    {
        ComboBox comboBox = element.AsComboBox();
        string label = comboBox.SelectedItem?.Properties.Name.ValueOrDefault ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(label))
        {
            return label;
        }

        comboBox.Expand();
        Thread.Sleep(100);
        label = comboBox.SelectedItem?.Properties.Name.ValueOrDefault ?? string.Empty;
        comboBox.Collapse();
        return label;
    }

    private static ScreenshotTarget CreateScreenshotTarget(Window window)
    {
        COMException? lastError = null;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                IntPtr windowHandle = new(window.Properties.NativeWindowHandle.ValueOrDefault);
                int processId = window.Properties.ProcessId.ValueOrDefault;
                if (windowHandle != IntPtr.Zero && processId > 0)
                {
                    _ = GetWindowThreadProcessId(windowHandle, out uint actualProcessId);
                    if (actualProcessId == (uint)processId)
                    {
                        return new ScreenshotTarget(windowHandle, processId);
                    }
                }
            }
            catch (COMException exception)
            {
                lastError = exception;
            }

            Thread.Sleep(75);
        }

        throw new InvalidOperationException("Could not resolve the verified native JitHub window for screenshots.", lastError);
    }

    private static void CaptureScreenshot(ScreenshotTarget target, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
        AssertNoForeignJitHubProcesses(target);
        NativeRect nativeBounds = default;
        bool hasBounds = false;
        for (int attempt = 0; attempt < 5 && !hasBounds; attempt++)
        {
            if (IsWindow(target.WindowHandle))
            {
                _ = GetWindowThreadProcessId(target.WindowHandle, out uint actualProcessId);
                if (actualProcessId != (uint)target.ProcessId)
                {
                    throw new InvalidOperationException(
                        $"Native window ownership changed before screenshot '{path}': expected {target.ProcessId}, actual {actualProcessId}.");
                }

                _ = SetForegroundWindow(target.WindowHandle);
                hasBounds = GetWindowRect(target.WindowHandle, out nativeBounds);
            }

            if (!hasBounds)
            {
                Thread.Sleep(100);
            }
        }

        if (!hasBounds)
        {
            throw new InvalidOperationException($"Could not read current native bounds for screenshot '{path}'.");
        }

        Thread.Sleep(100);

        Rectangle bounds = Rectangle.FromLTRB(
            nativeBounds.Left,
            nativeBounds.Top,
            nativeBounds.Right,
            nativeBounds.Bottom);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException($"Native screenshot bounds are empty for '{path}'.");
        }

        using Bitmap bitmap = new(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        AssertNoForeignJitHubProcesses(target);

        bitmap.Save(path, ImageFormat.Png);
    }

    private static void AssertNoForeignJitHubProcesses(ScreenshotTarget target)
    {
        int[] foreignProcessIds = Process.GetProcessesByName("JitHub.WinUI")
            .Where(process => process.Id != target.ProcessId && !process.HasExited)
            .Select(static process => process.Id)
            .OrderBy(static processId => processId)
            .ToArray();
        if (foreignProcessIds.Length > 0)
        {
            throw new InvalidOperationException(
                $"Foreign JitHub windows were active during screenshot capture: {string.Join(", ", foreignProcessIds)}.");
        }
    }

    private static bool ScreenshotRegionDiffers(
        string beforePath,
        string afterPath,
        ScreenshotTarget target,
        Rectangle screenRegion)
    {
        if (!GetWindowRect(target.WindowHandle, out NativeRect nativeBounds))
        {
            throw new InvalidOperationException("Could not resolve the native window bounds for hover comparison.");
        }

        Rectangle region = new(
            screenRegion.X - nativeBounds.Left,
            screenRegion.Y - nativeBounds.Top,
            screenRegion.Width,
            screenRegion.Height);
        using Bitmap before = new(beforePath);
        using Bitmap after = new(afterPath);
        region.Intersect(new Rectangle(0, 0, before.Width, before.Height));
        if (region.Width <= 0 || region.Height <= 0 || before.Size != after.Size)
        {
            return false;
        }

        int changedPixels = 0;
        int requiredChanges = Math.Max(12, region.Width * region.Height / 500);
        for (int y = region.Top; y < region.Bottom; y += 2)
        {
            for (int x = region.Left; x < region.Right; x += 2)
            {
                if (before.GetPixel(x, y) != after.GetPixel(x, y) && ++changedPixels >= requiredChanges)
                {
                    return true;
                }
            }
        }

        return false;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect bounds);

    private static void SendMouseMove(Point target)
    {
        int virtualLeft = GetSystemMetrics(76);
        int virtualTop = GetSystemMetrics(77);
        int virtualWidth = Math.Max(2, GetSystemMetrics(78));
        int virtualHeight = Math.Max(2, GetSystemMetrics(79));
        int normalizedX = (int)Math.Round((target.X - virtualLeft) * 65535d / (virtualWidth - 1));
        int normalizedY = (int)Math.Round((target.Y - virtualTop) * 65535d / (virtualHeight - 1));
        NativeInput[] inputs =
        [
            new NativeInput
            {
                Type = 0,
                Mouse = new NativeMouseInput
                {
                    X = normalizedX,
                    Y = normalizedY,
                    Flags = 0x0001 | 0x4000 | 0x8000
                }
            }
        ];

        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) != inputs.Length)
        {
            throw new InvalidOperationException("Could not deliver native mouse input for repository hover verification.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public NativeMouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    private readonly record struct ScreenshotTarget(IntPtr WindowHandle, int ProcessId);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void PressCtrlK()
    {
        using (Keyboard.Pressing(VirtualKeyShort.LCONTROL))
        {
            Thread.Sleep(75);
            Keyboard.Press(VirtualKeyShort.KEY_K);
            Thread.Sleep(75);
        }
    }
}
