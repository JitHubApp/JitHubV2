using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class NativeAotSourceContractTests
{
    private const string GeneratedBindableAttribute = "[WinRT.GeneratedBindableCustomProperty]";

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void ReleaseAndAotDebug_EnableTheStrictNativeAotContract()
    {
        string root = FindRepositoryRoot();
        string props = File.ReadAllText(Path.Combine(root, "eng", "NativeAot.props"));
        string project = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "JitHub.WinUI.csproj"));
        string launchSettings = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Properties", "launchSettings.json"));

        foreach (string property in new[]
        {
            "<PublishAot>true</PublishAot>",
            "<PublishTrimmed>true</PublishTrimmed>",
            "<SelfContained>true</SelfContained>",
            "<IsAotCompatible>true</IsAotCompatible>",
            "<EnableAotAnalyzer>true</EnableAotAnalyzer>",
            "<EnableTrimAnalyzer>true</EnableTrimAnalyzer>",
            "<TrimmerSingleWarn>false</TrimmerSingleWarn>",
            "<IlcTreatWarningsAsErrors>true</IlcTreatWarningsAsErrors>",
            "<PublishReadyToRun>false</PublishReadyToRun>",
        })
        {
            Assert.Contains(property, props, StringComparison.Ordinal);
        }

        Assert.Contains("'$(Configuration)' == 'Release' or '$(Configuration)' == 'AotDebug'", props, StringComparison.Ordinal);
        Assert.Contains("<Optimize>true</Optimize>", props, StringComparison.Ordinal);
        Assert.DoesNotContain("<Optimize>false</Optimize>", props, StringComparison.Ordinal);
        Assert.Contains("<JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>", project, StringComparison.Ordinal);
        Assert.Contains("<EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>", project, StringComparison.Ordinal);
        Assert.Contains("<CsWinRTAotWarningLevel>2</CsWinRTAotWarningLevel>", project, StringComparison.Ordinal);
        Assert.Contains("<RuntimeIdentifiers>win-x86;win-x64;win-arm64</RuntimeIdentifiers>", project, StringComparison.Ordinal);
        Assert.Contains("JitHub.WinUI (AotDebug)", launchSettings, StringComparison.Ordinal);
        Assert.Contains("\"nativeDebugging\": true", launchSettings, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void RuntimeGraph_UsesTheReviewedAotReplacements()
    {
        string root = FindRepositoryRoot();
        string project = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "JitHub.WinUI.csproj"));
        string ledger = File.ReadAllText(Path.Combine(root, "eng", "native-aot-dependencies.json"));
        string graph = project + Environment.NewLine + ledger;

        Assert.Contains("WinUIEdit" + "\" Version=\"0.0.5-prerelease", project, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Svg.Skia\" Version=\"5.2.1", project, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SkiaSharp\" Version=\"4.151.1", project, StringComparison.OrdinalIgnoreCase);

        foreach (string removedPackage in new[]
        {
            "CsvHelper",
            "CommunityToolkit.WinUI.Controls.DataGrid",
            "SkiaSharp.Views.WinUI",
            "Jint",
        })
        {
            Assert.DoesNotContain(removedPackage, graph, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void NativeAotUiMatrix_WaitsForRenderedSvgAndObservableEditorResults()
    {
        string root = FindRepositoryRoot();
        string viewport = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "App",
            "AppSvgViewport.xaml.cs"));
        string matrix = File.ReadAllText(Path.Combine(root, "eng", "Invoke-NativeAotUiMatrix.ps1"));

        Assert.Contains("SvgPreviewRenderedImage", viewport, StringComparison.Ordinal);
        Assert.Contains("AccessibilityView.Content", viewport, StringComparison.Ordinal);
        Assert.Contains("Wait-ForElement -AppProcessId $appProcessId -AutomationId 'SvgPreviewRenderedImage'", matrix, StringComparison.Ordinal);
        Assert.Contains("Wait-ForElement -AppProcessId $appProcessId -AutomationId 'DashboardWidget_recent_activity'", matrix, StringComparison.Ordinal);
        Assert.Contains("Wait-ForElement -AppProcessId $appProcessId -AutomationId 'DashboardWidget_overview'", matrix, StringComparison.Ordinal);
        Assert.Contains("ResizeProcessWindow", matrix, StringComparison.Ordinal);
        Assert.Contains("EnableHighContrast", matrix, StringComparison.Ordinal);
        Assert.Contains("RestoreHighContrast", matrix, StringComparison.Ordinal);
        Assert.Contains("finally {", matrix, StringComparison.Ordinal);
        Assert.Contains("Invoke-CompactSectionMatrix", matrix, StringComparison.Ordinal);
        Assert.Contains("Wait-ForAnyVisibleElement", matrix, StringComparison.Ordinal);
        Assert.Contains("for ($settingsCycle = 0; $settingsCycle -lt 2; $settingsCycle++)", matrix, StringComparison.Ordinal);
        Assert.Contains("Test-VisibleElement", matrix, StringComparison.Ordinal);
        Assert.Contains("$routeUsesCompactLayout", matrix, StringComparison.Ordinal);
        Assert.DoesNotContain("$isCompactViewport", matrix, StringComparison.Ordinal);
        Assert.Contains("Action = 'SettingsSection_about'; Target = 'SettingsViewSourceButton'; Interaction = 'invoke'", matrix, StringComparison.Ordinal);
        Assert.Contains("DashboardSideDrawerCloseButton", matrix, StringComparison.Ordinal);
        Assert.Contains("RepoCodeOpenFileTreeButton", matrix, StringComparison.Ordinal);
        Assert.Contains("StarsOpenCategories", matrix, StringComparison.Ordinal);
        Assert.Contains("GistsLeadingPaneButton", matrix, StringComparison.Ordinal);
        Assert.Contains("RepoPullRequestsSectionComboBox", matrix, StringComparison.Ordinal);
        Assert.Contains("RepoPullRequestsCompactSection_Timeline", matrix, StringComparison.Ordinal);
        Assert.Contains("--scenario=$Scenario", matrix, StringComparison.Ordinal);
        Assert.Contains("JITHUB_AUTOMATION_DATA_ROOT", matrix, StringComparison.Ordinal);
        Assert.Contains("Wait-ForElementProperty", matrix, StringComparison.Ordinal);
        Assert.Contains("-Property 'Name' -Value '5' -Contains", matrix, StringComparison.Ordinal);
        Assert.DoesNotContain("-Property 'ItemStatus'", matrix, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void StoreRelease_RequiresDeterministicMatchingHardwareValidation()
    {
        string root = FindRepositoryRoot();
        string hardwareWorkflow = File.ReadAllText(Path.Combine(
            root,
            ".github",
            "workflows",
            "native-aot-hardware-validation.yml"));
        string storeWorkflow = File.ReadAllText(Path.Combine(
            root,
            ".github",
            "workflows",
            "jithub-store-release.yml"));

        Assert.Contains("runner: X86", hardwareWorkflow, StringComparison.Ordinal);
        Assert.Contains("runner: X64", hardwareWorkflow, StringComparison.Ordinal);
        Assert.Contains("runner: ARM64", hardwareWorkflow, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(hardwareWorkflow, "-AutomationDataRoot"));
        Assert.Equal(3, CountOccurrences(hardwareWorkflow, "-Scenario vnext-native-aot"));
        Assert.Contains("native_aot_validation_run_id", storeWorkflow, StringComparison.Ordinal);
        Assert.Contains("$run.head_sha -ne $env:GITHUB_SHA", storeWorkflow, StringComparison.Ordinal);
        Assert.Contains("'x86|x64|ARM64'", storeWorkflow, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void RuntimeBindings_UseGeneratedCustomPropertyProviders()
    {
        string root = FindRepositoryRoot();
        string appRoot = Path.Combine(root, "JitHub.WinUI");
        string[] sourcePaths = Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .ToArray();
        string allSource = string.Join(Environment.NewLine, sourcePaths.Select(File.ReadAllText));

        Assert.DoesNotContain("[Bindable]", allSource, StringComparison.Ordinal);

        HashSet<string> typedRuntimeBindingSources = FindTypedRuntimeBindingSources(appRoot);
        typedRuntimeBindingSources.UnionWith(new[]
        {
            "RepoPullRequestPageViewModel",
            "RepoCommitsPageViewModel",
            "DashboardPageViewModel",
            "StarLibraryPageViewModel",
        });

        string[] frameworkOrInterfaceSources = ["IPullRequestReviewThreadItem"];
        typedRuntimeBindingSources.ExceptWith(frameworkOrInterfaceSources);

        List<string> violations = [];
        foreach (string typeName in typedRuntimeBindingSources.Order(StringComparer.Ordinal))
        {
            string declarationPattern =
                $@"{Regex.Escape(GeneratedBindableAttribute)}\s*(?:public\s+)?(?:sealed\s+)?partial\s+(?:class|record|struct)\s+{Regex.Escape(typeName)}\b";
            if (!Regex.IsMatch(allSource, declarationPattern, RegexOptions.CultureInvariant))
            {
                violations.Add(typeName);
            }
        }

        Assert.True(
            violations.Count == 0,
            "Runtime Binding sources must be partial and use GeneratedBindableCustomProperty: " +
            string.Join(", ", violations));
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void CommentReactionFlyout_PopulatesItsItemsSourceWithoutCompiledBinding()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "CommentInteractionBar.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "CommentInteractionBar.xaml.cs"));

        Assert.DoesNotContain("ItemsSource=\"{x:Bind ReactionOptions", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ReactionOptionsItems\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ReactionOptionsItems.ItemsSource", codeBehind, StringComparison.Ordinal);
        Assert.Contains(
            "ReactionOptionsItems.Items.Add(option);",
            codeBehind,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void CommitDiffRepeater_UsesAnAotProjectableObservableVector()
    {
        string root = FindRepositoryRoot();
        string models = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Services",
            "Commits",
            "CommitDiffModels.cs"));
        string typeRoots = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "NativeAotWinRtTypeRoots.cs"));
        int projectionStart = models.IndexOf(
            "public sealed partial class CommitDiffRowProjection",
            StringComparison.Ordinal);
        Assert.True(projectionStart >= 0, "CommitDiffRowProjection declaration was not found.");
        string projection = models[projectionStart..];

        Assert.Contains(
            "public ObservableCollection<CommitDiffRow> Rows { get; }",
            projection,
            StringComparison.Ordinal);
        Assert.Contains(
            "Rows = new ObservableCollection<CommitDiffRow>(rows);",
            projection,
            StringComparison.Ordinal);
        Assert.Contains(
            "GeneratedWinRTExposedExternalType(typeof(ObservableCollection<CommitDiffRow>))",
            typeRoots,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void SettingsSections_PopulateControlItemCollectionsWithoutGenericVectorProjection()
    {
        string root = FindRepositoryRoot();
        string viewModel = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "SettingsPageViewModel.cs"));
        string view = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "SettingsPage.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "SettingsPage.xaml.cs"));

        Assert.Contains(
            "public IReadOnlyList<SettingsSectionItem> SettingsSections { get; }",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "SettingsSections = new SettingsSectionItem[]",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "public sealed partial class SettingsSectionItem",
            viewModel,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource=", view, StringComparison.Ordinal);
        Assert.Contains("SettingsSectionList.Items.Add(section);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CompactSectionPicker.Items.Add(section);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("PopulateItems(SettingsCacheOwnersList, ViewModel.CacheOwners);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("PopulateContributors(SettingsDevelopersList, Developers);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("PopulateContributors(SettingsDesignersList, Designers);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("panel.Children.Add(new AppContributorCard(contributor));", codeBehind, StringComparison.Ordinal);
        Assert.Contains(
            "SynchronizeSectionSelection(ViewModel.SelectedSection);",
            codeBehind,
            StringComparison.Ordinal);
    }

    private static HashSet<string> FindTypedRuntimeBindingSources(string appRoot)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        HashSet<string> sources = new(StringComparer.Ordinal);

        foreach (string path in Directory.EnumerateFiles(appRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path)))
        {
            XDocument document = XDocument.Load(path, LoadOptions.None);
            Visit(document.Root, null);
        }

        return sources;

        void Visit(XElement? element, string? inheritedType)
        {
            if (element is null)
            {
                return;
            }

            string? currentType = element.Attribute(xaml + "DataType")?.Value ?? inheritedType;
            bool hasRuntimeBinding = element.Attributes().Any(attribute =>
                attribute.Value.Contains("{Binding", StringComparison.Ordinal));
            if (hasRuntimeBinding && currentType is not null)
            {
                string typeName = currentType[(currentType.LastIndexOf(':') + 1)..];
                if (!string.Equals(typeName, "TreeViewNode", StringComparison.Ordinal))
                {
                    sources.Add(typeName);
                }
            }

            foreach (XElement child in element.Elements())
            {
                Visit(child, currentType);
            }
        }
    }

    private static bool IsBuildOutput(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
