using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

internal static class HighContrastLiveProbe
{
    private const uint SpiGetHighContrast = 0x0042;
    private const uint SpiSetHighContrast = 0x0043;
    private const uint HcfHighContrastOn = 0x00000001;
    private const uint HcfOptionNoThemeChange = 0x00001000;
    private const uint SpifUpdateIniFile = 0x0001;
    private const uint SpifSendChange = 0x0002;

    private static readonly Viewport Wide = new(1366, 900);
    private static readonly Viewport Compact = new(760, 650);

    public static void Run(CaptureOptions options)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        EnsureNoExistingApplicationProcess(options.AppPath);

        NativeHighContrastSnapshot prior = ReadHighContrast();
        bool changedHighContrast = false;
        string dataRoot = Path.Combine(
            Path.GetTempPath(),
            "JitHub.WinUI.Automation",
            $"high-contrast-live-{Environment.ProcessId}");
        Exception? failure = null;

        try
        {
            RecreateDirectory(dataRoot);
            if (!prior.IsEnabled)
            {
                changedHighContrast = true;
                WriteHighContrast(prior with { Flags = prior.Flags | HcfHighContrastOn });
                WaitForHighContrast(
                    static state => state.IsEnabled,
                    "Windows High Contrast to become active");
                Thread.Sleep(1_250);
            }

            NativeHighContrastSnapshot active = ReadHighContrast();
            Assert(active.IsEnabled, "Windows did not report genuine High Contrast as active.");
            SystemColorPalette palette = SystemColorPalette.Capture();

            RunSettingsPage(options, dataRoot, palette);
            RunProfilePage(options, dataRoot, palette);
            RunMarkdownHostPage(options, dataRoot, palette);
            RunGistEditorDialog(options, dataRoot, palette);
            RunDashboardCustomShellDialog(options, dataRoot, palette);
            Console.WriteLine(
                "high-contrast-live probe: genuine Windows High Contrast, focus, selection, " +
                "destructive/editor/custom-shell dialogs, system colors, and responsive page captures passed; " +
                $"output={options.OutputDirectory}");
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (changedHighContrast)
            {
                try
                {
                    RestoreHighContrast(prior);
                }
                catch (Exception restoreException)
                {
                    failure = failure is null
                        ? restoreException
                        : new AggregateException(failure, restoreException);
                }
            }
            else
            {
                try
                {
                    NativeHighContrastSnapshot current = ReadHighContrast();
                    Assert(
                        current == prior,
                        "Windows High Contrast changed during the probe even though the probe did not mutate it.");
                }
                catch (Exception verificationException)
                {
                    failure = failure is null
                        ? verificationException
                        : new AggregateException(failure, verificationException);
                }
            }

            try
            {
                if (failure is null && Directory.Exists(dataRoot))
                {
                    Directory.Delete(dataRoot, recursive: true);
                }
            }
            catch (Exception cleanupException)
            {
                failure = failure is null
                    ? cleanupException
                    : new AggregateException(failure, cleanupException);
            }
        }

        if (failure is not null)
        {
            File.WriteAllText(
                Path.Combine(options.OutputDirectory, "high-contrast-live-failure.txt"),
                failure.ToString());
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        string failurePath = Path.Combine(options.OutputDirectory, "high-contrast-live-failure.txt");
        if (File.Exists(failurePath))
        {
            File.Delete(failurePath);
        }
    }

    private static void RunSettingsPage(
        CaptureOptions options,
        string dataRoot,
        SystemColorPalette palette)
    {
        RunPage(
            options.AppPath,
            dataRoot,
            options.OutputDirectory,
            "settings",
            (window, _) =>
            {
                WaitForElement(
                    "Settings Appearance section",
                    () => FindVisible(window, "SettingsSection_appearance"));

                ValidateSettingsWide(window, options.OutputDirectory, palette);
                ValidateSettingsCompact(window, options.OutputDirectory, palette);
                ValidateDestructiveDialog(window, options.OutputDirectory, palette, Wide, "1366x900");
                ValidateDestructiveDialog(window, options.OutputDirectory, palette, Compact, "760x650");
            });
    }

