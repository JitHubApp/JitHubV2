using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Native;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Elements.FocusQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Interaction.KeyboardInput;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowActivation;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Gists;

/// <summary>
/// Drives the gists-workspace and gists-crud probes.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because it holds RunGistsWorkspaceProbe, a single 589-line ordered script
/// that drives one app instance through the gist editor matrix and then the create, share, edit, reopen and delete
/// flows; the comment on that method states why it stays whole.
/// </remarks>
internal static class GistsWorkspaceProbe
{
    // This method stays whole because it is one ordered live-UI script on a single app instance that runs the
    // responsive editor matrix, filters, then creates, shares, edits, reopens and deletes the same Secret Gist before a
    // Public one, each step depending on the list, editor and clipboard state the previous step left.
    internal static void RunGistsWorkspaceProbe(CaptureOptions options, bool skipResponsiveMatrix = false)
    {
        KillExistingApplicationInstances(options.AppPath);
        using var app = LaunchApplication(options.AppPath, "--page=gists", "--theme=dark");
        using var automation = new UIA3Automation();
        Exception? probeFailure = null;
        try
        {
            Window window = GetReadyWindow(app, automation, "gists-workspace probe");
            ResizeWindow(window, 1366, 900);
            AutomationElement gistList = AssertNamedAutomationElement(window, "GistsList", ControlType.List);
            _ = AssertNamedAutomationElement(window, "GistsSearch", ControlType.Edit);
            _ = AssertNamedAutomationElement(window, "GistsVisibilityFilter", ControlType.ComboBox);
            _ = AssertNamedAutomationElement(window, "GistsSort", ControlType.ComboBox);
            _ = AssertNamedAutomationElement(window, "GistsNew", ControlType.Button);
            AutomationElement firstRow = WaitForElement(
                "first Gist row",
                () => gistList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
                TimeSpan.FromSeconds(15));
            AssertProbe(!string.IsNullOrWhiteSpace(firstRow.Name), "Gist rows must expose a useful accessible name.");
            AssertProbe(
                !firstRow.Name.Contains("GistViewItem", StringComparison.Ordinal),
                "Gist rows must not expose a CLR model name.");
            AssertProbe(
                !firstRow.Name.Contains("Updated Updated", StringComparison.OrdinalIgnoreCase),
                "Gist row repeated the relative-time label in its accessible name.");
            InvokeOrClick(firstRow);
            WaitForElement(
                "Gist detail title",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsDetailTitle")),
                TimeSpan.FromSeconds(8));

            (int Width, int Height)[] sizes = skipResponsiveMatrix
                ? []
                :
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
                int actualWidth = resizedBounds.Width;
                string viewportLabel = GetResponsiveViewportLabel(width, height, resizedBounds);
                Thread.Sleep(350);
                AutomationElement? listButton = window.FindFirstDescendant(cf => cf.ByAutomationId("GistsLeadingPaneButton"));
                bool libraryDrawerOpened = false;
                if (actualWidth <= 640)
                {
                    AssertProbe(IsVisible(listButton), $"Compact Gists omitted its library drawer button at {viewportLabel}.");
                    AssertProbe(IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("GistsDetailTitle"))), "Compact Gists did not preserve detail as the primary pane.");
                    InvokeOrClick(listButton!);
                    libraryDrawerOpened = true;
                    WaitUntil(
                        "Gists compact library drawer",
                        () => IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("GistsSearch"))),
                        TimeSpan.FromSeconds(5));
                }

                CaptureGistsWindowWithRetry(window, Path.Combine(options.OutputDirectory, $"gists-responsive-{viewportLabel}.png"));
                AutomationElement? responsiveNewGist = window.FindFirstDescendant(cf => cf.ByAutomationId("GistsNew"));
                if (!IsVisible(responsiveNewGist))
                {
                    AssertProbe(IsVisible(listButton), $"Gists library and its drawer command were both unavailable at {viewportLabel}.");
                    InvokeOrClick(listButton!);
                    libraryDrawerOpened = true;
                    responsiveNewGist = WaitForElement(
                        $"GistsNew in library drawer at {viewportLabel}",
                        () =>
                        {
                            AutomationElement? candidate = window.FindFirstDescendant(cf => cf.ByAutomationId("GistsNew"));
                            return IsVisible(candidate) ? candidate : null;
                        },
                        TimeSpan.FromSeconds(5));
                }

                AssertProbe(IsVisible(responsiveNewGist), $"New Gist was not reachable at {viewportLabel}.");
                responsiveNewGist = WaitForElement(
                    $"live GistsNew at {viewportLabel}",
                    () =>
                    {
                        AutomationElement? candidate = FindCurrentVisibleByAutomationId(window, "GistsNew");
                        return candidate is { IsEnabled: true } ? candidate : null;
                    },
                    TimeSpan.FromSeconds(5));
                InvokeOrClick(responsiveNewGist);
                AutomationElement responsiveVisibility = WaitForElement(
                    $"GistEditorVisibility at {viewportLabel}",
                    () => FindElementInWindowOrDialog(window, automation, "GistEditorVisibility"),
                    TimeSpan.FromSeconds(5));
                AutomationElement responsiveContentLabel = WaitForElement(
                    $"GistEditorContentLabel at {viewportLabel}",
                    () => FindElementInWindowOrDialog(window, automation, "GistEditorContentLabel"),
                    TimeSpan.FromSeconds(5));
                AssertProbe(IsVisible(responsiveVisibility), $"Gist visibility was not visible at {viewportLabel}.");
                AssertProbe(responsiveVisibility.Properties.IsEnabled.ValueOrDefault, $"Gist visibility was not clickable at {viewportLabel}.");
                AssertProbe(IsInsideWindowBounds(responsiveVisibility, window), $"Gist visibility was clipped at {viewportLabel}.");
                AssertProbe(IsVisible(responsiveContentLabel), $"The persistent File content label was not visible at {viewportLabel}.");
                AssertProbe(IsInsideWindowBounds(responsiveContentLabel, window), $"The File content label was clipped at {viewportLabel}.");
                InvokeOrClick(responsiveVisibility);
                responsiveVisibility = WaitForElement(
                    $"live GistEditorVisibility after toggle at {viewportLabel}",
                    () => FindElementInWindowOrDialog(window, automation, "GistEditorVisibility") is { } candidate && IsVisible(candidate)
                        ? candidate
                        : null,
                    TimeSpan.FromSeconds(5));
                InvokeOrClick(responsiveVisibility);
                CaptureGistsWindowWithRetry(window, Path.Combine(options.OutputDirectory, $"gists-editor-{viewportLabel}.png"), includePopups: true);
                AutomationElement cancelEditor = WaitForElement(
                    $"Cancel Gist editor at {viewportLabel}",
                    () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Cancel").And(cf.ByControlType(ControlType.Button))),
                    TimeSpan.FromSeconds(5));
                InvokeOrClick(cancelEditor);
                WaitUntil(
                    $"Gist editor closes at {viewportLabel}",
                    () => !IsVisible(FindElementInWindowOrDialog(window, automation, "GistEditorDialog")),
                    TimeSpan.FromSeconds(8));
                if (libraryDrawerOpened)
                {
                    Keyboard.Press(VirtualKeyShort.ESCAPE);
                    Thread.Sleep(250);
                }
            }

            ResizeWindow(window, 1366, 900);
            Thread.Sleep(500);
            gistList = WaitForElement(
                "live GistsList after responsive reparenting",
                () => FindCurrentVisibleByAutomationId(window, "GistsList"),
                TimeSpan.FromSeconds(5));
            AutomationElement search = WaitForElement(
                "GistsSearch",
                () => FindCurrentVisibleByAutomationId(window, "GistsSearch"),
                TimeSpan.FromSeconds(5));
            search.AsTextBox().Text = "workflow note 121";
            WaitUntil(
                "filtered Gist row",
                () => gistList.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Length == 1,
                TimeSpan.FromSeconds(5));
            CaptureGistsWindowWithRetry(window, Path.Combine(options.OutputDirectory, "gists-search-filtered.png"));
            search.AsTextBox().Text = string.Empty;
            Thread.Sleep(400);

            AutomationElement visibility = WaitForElement(
                "GistsVisibilityFilter",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsVisibilityFilter")),
                TimeSpan.FromSeconds(5));
            visibility.AsComboBox().Select(2);
            Thread.Sleep(400);
            firstRow = WaitForElement(
                "secret Gist row",
                () => gistList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
                TimeSpan.FromSeconds(5));
            AssertProbe(firstRow.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase), "Secret filter returned a row without a Secret accessible state.");

            AutomationElement sort = WaitForElement(
                "GistsSort",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsSort")),
                TimeSpan.FromSeconds(5));
            sort.AsComboBox().Select(3);
            Thread.Sleep(350);
            CaptureGistsWindowWithRetry(window, Path.Combine(options.OutputDirectory, "gists-filter-sort.png"));
            visibility = WaitForElement(
                "current GistsVisibilityFilter",
                () => FindCurrentVisibleByAutomationId(window, "GistsVisibilityFilter"),
                TimeSpan.FromSeconds(5));
            visibility.AsComboBox().Select(0);

            AutomationElement newGist = WaitForElement(
                "GistsNew",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsNew")),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(newGist);
            const string firstFilename = "native.cs";
            const string firstFileContent = "public static bool Ready => true;";
            const string secondFilename = "second.txt";
            const string secondFileContent = "second file persisted across create and reopen";
            const string renamedSecondFilename = "renamed-second.txt";
            const string updatedSecondFileContent = "second file updated and preserved in full";
            AutomationElement description = WaitForElement(
                "GistEditorDescription",
                () => FindElementInWindowOrDialog(window, automation, "GistEditorDescription"),
                TimeSpan.FromSeconds(5));
            description.AsTextBox().Text = "Automation native gist";
            AutomationElement filename = WaitForElement(
                "GistEditorFilename",
                () => FindElementInWindowOrDialog(window, automation, "GistEditorFilename"),
                TimeSpan.FromSeconds(5));
            filename.AsTextBox().Text = firstFilename;
            AutomationElement content = WaitForElement(
                "GistEditorContent",
                () => FindElementInWindowOrDialog(window, automation, "GistEditorContent"),
                TimeSpan.FromSeconds(5));
            content.AsTextBox().Text = firstFileContent;
            AutomationElement editorFiles = WaitForElement(
                "GistEditorFiles",
                () => FindElementInWindowOrDialog(window, automation, "GistEditorFiles"),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(WaitForElement(
                "GistEditorAddFile",
                () => FindElementInWindowOrDialog(window, automation, "GistEditorAddFile"),
                TimeSpan.FromSeconds(5)));
            WaitUntil(
                "second Gist editor file",
                () => editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Length == 2,
                TimeSpan.FromSeconds(5));
            AutomationElement secondCreatedRow = editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .First(item => !item.Name.Contains(firstFilename, StringComparison.OrdinalIgnoreCase));
            AssertProbe(
                secondCreatedRow.Patterns.SelectionItem.IsSupported && secondCreatedRow.Patterns.SelectionItem.Pattern.IsSelected.Value,
                "Adding a second Gist file did not select it for immediate editing.");
            filename.AsTextBox().Text = secondFilename;
            content.AsTextBox().Text = secondFileContent;

            secondCreatedRow = editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .First(item =>
                    item.Patterns.SelectionItem.IsSupported &&
                    item.Patterns.SelectionItem.Pattern.IsSelected.Value);
            secondCreatedRow.FocusNative();
            WaitUntil(
                "second Gist editor row keyboard focus",
                () => IsElementFocused(secondCreatedRow),
                TimeSpan.FromSeconds(4));
            PressKeyForWindow(window, VirtualKeyShort.UP);
            AutomationElement firstCreatedRow = editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .First(item => !string.Equals(GetAutomationId(item), GetAutomationId(secondCreatedRow), StringComparison.Ordinal));
            ApplySelectionFallbackForInvisibleForeignForeground(
                window,
                () => string.Equals(filename.AsTextBox().Text, firstFilename, StringComparison.Ordinal),
                firstCreatedRow,
                "first Gist editor file");
            WaitUntil(
                "keyboard selects first Gist file",
                () => string.Equals(filename.AsTextBox().Text, firstFilename, StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
            AssertProbe(
                string.Equals(content.AsTextBox().Text, firstFileContent, StringComparison.Ordinal),
                "Keyboard file traversal did not preserve the first file content.");
            PressKeyForWindow(window, VirtualKeyShort.DOWN);
            ApplySelectionFallbackForInvisibleForeignForeground(
                window,
                () => string.Equals(filename.AsTextBox().Text, secondFilename, StringComparison.Ordinal),
                secondCreatedRow,
                "second Gist editor file");
            WaitUntil(
                "keyboard selects second Gist file",
                () => string.Equals(filename.AsTextBox().Text, secondFilename, StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
            AssertProbe(
                string.Equals(content.AsTextBox().Text, secondFileContent, StringComparison.Ordinal),
                "Keyboard file traversal did not restore the second file content.");
            CaptureGistsWindowWithRetry(window, Path.Combine(options.OutputDirectory, "gists-new-editor.png"), includePopups: true);
            AutomationElement save = WaitForElement(
                "Save gist",
                () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Save").And(cf.ByControlType(ControlType.Button))),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(save);
            WaitUntil(
                "Gist editor closes after create",
                () => !IsVisible(FindElementInWindowOrDialog(window, automation, "GistEditorDescription")),
                TimeSpan.FromSeconds(7));

            search = WaitForElement("GistsSearch after create", () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsSearch")), TimeSpan.FromSeconds(5));
            search.AsTextBox().Text = "Automation native gist";
            firstRow = WaitForElement(
                "created Gist row",
                () => gistList.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                    .FirstOrDefault(item => item.Name.Contains("Automation native gist", StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(5));
            AssertProbe(firstRow.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase), "Default Gist creation did not produce a Secret Gist row.");
            InvokeOrClick(firstRow);
            const string gistClipboardSentinel = "__jithub_gist_copy_pending__";
            NativeMethods.SetClipboardText(gistClipboardSentinel);
            InvokeOrClick(WaitForElement("GistsCopyLink", () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsCopyLink")), TimeSpan.FromSeconds(5)));
            string copiedGistLink = string.Empty;
            WaitUntil(
                "Gist link copied to clipboard",
                () =>
                {
                    copiedGistLink = NativeMethods.GetClipboardText();
                    return Uri.TryCreate(copiedGistLink, UriKind.Absolute, out Uri? copiedUri) &&
                        copiedUri.Scheme == Uri.UriSchemeHttps &&
                        copiedUri.Host.Equals("gist.github.com", StringComparison.OrdinalIgnoreCase);
                },
                TimeSpan.FromSeconds(5));
            AssertProbe(
                Uri.TryCreate(copiedGistLink, UriKind.Absolute, out Uri? copiedGistUri) &&
                copiedGistUri.Scheme == Uri.UriSchemeHttps &&
                copiedGistUri.Host.Equals("gist.github.com", StringComparison.OrdinalIgnoreCase),
                "Copy Gist link did not place a trusted gist.github.com URL on the clipboard.");

            InvokeOrClick(WaitForElement("GistsShare", () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsShare")), TimeSpan.FromSeconds(5)));
            Thread.Sleep(600);
            AssertProbe(!app.HasExited, "Invoking Windows Share terminated the Gists workspace.");
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            TryActivateWindow(window);
            Thread.Sleep(250);
            CaptureGistsWindowWithRetry(window, Path.Combine(options.OutputDirectory, "gists-share-returned.png"));

            firstRow = WaitForElement(
                "live created Gist row after Windows Share",
                () => gistList.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                    .FirstOrDefault(item =>
                        IsVisible(item) &&
                        item.Name.Contains("Automation native gist", StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(5));
            firstRow.FocusNative();
            WaitUntil(
                "created Gist row keyboard focus after Windows Share",
                () => IsElementFocused(firstRow),
                TimeSpan.FromSeconds(4));
            string contextRowAutomationId = GetAutomationId(firstRow);
            using (Keyboard.Pressing(VirtualKeyShort.LSHIFT))
            {
                Keyboard.Press(VirtualKeyShort.F10);
            }
            AutomationElement? contextEdit = null;
            bool keyboardContextOpened = WaitUntilAvailable(
                () =>
                {
                    contextEdit = automation.GetDesktop().FindAllDescendants(cf => cf.ByAutomationId("GistsContextEdit"))
                        .FirstOrDefault(IsVisible);
                    return contextEdit is not null;
                },
                TimeSpan.FromSeconds(2));
            if (!keyboardContextOpened && NativeMethods.HasInvisibleForeignForegroundWindow(
                    window.Properties.NativeWindowHandle.ValueOrDefault))
            {
                firstRow.Click();
                Thread.Sleep(150);
                firstRow = WaitForElement(
                    "live created Gist row after activation click",
                    () => FindCurrentVisibleByAutomationId(window, contextRowAutomationId),
                    TimeSpan.FromSeconds(5));
                firstRow.RightClick();
                keyboardContextOpened = WaitUntilAvailable(
                    () =>
                    {
                        contextEdit = automation.GetDesktop().FindAllDescendants(cf => cf.ByAutomationId("GistsContextEdit"))
                            .FirstOrDefault(IsVisible);
                        return contextEdit is not null;
                    },
                    TimeSpan.FromSeconds(2));
            }

            if (keyboardContextOpened)
            {
                contextEdit = WaitForElement(
                    "Gist row keyboard context Edit",
                    () => automation.GetDesktop().FindAllDescendants(cf => cf.ByAutomationId("GistsContextEdit"))
                        .FirstOrDefault(IsVisible),
                    TimeSpan.FromSeconds(5));
                CaptureGistsWindowWithRetry(
                    window,
                    Path.Combine(options.OutputDirectory, "gists-keyboard-context-menu.png"),
                    includePopups: true);
                InvokeOrClick(contextEdit);
            }
            else
            {
                AssertProbe(
                    NativeMethods.HasInvisibleForeignForegroundWindow(window.Properties.NativeWindowHandle.ValueOrDefault),
                    "The Gist row keyboard context menu did not open on a normal foreground desktop.");
                Console.WriteLine("gists-workspace: used the visible Edit action because an invisible foreign window blocked system context gestures.");
                InvokeOrClick(WaitForElement(
                    "GistsEdit fallback",
                    () => FindCurrentVisibleByAutomationId(window, "GistsEdit"),
                    TimeSpan.FromSeconds(5)));
            }
            description = WaitForElement(
                "GistEditorDescription edit",
                () => FindElementInWindowOrDialog(window, automation, "GistEditorDescription"),
                TimeSpan.FromSeconds(5));
            description.AsTextBox().Text = "Automation edited gist";
            editorFiles = WaitForElement(
                "GistEditorFiles edit",
                () => FindElementInWindowOrDialog(window, automation, "GistEditorFiles"),
                TimeSpan.FromSeconds(5));
            WaitUntil(
                "two persisted Gist files before edit",
                () => editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Length == 2,
                TimeSpan.FromSeconds(5));
            AutomationElement secondRow = editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .First(item => item.Name.Contains(secondFilename, StringComparison.OrdinalIgnoreCase));
            AssertProbe(secondRow.Patterns.SelectionItem.IsSupported, "The second Gist editor row omitted native selection semantics.");
            secondRow.Patterns.SelectionItem.Pattern.Select();
            filename = WaitForElement("GistEditorFilename edit", () => FindElementInWindowOrDialog(window, automation, "GistEditorFilename"), TimeSpan.FromSeconds(5));
            content = WaitForElement("GistEditorContent edit", () => FindElementInWindowOrDialog(window, automation, "GistEditorContent"), TimeSpan.FromSeconds(5));
            WaitUntil(
                "second Gist file selected for edit",
                () => string.Equals(filename.AsTextBox().Text, secondFilename, StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
            AssertProbe(
                string.Equals(content.AsTextBox().Text, secondFileContent, StringComparison.Ordinal),
                "The second Gist file did not preserve its full content after create.");
            filename.AsTextBox().Text = renamedSecondFilename;
            content.AsTextBox().Text = updatedSecondFileContent;

            InvokeOrClick(WaitForElement(
                "GistEditorAddFile edit",
                () => FindElementInWindowOrDialog(window, automation, "GistEditorAddFile"),
                TimeSpan.FromSeconds(5)));
            WaitUntil(
                "temporary third Gist file",
                () => editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Length == 3,
                TimeSpan.FromSeconds(5));
            filename.AsTextBox().Text = "remove-me.txt";
            content.AsTextBox().Text = "this temporary file must not survive";
            InvokeOrClick(WaitForElement(
                "GistEditorRemoveFile",
                () => FindElementInWindowOrDialog(window, automation, "GistEditorRemoveFile"),
                TimeSpan.FromSeconds(5)));
            WaitUntil(
                "temporary Gist file removed",
                () => editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Length == 2,
                TimeSpan.FromSeconds(5));
            AssertProbe(
                editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                    .All(item => !item.Name.Contains("remove-me.txt", StringComparison.OrdinalIgnoreCase)),
                "Removed Gist editor file remained in the editor list.");

            secondRow = editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .First(item =>
                    item.Patterns.SelectionItem.IsSupported &&
                    item.Patterns.SelectionItem.Pattern.IsSelected.Value);
            secondRow.FocusNative();
            WaitUntil(
                "edited Gist file row keyboard focus",
                () => IsElementFocused(secondRow),
                TimeSpan.FromSeconds(4));
            PressKeyForWindow(window, VirtualKeyShort.HOME);
            AutomationElement firstEditRow = editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .First(item => item.Name.Contains(firstFilename, StringComparison.OrdinalIgnoreCase));
            ApplySelectionFallbackForInvisibleForeignForeground(
                window,
                () => string.Equals(filename.AsTextBox().Text, firstFilename, StringComparison.Ordinal),
                firstEditRow,
                "first edited Gist file");
            WaitUntil(
                "keyboard returns to first Gist file during edit",
                () => string.Equals(filename.AsTextBox().Text, firstFilename, StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
            AssertProbe(
                string.Equals(content.AsTextBox().Text, firstFileContent, StringComparison.Ordinal),
                "Editing another file changed the first Gist file content.");
            CaptureGistsWindowWithRetry(window, Path.Combine(options.OutputDirectory, "gists-edit-editor.png"), includePopups: true);
            save = WaitForElement(
                "Save edited gist",
                () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Save").And(cf.ByControlType(ControlType.Button))),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(save);
            WaitUntil(
                "Gist editor closes after edit",
                () => !IsVisible(FindElementInWindowOrDialog(window, automation, "GistEditorDescription")),
                TimeSpan.FromSeconds(7));

            search = WaitForElement("GistsSearch after edit", () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsSearch")), TimeSpan.FromSeconds(5));
            search.AsTextBox().Text = "Automation edited gist";
            firstRow = WaitForElement(
                "edited Gist row",
                () => gistList.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                    .FirstOrDefault(item => item.Name.Contains("Automation edited gist", StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(firstRow);

            InvokeOrClick(WaitForElement("GistsEdit reopen", () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsEdit")), TimeSpan.FromSeconds(5)));
            editorFiles = WaitForElement(
                "GistEditorFiles reopen",
                () => FindElementInWindowOrDialog(window, automation, "GistEditorFiles"),
                TimeSpan.FromSeconds(5));
            WaitUntil(
                "two Gist files after reopen",
                () => editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Length == 2,
                TimeSpan.FromSeconds(5));
            AutomationElement[] reopenedRows = editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem));
            AssertProbe(reopenedRows.Any(item => item.Name.Contains(firstFilename, StringComparison.OrdinalIgnoreCase)), "The first Gist file was missing after reopen.");
            AssertProbe(reopenedRows.Any(item => item.Name.Contains(renamedSecondFilename, StringComparison.OrdinalIgnoreCase)), "The renamed second Gist file was missing after reopen.");
            AssertProbe(reopenedRows.All(item => !item.Name.Contains("remove-me.txt", StringComparison.OrdinalIgnoreCase)), "The removed Gist file reappeared after reopen.");
            filename = WaitForElement("GistEditorFilename reopen", () => FindElementInWindowOrDialog(window, automation, "GistEditorFilename"), TimeSpan.FromSeconds(5));
            content = WaitForElement("GistEditorContent reopen", () => FindElementInWindowOrDialog(window, automation, "GistEditorContent"), TimeSpan.FromSeconds(5));
            reopenedRows.First(item => item.Name.Contains(firstFilename, StringComparison.OrdinalIgnoreCase))
                .Patterns.SelectionItem.Pattern.Select();
            WaitUntil("first file after reopen", () => string.Equals(filename.AsTextBox().Text, firstFilename, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
            AssertProbe(string.Equals(content.AsTextBox().Text, firstFileContent, StringComparison.Ordinal), "The first file content was not fully preserved after reopen.");
            reopenedRows.First(item => item.Name.Contains(renamedSecondFilename, StringComparison.OrdinalIgnoreCase))
                .Patterns.SelectionItem.Pattern.Select();
            WaitUntil("renamed second file after reopen", () => string.Equals(filename.AsTextBox().Text, renamedSecondFilename, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
            AssertProbe(string.Equals(content.AsTextBox().Text, updatedSecondFileContent, StringComparison.Ordinal), "The renamed file content was not fully preserved after reopen.");
            save = WaitForElement(
                "Save reopened gist",
                () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Save").And(cf.ByControlType(ControlType.Button))),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(save);
            WaitUntil(
                "Gist editor closes after preservation check",
                () => !IsVisible(FindElementInWindowOrDialog(window, automation, "GistEditorDescription")),
                TimeSpan.FromSeconds(7));

            InvokeOrClick(WaitForElement("GistsDelete", () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsDelete")), TimeSpan.FromSeconds(5)));
            CaptureGistsWindowWithRetry(window, Path.Combine(options.OutputDirectory, "gists-delete-confirmation.png"), includePopups: true);
            AutomationElement confirmDelete = WaitForElement(
                "Confirm gist deletion",
                () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Delete").And(cf.ByControlType(ControlType.Button))),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(confirmDelete);
            WaitUntil(
                "created Secret Gist deleted",
                () => !gistList.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                    .Any(item => item.Name.Contains("Automation edited gist", StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(7));

            search = WaitForElement("GistsSearch after delete", () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsSearch")), TimeSpan.FromSeconds(5));
            search.AsTextBox().Text = string.Empty;
            InvokeOrClick(newGist);
            description = WaitForElement(
                "GistEditorDescription public",
                () => FindElementInWindowOrDialog(window, automation, "GistEditorDescription"),
                TimeSpan.FromSeconds(5));
            description.AsTextBox().Text = "Automation public gist";
            filename = WaitForElement(
                "GistEditorFilename public",
                () => FindElementInWindowOrDialog(window, automation, "GistEditorFilename"),
                TimeSpan.FromSeconds(5));
            filename.AsTextBox().Text = "public.txt";
            content = WaitForElement(
                "GistEditorContent public",
                () => FindElementInWindowOrDialog(window, automation, "GistEditorContent"),
                TimeSpan.FromSeconds(5));
            content.AsTextBox().Text = "public gist content";
            AutomationElement publicToggle = WaitForElement(
                "GistEditorVisibility",
                () => FindElementInWindowOrDialog(window, automation, "GistEditorVisibility"),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(publicToggle);
            save = WaitForElement(
                "Save public gist",
                () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Save").And(cf.ByControlType(ControlType.Button))),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(save);
            WaitUntil(
                "public Gist editor closes",
                () => !IsVisible(FindElementInWindowOrDialog(window, automation, "GistEditorDescription")),
                TimeSpan.FromSeconds(7));
            search = WaitForElement("GistsSearch public", () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsSearch")), TimeSpan.FromSeconds(5));
            search.AsTextBox().Text = "Automation public gist";
            firstRow = WaitForElement(
                "created Public Gist row",
                () => gistList.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                    .FirstOrDefault(item => item.Name.Contains("Automation public gist", StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(5));
            AssertProbe(firstRow.Name.Contains("Public", StringComparison.OrdinalIgnoreCase), "Public Gist creation did not expose a Public row state.");
            InvokeOrClick(firstRow);
            InvokeOrClick(WaitForElement("GistsDelete public", () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsDelete")), TimeSpan.FromSeconds(5)));
            confirmDelete = WaitForElement(
                "Confirm public gist deletion",
                () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Delete").And(cf.ByControlType(ControlType.Button))),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(confirmDelete);
            WaitUntil(
                "created Public Gist deleted",
                () => !gistList.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                    .Any(item => item.Name.Contains("Automation public gist", StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(7));
            Console.WriteLine($"gists-workspace probe: library, detail, filters, two-file CRUD/persistence, keyboard/context paths, actions, and {sizes.Length} responsive/editor captures passed; output={options.OutputDirectory}");
        }
        catch (Exception exception)
        {
            probeFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                TryClose(app);
            }
            catch (Exception closeException) when (probeFailure is not null)
            {
                Console.Error.WriteLine($"Gists cleanup also failed after the primary probe failure: {closeException.Message}");
            }
            finally
            {
                KillExistingApplicationInstances(options.AppPath);
            }
        }
    }

    private static void CaptureGistsWindowWithRetry(Window window, string path, bool includePopups = false)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (!includePopups)
                {
                    NativeMethods.ActivateForKeyboard(new IntPtr(window.Properties.NativeWindowHandle.ValueOrDefault));
                    Thread.Sleep(100);
                }

                if (includePopups)
                {
                    CaptureWindowWithPopups(window, path);
                }
                else
                {
                    CaptureWindow(window, path);
                }

                return;
            }
            catch (COMException ex) when (attempt < 3)
            {
                lastError = ex;
                Thread.Sleep(250 * (attempt + 1));
            }
            catch (InvalidOperationException ex) when (
                attempt < 3
                && ex.Message.StartsWith("Refusing popup capture because the foreground window belongs to process", StringComparison.Ordinal))
            {
                lastError = ex;
                Thread.Sleep(250 * (attempt + 1));
            }
        }

        throw new InvalidOperationException("The Gists window did not stabilize for screenshot capture.", lastError);
    }
}
