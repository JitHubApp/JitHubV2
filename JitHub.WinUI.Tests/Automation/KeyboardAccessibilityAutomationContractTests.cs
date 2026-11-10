using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace JitHub.WinUI.Tests.Automation;

public sealed class KeyboardAccessibilityAutomationContractTests
{
    [Fact]
    public void HarnessExposesOneRepeatableKeyboardAccessibilityMatrixProbe()
    {
        string source = LoadAutomationSource();
        string catalog = LoadProbeCatalog();

        Assert.Contains(
            "new Dictionary<string, Action<CaptureOptions>>(StringComparer.OrdinalIgnoreCase)",
            catalog,
            StringComparison.Ordinal);
        Assert.Contains(
            "{ \"keyboard-accessibility-matrix\", KeyboardAccessibilityMatrixProbe.RunKeyboardAccessibilityMatrixProbe },",
            catalog,
            StringComparison.Ordinal);

        string matrix = ExtractMethod(source, "static void RunKeyboardAccessibilityMatrixProbe", "static void RunKeyboardListTraversalMatrix");
        string[] passes =
        [
            "RunKeyboardListTraversalMatrix(options);",
            "RunKeyboardModeSelectorMatrix(options);",
            "RunKeyboardAdaptiveDrawerMatrix(options);",
            "RunKeyboardContextMenuMatrix(options);",
            "RunKeyboardDialogMatrix(options);",
            "RunKeyboardCommitDiffSearchMatrix(options);",
        ];

        Assert.All(passes, pass => Assert.Contains(pass, matrix, StringComparison.Ordinal));
    }

    [Fact]
    public void MatrixCoversTheRequiredKeyboardInputFamiliesAndFocusReturn()
    {
        string source = ExtractKeyboardMatrixRegion(LoadAutomationSource());
        string[] requiredInput =
        [
            "VirtualKeyShort.TAB",
            "VirtualKeyShort.LSHIFT",
            "VirtualKeyShort.DOWN",
            "VirtualKeyShort.UP",
            "VirtualKeyShort.SPACE",
            "VirtualKeyShort.ENTER",
            "VirtualKeyShort.ESCAPE",
            "VirtualKeyShort.F10",
        ];
        Assert.All(requiredInput, input => Assert.Contains(input, source, StringComparison.Ordinal));

        string[] focusAssertions =
        [
            "Shift+Tab returns to issue scope",
            "AssertDrawerKeyboardFocusContained",
            "AssertPaneFocusReturned",
            "Gist context menu restores row focus",
            "Sign-out dialog restores opener focus",
            "Shift+Tab returns to diff search",
        ];
        Assert.All(focusAssertions, assertion => Assert.Contains(assertion, source, StringComparison.Ordinal));
    }

    [Fact]
    public void MatrixExercisesNativeSemanticsAcrossEveryAuditedSurface()
    {
        string source = ExtractKeyboardMatrixRegion(LoadAutomationSource());
        Dictionary<string, string[]> contracts = new(StringComparer.Ordinal)
        {
            ["list traversal"] =
            [
                "MyIssuesList",
                "ControlType.ListItem",
                "Patterns.SelectionItem",
                "Properties.IsKeyboardFocusable",
            ],
            ["mode selectors"] =
            [
                "ProfileModeOverviewItem",
                "ProfileModeRepositoriesItem",
                "ProfileModeActivityItem",
                "ProfileModeReadmeItem",
                "ProfileReadmeScrollViewer",
            ],
            ["adaptive drawers"] =
            [
                "MyIssuesAdaptiveWorkspace",
                "FindAdaptivePaneButton",
                "WaitForDrawerSettled",
            ],
            ["context menus"] =
            [
                "GistsContextEdit",
                "GistsContextCopyLink",
                "gist.github.com",
            ],
            ["dialogs"] =
            [
                "SettingsSignOutButton",
                "SignOutConfirmationDialog",
                "AssertDialogFocusContained",
            ],
            ["commit diff search"] =
            [
                "RepoCommitsDiffSearchBox",
                "RepoCommitsDiffSearchMatchCount",
                "RepoCommitsPreviousDiffMatchButton",
                "RepoCommitsNextDiffMatchButton",
            ],
        };

        foreach ((string surface, string[] markers) in contracts)
        {
            Assert.All(
                markers,
                marker => Assert.True(
                    source.Contains(marker, StringComparison.Ordinal),
                    $"The keyboard matrix omitted {surface} marker '{marker}'."));
        }
    }

