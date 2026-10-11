using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class SameByteSourceAdmissionOrderingContractTests
{
    [Fact]
    public void MarkdownAssignmentWaitsForTheRendererLoadPipelineForEveryHost()
    {
        string root = FindRepositoryRoot();
        string viewer = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownViewer.xaml.cs"));
        string renderer = File.ReadAllText(Path.Combine(
            root,
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Controls",
            "MarkdownRendererControl.cs"));

        int ensureStart = viewer.IndexOf("private void EnsureRenderer()", StringComparison.Ordinal);
        int ensureEnd = ensureStart >= 0
            ? viewer.IndexOf("private void EnsureRendererLoadedSettingsHandler(", ensureStart, StringComparison.Ordinal)
            : -1;
        Assert.True(ensureStart >= 0 && ensureEnd > ensureStart);
        string ensureMethod = viewer[ensureStart..ensureEnd];
        int loadSubscription = ensureMethod.LastIndexOf(
            "EnsureRendererLoadedSettingsHandler(_renderer);",
            StringComparison.Ordinal);
        int rendererAttachment = ensureMethod.LastIndexOf(
            "RendererHost.Children.Add(_renderer);",
            StringComparison.Ordinal);
        int rendererConstruction = Math.Max(
            ensureMethod.IndexOf("new MarkdownScrollView().UseGitHubReadme(", StringComparison.Ordinal),
            ensureMethod.IndexOf("new MarkdownDocumentView().UseGitHubReadme(", StringComparison.Ordinal));
        Assert.True(rendererConstruction >= 0 && loadSubscription > rendererConstruction && rendererAttachment > loadSubscription,
            "The one-shot Loaded handler must be installed before attaching a newly created renderer.");
        int loadedHandlerSubscription = viewer.IndexOf(
            "renderer.Loaded += OnRendererLoadedForSettings;",
            StringComparison.Ordinal);
        int loadedHandlerImplementation = viewer.IndexOf(
            "private void OnRendererLoadedForSettings(",
            StringComparison.Ordinal);
        Assert.True(loadedHandlerSubscription >= 0 && loadedHandlerImplementation > loadedHandlerSubscription);
        Assert.Contains("if (renderer.IsLoaded)", viewer, StringComparison.Ordinal);
        Assert.Contains("if (_rendererLoadedSettingsHandlerAttached)", viewer, StringComparison.Ordinal);

        int handlerStart = viewer.IndexOf(
            "private void OnRendererLoadedForSettings(",
            ensureEnd,
            StringComparison.Ordinal);
        int disposeStart = handlerStart >= 0
            ? viewer.IndexOf("private void DisposeRenderer()", handlerStart, StringComparison.Ordinal)
            : -1;
        Assert.True(handlerStart >= 0 && disposeStart > handlerStart);
        string loadedHandler = viewer[handlerStart..disposeStart];
        Assert.Contains("renderer.Loaded -= OnRendererLoadedForSettings;", loadedHandler, StringComparison.Ordinal);
        Assert.Contains("_rendererLoadedSettingsHandlerAttached = false;", loadedHandler, StringComparison.Ordinal);
        Assert.Contains("EnsureRendererLayoutSettingsHandler(renderer);", loadedHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("IsSameByteReplayEnabled", loadedHandler, StringComparison.Ordinal);

        int layoutHandlerStart = viewer.IndexOf(
            "private void EnsureRendererLayoutSettingsHandler(",
            handlerStart,
            StringComparison.Ordinal);
        int layoutReadyStart = viewer.IndexOf(
            "private void OnRendererLayoutUpdatedForSettings(",
            layoutHandlerStart,
            StringComparison.Ordinal);
        int clearLayoutHandlerStart = viewer.IndexOf(
            "private void ClearRendererLayoutSettingsHandler()",
            layoutReadyStart,
            StringComparison.Ordinal);
        Assert.True(layoutHandlerStart > handlerStart && layoutReadyStart > layoutHandlerStart &&
            clearLayoutHandlerStart > layoutReadyStart && clearLayoutHandlerStart < disposeStart);
        string layoutSetup = viewer[layoutHandlerStart..layoutReadyStart];
        Assert.Contains("renderer.LayoutUpdated += handler;", layoutSetup, StringComparison.Ordinal);
        Assert.Contains("_rendererLayoutSettingsHandlerAttached)", layoutSetup, StringComparison.Ordinal);
        Assert.Contains(
            "EventHandler<object> handler = (_, _) => OnRendererLayoutUpdatedForSettings(renderer, ticket);",
            layoutSetup,
            StringComparison.Ordinal);
        string layoutReady = viewer[layoutReadyStart..clearLayoutHandlerStart];
        Assert.Contains("double.IsFinite(renderer.ActualWidth)", layoutReady, StringComparison.Ordinal);
        Assert.Contains("renderer.ActualWidth <= 0", layoutReady, StringComparison.Ordinal);
        Assert.Contains("double.IsFinite(renderer.ActualHeight)", layoutReady, StringComparison.Ordinal);
        Assert.Contains("renderer.ActualHeight <= 0", layoutReady, StringComparison.Ordinal);
        Assert.Contains("_rendererInitialLayoutReady = true;", layoutReady, StringComparison.Ordinal);
        Assert.Contains("ApplyRendererSettings();", layoutReady, StringComparison.Ordinal);
        Assert.DoesNotContain("sender is", layoutReady, StringComparison.Ordinal);
        Assert.DoesNotContain("IsSameByteReplayEnabled", layoutReady, StringComparison.Ordinal);

        int disposalCompletedHandler = viewer.IndexOf(
            "private void OnRendererDisposalCompleted(",
            disposeStart,
            StringComparison.Ordinal);
        Assert.True(disposalCompletedHandler > disposeStart);
        string disposeMethod = viewer[disposeStart..disposalCompletedHandler];
        Assert.Contains("renderer.Loaded -= OnRendererLoadedForSettings;", disposeMethod, StringComparison.Ordinal);
        Assert.Contains("ClearRendererLayoutSettingsHandler();", disposeMethod, StringComparison.Ordinal);
        string clearLayout = viewer[clearLayoutHandlerStart..disposeStart];
        Assert.Contains("_rendererLayoutSettingsTarget = null;", clearLayout, StringComparison.Ordinal);
        Assert.Contains("_rendererLayoutSettingsHandler = null;", clearLayout, StringComparison.Ordinal);
        Assert.Contains("_rendererLayoutSettingsHandlerAttached = false;", clearLayout, StringComparison.Ordinal);
        Assert.Contains("++_rendererLayoutSettingsTicket;", clearLayout, StringComparison.Ordinal);

        int applyStart = viewer.IndexOf("private void ApplyRendererSettings()", StringComparison.Ordinal);
        int applyEnd = applyStart >= 0
            ? viewer.IndexOf("private string? GetSameByteReadmeGitBlobSha1(", applyStart, StringComparison.Ordinal)
            : -1;
        Assert.True(applyStart >= 0 && applyEnd > applyStart);
        string applyMethod = viewer[applyStart..applyEnd];
        int loadedGuard = applyMethod.IndexOf(
            "if (!_renderer.IsLoaded || !_rendererInitialLayoutReady)",
            StringComparison.Ordinal);
        int sourceTick = applyMethod.IndexOf(
            "sourceAdmissionStopwatchTicks = sameByteSourceIdentity is null ? 0 : Stopwatch.GetTimestamp();",
            StringComparison.Ordinal);
        int markdownAssignment = applyMethod.IndexOf("_renderer.Markdown = markdown;", StringComparison.Ordinal);
        Assert.True(loadedGuard >= 0 && sourceTick > loadedGuard && markdownAssignment > sourceTick,
            "The frozen source boundary must be captured only in the loaded apply path, immediately before Markdown assignment.");

        Assert.Contains("Loaded += (_, _) => OnLoadedInternal();", renderer, StringComparison.Ordinal);
        Assert.Contains("_isUnloaded = false;", renderer, StringComparison.Ordinal);
    }

    [Fact]
    public void SameByteAdmissionFailsClosedForMovingRefsAndBindsAllSourceContext()
    {
        string root = FindRepositoryRoot();
        string viewer = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownViewer.xaml.cs"));
        string bridge = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Services",
            "Markdown",
            "MarkdownLifecycleAutomationBridge.cs"));
        string factory = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Services",
            "Markdown",
            "MarkdownDocumentSourceFactory.cs"));

        Assert.Contains("GitReferencePolicy.IsImmutableObjectId(source.Ref)", viewer, StringComparison.Ordinal);
        Assert.Contains("failureReason = \"mutable-source-ref\"", viewer, StringComparison.Ordinal);
        Assert.Contains("MarkdownDocumentSource? DocumentSource", bridge, StringComparison.Ordinal);
        Assert.Contains("string.Equals(expected.DocumentId, actual.DocumentId, StringComparison.Ordinal)", bridge, StringComparison.Ordinal);
        Assert.Contains("string.Equals(expected.Ref, actual.Ref, StringComparison.Ordinal)", bridge, StringComparison.Ordinal);
        Assert.Contains("string.Equals(expected.Path, actual.Path, StringComparison.Ordinal)", bridge, StringComparison.Ordinal);
        Assert.Contains("internal bool IsBoundToPipelineGeneration(long generation)", bridge, StringComparison.Ordinal);
        Assert.Contains("Uri.EscapeDataString(normalizedRef)", factory, StringComparison.Ordinal);
        Assert.Contains("Uri.EscapeDataString(normalizedPath)", factory, StringComparison.Ordinal);
        Assert.Contains("stableId.Trim());", factory, StringComparison.Ordinal);
        Assert.Contains("GitReferencePolicy.IsImmutableObjectId(repositoryTreeSha)", factory, StringComparison.Ordinal);
        Assert.Contains("Tree.RootTreeSha", File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "CodeViewer",
            "RepoCodePageViewModel.cs")), StringComparison.Ordinal);
        string repoPageViewModel = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "CodeViewer",
            "RepoCodePageViewModel.cs"));
        Assert.Contains("ShouldReloadMarkdownForRepositoryTreeRevision(", repoPageViewModel, StringComparison.Ordinal);
        Assert.Contains("previousTreeIsKnown && currentTreeIsKnown", repoPageViewModel, StringComparison.Ordinal);
        Assert.Contains("GitReferencePolicy.IsImmutableObjectId(gitRef)", repoPageViewModel, StringComparison.Ordinal);
        Assert.Contains("if (!IsMarkdownPreviewPath(path))", repoPageViewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void RendererSettingsPrecedeTheFrozenSourceBoundaryAndCompletedGenerationBinding()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownViewer.xaml.cs"));
        int methodStart = source.IndexOf("private void ApplyRendererSettings()", StringComparison.Ordinal);
        int methodEnd = methodStart >= 0
            ? source.IndexOf("private string GetEffectiveMarkdown(", methodStart, StringComparison.Ordinal)
            : -1;

        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        string method = source[methodStart..methodEnd];
        int markdownAssignment = method.IndexOf("_renderer.Markdown = markdown;", StringComparison.Ordinal);
        int generationCapture = method.IndexOf(
            "long generationBeforeMarkdown = _renderer.AutomationPipelineGeneration;",
            StringComparison.Ordinal);
        int pendingCapture = method.IndexOf(
            "_pendingSameByteSourceAdmission = new PendingSameByteReadmeSourceAdmission(",
            StringComparison.Ordinal);

        Assert.True(generationCapture >= 0 && markdownAssignment > generationCapture);
        Assert.True(pendingCapture > generationCapture && pendingCapture < markdownAssignment);
        Assert.Contains("long pipelineGenerationAtEntry = _renderer.AutomationPipelineGeneration;", method, StringComparison.Ordinal);
        Assert.Contains(
            "if (sameByteReplayEnabled && (markdownChanged || sameByteSourceAdmissionFailure is not null))",
            method,
            StringComparison.Ordinal);
        string completionMethod = source[source.IndexOf("private void OnRendererRenderCompleted(", StringComparison.Ordinal)..];
        int completedAdmission = completionMethod.IndexOf("RecordSameByteReadmeSourceAdmission(", StringComparison.Ordinal);
        int completedSignal = completionMethod.IndexOf("RecordRenderComplete(", StringComparison.Ordinal);
        int pendingConsume = completionMethod.IndexOf("_pendingSameByteSourceAdmission = null;", StringComparison.Ordinal);
        Assert.True(completedAdmission >= 0 && completedAdmission < completedSignal);
        Assert.True(pendingConsume >= 0 && pendingConsume < completedAdmission);
        Assert.Contains("pendingAdmission.Matches(", completionMethod, StringComparison.Ordinal);
        int assignmentGenerationCapture = method.IndexOf(
            "long assignmentPipelineGeneration = _renderer.AutomationPipelineGeneration;",
            markdownAssignment,
            StringComparison.Ordinal);
        Assert.True(assignmentGenerationCapture > markdownAssignment);
        Assert.Contains(
            "AssignmentPipelineGeneration = assignmentPipelineGeneration",
            method,
            StringComparison.Ordinal);
        Assert.Contains("pendingAdmission.IsBoundToPipelineGeneration(generation)", completionMethod, StringComparison.Ordinal);
        Assert.Contains("_pendingSameByteSourceAdmission = null;", completionMethod, StringComparison.Ordinal);
        Assert.Contains("assignment-generation-unavailable", completionMethod, StringComparison.Ordinal);
        Assert.Contains("assignment-generation-superseded", completionMethod, StringComparison.Ordinal);
        string[] rendererSettings =
        [
            "_renderer.IsSelectionEnabled =",
            "_renderer.IsCodeBlockCopyEnabled =",
            "_renderer.CodeBlockCopyButtonStyle =",
            "_renderer.CodeHighlighter =",
            "_renderer.IsCodeBlockSyntaxHighlightingEnabled =",
            "_renderer.ImageResolver =",
            "_renderer.SvgRenderer =",
            "_renderer.PerformanceSession =",
            "_renderer.ImageBaseUri =",
            "_renderer.ImageDocumentPath =",
            "_renderer.ImageDocumentSource =",
            "_renderer.AllowThirdPartyRemoteImages =",
        ];
        foreach (string setting in rendererSettings)
        {
            int settingAssignment = method.IndexOf(setting, StringComparison.Ordinal);
            Assert.True(settingAssignment >= 0, $"Missing renderer setting assignment: {setting}");
            Assert.True(
                settingAssignment < generationCapture,
                $"Renderer setting can advance the pipeline generation after it is captured: {setting}");
        }

        MatchCollection rendererAssignmentsAfterGenerationCapture = Regex.Matches(
            method[generationCapture..],
            @"_renderer\.(?<property>[A-Za-z][A-Za-z0-9]*)\s*=(?!=)",
            RegexOptions.CultureInvariant);
        Assert.Single(rendererAssignmentsAfterGenerationCapture);
        Assert.Equal("Markdown", rendererAssignmentsAfterGenerationCapture[0].Groups["property"].Value);
    }

    [Fact]
    public void PreparedReadmeBindsBlobIdentityBeforePublishingItsBody()
    {
        string root = FindRepositoryRoot();
        string viewModel = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "CodeViewer",
            "RepoCodePageViewModel.cs"));
        int methodStart = viewModel.IndexOf("private void ApplyPreparedFilePreview(", StringComparison.Ordinal);
        int methodEnd = methodStart >= 0
            ? viewModel.IndexOf("private async Task<RepoFileCacheEntry> GetFileEntryAsync(", methodStart, StringComparison.Ordinal)
            : -1;

        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        string method = viewModel[methodStart..methodEnd];
        int blobUrl = method.IndexOf("Preview.GitHubBlobUrl = prepared.GitHubUrl;", StringComparison.Ordinal);
        int currentFile = method.IndexOf("Preview.CurrentFile = node;", StringComparison.Ordinal);
        int text = method.IndexOf("Preview.Text =", StringComparison.Ordinal);
        int renderedText = method.IndexOf("Preview.RenderedText =", StringComparison.Ordinal);

        Assert.True(blobUrl >= 0 && currentFile > blobUrl);
        Assert.True(text < currentFile && currentFile < renderedText,
            "Raw text must preserve lazy plain-preview ordering, and the blob identity must precede the rendered body assignment.");
        Assert.Contains("useRawReadmeSourceForAudit", method, StringComparison.Ordinal);
        Assert.Contains("prepared.RichText ?? entry.Text", method, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryMarkdownPreviewPublishesOneAtomicTextAndIdentityPayload()
    {
        string root = FindRepositoryRoot();
        string viewModel = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "CodeViewer",
            "RepoCodePageViewModel.cs"));
        int methodStart = viewModel.IndexOf("private void ApplyPreparedFilePreview(", StringComparison.Ordinal);
        int methodEnd = methodStart >= 0
            ? viewModel.IndexOf("private async Task<RepoFileCacheEntry> GetFileEntryAsync(", methodStart, StringComparison.Ordinal)
            : -1;
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        string previewAssignment = viewModel[methodStart..methodEnd];
        int renderedText = previewAssignment.IndexOf("Preview.RenderedText =", StringComparison.Ordinal);
        int payload = previewAssignment.IndexOf("Preview.RichPreviewDocument =", StringComparison.Ordinal);
        Assert.True(payload > renderedText,
            "The single renderer payload must be published after its text, file, and URLs have reached their final preview values.");
        Assert.Contains("new MarkdownViewerDocumentPayload(", previewAssignment, StringComparison.Ordinal);
        Assert.Contains("MarkdownDocumentSourceFactory.TryCreateRepositoryFile(", previewAssignment, StringComparison.Ordinal);

        string previewRoot = Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "CodeViewer",
            "Renderers");
        string xaml = File.ReadAllText(Path.Combine(previewRoot, "MarkdownPreview.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(previewRoot, "MarkdownPreview.xaml.cs"));
        Assert.DoesNotContain("BaseUrl=", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DocumentSource=", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DocumentPath=", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=", xaml, StringComparison.Ordinal);
        Assert.Contains("RichPreviewDocument", codeBehind, StringComparison.Ordinal);
        Assert.Contains("RichPanel.SetDocumentPayload", codeBehind, StringComparison.Ordinal);

        string viewer = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownViewer.xaml.cs"));
        Assert.Contains("internal void SetDocumentPayload(MarkdownViewerDocumentPayload? payload)", viewer, StringComparison.Ordinal);
        Assert.Contains("Equals(_renderer.ImageBaseUri, imageBaseUri)", viewer, StringComparison.Ordinal);
        Assert.Contains("Equals(_renderer.ImageDocumentSource, documentSource)", viewer, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceAdmissionMatchesExactDecodedTextAndUsesTheFrozenMonotonicBoundary()
    {
        string root = FindRepositoryRoot();
        string treeService = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Services",
            "CodeViewer",
            "RepoTreeService.cs"));
        string bridge = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Services",
            "Markdown",
            "MarkdownLifecycleAutomationBridge.cs"));
        string page = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoCodePage.xaml.cs"));

        int decode = treeService.IndexOf("string? readmeText = isBinary ? null : DecodeText(bytes);", StringComparison.Ordinal);
        int sourceIdentityRecord = decode >= 0
            ? treeService.IndexOf("RecordSameByteReadmeSource(", decode, StringComparison.Ordinal)
            : -1;
        int renderedHtml = sourceIdentityRecord >= 0
            ? treeService.IndexOf("string? renderedHtml = null;", sourceIdentityRecord, StringComparison.Ordinal)
            : -1;
        Assert.True(decode >= 0 && sourceIdentityRecord > decode && renderedHtml > sourceIdentityRecord);
        Assert.Matches(
            new Regex(@"RecordSameByteReadmeSource\(\s*bytes,\s*readmeText,\s*content\.Sha", RegexOptions.CultureInvariant),
            treeService);

        Assert.Contains("DecodedTextSha256", bridge, StringComparison.Ordinal);
        Assert.Contains("RawSha256", bridge, StringComparison.Ordinal);
        Assert.Contains("ReadmeGitBlobSha1", bridge, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"string\.Equals\(identity\.ReadmeGitBlobSha1,\s*expectedReadmeGitBlobSha1", RegexOptions.CultureInvariant),
            bridge);
        string viewer = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownViewer.xaml.cs"));
        Assert.Contains("long generationBeforeMarkdown = _renderer.AutomationPipelineGeneration;", viewer, StringComparison.Ordinal);
        Assert.Contains("source.DocumentId", viewer, StringComparison.Ordinal);
        int tickCapture = viewer.IndexOf(
            "long sourceAdmissionStopwatchTicks = sameByteSourceIdentity is null ? 0 : Stopwatch.GetTimestamp();",
            StringComparison.Ordinal);
        int pendingStore = viewer.IndexOf("_pendingSameByteSourceAdmission = new PendingSameByteReadmeSourceAdmission(",
            tickCapture, StringComparison.Ordinal);
        int assignment = viewer.IndexOf("_renderer.Markdown = markdown;", pendingStore, StringComparison.Ordinal);
        Assert.True(tickCapture >= 0 && pendingStore > tickCapture && assignment > pendingStore);
        Assert.Contains("failureReason = \"decoded-text-digest-mismatch\"", bridge, StringComparison.Ordinal);
        Assert.Contains("SameByteReadmeSourceAdmissionFailure", bridge, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(
                @"ViewModel\.UseRawReadmeSourceForSameByteAudit\s*=\s*MarkdownLifecycleAutomationBridge\.IsSameByteReplayEnabled;",
                RegexOptions.CultureInvariant),
            page);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