    private static void ValidateDestructiveDialog(
        Window window,
        string outputDirectory,
        SystemColorPalette palette,
        Viewport viewport,
        string viewportName)
    {
        Resize(window, viewport);
        if (viewport.Width <= Compact.Width)
        {
            AutomationElement picker = WaitForVisibleElement(window, "SettingsCompactSectionPicker");
            picker.AsComboBox().Select("Data & Cache");
            WaitForVisibleElement(window, "SettingsClearQueryCacheButton");
        }
        else
        {
            Invoke(WaitForVisibleElement(window, "SettingsSection_data-cache"), "Settings Data & Cache section");
        }
        AutomationElement clearButton = WaitForElement(
            "Settings clear-all-cache action",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsClearAllCacheButton")));
        Assert(
            clearButton.Patterns.ScrollItem.IsSupported,
            "Settings clear-all-cache action does not expose the native ScrollItem pattern.");
        clearButton.Patterns.ScrollItem.Pattern.ScrollIntoView();
        clearButton = WaitForVisibleElement(window, "SettingsClearAllCacheButton");
        Invoke(clearButton, "Settings clear-all-cache action");
        AutomationElement dialog = WaitForVisibleElement(window, "SettingsConfirmClearAllCache");
        AutomationElement destructiveButton = WaitForElement(
            "Settings destructive dialog primary button",
            () => dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(element => IsVisible(element) &&
                    SafeAutomationName(element).Contains("Clear", StringComparison.OrdinalIgnoreCase)));
        CaptureDialogState(
            window,
            dialog,
            destructiveButton,
            palette,
            Path.Combine(outputDirectory, $"high-contrast-live-destructive-dialog-{viewportName}.png"),
            "Settings destructive confirmation dialog");
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        WaitUntil(
            "Settings destructive dialog dismissal",
            () => FindVisible(window, "SettingsConfirmClearAllCache") is null);
    }

    private static void ValidateSettingsWide(
        Window window,
        string outputDirectory,
        SystemColorPalette palette)
    {
        Resize(window, Wide);
        AutomationElement appearance = WaitForVisibleElement(window, "SettingsSection_appearance");
        AutomationElement general = WaitForVisibleElement(window, "SettingsSection_general");
        AssertSelected(appearance, "Settings Appearance section at wide width");
        CaptureKeyboardSelectionState(
            window,
            appearance,
            general,
            VirtualKeyShort.DOWN,
            VirtualKeyShort.UP,
            palette,
            Path.Combine(outputDirectory, "high-contrast-live-settings-1366x900.png"),
            "Settings wide keyboard section selection");
    }

    private static void ValidateSettingsCompact(
        Window window,
        string outputDirectory,
        SystemColorPalette palette)
    {
        Resize(window, Compact);
        AutomationElement picker = WaitForVisibleElement(window, "SettingsCompactSectionPicker");
        AutomationElement neutralFocus = WaitForVisibleElement(window, "SettingsThemeSystem");
        string selectedLabel = picker.AsComboBox().SelectedItem?.Properties.Name.ValueOrDefault ?? string.Empty;
        Assert(
            selectedLabel.Contains("Appearance", StringComparison.OrdinalIgnoreCase),
            $"Settings compact picker did not expose its selected Appearance section: '{selectedLabel}'.");
        CaptureFocusedState(
            window,
            neutralFocus,
            picker,
            palette,
            Path.Combine(outputDirectory, "high-contrast-live-settings-760x650.png"),
            "Settings compact section picker");
    }

    private static void RunProfilePage(
        CaptureOptions options,
        string dataRoot,
        SystemColorPalette palette)
    {
        RunPage(
            options.AppPath,
            dataRoot,
            options.OutputDirectory,
            "profile",
            (window, automation) =>
            {
                WaitForElement(
                    "Profile Overview mode",
                    () => FindVisible(window, "ProfileModeOverviewItem"));

                ValidateProfileViewport(
                    window,
                    Wide,
                    "ProfileEditButton",
                    palette,
                    Path.Combine(options.OutputDirectory, "high-contrast-live-profile-1366x900.png"),
                    "Profile wide Overview selection");
                ValidateContributionGraph(window, automation, options.OutputDirectory, palette);
                ValidateProfileViewport(
                    window,
                    Compact,
                    "ProfileCompactEditButton",
                    palette,
                    Path.Combine(options.OutputDirectory, "high-contrast-live-profile-760x650.png"),
                    "Profile compact Overview selection");
            });
    }

    private static void RunGistEditorDialog(
        CaptureOptions options,
        string dataRoot,
        SystemColorPalette palette)
    {
        RunPage(
            options.AppPath,
            dataRoot,
            options.OutputDirectory,
            "gists",
            (window, _) =>
            {
                ValidateGistEditorDialog(window, options.OutputDirectory, palette, Wide, "1366x900");
                ValidateGistEditorDialog(window, options.OutputDirectory, palette, Compact, "760x650");
            });
    }

    private static void RunMarkdownHostPage(
        CaptureOptions options,
        string dataRoot,
        SystemColorPalette palette)
    {
        const string hostId = "MarkdownHost_RepositoryReadme_RepoCodeReadme";
        RunPage(
            options.AppPath,
            dataRoot,
            options.OutputDirectory,
            "repo-code",
            (window, _) =>
            {
                ValidateMarkdownHostViewport(
                    window,
                    Wide,
                    hostId,
                    palette,
                    Path.Combine(options.OutputDirectory, "high-contrast-live-markdown-1366x900.png"),
                    "Repository README Markdown at wide width");
                ValidateMarkdownHostViewport(
                    window,
                    Compact,
                    hostId,
                    palette,
                    Path.Combine(options.OutputDirectory, "high-contrast-live-markdown-760x650.png"),
                    "Repository README Markdown at compact width");
            },
            startInfo =>
            {
                startInfo.ArgumentList.Add($"--repo={options.RepositoryFullName}");
                startInfo.Environment["JITHUB_MARKDOWN_LIFECYCLE_FIXTURE"] = "1";
                startInfo.Environment["JITHUB_MARKDOWN_LIFECYCLE_HOST"] = hostId;
                startInfo.Environment["JITHUB_AUTOMATION_TEXT_SCALE_FACTOR"] = "1";
                startInfo.Environment["JITHUB_MARKDOWN_RENDER_FAILURE_EVIDENCE_PATH"] = Path.Combine(
                    options.OutputDirectory,
                    "high-contrast-live-markdown-render-failure.txt");
            });
    }

    private static void ValidateMarkdownHostViewport(
        Window window,
        Viewport viewport,
        string hostId,
        SystemColorPalette palette,
        string screenshotPath,
        string description)
    {
        Resize(window, viewport);
        AutomationElement host = WaitForElement(
            description,
            () =>
            {
                AutomationElement? candidate = FindVisible(window, hostId);
                string text = candidate?.Patterns.Text.PatternOrDefault?.DocumentRange.GetText(-1) ?? string.Empty;
                return text.Contains("Lifecycle long document final marker", StringComparison.Ordinal)
                    ? candidate
                    : null;
            });
        Assert(host.Patterns.Text.IsSupported, $"{description} does not expose TextPattern.");
        Assert(host.Properties.IsKeyboardFocusable.ValueOrDefault, $"{description} is not keyboard focusable.");
        Focus(host, description);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        WaitUntil(
            $"{description} keyboard text selection",
            () => host.Patterns.Text.Pattern.GetSelection().Any(range =>
                !string.IsNullOrWhiteSpace(range.GetText(-1))));
        using CapturedFrame frame = CaptureWindow(window);
        frame.Save(screenshotPath);
        Assert(window.BoundingRectangle.Contains(host.BoundingRectangle),
            $"{description} escaped or was clipped by the app window.");
        AssertSystemColorTreatment(frame, host.BoundingRectangle, palette, description);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
        Assert(ReadHighContrast().IsEnabled, $"High Contrast became inactive while selecting {description}.");
    }

    private static void RunDashboardCustomShellDialog(
        CaptureOptions options,
        string dataRoot,
        SystemColorPalette palette)
    {
        RunPage(
            options.AppPath,
            dataRoot,
            options.OutputDirectory,
            "home",
            (window, _) =>
            {
                ValidateDashboardCustomShellDialog(window, options.OutputDirectory, palette, Wide, "1366x900");
                ValidateDashboardCustomShellDialog(window, options.OutputDirectory, palette, Compact, "760x650");
            });
    }

    private static void ValidateGistEditorDialog(
        Window window,
        string outputDirectory,
        SystemColorPalette palette,
        Viewport viewport,
        string viewportName)
    {
        Resize(window, viewport);
        if (viewport.Width <= Compact.Width)
        {
            Invoke(WaitForVisibleElement(window, "GistsLeadingPaneButton"), "Open Gists list");
        }

        Invoke(WaitForVisibleElement(window, "GistsNew"), "New gist action");
        AutomationElement dialog = WaitForVisibleElement(window, "GistEditorDialog");
        AutomationElement editor = WaitForVisibleElement(window, "GistEditorDescription");
        CaptureDialogState(
            window,
            dialog,
            editor,
            palette,
            Path.Combine(outputDirectory, $"high-contrast-live-editor-dialog-{viewportName}.png"),
            "Gist editor dialog");
        AutomationElement cancelButton = WaitForElement(
            "Gist editor Cancel button",
            () => dialog.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton")) is { } candidate &&
                IsVisible(candidate)
                    ? candidate
                    : null);
        Invoke(cancelButton, "Gist editor Cancel action");
        WaitUntil(
            "Gist editor dialog dismissal",
            () => FindVisible(window, "GistEditorDialog") is null);
    }

    private static void ValidateDashboardCustomShellDialog(
        Window window,
        string outputDirectory,
        SystemColorPalette palette,
        Viewport viewport,
        string viewportName)
    {
        Resize(window, viewport);
        Invoke(WaitForVisibleElement(window, "DashboardCustomizeButton"), "Dashboard customize action");
        AutomationElement dialog = WaitForVisibleElement(window, "DashboardCustomizeDialog");
        AutomationElement saveButton = WaitForVisibleElement(window, "DashboardCustomizeSaveButton");
        CaptureDialogState(
            window,
            dialog,
            saveButton,
            palette,
            Path.Combine(outputDirectory, $"high-contrast-live-custom-shell-dialog-{viewportName}.png"),
            "Dashboard custom-shell dialog");
        Invoke(WaitForVisibleElement(window, "DashboardCustomizeCancelButton"), "Dashboard customize cancel action");
        WaitUntil(
            "Dashboard custom-shell dialog dismissal",
            () => FindVisible(window, "DashboardCustomizeDialog") is null);
    }

    private static void CaptureDialogState(
        Window window,
        AutomationElement dialog,
        AutomationElement focusTarget,
        SystemColorPalette palette,
        string screenshotPath,
        string description)
    {
        Rectangle windowBounds = window.BoundingRectangle;
        Rectangle dialogBounds = dialog.BoundingRectangle;
        Assert(
            windowBounds.Contains(dialogBounds),
            $"{description} escaped or was clipped by the app window: window={windowBounds}, dialog={dialogBounds}.");
        Focus(focusTarget, $"{description} focus target");
        using CapturedFrame frame = CaptureWindow(window);
        frame.Save(screenshotPath);
        AssertSystemColorTreatment(
            frame,
            dialogBounds,
            focusTarget.BoundingRectangle,
            palette,
            description);
    }

    private static void Invoke(AutomationElement element, string description)
    {
        if (element.Patterns.Invoke.IsSupported)
        {
            element.Patterns.Invoke.Pattern.Invoke();
        }
        else
        {
            element.Click();
        }

        Thread.Sleep(200);
        Assert(ReadHighContrast().IsEnabled, $"High Contrast became inactive while invoking {description}.");
    }

    private static void ValidateProfileViewport(
        Window window,
        Viewport viewport,
        string neutralFocusId,
        SystemColorPalette palette,
        string screenshotPath,
        string description)
    {
        Resize(window, viewport);
        AutomationElement overview = WaitForVisibleElement(window, "ProfileModeOverviewItem");
        AutomationElement repositories = WaitForVisibleElement(window, "ProfileModeRepositoriesItem");
        _ = WaitForVisibleElement(window, neutralFocusId);
        AssertSelected(overview, description);
        CaptureKeyboardSelectionState(
            window,
            overview,
            repositories,
            VirtualKeyShort.RIGHT,
            VirtualKeyShort.LEFT,
            palette,
            screenshotPath,
            description);
    }

    private static void ValidateContributionGraph(
        Window window,
        UIA3Automation automation,
        string outputDirectory,
        SystemColorPalette palette)
    {
        Resize(window, Wide);
        AutomationElement graph = WaitForElement(
            "populated Profile contribution graph",
            () =>
            {
                AutomationElement? candidate = window.FindFirstDescendant(
                    cf => cf.ByAutomationId("ProfileContributionGraph"));
                string name = candidate?.Name ?? string.Empty;
                return IsVisible(candidate) &&
                       name.StartsWith("Contribution calendar. ", StringComparison.Ordinal) &&
                       !name.Contains("No contribution data", StringComparison.OrdinalIgnoreCase)
                    ? candidate
                    : null;
            });
        AutomationElement neutralFocus = WaitForVisibleElement(window, "ProfileEditButton");
        Assert(graph.ControlType == ControlType.Calendar, "Profile contribution graph is not exposed as a Calendar.");
        Assert(
            graph.Properties.IsKeyboardFocusable.ValueOrDefault,
            "Profile contribution graph is not keyboard focusable.");

        using CapturedFrame before = CaptureWithFocus(window, neutralFocus);
        Focus(graph, "Profile contribution graph");
        WaitForElement(
            "Profile contribution keyboard tooltip",
            () => automation.GetDesktop()
                .FindAllDescendants(cf => cf.ByControlType(ControlType.ToolTip))
                .FirstOrDefault(IsVisible));
        using CapturedFrame after = CaptureWindow(window);
        after.Save(Path.Combine(outputDirectory, "high-contrast-live-profile-graph-focus-1366x900.png"));
        AssertFocusPixelsChanged(before, after, graph.BoundingRectangle, "Profile contribution graph");
        AssertSystemColorTreatment(after, graph.BoundingRectangle, palette, "Profile contribution graph");

        string lastDayName = graph.Name;
        ActivateForKeyboard(window, graph, "Profile contribution graph Home navigation");
        Keyboard.Press(VirtualKeyShort.HOME);
        WaitUntil(
            "Profile contribution graph Home navigation to reach the first day",
            () => !string.Equals(graph.Name, lastDayName, StringComparison.Ordinal));
        string firstDayName = graph.Name;
        ActivateForKeyboard(window, graph, "Profile contribution graph End navigation");
        Keyboard.Press(VirtualKeyShort.END);
        WaitUntil(
            "Profile contribution graph End navigation to restore the last day",
            () => !string.Equals(graph.Name, firstDayName, StringComparison.Ordinal));
        Assert(
            graph.Name.StartsWith("Contribution calendar. ", StringComparison.Ordinal),
            "Profile contribution graph lost its accessible selected-day identity after keyboard navigation.");
    }

    private static void RunPage(
        string appPath,
        string dataRoot,
        string outputDirectory,
        string page,
        Action<Window, UIA3Automation> exercise,
        Action<ProcessStartInfo>? configureStartInfo = null)
    {
        EnsureNoExistingApplicationProcess(appPath);
        Assert(ReadHighContrast().IsEnabled, $"High Contrast was not active before launching {page}.");

        ProcessStartInfo startInfo = new(appPath)
        {
            WorkingDirectory = Path.GetDirectoryName(appPath) ?? Environment.CurrentDirectory,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add($"--page={page}");
        startInfo.Environment["JITHUB_AUTOMATION_DATA_ROOT"] = dataRoot;
        startInfo.Environment["JITHUB_PREVIEW_PAGE"] = page;
        configureStartInfo?.Invoke(startInfo);

        Application? app = null;
        UIA3Automation? automation = null;
        Window? window = null;
        Exception? failure = null;
        try
        {
            app = Application.Launch(startInfo);
            automation = new UIA3Automation();
            window = WaitForWindow(app, automation, page);
            exercise(window, automation);
        }
        catch (Exception exception)
        {
            failure = exception;
            if (window is not null && automation is not null)
            {
                try
                {
                    using CapturedFrame frame = CaptureWindow(window);
                    frame.Save(Path.Combine(outputDirectory, $"high-contrast-live-{page}-failure.png"));
                    string tree = string.Join(
                        Environment.NewLine,
                        window.FindAllDescendants().Take(400).Select(element =>
                            $"{element.ControlType}\t{element.Properties.AutomationId.ValueOrDefault}\t" +
                            $"{SafeAutomationName(element)}\t{element.BoundingRectangle}"));
                    File.WriteAllText(
                        Path.Combine(outputDirectory, $"high-contrast-live-{page}-failure-uia.txt"),
                        tree);
                }
                catch (Exception evidenceException)
                {
                    failure = new AggregateException(failure, evidenceException);
                }
            }
        }
        finally
        {
            if (app is not null)
            {
                try
                {
                    CloseOwnedApplication(app);
                }
                catch (Exception closeException)
                {
                    failure = failure is null
                        ? closeException
                        : new AggregateException(failure, closeException);
                }
                finally
                {
                    app.Dispose();
                }
            }

            automation?.Dispose();
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void CaptureFocusedState(
        Window window,
        AutomationElement neutralFocus,
        AutomationElement target,
        SystemColorPalette palette,
        string screenshotPath,
        string description)
    {
        Assert(
            target.Properties.IsKeyboardFocusable.ValueOrDefault,
            $"{description} is not keyboard focusable.");
        using CapturedFrame before = CaptureWithFocus(window, neutralFocus);
        Focus(target, description);
        using CapturedFrame after = CaptureWindow(window);
        after.Save(screenshotPath);
        AssertFocusPixelsChanged(before, after, target.BoundingRectangle, description);
        AssertSystemColorTreatment(after, target.BoundingRectangle, palette, description);
    }

    private static void CaptureKeyboardSelectionState(
        Window window,
        AutomationElement initial,
        AutomationElement target,
        VirtualKeyShort moveKey,
        VirtualKeyShort restoreKey,
        SystemColorPalette palette,
        string screenshotPath,
        string description)
    {
        AssertSelected(initial, $"{description} initial item");
        Assert(target.Patterns.SelectionItem.IsSupported, $"{description} target has no native selection state.");
        using CapturedFrame before = CaptureWithFocus(window, initial);
        Keyboard.Press(moveKey);
        WaitUntil(
            $"{description} keyboard selection",
            () => target.Properties.HasKeyboardFocus.ValueOrDefault &&
                target.Patterns.SelectionItem.Pattern.IsSelected.Value);
        Thread.Sleep(150);
        using CapturedFrame after = CaptureWindow(window);
        after.Save(screenshotPath);
        AssertFocusPixelsChanged(before, after, target.BoundingRectangle, description);
        AssertSystemColorTreatment(after, target.BoundingRectangle, palette, description);
        Keyboard.Press(restoreKey);
        WaitUntil(
            $"{description} selection restoration",
            () => initial.Properties.HasKeyboardFocus.ValueOrDefault &&
                initial.Patterns.SelectionItem.Pattern.IsSelected.Value);
    }

    private static CapturedFrame CaptureWithFocus(Window window, AutomationElement target)
    {
        Focus(target, target.AutomationId ?? target.Name ?? "baseline focus target");
        return CaptureWindow(window);
    }

    private static void Focus(AutomationElement element, string description)
    {
        element.FocusNative();
        WaitUntil(
            $"{description} keyboard focus",
            () => element.Properties.HasKeyboardFocus.ValueOrDefault);
        Thread.Sleep(150);
    }

    private static void ActivateForKeyboard(Window window, AutomationElement element, string description)
    {
        IntPtr windowHandle = new(window.Properties.NativeWindowHandle.ValueOrDefault);
        Assert(windowHandle != IntPtr.Zero, $"{description} did not expose a native window handle.");
        window.SetForeground();
        _ = SetForegroundWindow(windowHandle);
        WaitUntil(
            $"{description} foreground ownership",
            () => GetForegroundWindow() == windowHandle);
        Focus(element, description);
    }

    private static void AssertSelected(AutomationElement element, string description)
    {
        Assert(
            element.Patterns.SelectionItem.IsSupported,
            $"{description} does not expose native SelectionItem state.");
        Assert(
            element.Patterns.SelectionItem.Pattern.IsSelected.Value,
            $"{description} is not selected.");
    }

    private static void AssertFocusPixelsChanged(
        CapturedFrame before,
        CapturedFrame after,
        Rectangle screenRegion,
        string description)
    {
        Rectangle region = after.ToLocal(screenRegion);
        Assert(region.Width > 0 && region.Height > 0, $"{description} has no visible screenshot region.");
        Assert(before.Bounds == after.Bounds, $"{description} window bounds shifted during focus capture.");

        int changedPixels = 0;
        for (int y = region.Top; y < region.Bottom; y++)
        {
            for (int x = region.Left; x < region.Right; x++)
            {
                if (before.Bitmap.GetPixel(x, y) != after.Bitmap.GetPixel(x, y))
                {
                    changedPixels++;
                }
            }
        }

        Assert(
            changedPixels >= 8,
            $"{description} focus produced only {changedPixels} changed pixels in its bounds.");
    }

    private static void AssertSystemColorTreatment(
        CapturedFrame frame,
        Rectangle focusRegion,
        SystemColorPalette palette,
        string description) =>
        AssertSystemColorTreatment(frame, frame.Bounds, focusRegion, palette, description);

    private static void AssertSystemColorTreatment(
        CapturedFrame frame,
        Rectangle contentRegion,
        Rectangle focusRegion,
        SystemColorPalette palette,
        string description)
    {
        Rectangle localContent = frame.ToLocal(contentRegion);
        AssertColorCount(frame.Bitmap, localContent, palette.Window, 64, $"{description} SystemColorWindow");
        AssertColorCount(frame.Bitmap, localContent, palette.WindowText, 8, $"{description} SystemColorWindowText");
        AssertColorCount(frame.Bitmap, localContent, palette.Highlight, 4, $"{description} SystemColorHighlight");

        Rectangle localFocus = frame.ToLocal(focusRegion);
        int focusSystemPixels = CountColor(frame.Bitmap, localFocus, palette.Highlight) +
            CountColor(frame.Bitmap, localFocus, palette.HighlightText);
        Assert(
            focusSystemPixels >= 2,
            $"{description} focus/selection region did not contain a system highlight color.");
    }

    private static void AssertColorCount(
        Bitmap bitmap,
        Rectangle region,
        Color color,
        int minimum,
        string description)
    {
        int count = CountColor(bitmap, region, color);
        Assert(count >= minimum, $"{description} appeared in only {count} screenshot pixels; expected {minimum}.");
    }

    private static int CountColor(Bitmap bitmap, Rectangle region, Color color)
    {
        region.Intersect(new Rectangle(Point.Empty, bitmap.Size));
        int count = 0;
        for (int y = region.Top; y < region.Bottom; y++)
        {
            for (int x = region.Left; x < region.Right; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                if (pixel.R == color.R && pixel.G == color.G && pixel.B == color.B)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static CapturedFrame CaptureWindow(Window window)
    {
        const int DwmwaExtendedFrameBounds = 9;
        const uint PwRenderFullContent = 0x00000002;
        IntPtr windowHandle = new(window.Properties.NativeWindowHandle.ValueOrDefault);
        Assert(windowHandle != IntPtr.Zero, "JitHub did not expose a native window handle for capture.");
        Assert(GetWindowRect(windowHandle, out NativeRect logicalBounds), "Could not read JitHub logical window bounds.");
        _ = SetForegroundWindow(windowHandle);
        Thread.Sleep(150);

        IntPtr priorDpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            Assert(GetWindowRect(windowHandle, out NativeRect physicalBounds), "Could not read JitHub physical window bounds.");
            Rectangle logicalRectangle = Rectangle.FromLTRB(
                logicalBounds.Left,
                logicalBounds.Top,
                logicalBounds.Right,
                logicalBounds.Bottom);
            Rectangle physicalRectangle = Rectangle.FromLTRB(
                physicalBounds.Left,
                physicalBounds.Top,
                physicalBounds.Right,
                physicalBounds.Bottom);
            Assert(logicalRectangle.Width > 0 && logicalRectangle.Height > 0, "JitHub logical window bounds were empty.");
            Assert(physicalRectangle.Width > 0 && physicalRectangle.Height > 0, "JitHub physical window bounds were empty.");

            NativeRect visibleBounds = physicalBounds;
            if (DwmGetWindowAttribute(
                    windowHandle,
                    DwmwaExtendedFrameBounds,
                    out NativeRect extendedFrameBounds,
                    Marshal.SizeOf<NativeRect>()) == 0)
            {
                visibleBounds = extendedFrameBounds;
            }
            Rectangle visibleRectangle = Rectangle.FromLTRB(
                visibleBounds.Left,
                visibleBounds.Top,
                visibleBounds.Right,
                visibleBounds.Bottom);
            Rectangle crop = Rectangle.Intersect(
                new Rectangle(
                    visibleRectangle.Left - physicalRectangle.Left,
                    visibleRectangle.Top - physicalRectangle.Top,
                    visibleRectangle.Width,
                    visibleRectangle.Height),
                new Rectangle(Point.Empty, physicalRectangle.Size));
            Assert(crop.Width > 0 && crop.Height > 0, "JitHub DWM bounds did not intersect its rendered window surface.");

            using var fullWindow = new Bitmap(
                physicalRectangle.Width,
                physicalRectangle.Height,
                PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(fullWindow))
            {
                IntPtr deviceContext = graphics.GetHdc();
                try
                {
                    Assert(
                        PrintWindow(windowHandle, deviceContext, PwRenderFullContent),
                        "Windows could not render the JitHub surface for High Contrast capture.");
                }
                finally
                {
                    graphics.ReleaseHdc(deviceContext);
                }
            }

            Bitmap bitmap = fullWindow.Clone(crop, PixelFormat.Format32bppArgb);
            double logicalScaleX = logicalRectangle.Width / (double)physicalRectangle.Width;
            double logicalScaleY = logicalRectangle.Height / (double)physicalRectangle.Height;
            Rectangle logicalVisibleRectangle = Rectangle.FromLTRB(
                logicalRectangle.Left + (int)Math.Round(crop.Left * logicalScaleX),
                logicalRectangle.Top + (int)Math.Round(crop.Top * logicalScaleY),
                logicalRectangle.Left + (int)Math.Round(crop.Right * logicalScaleX),
                logicalRectangle.Top + (int)Math.Round(crop.Bottom * logicalScaleY));
            return new CapturedFrame(bitmap, logicalVisibleRectangle);
        }
        finally
        {
            if (priorDpiContext != IntPtr.Zero)
            {
                _ = SetThreadDpiAwarenessContext(priorDpiContext);
            }
        }
    }

    private static Window WaitForWindow(Application app, UIA3Automation automation, string page)
    {
        var retry = Retry.WhileNull(
            () => app.GetMainWindow(automation),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromMilliseconds(100));
        return retry.Success && retry.Result is not null
            ? retry.Result
            : throw new InvalidOperationException($"JitHub {page} window did not appear.");
    }

    private static AutomationElement WaitForVisibleElement(Window window, string automationId) =>
        WaitForElement(
            automationId,
            () =>
            {
                AutomationElement? candidate = window.FindFirstDescendant(
                    cf => cf.ByAutomationId(automationId));
                return IsVisible(candidate) ? candidate : null;
            });

    private static AutomationElement? FindVisible(Window window, string automationId)
    {
        AutomationElement? candidate = window.FindFirstDescendant(
            cf => cf.ByAutomationId(automationId));
        return IsVisible(candidate) ? candidate : null;
    }

    private static string SafeAutomationName(AutomationElement element)
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

    private static AutomationElement WaitForElement(
        string description,
        Func<AutomationElement?> factory)
    {
        var retry = Retry.WhileNull(
            factory,
            TimeSpan.FromSeconds(12),
            TimeSpan.FromMilliseconds(100),
            ignoreException: true);
        return retry.Success && retry.Result is not null
            ? retry.Result
            : throw new InvalidOperationException($"Timed out waiting for {description}.");
    }

    private static void WaitUntil(string description, Func<bool> condition)
    {
        RetryResult<bool> retry = Retry.WhileFalse(
            condition,
            TimeSpan.FromSeconds(8),
            TimeSpan.FromMilliseconds(100),
            ignoreException: true);
        Assert(retry.Success, $"Timed out waiting for {description}.");
    }

    private static void Resize(Window window, Viewport viewport)
    {
        Assert(window.Patterns.Transform.IsSupported, "JitHub window does not support UIA resize.");
        IntPtr windowHandle = new(window.Properties.NativeWindowHandle.ValueOrDefault);
        Assert(windowHandle != IntPtr.Zero, "JitHub did not expose a native window handle for resize.");
        uint dpi = GetDpiForWindow(windowHandle);
        Assert(dpi > 0, "Windows did not report the JitHub window DPI.");
        double scale = dpi / 96d;
        int requestedPhysicalWidth = Math.Max(1, (int)Math.Round(viewport.Width * scale));
        int requestedPhysicalHeight = Math.Max(1, (int)Math.Round(viewport.Height * scale));
        const int outerMargin = 10;
        window.Patterns.Transform.Pattern.Resize(
            requestedPhysicalWidth,
            requestedPhysicalHeight);
        window.Move(outerMargin, outerMargin);
        window.SetForeground();
        Thread.Sleep(500);
        Assert(GetWindowRect(windowHandle, out NativeRect actualBounds), "Could not read JitHub bounds after resize.");
        int actualLogicalWidth = (int)Math.Round((actualBounds.Right - actualBounds.Left) / scale);
        int actualLogicalHeight = (int)Math.Round((actualBounds.Bottom - actualBounds.Top) / scale);
        int minimumUsableHeight = Math.Min(viewport.Height, 760);
        Assert(
            Math.Abs(actualLogicalWidth - viewport.Width) <= 4 &&
            actualLogicalHeight >= minimumUsableHeight &&
            actualLogicalHeight <= viewport.Height + 4,
            $"JitHub logical resize did not preserve the requested responsive width and usable height " +
            $"for {viewport.Width}x{viewport.Height}; actual={actualLogicalWidth}x{actualLogicalHeight}, dpi={dpi}.");
        Console.WriteLine(
            $"High Contrast logical viewport requested={viewport.Width}x{viewport.Height}; " +
            $"actual={actualLogicalWidth}x{actualLogicalHeight}; dpi={dpi}.");
        Assert(ReadHighContrast().IsEnabled, "High Contrast became inactive while resizing JitHub.");
    }

    private static bool IsVisible(AutomationElement? element) =>
        element is not null &&
        !element.Properties.IsOffscreen.ValueOrDefault &&
        element.BoundingRectangle.Width > 0 &&
        element.BoundingRectangle.Height > 0;

    private static void CloseOwnedApplication(Application app)
    {
        int processId = app.ProcessId;
        Exception? closeFailure = null;
        try
        {
            if (!app.HasExited)
            {
                app.Close();
            }
        }
        catch (Exception exception)
        {
            closeFailure = exception;
        }

        if (!WaitForProcessExit(processId, TimeSpan.FromSeconds(8)))
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException)
            {
            }
            catch (Exception exception)
            {
                closeFailure = closeFailure is null
                    ? exception
                    : new AggregateException(closeFailure, exception);
            }
        }

        Assert(
            WaitForProcessExit(processId, TimeSpan.FromSeconds(5)),
            $"Owned JitHub process {processId} remained active after cleanup.");
        if (closeFailure is not null)
        {
            ExceptionDispatchInfo.Capture(closeFailure).Throw();
        }
    }

    private static bool WaitForProcessExit(int processId, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        do
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            Thread.Sleep(100);
        }
        while (stopwatch.Elapsed < timeout);

        return false;
    }

    private static void EnsureNoExistingApplicationProcess(string appPath)
    {
        string processName = Path.GetFileNameWithoutExtension(appPath);
        List<int> processIds = [];
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                if (!process.HasExited)
                {
                    processIds.Add(process.Id);
                }
            }
        }

        processIds.Sort();
        Assert(
            processIds.Count == 0,
            $"Refusing to disturb existing {processName} processes: {string.Join(", ", processIds)}.");
    }

    private static void RecreateDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }

    private static NativeHighContrastSnapshot ReadHighContrast()
    {
        NativeHighContrast native = new()
        {
            Size = (uint)Marshal.SizeOf<NativeHighContrast>()
        };
        if (!SystemParametersInfoW(SpiGetHighContrast, native.Size, ref native, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SPI_GETHIGHCONTRAST failed.");
        }

        string? scheme = native.DefaultScheme == IntPtr.Zero
            ? null
            : Marshal.PtrToStringUni(native.DefaultScheme);
        return new NativeHighContrastSnapshot(native.Flags, scheme);
    }

    private static void WriteHighContrast(NativeHighContrastSnapshot target)
    {
        NativeHighContrastSnapshot current = ReadHighContrast();
        uint targetFlags = target.Flags;
        if (current.IsEnabled != target.IsEnabled)
        {
            targetFlags &= ~HcfOptionNoThemeChange;
        }

        IntPtr scheme = target.DefaultScheme is null
            ? IntPtr.Zero
            : Marshal.StringToHGlobalUni(target.DefaultScheme);
        try
        {
            NativeHighContrast native = new()
            {
                Size = (uint)Marshal.SizeOf<NativeHighContrast>(),
                Flags = targetFlags,
                DefaultScheme = scheme
            };
            if (!SystemParametersInfoW(
                    SpiSetHighContrast,
                    native.Size,
                    ref native,
                    SpifUpdateIniFile | SpifSendChange))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SPI_SETHIGHCONTRAST failed.");
            }
        }
        finally
        {
            if (scheme != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(scheme);
            }
        }
    }