    [Fact]
    public void ContextMenuCoverageUsesWindowsKeyboardGestureAndShipsDedicatedKeyHandling()
    {
        string source = LoadAutomationSource();
        string contextMenu = ExtractMethod(
            source,
            "static void RunKeyboardContextMenuMatrix",
            "static void RunKeyboardDialogMatrix");

        Assert.Contains("VirtualKeyShort.LSHIFT", contextMenu, StringComparison.Ordinal);
        Assert.Contains("VirtualKeyShort.F10", contextMenu, StringComparison.Ordinal);
        Assert.Contains("FocusForKeyboardActivation(window, copy);", contextMenu, StringComparison.Ordinal);
        Assert.Contains("Keyboard.Type([VirtualKeyShort.ENTER]);", contextMenu, StringComparison.Ordinal);
        Assert.DoesNotContain("Mouse.", contextMenu, StringComparison.Ordinal);
        Assert.DoesNotContain("InvokeOrClick", contextMenu, StringComparison.Ordinal);

        string root = FindRepositoryRoot();
        string gistsXaml = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "GistsPage.xaml"));
        string gistsCodeBehind = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "GistsPage.xaml.cs"));
        Assert.Contains("KeyDown=\"GistsList_KeyDown\"", gistsXaml, StringComparison.Ordinal);
        Assert.Contains("const int applicationKey = 0x5D;", gistsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("flyout.ShowAt(container);", gistsCodeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryKeyboardPassOwnsAndCleansUpItsPreviewProcess()
    {
        string source = LoadAutomationSource();
        string[] methods =
        [
            "RunKeyboardListTraversalMatrix",
            "RunKeyboardModeSelectorMatrix",
            "RunKeyboardAdaptiveDrawerMatrix",
            "RunKeyboardContextMenuMatrix",
            "RunKeyboardDialogMatrix",
            "RunKeyboardCommitDiffSearchMatrix",
        ];

        for (int index = 0; index < methods.Length; index++)
        {
            string? nextMarker = index + 1 < methods.Length
                ? $"static void {methods[index + 1]}"
                : null;
            string method = ExtractMethod(source, $"static void {methods[index]}", nextMarker);
            Assert.Contains("using var app = LaunchApplication", method, StringComparison.Ordinal);
            Assert.Contains("finally", method, StringComparison.Ordinal);
            Assert.Contains("TryClose(app);", method, StringComparison.Ordinal);
            Assert.Contains("KillExistingApplicationInstances(options.AppPath);", method, StringComparison.Ordinal);
        }
    }

    private static string ExtractKeyboardMatrixRegion(string source) => ExtractMethod(
        source,
        "static void RunKeyboardAccessibilityMatrixProbe",
        endMarker: null);

    /// <summary>
    /// The source from <paramref name="startMarker"/> up to
    /// <paramref name="endMarker"/>, or to the end of the matrix source when the
    /// method is the last one in it.
    /// </summary>
    private static string ExtractMethod(string source, string startMarker, string? endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{startMarker}'.");
        if (endMarker is null)
        {
            return source[start..];
        }

        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find '{endMarker}' after '{startMarker}'.");
        return source[start..end];
    }

    /// <summary>
    /// The keyboard accessibility matrix: every class in its folder of the
    /// automation project, in file name order.
    /// </summary>
    private static string LoadAutomationSource() => string.Concat(Directory
        .EnumerateFiles(Path.Combine(FindRepositoryRoot(), "JitHub.WinUI.Automation", "Probes", "KeyboardAccessibility"), "*.cs")
        .Order(StringComparer.Ordinal)
        .Select(File.ReadAllText));

    private static string LoadProbeCatalog() => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "JitHub.WinUI.Automation",
        "Probes",
        "ProbeCatalog.cs"));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
