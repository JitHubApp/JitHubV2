using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed partial class GitHubReadArchitectureConformanceTests
{
    private static readonly IReadOnlySet<string> DirectHttpAdapters =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Protocol/compatibility adapters. Reachable pages must consume their query facades instead.
            "GitHubClientService.cs",
            "GitHubRestTransport.cs",
            "GitHubService.cs",
            "GitHubService.Post.cs",
            "GitHubGistQueryService.cs",
            "GitHubNotificationQueryService.cs",
            // GraphQL HTTP is confined to its protocol adapter. Profile's direct HTTP is mutation-only.
            "GitHubGraphQlTransport.cs",
            "GitHubProfileQueryService.cs",
            // Remote image traffic is separately constrained by MarkdownRemoteImagePolicy.
            "GitHubImageService.cs"
        };

    [Fact]
    public void CanonicalRepositoryMetadataCallersUseThePhaseZeroQueryFacade()
    {
        string root = FindRepositoryRoot();
        string settings = Read(root, "JitHub.WinUI", "Views", "Pages", "SettingsPage.xaml.cs");
        string settingsViewModel = Read(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "SettingsPageViewModel.cs");
        string settingsNavigation = Read(
            root,
            "JitHub.WinUI",
            "Services",
            "Settings",
            "SettingsSourceNavigationService.cs");
        string shell = Read(root, "JitHub.WinUI", "ViewModels", "Pages", "ShellPageViewModel.cs");
        string stars = Read(root, "JitHub.WinUI", "Services", "Stars", "GitHubStarLibraryService.cs");

        Assert.Contains("ViewModel.OpenSourceRepositoryAsync", settings, StringComparison.Ordinal);
        Assert.Contains("ISettingsSourceNavigationService", settingsViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("GetService<IGitHubService>", settings, StringComparison.Ordinal);
        Assert.Contains("IGitHubRepositoryQueryService", settingsNavigation, StringComparison.Ordinal);
        Assert.Contains("_repositoryQueryService.GetRepositoryAsync", settingsNavigation, StringComparison.Ordinal);

        Assert.Contains("_repositoryQueryService.GetRepositoryAsync", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("_gitHubClientService.GetRepositoryAsync", shell, StringComparison.Ordinal);

        Assert.Contains("_repositoryQueryService.GetRepositoryAsync", stars, StringComparison.Ordinal);
        Assert.DoesNotContain("_clientService.GetRepositoryAsync", stars, StringComparison.Ordinal);
        Assert.Contains("GitHubRequestPriority.BackgroundRefresh", stars, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileGraphQlReadsUseTheSharedAccountPartitionedQueryFacade()
    {
        string root = FindRepositoryRoot();
        string profile = Read(root, "JitHub.WinUI", "Services", "Profile", "GitHubProfileQueryService.cs");
        string graphQlQuery = Read(root, "JitHub.WinUI", "Services", "Profile", "GitHubGraphQlQueryService.cs");

        Assert.Contains("IGitHubGraphQlQueryService", profile, StringComparison.Ordinal);
        Assert.Contains("_graphQlQueryService.GetAsync", profile, StringComparison.Ordinal);
        Assert.Contains("QueryFetchPolicy.StaleFirst", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("IGitHubGraphQlTransport", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshGraphQlAsync", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("profile.graphql.background_refresh", profile, StringComparison.Ordinal);

        Assert.Contains("IGitHubRequestQueue", graphQlQuery, StringComparison.Ordinal);
        Assert.Contains("GitHubAccountPartition.Require", graphQlQuery, StringComparison.Ordinal);
        Assert.Contains("_cacheStore.TryGetAsync", graphQlQuery, StringComparison.Ordinal);
        Assert.Contains("_cacheStore.PutAsync", graphQlQuery, StringComparison.Ordinal);
        Assert.Contains("GitHubRequestPriority.BackgroundRefresh", graphQlQuery, StringComparison.Ordinal);
        Assert.Contains("github.graphql.background_refresh", graphQlQuery, StringComparison.Ordinal);

        Assert.DoesNotContain("_httpClient.GetAsync", profile, StringComparison.Ordinal);
        Assert.Contains("new(HttpMethod.Patch, \"user\")", profile, StringComparison.Ordinal);
        Assert.Contains("SendFollowMutationAsync(accessToken, userId, login, HttpMethod.Put", profile, StringComparison.Ordinal);
        Assert.Contains("SendFollowMutationAsync(accessToken, userId, login, HttpMethod.Delete", profile, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileMutationsUseOwnedAccountWorkAndViewModelCancellation()
    {
        string root = FindRepositoryRoot();
        string service = Read(root, "JitHub.WinUI", "Services", "Profile", "GitHubProfileQueryService.cs");
        string viewModel = Read(root, "JitHub.WinUI", "ViewModels", "Pages", "ProfilePageViewModel.cs");

        Assert.Contains("_requestQueue.EnqueueForAccountAsync", service, StringComparison.Ordinal);
        Assert.Contains("GitHubRequestPriority.Mutation", service, StringComparison.Ordinal);
        Assert.Contains("_taskCoordinator.RunAsync", service, StringComparison.Ordinal);
        Assert.Contains("new ApplicationTaskOptions(taskName, accountPartition)", service, StringComparison.Ordinal);
        Assert.Contains("using CancellationTokenSource mutation = BeginMutation(cancellationToken);", viewModel, StringComparison.Ordinal);
        Assert.Contains("UpdateAuthenticatedProfileAsync(", viewModel, StringComparison.Ordinal);
        Assert.Contains("UnfollowUserAsync(", viewModel, StringComparison.Ordinal);
        Assert.Contains("FollowUserAsync(", viewModel, StringComparison.Ordinal);
        Assert.True(
            Regex.Matches(viewModel, @"\bmutationToken\s*\);").Count >= 3,
            "Profile edit, follow, and unfollow calls must receive the active mutation token.");
    }

    [Fact]
    public void ReachableOwnersDoNotAddDirectGitHubClientReads()
    {
        string root = FindRepositoryRoot();
        string appRoot = Path.Combine(root, "JitHub.WinUI");
        List<string> violations = [];

        foreach (string file in EnumerateOwnerFiles(appRoot))
        {
            string fileName = Path.GetFileName(file);
            string source = File.ReadAllText(file);
            foreach (string method in FindReadInvocations(source, ClientDeclarationRegex()))
            {
                if (IsAllowedDirectClientRead(fileName, method))
                {
                    continue;
                }

                violations.Add($"{Path.GetRelativePath(root, file)} owns direct read {method}");
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void ViewsAndViewModelsDoNotInvokeLegacyGitHubReads()
    {
        string root = FindRepositoryRoot();
        string appRoot = Path.Combine(root, "JitHub.WinUI");
        string[] violations = EnumerateOwnerFiles(appRoot)
            .Where(file => file.Contains($"{Path.DirectorySeparatorChar}Views{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                           file.Contains($"{Path.DirectorySeparatorChar}ViewModels{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => FindReadInvocations(File.ReadAllText(file), LegacyServiceDeclarationRegex()).Any())
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void DirectHttpTrafficIsConfinedToDocumentedAdapters()
    {
        string root = FindRepositoryRoot();
        string servicesRoot = Path.Combine(root, "JitHub.WinUI", "Services");
        string[] violations = Directory.EnumerateFiles(servicesRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => FindHttpInvocations(File.ReadAllText(file)).Any())
            .Where(file => !DirectHttpAdapters.Contains(Path.GetFileName(file)))
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    private static bool IsAllowedDirectClientRead(string fileName, string method)
    {
        if (fileName.Equals("AuthService.cs", StringComparison.OrdinalIgnoreCase))
        {
            return method == "GetCurrentUserAsync";
        }

        if (fileName is "GitHubClientService.cs" or "GitHubService.cs" or "GitHubService.Post.cs")
        {
            return true;
        }

        return false;
    }

    private static IEnumerable<string> EnumerateOwnerFiles(string appRoot) =>
        new[] { "Views", "ViewModels", "Services" }
            .SelectMany(directory => Directory.EnumerateFiles(
                Path.Combine(appRoot, directory),
                "*.cs",
                SearchOption.AllDirectories));

    private static IEnumerable<string> FindReadInvocations(string source, Regex declarationRegex)
    {
        foreach (Match declaration in declarationRegex.Matches(source))
        {
            string variable = declaration.Groups["variable"].Value;
            Regex invocation = new(
                $@"\b{Regex.Escape(variable)}\.(?<method>(?:Get|Search|Is)[A-Za-z0-9_]*)\s*\(",
                RegexOptions.CultureInvariant);
            foreach (Match match in invocation.Matches(source))
            {
                yield return match.Groups["method"].Value;
            }
        }
    }

    private static IEnumerable<string> FindHttpInvocations(string source)
    {
        foreach (Match declaration in HttpClientDeclarationRegex().Matches(source))
        {
            string variable = declaration.Groups["variable"].Value;
            Regex invocation = new(
                $@"\b{Regex.Escape(variable)}\.(?:GetAsync|GetStringAsync|SendAsync)\s*\(",
                RegexOptions.CultureInvariant);
            if (invocation.IsMatch(source))
            {
                yield return variable;
            }
        }
    }

    private static string Read(string root, params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { root }.Concat(segments).ToArray()));

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

        throw new DirectoryNotFoundException("Could not find the JitHub repository root.");
    }

    [GeneratedRegex(@"\bIGitHubClientService\s+(?<variable>[_A-Za-z][_A-Za-z0-9]*)", RegexOptions.CultureInvariant)]
    private static partial Regex ClientDeclarationRegex();

    [GeneratedRegex(@"\bIGitHubService\s+(?<variable>[_A-Za-z][_A-Za-z0-9]*)", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyServiceDeclarationRegex();

    [GeneratedRegex(@"\bHttpClient\s+(?<variable>[_A-Za-z][_A-Za-z0-9]*)", RegexOptions.CultureInvariant)]
    private static partial Regex HttpClientDeclarationRegex();
}
