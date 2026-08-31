using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class GistsWorkspaceContractTests
{
    [Fact]
    public void Workspace_UsesAdaptiveFixedPanesAndNoPageScrollViewer()
    {
        XDocument document = XDocument.Load(GistsXamlPath());
        XElement workspace = Assert.Single(document.Descendants(), static element => element.Name.LocalName == "AdaptiveWorkspace");

        Assert.Equal("344", workspace.Attribute("LeadingPaneWidth")?.Value);
        Assert.Contains(document.Descendants(), static element => element.Name.LocalName == "AdaptiveWorkspace.LeadingPane");
        Assert.Contains(document.Descendants(), static element => element.Name.LocalName == "AdaptiveWorkspace.PrimaryPane");
        Assert.DoesNotContain(document.Root!.Elements(), static element => element.Name.LocalName == "ScrollViewer");
    }

    [Fact]
    public void InteractiveControls_HaveStableAccessibleAutomationIdentity()
    {
        XDocument document = XDocument.Load(GistsXamlPath());
        HashSet<string> required =
        [
            "GistsNew",
            "GistsSearch",
            "GistsVisibilityFilter",
            "GistsSort",
            "GistsList",
            "GistsEdit",
            "GistsCopyLink",
            "GistsShare",
            "GistsDelete",
            "GistsFilePicker",
            "GistsCopyFile",
            "GistsSaveFile",
            "GistsFilePreview"
        ];
        const string IdAttributeName = "AutomationProperties.AutomationId";
        const string NameAttributeName = "AutomationProperties.Name";
        Dictionary<string, XElement> controls = document.Descendants()
            .Where(element => element.Attribute(IdAttributeName) is not null)
            .ToDictionary(element => element.Attribute(IdAttributeName)!.Value, StringComparer.Ordinal);

        foreach (string id in required)
        {
            Assert.True(controls.TryGetValue(id, out XElement? control), $"Missing Gists automation id {id}.");
            Assert.False(string.IsNullOrWhiteSpace(control!.Attribute(NameAttributeName)?.Value), $"{id} has no accessible name.");
        }

        Assert.Equal(controls.Count, controls.Keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DurabilityDegradation_UsesASeparateNonBlockingStatusSurface()
    {
        XDocument document = XDocument.Load(GistsXamlPath());
        XElement warning = Assert.Single(document.Descendants(), static element =>
            element.Attribute("AutomationProperties.AutomationId")?.Value == "GistsDurabilityStatus");

        Assert.Equal("Warning", warning.Attribute("Severity")?.Value);
        Assert.Contains("IsDurabilityWarningVisible", warning.Attribute("IsOpen")?.Value, StringComparison.Ordinal);
        Assert.Contains("DurabilityWarningMessage", warning.Attribute("Message")?.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewModel_ProjectsOffThreadAndOnlyClearsAtAccountBoundary()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "GistsPageViewModel.cs"));

        Assert.Contains("private const int PageSize = 100", source, StringComparison.Ordinal);
        Assert.Contains("GistLibraryProjection.CreateSnapshot", source, StringComparison.Ordinal);
        Assert.Contains("Task.Run", source, StringComparison.Ordinal);
        Assert.Contains("ApplyProjectionBudgetedAsync", source, StringComparison.Ordinal);
        Assert.Contains("GistProjectionApplyPolicy.MaximumOperationsPerSlice", source, StringComparison.Ordinal);
        Assert.Contains("Files.ApplySnapshot", source, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(source, "Gists.Clear();"));
        Assert.Equal(1, CountOccurrences(source, "Files.Clear();"));
        Assert.Contains("ResetForAccountChange", source, StringComparison.Ordinal);
        Assert.Contains("_activeAccountPartition", source, StringComparison.Ordinal);
        Assert.Contains("SynchronizeAllPagesAsync", source, StringComparison.Ordinal);

        string pageSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "GistsPage.xaml.cs"));
        Assert.Contains("ListViewScrollAnchor.Capture", pageSource, StringComparison.Ordinal);
        Assert.Contains("RestoreAfterCollectionChange", pageSource, StringComparison.Ordinal);
        Assert.Contains("VisibleProjectionApplying", pageSource, StringComparison.Ordinal);
        Assert.Contains("VisibleProjectionApplied", pageSource, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorContentStyle_IsStretchingMultilineMonoAndVerticallyScrollable()
    {
        XDocument document = XDocument.Load(GistsXamlPath());
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement style = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "Style" &&
            element.Attribute(x + "Key")?.Value == "GistEditorContentTextBoxStyle");
        Dictionary<string, string> setters = style.Elements()
            .Where(static element => element.Name.LocalName == "Setter")
            .ToDictionary(
                element => element.Attribute("Property")!.Value,
                element => element.Attribute("Value")!.Value,
                StringComparer.Ordinal);

        Assert.Equal("True", setters["AcceptsReturn"]);
        Assert.Equal("Stretch", setters["HorizontalAlignment"]);
        Assert.Equal("Stretch", setters["VerticalAlignment"]);
        Assert.Equal("Top", setters["VerticalContentAlignment"]);
        Assert.Equal("{ThemeResource AppMonoFontFamily}", setters["FontFamily"]);
        Assert.Equal("Auto", setters["ScrollViewer.VerticalScrollBarVisibility"]);
        Assert.Equal("Disabled", setters["ScrollViewer.HorizontalScrollBarVisibility"]);
        Assert.Equal("{ThemeResource AppDimension180}", setters["MinHeight"]);
    }

    [Fact]
    public void EditorLayout_UsesResponsiveDialogBoundsAndCompactStacking()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "GistsPage.xaml.cs"));
        string normalizedSource = source.ReplaceLineEndings("\n");

        Assert.Contains("layoutKind: AppDialogLayoutKind.Editor", source, StringComparison.Ordinal);
        Assert.DoesNotContain("editor.Height =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("dialog.Resources[\"ContentDialogMaxWidth\"]", source, StringComparison.Ordinal);
        Assert.Contains("canSubmit: () => session.CanSave", source, StringComparison.Ordinal);
        Assert.Contains("bool shortWindow = XamlRoot.Size.Height < AppResource<double>(\"AppDialogCompactBreakpoint\")", source, StringComparison.Ordinal);
        Assert.Contains("bool compactMetadata = width < AppResource<double>(\"AppGistEditorCompactBreakpoint\") && !shortWindow", source, StringComparison.Ordinal);
        Assert.Contains("bool stackedFiles = width < AppResource<double>(\"AppGistEditorStackedFileBreakpoint\")", source, StringComparison.Ordinal);
        Assert.Contains("GistEditorContentTextBoxStyle", source, StringComparison.Ordinal);
        Assert.Contains("T(\"Gists/Editor/FileContent\", \"File content\")", source, StringComparison.Ordinal);
        Assert.Contains("GistEditorContentLabel", source, StringComparison.Ordinal);
        Assert.Contains("AppDialogScrollableContent dialogContent", source, StringComparison.Ordinal);
        Assert.Contains("GistEditorWorkspace", source, StringComparison.Ordinal);
        Assert.Contains("GistEditorFileRail", source, StringComparison.Ordinal);
        Assert.Contains("AppDialogFileListRowStyle", source, StringComparison.Ordinal);
        Assert.Contains("AppGistEditorFileRailWidth", source, StringComparison.Ordinal);
        Assert.Contains("AppBottomHairlineBorderThickness", source, StringComparison.Ordinal);
        Assert.Contains("CommitCurrentEditorFields();", source, StringComparison.Ordinal);
        Assert.True(
            normalizedSource.IndexOf("CommitCurrentEditorFields();\n            session.AddFile();", StringComparison.Ordinal) >= 0,
            "Add file must commit the displayed draft before changing selection.");
        Assert.True(
            normalizedSource.IndexOf("CommitCurrentEditorFields();\n            session.RemoveSelectedFile();", StringComparison.Ordinal) >= 0,
            "Remove file must commit the displayed draft before changing selection.");
        Assert.Contains("session.CommitDisplayedFile(displayedDraft", source, StringComparison.Ordinal);
        Assert.Contains("!content.IsReadOnly", source, StringComparison.Ordinal);
        Assert.Contains("Header = visibilityText", source, StringComparison.Ordinal);
        Assert.Contains("descriptionText,", source, StringComparison.Ordinal);
        Assert.Contains("Grid.SetRow(visibility, compactMetadata ? 1 : 0)", source, StringComparison.Ordinal);
        Assert.Contains("Grid.SetRow(editorFrame, 1)", source, StringComparison.Ordinal);
        Assert.Contains("content.MinHeight = shortWindow", source, StringComparison.Ordinal);
        Assert.Contains("AppDialogEditorPreferredHeight", source, StringComparison.Ordinal);
        Assert.Contains("AppDimension120", source, StringComparison.Ordinal);
        Assert.Contains("AppDimension180", source, StringComparison.Ordinal);
        Assert.Contains("AppGistEditorCompactContentMinHeight", source, StringComparison.Ordinal);
        Assert.Contains("AppGistEditorVisibilityWidth", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Height = 420", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorRequests_AreIgnoredUntilTheNativeDialogFinishesClosing()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "GistsPage.xaml.cs"));

        Assert.True(
            CountOccurrences(source, "if (_activeDialog is not null)") >= 2,
            "New and Edit must both reject duplicate requests while the native dialog is active.");
        Assert.Contains("ReferenceEquals(_activeDialog, dialog)", source, StringComparison.Ordinal);
        Assert.Contains("_activeDialog = null", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LargeFileContent_IsCappedBeforeAnyPreviewOrEditorTextBoxAssignment()
    {
        string viewModelSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "GistsPageViewModel.cs"));
        string pageSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "GistsPage.xaml.cs"));

        Assert.Contains("GistFileRenderPolicy.Create(GistFileContentPolicy.GetPreviewText(file))", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("GistFileRenderPolicy.Create(selected?.Content)", pageSource, StringComparison.Ordinal);
        Assert.Contains("content.MaxLength = GistFileRenderPolicy.MaximumPreviewCharacters", pageSource, StringComparison.Ordinal);
        Assert.Contains("content.Text = renderModel.PreviewText", pageSource, StringComparison.Ordinal);
        Assert.DoesNotContain("content.Text = selected?.Content", pageSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewModel_TracksAndDrainsBackgroundWorkBeforeTokenDisposal()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "GistsPageViewModel.cs"));
        string pageSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "GistsPage.xaml.cs"));
        string compactSource = string.Concat(source.Where(static character => !char.IsWhiteSpace(character)));

        Assert.Contains(
            "TrackBackgroundTask(SynchronizeAllPagesAsync(accessToken,userId,generation,followsCommittedMutation,committedMutationDurabilityDegraded,source.Token),source);",
            compactSource,
            StringComparison.Ordinal);
        Assert.Contains("TrackBackgroundTask(LoadSelectedDetailAsync", source, StringComparison.Ordinal);
        Assert.Contains("TrackBackgroundTask(ApplySearchAfterDelayAsync", source, StringComparison.Ordinal);
        Assert.Contains("TrackBackgroundTask(operationTask, source)", source, StringComparison.Ordinal);
        Assert.Contains("public async Task StopAsync", source, StringComparison.Ordinal);
        Assert.Contains("await _queryService.DrainBackgroundWorkAsync", source, StringComparison.Ordinal);
        Assert.Contains("DisposeCancellationTokenSources();", source, StringComparison.Ordinal);
        Assert.Contains("_stopTask = StopAfterAsync(priorStop, pageCancellationTokenSource);", pageSource, StringComparison.Ordinal);
        Assert.Contains("await AwaitLatestStopAsync();", pageSource, StringComparison.Ordinal);
        Assert.Contains("saveAsync(session, cancellationToken)", pageSource, StringComparison.Ordinal);
        Assert.Contains("DeleteSelectedGistAsync(pageCancellationTokenSource.Token)", pageSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_activeDialog?.Hide()", pageSource, StringComparison.Ordinal);
        Assert.DoesNotContain("saveAsync(session, default)", pageSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = SynchronizeAllPagesAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Telemetry_UsesSanitizedDurationBucketsForRequiredGistPhases()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "GistsPageViewModel.cs"));

        Assert.Contains("\"cached_first\"", source, StringComparison.Ordinal);
        Assert.Contains("\"reconciliation\"", source, StringComparison.Ordinal);
        Assert.Contains("TrackAction(\"detail_selection\"", source, StringComparison.Ordinal);
        Assert.Contains("RunOwnedMutationAsync", source, StringComparison.Ordinal);
        Assert.Contains("TelemetrySanitizer.CreateDurationBucket", source, StringComparison.Ordinal);
        Assert.Contains("TrackCopyFileSuccess() => TrackAction(\"copy_file\", \"success\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_UseNativeContextFlyoutForPointerAndKeyboardInvocation()
    {
        string xaml = File.ReadAllText(GistsXamlPath());
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "GistsPage.xaml.cs"));

        Assert.DoesNotContain("RightTapped=", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("GistRow_RightTapped", source, StringComparison.Ordinal);
        Assert.Contains("container.ContextFlyout = CreateGistRowContextFlyout(item)", source, StringComparison.Ordinal);
        Assert.Contains("menu.Opening += (_, _) => ViewModel.SelectedGistItem = item", source, StringComparison.Ordinal);
        Assert.Contains("CreateMenuItem(\"CopyLink\", T(\"Common/CopyLink\", \"Copy link\")", source, StringComparison.Ordinal);
        Assert.Contains("$\"GistsContext{automationSuffix}\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GistsContext{SanitizeAutomationId(text)}", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_UsesDistinctGistsGlyph()
    {
        string shell = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "ShellPageViewModel.cs"));

        Assert.Contains(
            "new(\"gists\", ShellNavigationText(\"Gists\", \"Gists\"), \"\\uE943\"",
            shell,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new(\"gists\", ShellNavigationText(\"Gists\", \"Gists\"), \"\\uE8A5\"",
            shell,
            StringComparison.Ordinal);
    }

    private static string GistsXamlPath() => Path.Combine(
        FindRepositoryRoot(),
        "JitHub.WinUI",
        "Views",
        "Pages",
        "GistsPage.xaml");

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) || Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
