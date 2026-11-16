using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Native;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Probes.RepoCode.RepoCodeNavigation;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowActivation;
using static JitHub.WinUI.Automation.Windows.WindowSizing;
using NativeTableElementArray = Interop.UIAutomationClient.IUIAutomationElementArray;
using NativeTableItemPattern = Interop.UIAutomationClient.IUIAutomationTableItemPattern;

namespace JitHub.WinUI.Automation.Probes.RepoCode;

/// <summary>
/// Drives the repo-code-content-surfaces probe: fixture files, segmented views and SVG zoom.
/// </summary>
internal static class RepoCodeContentSurfacesProbe
{
    // This method stays whole because it walks one repo-code window from the data.csv rich table (grid, native column
    // headers, sort) to Plain mode, back to Rich at 640px, and then to architecture.svg zoom checks at 1x, 8x, 0.1x and
    // compact width, reusing the window size each step leaves behind.
    internal static void ExerciseRepoCodeContentSurfaces(Window window, CaptureOptions options)
    {
        ResizeWindow(window, 1366, 900);
        Thread.Sleep(320);

        SelectRepoCodeFixtureFile(window, "data.csv", "data.csv, file");
        AutomationElement dataTable = WaitForElement(
            "ready CSV data table",
            () =>
            {
                AutomationElement? candidate = window.FindAllDescendants()
                    .FirstOrDefault(element =>
                        IsVisible(element) &&
                        (string.Equals(GetAutomationId(element), "CsvPreviewDataGrid", StringComparison.Ordinal) ||
                         string.Equals(GetAutomationId(element), "CsvPreviewDataTable", StringComparison.Ordinal)));
                if (candidate is null ||
                    !candidate.Patterns.Grid.IsSupported ||
                    !candidate.Patterns.Table.IsSupported)
                {
                    return null;
                }

                try
                {
                    return candidate.Patterns.Grid.Pattern.RowCount.Value == 7 &&
                        candidate.Patterns.Grid.Pattern.ColumnCount.Value == 5
                        ? candidate
                        : null;
                }
                catch (COMException)
                {
                    return null;
                }
            },
            TimeSpan.FromSeconds(12));

        Grid csvGrid = dataTable.AsGrid();
        AssertProbe(csvGrid.RowCount == 7, $"repo-code CSV: expected 7 data rows, found {csvGrid.RowCount}.");
        AssertProbe(csvGrid.ColumnCount == 5, $"repo-code CSV: expected 5 columns, found {csvGrid.ColumnCount}.");
        AutomationElement[] visibleHeaders = window.FindAllDescendants()
            .Where(element =>
                IsVisible(element) &&
                GetAutomationId(element).StartsWith("CsvPreviewDataTableSortColumn_", StringComparison.Ordinal))
            .ToArray();
        AssertProbe(visibleHeaders.Length == 5, $"repo-code CSV: visual tree exposed {visibleHeaders.Length} of five column headers.");
        var tablePattern = dataTable.Patterns.Table.Pattern;
        AssertProbe(
            tablePattern.RowOrColumnMajor.IsSupported &&
            tablePattern.RowOrColumnMajor.Value == FlaUI.Core.Definitions.RowOrColumnMajor.RowMajor,
            "repo-code CSV: TablePattern omitted its row-major traversal contract.");

        AutomationElement firstCell = dataTable.Patterns.Grid.Pattern.GetItem(0, 0);
        AssertProbe(firstCell.Patterns.GridItem.IsSupported, "repo-code CSV: cells omitted GridItem semantics.");
        AssertProbe(firstCell.Patterns.TableItem.IsSupported, "repo-code CSV: cells omitted TableItem semantics.");
        for (int column = 0; column < csvGrid.ColumnCount; column++)
        {
            AutomationElement cell = dataTable.Patterns.Grid.Pattern.GetItem(0, column);
            AssertProbe(cell.Patterns.TableItem.IsSupported, $"repo-code CSV: column {column} omitted TableItem semantics.");
            UIA3FrameworkAutomationElement nativeCell =
                (UIA3FrameworkAutomationElement)cell.FrameworkAutomationElement;
            NativeTableItemPattern nativeTableItemPattern =
                (NativeTableItemPattern)nativeCell.NativeElement.GetCurrentPattern(10013);
            NativeTableElementArray nativeColumnHeaders =
                nativeTableItemPattern.GetCurrentColumnHeaderItems();
            string nativeHeaderId = nativeColumnHeaders?.Length == 1
                ? nativeColumnHeaders.GetElement(0).CurrentAutomationId
                : string.Empty;
            AssertProbe(
                string.Equals(nativeHeaderId, $"CsvPreviewDataTableSortColumn_{column}", StringComparison.Ordinal),
                $"repo-code CSV: column {column} did not resolve to its native column header.");
        }

        AssertProbe(
            string.Equals(GetElementName(firstCell), "Repository: JitHub", StringComparison.Ordinal),
            $"repo-code CSV: unexpected initial first cell '{GetElementName(firstCell)}'.");

        AutomationElement repositorySort = WaitForElement(
            "CSV Repository sort header",
            () => window.FindAllDescendants(cf => cf.ByAutomationId("CsvPreviewDataTableSortColumn_0"))
                .FirstOrDefault(IsVisible),
            TimeSpan.FromSeconds(5));
        AssertProbe(repositorySort.Patterns.Invoke.IsSupported, "repo-code CSV: sort header omitted Invoke semantics.");
        repositorySort.Patterns.Invoke.Pattern.Invoke();
        WaitUntil(
            "CSV ascending Repository sort",
            () => string.Equals(
                GetElementName(dataTable.Patterns.Grid.Pattern.GetItem(0, 0)),
                "Repository: AppDataTable",
                StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-code-csv-rich-1366x900.png"));

        SelectSegmentedItem(window, "CsvPreviewViewMode_Plain", "Plain CSV view");
        AutomationElement plainEditor = WaitForElement(
            "read-only plain CSV editor",
            () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeEditor"))
                .FirstOrDefault(element =>
                    IsVisible(element) &&
                    element.Patterns.Value.IsSupported &&
                    (element.Patterns.Value.Pattern.Value.Value ?? string.Empty)
                        .Contains("Repository,Language,Open issues,Status,Notes", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10));
        AssertProbe(
            plainEditor.Patterns.Value.Pattern.IsReadOnly.Value,
            "repo-code CSV: Plain mode exposed an editable source control.");
        AssertProbe(
            !window.FindAllDescendants()
                .Any(element =>
                    IsVisible(element) &&
                    (string.Equals(GetAutomationId(element), "CsvPreviewDataGrid", StringComparison.Ordinal) ||
                     string.Equals(GetAutomationId(element), "CsvPreviewDataTable", StringComparison.Ordinal))),
            "repo-code CSV: Rich table remained visible behind Plain mode.");
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-code-csv-plain-1366x900.png"));

        SelectSegmentedItem(window, "CsvPreviewViewMode_Rich", "Rich CSV view");
        dataTable = WaitForElement(
            "restored rich CSV table",
            () => window.FindAllDescendants()
                .FirstOrDefault(element =>
                    IsVisible(element) &&
                    (string.Equals(GetAutomationId(element), "CsvPreviewDataGrid", StringComparison.Ordinal) ||
                     string.Equals(GetAutomationId(element), "CsvPreviewDataTable", StringComparison.Ordinal)) &&
                    element.Patterns.Grid.IsSupported),
            TimeSpan.FromSeconds(10));

        ResizeWindow(window, 640, 600);
        Thread.Sleep(420);
        AssertProbe(IsInsideWindowBounds(dataTable, window), "repo-code CSV: compact table escaped the 640px viewport.");
        AssertProbe(IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("CsvPreviewViewMode"))), "repo-code CSV: view switcher disappeared at 640px.");
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-code-csv-rich-640x600.png"));

        ResizeWindow(window, 1366, 900);
        Thread.Sleep(320);
        SelectRepoCodeFixtureFile(window, "architecture.svg", "architecture.svg, file");
        AutomationElement svgViewport = WaitForElement(
            "rendered SVG viewport",
            () =>
            {
                AutomationElement? candidate = window.FindFirstDescendant(cf => cf.ByAutomationId("SvgPreviewViewport"));
                string status = candidate?.Properties.ItemStatus.ValueOrDefault ?? string.Empty;
                return candidate is not null &&
                    IsVisible(candidate) &&
                    status.StartsWith("rendered:tiles:", StringComparison.Ordinal)
                    ? candidate
                    : null;
            },
            TimeSpan.FromSeconds(14));
        AutomationElement renderedImage = WaitForElement(
            "rendered SVG image",
            () => window.FindAllDescendants(cf => cf.ByAutomationId("SvgPreviewRenderedImage"))
                .FirstOrDefault(IsVisible),
            TimeSpan.FromSeconds(8));
        AssertProbe(
            renderedImage.BoundingRectangle.Width > 0 && renderedImage.BoundingRectangle.Height > 0,
            "repo-code SVG: rendered image had empty bounds.");
        AutomationElement svgScrollViewport = WaitForElement(
            "SVG scroll viewport",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SvgPreviewScrollViewer")),
            TimeSpan.FromSeconds(5));

        AssertProbe(svgViewport.Patterns.Transform2.IsSupported, "repo-code SVG: zoom surface omitted Transform2 semantics.");
        var zoom = svgViewport.Patterns.Transform2.Pattern;
        AssertProbe(zoom.CanZoom.Value, "repo-code SVG: Transform2 reported that zoom was unavailable.");
        AssertProbe(Math.Abs(zoom.ZoomMinimum.Value - 10) < 0.1, $"repo-code SVG: expected 0.1x minimum zoom, found {zoom.ZoomMinimum.Value / 100:F2}x.");
        AssertProbe(Math.Abs(zoom.ZoomMaximum.Value - 800) < 0.1, $"repo-code SVG: expected 8x maximum zoom, found {zoom.ZoomMaximum.Value / 100:F2}x.");

        VerifyRepoCodeSvgZoom(window, svgViewport, svgScrollViewport, zoom, 100, "repo-code-svg-zoom-1x.png", options.OutputDirectory);
        VerifyRepoCodeSvgZoom(window, svgViewport, svgScrollViewport, zoom, 800, "repo-code-svg-zoom-8x.png", options.OutputDirectory);
        VerifyRepoCodeSvgZoom(window, svgViewport, svgScrollViewport, zoom, 10, "repo-code-svg-zoom-0.1x.png", options.OutputDirectory);
        VerifyRepoCodeSvgZoom(window, svgViewport, svgScrollViewport, zoom, 100, "repo-code-svg-zoom-restored-1x.png", options.OutputDirectory);

        ResizeWindow(window, 640, 600);
        Thread.Sleep(420);
        AssertProbe(IsVisible(svgViewport), "repo-code SVG: viewport disappeared at 640px.");
        AssertProbe(IsInsideWindowBounds(svgViewport, window), "repo-code SVG: compact viewport escaped the 640px window.");
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-code-svg-640x600.png"));
    }

    internal static void RunRepoCodeContentSurfacesProbe(CaptureOptions options)
    {
        KillExistingApplicationInstances(options.AppPath);
        using var app = LaunchApplication(
            options.AppPath,
            "--page=repo-code",
            "--theme=dark",
            $"--repo={options.RepositoryFullName}");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "repo-code-content-surfaces probe");
            ExerciseRepoCodeContentSurfaces(window, options);
            Console.WriteLine($"repo-code-content-surfaces probe: CSV and SVG behaviors passed; output={options.OutputDirectory}");
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }

