using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ReleaseExceptionContainmentContractTests
{
    [Fact]
    public void LegacyIssueTimeline_UnknownEventsUseTheFallbackPresentation()
    {
        string root = FindRepositoryRoot();
        string service = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Services", "GitHubService.cs"));
        string models = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Models",
            "LegacyGitHub",
            "LegacyGitHubModels.cs"));

        Assert.Contains("ParseGitHubEnum<EventInfoState>(issueEvent.Event) ?? EventInfoState.Unknown", service, StringComparison.Ordinal);
        Assert.Contains("StringValue = issueEvent.Event", service, StringComparison.Ordinal);
        Assert.DoesNotContain("Unsupported issue event", service, StringComparison.Ordinal);
        Assert.Contains("public enum EventInfoState\n{\n    Unknown,", NormalizeNewlines(models), StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryTreeApply_HasNoBlockingTaskBridge()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "CodeViewer",
            "RepoFileTreeViewModel.cs"));

        Assert.DoesNotContain("GetAwaiter().GetResult()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public void Load(RepoTree", source, StringComparison.Ordinal);
        Assert.Contains("LoadIncrementallyAsync", source, StringComparison.Ordinal);
        Assert.Contains("await YieldIfNeededAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductCode_HasNoContinuationBasedExceptionOwnership()
    {
        string productRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI");
        string[] offenders = Directory
            .EnumerateFiles(productRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedPath(path))
            .Where(path => File.ReadAllText(path).Contains(".ContinueWith(", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(productRoot, path))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void LegacyGitHubService_PreservesTypedFailuresAndDoesNotPushRawServiceErrors()
    {
        string serviceRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Services");
        string[] sourceFiles =
        [
            Path.Combine(serviceRoot, "GitHubService.cs"),
            Path.Combine(serviceRoot, "GitHubService.Post.cs")
        ];
        string source = string.Join("\n", sourceFiles.Select(File.ReadAllText));

        Assert.DoesNotContain("throw new Exception(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NotificationService.Push", source, StringComparison.Ordinal);
        Assert.DoesNotContain("INotificationService notificationService", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GitHubErrorReaders_DoNotConvertCancellationIntoApiFailures()
    {
        string servicesRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Services");
        string[] sourceFiles =
        [
            Path.Combine(servicesRoot, "Gists", "GitHubGistQueryService.cs"),
            Path.Combine(servicesRoot, "Notifications", "GitHubNotificationQueryService.cs"),
            Path.Combine(servicesRoot, "Profile", "GitHubGraphQlTransport.cs"),
            Path.Combine(servicesRoot, "Profile", "GitHubProfileQueryService.cs")
        ];

        foreach (string sourceFile in sourceFiles)
        {
            string source = File.ReadAllText(sourceFile);
            Assert.Contains("catch (OperationCanceledException)\n        {\n            throw;\n        }", NormalizeNewlines(source), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AppResources_HaveNoAsyncSvgConverterOrDeadHardcodedGradientConverter()
    {
        string root = FindRepositoryRoot();
        string appResources = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "App.xaml"));

        Assert.DoesNotContain("StringToSvgSourceConverter", appResources, StringComparison.Ordinal);
        Assert.DoesNotContain("UseGradientToForegroundConverter", appResources, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "JitHub.WinUI", "Converters", "Common", "StringToSvgSourceConverter.cs")));
        Assert.False(File.Exists(Path.Combine(root, "JitHub.WinUI", "Converters", "Common", "UseGradientToForegroundConverter.cs")));
    }

    [Fact]
    public void UserFacingErrors_ReportBoundedHandledFailureTelemetry()
    {
        string root = FindRepositoryRoot();
        string helper = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Helpers",
            "UserFacingError.cs"));
        string app = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "App.xaml.cs"));

        Assert.Contains("HandledFailureReporter.Report(exception, NormalizeContext(context));", helper, StringComparison.Ordinal);
        Assert.Contains("HandledFailureReporter.Report(internalMessage, NormalizeContext(context));", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("Debug.WriteLine", helper, StringComparison.Ordinal);
        Assert.Contains("TrackExceptionTelemetry(\"app.exception.handled\", exception: null, category);", app, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeEditorFailures_AreReportedOnceWithoutSensitiveOperationValues()
    {
        string root = FindRepositoryRoot();
        string editor = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "CodeViewer",
            "CodeEditorControl.xaml.cs"));

        Assert.Contains("private void ReportFailureOnce(Exception exception, string category)", editor, StringComparison.Ordinal);
        Assert.Contains("_reportedFailureCategories.Add(category)", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("Debug.WriteLine", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyLanguageId({langId})", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("TryLoadLexilla('{lexerName}')", editor, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductFailures_AreNotOwnedOnlyByTheVisualStudioDebugger()
    {
        string productRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI");
        string[] offenders = Directory
            .EnumerateFiles(productRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedPath(path))
            .Where(path => File.ReadAllText(path).Contains("Debug.Write", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(productRoot, path))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void IncrementalLoading_DoesNotImmediatelyRetryAFailedPage()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Behaviors",
            "IncrementalLoadingBehavior.cs"));

        Assert.Contains("bool loadSucceeded = false;", source, StringComparison.Ordinal);
        Assert.Contains("if (loadSucceeded && source.HasMoreItems", source, StringComparison.Ordinal);
        Assert.Contains("if (!state.HasReportedFailure)", source, StringComparison.Ordinal);
        Assert.Contains("App.LogHandledException(ex, \"ui-incremental-loading-behavior\")", source, StringComparison.Ordinal);
    }

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static bool IsGeneratedPath(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

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

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