    private static void RestoreHighContrast(NativeHighContrastSnapshot prior)
    {
        NativeHighContrastSnapshot current = ReadHighContrast();
        if (current != prior)
        {
            WriteHighContrast(prior);
        }

        WaitForHighContrast(state => state == prior, "the exact prior High Contrast state to be restored");
    }

    private static void WaitForHighContrast(
        Func<NativeHighContrastSnapshot, bool> predicate,
        string description)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        do
        {
            if (predicate(ReadHighContrast()))
            {
                return;
            }

            Thread.Sleep(100);
        }
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(15));

        throw new InvalidOperationException($"Timed out waiting for {description}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(
        uint action,
        uint parameter,
        ref NativeHighContrast highContrast,
        uint updateFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect bounds);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr windowHandle, IntPtr deviceContext, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        out NativeRect value,
        int valueSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeHighContrast
    {
        public uint Size;
        public uint Flags;
        public IntPtr DefaultScheme;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private readonly record struct NativeHighContrastSnapshot(uint Flags, string? DefaultScheme)
    {
        public bool IsEnabled => (Flags & HcfHighContrastOn) != 0;
    }

    private readonly record struct Viewport(int Width, int Height);

    private readonly record struct SystemColorPalette(
        Color Window,
        Color WindowText,
        Color Highlight,
        Color HighlightText)
    {
        private const int ColorWindow = 5;
        private const int ColorWindowText = 8;
        private const int ColorHighlight = 13;
        private const int ColorHighlightText = 14;

        public static SystemColorPalette Capture() => new(
            FromColorRef(GetSysColor(ColorWindow)),
            FromColorRef(GetSysColor(ColorWindowText)),
            FromColorRef(GetSysColor(ColorHighlight)),
            FromColorRef(GetSysColor(ColorHighlightText)));

        private static Color FromColorRef(uint colorRef) => Color.FromArgb(
            255,
            (int)(colorRef & 0xFF),
            (int)((colorRef >> 8) & 0xFF),
            (int)((colorRef >> 16) & 0xFF));
    }

    private sealed class CapturedFrame(Bitmap bitmap, Rectangle bounds) : IDisposable
    {
        public Bitmap Bitmap { get; } = bitmap;

        public Rectangle Bounds { get; } = bounds;

        public Rectangle ToLocal(Rectangle screenRegion)
        {
            double scaleX = Bitmap.Width / (double)Bounds.Width;
            double scaleY = Bitmap.Height / (double)Bounds.Height;
            Rectangle local = Rectangle.FromLTRB(
                (int)Math.Floor((screenRegion.Left - Bounds.Left) * scaleX),
                (int)Math.Floor((screenRegion.Top - Bounds.Top) * scaleY),
                (int)Math.Ceiling((screenRegion.Right - Bounds.Left) * scaleX),
                (int)Math.Ceiling((screenRegion.Bottom - Bounds.Top) * scaleY));
            local.Intersect(new Rectangle(Point.Empty, Bitmap.Size));
            return local;
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
            Bitmap.Save(path, ImageFormat.Png);
        }

        public void Dispose() => Bitmap.Dispose();
    }
}