    private static void SelectSegmentedItem(Window window, string automationId, string accessibleName)
    {
        AutomationElement item = WaitForElement(
            accessibleName,
            () => window.FindAllDescendants(cf => cf.ByAutomationId(automationId)).FirstOrDefault(IsVisible),
            TimeSpan.FromSeconds(5));
        AssertProbe(item.Patterns.SelectionItem.IsSupported, $"{accessibleName} omitted SelectionItem semantics.");
        item.Patterns.SelectionItem.Pattern.Select();
        WaitUntil(
            $"{accessibleName} selected",
            () => item.Patterns.SelectionItem.Pattern.IsSelected.Value,
            TimeSpan.FromSeconds(5));
    }

    private static void VerifyRepoCodeSvgZoom(
        Window window,
        AutomationElement svgViewport,
        AutomationElement svgScrollViewport,
        FlaUI.Core.Patterns.ITransform2Pattern zoom,
        double percent,
        string artifactName,
        string outputDirectory)
    {
        zoom.Zoom(percent);
        WaitUntil(
            $"SVG zoom settles at {percent / 100:F1}x",
            () => Math.Abs(zoom.ZoomLevel.Value - percent) < 0.1,
            TimeSpan.FromSeconds(5));
        Thread.Sleep(520);
        AssertProbe(
            (svgViewport.Properties.ItemStatus.ValueOrDefault ?? string.Empty)
                .StartsWith("rendered:tiles:", StringComparison.Ordinal),
            $"repo-code SVG: render did not recover after zooming to {percent / 100:F1}x.");
        AssertProbe(
            !window.FindAllDescendants(cf => cf.ByName("Unable to render this SVG."))
                .Any(IsVisible),
            $"repo-code SVG: error state appeared after zooming to {percent / 100:F1}x.");
        string artifactPath = Path.Combine(outputDirectory, artifactName);
        CaptureWindow(window, artifactPath);
        AssertSvgViewportContainsRenderedColor(window, svgScrollViewport, artifactPath, percent);
    }

