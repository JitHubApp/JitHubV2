using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class RuntimeCreatedAccessibilityContractTests
{
    private static readonly string[] InteractiveTypes =
    [
        "Button",
        "AutoSuggestBox",
        "CheckBox",
        "ComboBox",
        "ContentDialog",
        "DropDownButton",
        "HyperlinkButton",
        "ListView",
        "MenuFlyoutItem",
        "PasswordBox",
        "RadioButton",
        "TextBox",
        "ToggleMenuFlyoutItem",
        "ToggleSwitch"
    ];

    public static IEnumerable<object[]> RuntimeCreationSites()
    {
        string productRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI");
        foreach (string path in Directory.EnumerateFiles(productRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(static path => !IsGeneratedOrBuildOutput(path))
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            yield return [Path.GetRelativePath(productRoot, path).Replace(Path.DirectorySeparatorChar, '/')];
        }
    }

    [Theory]
    [MemberData(nameof(RuntimeCreationSites))]
    public void EveryRuntimeCreatedInteractiveControlGetsImmediateIdentity(string relativePath)
    {
        string source = ReadProductSource(relativePath);
        (int declarationCount, List<string> failures) = AnalyzeRuntimeCreationContracts(source);
        if (declarationCount == 0)
        {
            return;
        }

        Assert.True(failures.Count == 0, $"{relativePath}{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    [Fact]
    public void RuntimeDiscoveryRecognizesAdversarialDeclarationAndHelperShapes()
    {
        const string source = """
            void Build()
            {
                var checkBox = new CheckBox();
                AutomationIdentity.Apply(checkBox, "Check_1", "Select item");
                ContentDialog dialog = new() { Title = "Confirm" };
                AutomationProperties.SetAutomationId(dialog, "ConfirmDialog");
                AutomationProperties.SetName(dialog, "Confirm action");
                Button missing = new Button { Content = "Missing" };
                Button CreateAnonymous() => new Button();
            }
            """;

        (int count, List<string> failures) = AnalyzeRuntimeCreationContracts(source);

        Assert.Equal(4, count);
        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, failure => failure.Contains("Button missing", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("anonymous Button", StringComparison.Ordinal));
    }

    [Fact]
    public void IssueSidePanelRuntimeCheckboxIsCoveredByProductWideDiscovery()
    {
        string source = ReadProductSource("Views/Controls/Common/IssueSidePanelSelectableItem.xaml.cs");
        (int count, List<string> failures) = AnalyzeRuntimeCreationContracts(source);

        Assert.True(count > 0);
        Assert.Empty(failures);
    }

    [Fact]
    public void RuntimeDialogIdentitiesAreStableAndTaskSpecific()
    {
        AssertIdentities("Views/Pages/RepoPullRequestPage.xaml.cs",
            "RepoPullRequestsSubmitReviewDialog",
            "RepoPullRequestsCreateDialog",
            "RepoPullRequestsEditDialog",
            "RepoPullRequestsMetadataDialog",
            "RepoPullRequestsMergeDialog");
        AssertIdentities("Views/Pages/RepoIssuePage.xaml.cs",
            "RepoIssuesEditDialog",
            "RepoIssuesMetadataDialog",
            "RepoIssuesCreateDialog");
        AssertIdentities("Views/Pages/ProfilePage.xaml.cs", "ProfileEditDialog");
        AssertIdentities("Views/Pages/GistsPage.xaml.cs", "GistEditorDialog", "GistDeleteDialog");
        AssertIdentities("Views/Pages/StarsPage.xaml.cs",
            "StarsCategoryNameBox",
            "StarsCategoryColorPicker",
            "StarsCategoryPickerList");
        AssertIdentities("Views/Pages/RepoManagePage.xaml.cs",
            "RepositoryDeleteConfirmation",
            "RepositoryDeleteFailures");
    }

    [Theory]
    [InlineData("Views/Controls/App/ActivityCard.xaml.cs", "\"ActivityInlineAction\"")]
    [InlineData("Views/Controls/App/ActivitySentenceLine.xaml.cs", "\"ActivitySentenceInlineAction\"")]
    [InlineData("Views/Controls/PullRequest/Conversation/PullRequestTimelineItem.xaml.cs", "\"PullRequestTimelineInlineAction\"")]
    public void DynamicInlineActionsUseNativeHyperlinksWithStableSemantics(
        string relativePath,
        string automationIdPrefix)
    {
        string source = ReadProductSource(relativePath);
        Assert.Contains("new Hyperlink", source, StringComparison.Ordinal);
        Assert.Contains(automationIdPrefix, source, StringComparison.Ordinal);
        Assert.Contains("AutomationIdentity.CreateScopedId", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetAutomationId(", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(hyperlink, part.Text);", source, StringComparison.Ordinal);
        Assert.Contains("hyperlink.Click +=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("linkText.Tapped +=", source, StringComparison.Ordinal);
    }

    private static (int DeclarationCount, List<string> Failures) AnalyzeRuntimeCreationContracts(string source)
    {
        string typePattern = string.Join('|', InteractiveTypes.Select(Regex.Escape));
        MatchCollection declarations = Regex.Matches(
            source,
            $@"\b(?:(?<type>{typePattern})\s+(?<variable>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*new(?:\s+(?<constructorType>{typePattern}))?|var\s+(?<variable>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*new\s+(?<constructorType>{typePattern}))\s*(?:\([^;{{}}]*\))?\s*(?:\{{|;)",
            RegexOptions.CultureInvariant);
        List<string> failures = [];

        foreach (Match declaration in declarations)
        {
            string variable = declaration.Groups["variable"].Value;
            string type = declaration.Groups["constructorType"].Success
                ? declaration.Groups["constructorType"].Value
                : declaration.Groups["type"].Value;
            int contractEnd = Math.Min(source.Length, declaration.Index + 1600);
            string contractRegion = source[declaration.Index..contractEnd];
            bool helperContract = Regex.IsMatch(
                contractRegion,
                $@"\bAutomationIdentity\.Apply\s*\(\s*{Regex.Escape(variable)}\s*,",
                RegexOptions.CultureInvariant);
            bool directContract = Regex.IsMatch(
                contractRegion,
                $@"\bAutomationProperties\.SetAutomationId\s*\(\s*{Regex.Escape(variable)}\s*,",
                RegexOptions.CultureInvariant) && Regex.IsMatch(
                contractRegion,
                $@"\bAutomationProperties\.SetName\s*\(\s*{Regex.Escape(variable)}\s*,",
                RegexOptions.CultureInvariant);
            if (!helperContract && !directContract)
            {
                int line = 1 + source[..declaration.Index].Count(character => character == '\n');
                failures.Add($"{type} {variable} at line {line} lacks an immediate ID/name contract.");
            }
        }

        MatchCollection explicitCreations = Regex.Matches(
            source,
            $@"\bnew\s+(?:Microsoft\.UI\.Xaml\.Controls\.)?(?<type>{typePattern})\s*(?:\(|\{{)",
            RegexOptions.CultureInvariant);
        int anonymousCount = 0;
        foreach (Match creation in explicitCreations)
        {
            bool belongsToDeclaration = declarations.Cast<Match>().Any(declaration =>
                creation.Index >= declaration.Index && creation.Index < declaration.Index + declaration.Length);
            if (belongsToDeclaration)
            {
                continue;
            }

            anonymousCount++;
            int line = 1 + source[..creation.Index].Count(character => character == '\n');
            failures.Add($"anonymous {creation.Groups["type"].Value} at line {line} cannot receive a stable ID/name contract.");
        }

        return (declarations.Count + anonymousCount, failures);
    }

    private static bool IsGeneratedOrBuildOutput(string path)
    {
        string normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertIdentities(string relativePath, params string[] ids)
    {
        string source = ReadProductSource(relativePath);
        foreach (string id in ids)
        {
            Assert.Contains($"\"{id}\"", source, StringComparison.Ordinal);
        }
    }

    private static int FindInitializerEnd(string source, int openingBrace)
    {
        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int index = openingBrace; index < source.Length; index++)
        {
            char character = source[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
                continue;
            }

            depth += character switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0
            };
            if (depth == 0)
            {
                return index + 1;
            }
        }

        return -1;
    }

    private static string ReadProductSource(string relativePath) => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "JitHub.WinUI",
        relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
