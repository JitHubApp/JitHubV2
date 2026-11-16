using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Native;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Probes.Website.WebsiteShowcaseSurfaces;
using static JitHub.WinUI.Automation.Screenshots.CaptureFiles;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowActivation;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Website;

/// <summary>
/// Drives the website-showcase probe: launches each showcase surface, captures it at the showcase size and validates the still.
/// </summary>
internal static class WebsiteShowcaseProbe
{
    // This method stays whole because it is one capture-and-manifest pass: filter the eight-surface catalog by
    // requested IDs, require exactly light and dark for a complete gallery, capture every theme-surface pair, check the
    // asset count, and write media-manifest.json from the same constants used during capture.
    internal static void RunWebsiteShowcaseProbe(CaptureOptions options)
    {
        const int captureWidth = 3200;
        const int captureHeight = 1800;
        const int minimumLogicalWidth = 1200;
        const int minimumLogicalHeight = 675;
        (string Id, string Page, string? Scenario, string ReadyAutomationId, string SourceState)[] surfaces =
        [
            ("home-workspace", "home", "website-showcase", "DashboardWidgetBoard", "home-expanded"),
            ("pull-request-conversation", "repo-pulls", "pr-shy-header", "RepoPullRequestsDetailTitle", "pull-request-conversation"),
            ("code-editor", "repo-code", "website-showcase", "RepoCodePageRoot", "repository-source-editor"),
            ("csv-table", "repo-code", "website-showcase", "RepoCodePageRoot", "repository-csv-rich"),
            ("commit-diff", "repo-commits", "website-showcase", "RepoCommitsAdaptiveWorkspace", "commit-diff"),
            ("stars-library", "stars", "website-showcase", "StarsPageRoot", "stars-all"),
            ("gists-editor", "gists", "website-showcase", "GistsWorkspace", "gist-editor"),
            ("profile-overview", "profile", "website-showcase", "ProfilePageRoot", "profile-overview")
        ];

        if (options.ShowcaseIds.Count > 0)
        {
            string[] requestedIds = options.ShowcaseIds
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            surfaces = surfaces
                .Where(surface => requestedIds.Contains(surface.Id, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            AssertProbe(
                surfaces.Length == requestedIds.Length,
                "website-showcase received an unknown --showcase-ids value.");
        }

        string[] themes = options.Themes
            .Select(static theme => theme.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        bool isCompleteGallery = surfaces.Length == 8;
        AssertProbe(
            themes.Length > 0 && themes.All(theme => theme is "light" or "dark"),
            "website-showcase themes must be light or dark.");
        AssertProbe(
            !isCompleteGallery ||
                (themes.Length == 2 && themes.Contains("light", StringComparer.Ordinal) && themes.Contains("dark", StringComparer.Ordinal)),
            "A complete website-showcase run requires exactly the light and dark themes so every catalog asset remains paired.");

        var assets = new JsonArray();
        foreach (string theme in themes)
        {
            foreach ((string id, string page, string? scenario, string readyAutomationId, string sourceState) in surfaces)
            {
                JsonObject captured = CaptureWebsiteShowcaseSurface(
                    options,
                    id,
                    page,
                    scenario,
                    readyAutomationId,
                    sourceState,
                    theme,
                    captureWidth,
                    captureHeight,
                    minimumLogicalWidth,
                    minimumLogicalHeight);
                assets.Add(captured);
            }
        }

        int expectedAssetCount = surfaces.Length * themes.Length;
        AssertProbe(
            assets.Count == expectedAssetCount,
            $"website-showcase captured {assets.Count} of {expectedAssetCount} requested still assets.");
        var manifest = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["generatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["captureWidth"] = captureWidth,
            ["captureHeight"] = captureHeight,
            ["minimumLogicalWidth"] = minimumLogicalWidth,
            ["minimumLogicalHeight"] = minimumLogicalHeight,
            ["source"] = "synthetic-public-preview",
            ["networkPolicy"] = "blocked-loopback-proxy",
            ["assets"] = assets
        };
        string manifestPath = Path.Combine(options.OutputDirectory, "media-manifest.json");
        File.WriteAllText(
            manifestPath,
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine(
            $"website-showcase: captured {assets.Count} exact {captureWidth}x{captureHeight} stills " +
            $"with at least {minimumLogicalWidth}x{minimumLogicalHeight} logical workspace; manifest={manifestPath}");
    }

    // This method stays whole because it runs one app launch for one still: build page, theme, scenario, and repo
    // arguments, resize to the exact capture size, check logical pixels at the window DPI, prepare the surface and
    // tooltips, capture and hash the PNG, and return the manifest entry built from those values.
    private static JsonObject CaptureWebsiteShowcaseSurface(
        CaptureOptions options,
        string id,
        string page,
        string? scenario,
        string readyAutomationId,
        string sourceState,
        string theme,
        int captureWidth,
        int captureHeight,
        int minimumLogicalWidth,
        int minimumLogicalHeight)
    {
        KillExistingApplicationInstances(options.AppPath);
        var arguments = new List<string>
        {
            $"--page={page}",
            $"--theme={theme}",
            "--website-showcase",
            "--network-disabled"
        };
        if (!string.IsNullOrWhiteSpace(scenario))
        {
            arguments.Add($"--scenario={scenario}");
        }
        if (page.StartsWith("repo", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add($"--repo={options.RepositoryFullName}");
        }

        using var app = LaunchApplication(options.AppPath, arguments.ToArray());
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, $"website-showcase {theme} {id}");
            Rectangle physicalBounds = ResizeWebsiteShowcaseWindow(window, captureWidth, captureHeight);
            uint windowDpi = NativeMethods.GetWindowDpi(GetNativeWindowHandle(window));
            int logicalWidth = (int)Math.Round(physicalBounds.Width * 96d / windowDpi);
            int logicalHeight = (int)Math.Round(physicalBounds.Height * 96d / windowDpi);
            AssertProbe(
                logicalWidth >= minimumLogicalWidth && logicalHeight >= minimumLogicalHeight,
                $"website-showcase requires at least {minimumLogicalWidth}x{minimumLogicalHeight} logical pixels; " +
                $"capture={physicalBounds.Width}x{physicalBounds.Height}, dpi={windowDpi}, logical={logicalWidth}x{logicalHeight}.");
            WaitForElement(
                $"website-showcase {id} root",
                () => FindCurrentVisibleByAutomationId(window, readyAutomationId),
                TimeSpan.FromSeconds(18));
            PrepareWebsiteShowcasePresentation(window, automation);
            PrepareWebsiteShowcaseSurface(window, id, options.OutputDirectory);
            if (page.StartsWith("repo", StringComparison.OrdinalIgnoreCase))
            {
                WaitForWebsiteRepositoryStatistics(window);
            }
            PrepareWebsiteShowcaseTooltips(window, automation);
            Thread.Sleep(900);

            string fileName = $"{id}-{theme}.png";
            string filePath = Path.Combine(options.OutputDirectory, fileName);
            DateTime captureStartedUtc = DateTime.UtcNow;
            TryDeleteFile(filePath);
            CaptureWindow(window, filePath);
            string hash = ValidateWebsiteShowcaseStill(filePath, captureStartedUtc, captureWidth, captureHeight);

            Console.WriteLine(
                $"website-showcase: {theme}/{id} -> {fileName} ({hash[..12]}); " +
                $"dpi={windowDpi}, logical={logicalWidth}x{logicalHeight}");
            return new JsonObject
            {
                ["id"] = id,
                ["theme"] = theme,
                ["file"] = fileName,
                ["sourceState"] = $"synthetic-public-preview/{sourceState}",
                ["width"] = captureWidth,
                ["height"] = captureHeight,
                ["windowDpi"] = windowDpi,
                ["logicalWidth"] = logicalWidth,
                ["logicalHeight"] = logicalHeight,
                ["sha256"] = hash
            };
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }

    private static void WaitForWebsiteRepositoryStatistics(Window window)
    {
        WaitForElement(
            "website-showcase repository watcher count",
            () => FindRepositoryStatisticWithValue(window, "RepoDetailWatchButton", "7"),
            TimeSpan.FromSeconds(12));
        WaitForElement(
            "website-showcase repository star count",
            () => FindRepositoryStatisticWithValue(window, "RepoDetailStarButton", "42"),
            TimeSpan.FromSeconds(12));
    }

    private static AutomationElement? FindRepositoryStatisticWithValue(
        Window window,
        string automationId,
        string expectedValue)
    {
        AutomationElement? statistic = FindCurrentVisibleByAutomationId(window, automationId);
        return statistic is not null && statistic.FindAllDescendants()
            .Any(element =>
                IsVisible(element) &&
                string.Equals(element.Name, expectedValue, StringComparison.Ordinal))
            ? statistic
            : null;
    }

    private static Rectangle ResizeWebsiteShowcaseWindow(Window window, int physicalWidth, int physicalHeight)
    {
        Rectangle workArea = NativeMethods.GetWorkArea();
        AssertProbe(
            workArea.Width >= physicalWidth && workArea.Height >= physicalHeight,
            $"website-showcase requires a physical work area of at least {physicalWidth}x{physicalHeight}; current work area is {workArea.Width}x{workArea.Height}.");

        IntPtr handle = GetNativeWindowHandle(window);
        _ = ResizeWindow(window, physicalWidth, physicalHeight, reactivate: false);
        for (int attempt = 0; attempt < 6; attempt++)
        {
            Rectangle physical = NativeMethods.GetPhysicalWindowBounds(handle);
            if (physical.Width == physicalWidth && physical.Height == physicalHeight)
            {
                TryActivateWindow(window);
                Thread.Sleep(650);
                return physical;
            }

            Rectangle outer = NativeMethods.GetWindowBounds(handle);
            int nextWidth = outer.Width + (physicalWidth - physical.Width);
            int nextHeight = outer.Height + (physicalHeight - physical.Height);
            NativeMethods.ResizeWindow(handle, nextWidth, nextHeight);
            Thread.Sleep(260);
        }

        Rectangle actual = NativeMethods.GetPhysicalWindowBounds(handle);
        throw new InvalidOperationException(
            $"website-showcase refused a constrained capture. Requested physical DWM bounds {physicalWidth}x{physicalHeight}; actual={actual.Width}x{actual.Height}.");
    }

    private static string ValidateWebsiteShowcaseStill(
        string filePath,
        DateTime captureStartedUtc,
        int expectedWidth,
        int expectedHeight)
    {
        FileInfo file = new(filePath);
        AssertProbe(file.Exists && file.Length > 10_000, $"website-showcase produced an empty still '{filePath}'.");
        AssertProbe(
            file.LastWriteTimeUtc >= captureStartedUtc.AddSeconds(-1),
            $"website-showcase refused stale output '{filePath}'.");
        using var image = new Bitmap(filePath);
        AssertProbe(
            image.Width == expectedWidth && image.Height == expectedHeight,
            $"website-showcase expected {expectedWidth}x{expectedHeight} pixels for '{file.Name}', found {image.Width}x{image.Height}.");
        var sampledColors = new HashSet<int>();
        int horizontalStep = Math.Max(1, image.Width / 80);
        int verticalStep = Math.Max(1, image.Height / 50);
        for (int y = verticalStep / 2; y < image.Height; y += verticalStep)
        {
            for (int x = horizontalStep / 2; x < image.Width; x += horizontalStep)
            {
                sampledColors.Add(image.GetPixel(x, y).ToArgb());
            }
        }
        AssertProbe(
            sampledColors.Count >= 12,
            $"website-showcase refused a visually blank still '{filePath}' ({sampledColors.Count} sampled colors).");
        return ComputeFileSha256(filePath);
    }

    private static string ComputeFileSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