    private static void AssertSvgViewportContainsRenderedColor(
        Window window,
        AutomationElement svgViewport,
        string screenshotPath,
        double percent)
    {
        Rectangle captureBounds = NativeMethods.GetPhysicalWindowBounds(GetNativeWindowHandle(window));
        Rectangle viewportBounds = svgViewport.BoundingRectangle;
        using var screenshot = new Bitmap(screenshotPath);

        int left = Math.Clamp(viewportBounds.Left - captureBounds.Left, 0, screenshot.Width);
        int top = Math.Clamp(viewportBounds.Top - captureBounds.Top, 0, screenshot.Height);
        int right = Math.Clamp(viewportBounds.Right - captureBounds.Left, 0, screenshot.Width);
        int bottom = Math.Clamp(viewportBounds.Bottom - captureBounds.Top, 0, screenshot.Height);
        AssertProbe(right > left && bottom > top, "repo-code SVG: viewport had no capturable pixel area.");

        const int sampleStep = 2;
        int colorfulPixels = 0;
        int sampledPixels = 0;
        for (int y = top; y < bottom; y += sampleStep)
        {
            for (int x = left; x < right; x += sampleStep)
            {
                Color pixel = screenshot.GetPixel(x, y);
                int maximum = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
                int minimum = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
                if (maximum >= 96 && maximum - minimum >= 32)
                {
                    colorfulPixels++;
                }

                sampledPixels++;
            }
        }

        int requiredPixels = Math.Max(32, sampledPixels / 10_000);
        AssertProbe(
            colorfulPixels >= requiredPixels,
            $"repo-code SVG: the {percent / 100:F1}x viewport was blank or framed outside the rendered fixture " +
            $"({colorfulPixels} colorful pixels; required {requiredPixels}).");
    }
}
