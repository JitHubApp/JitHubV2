using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services.CodeViewer;

public sealed class GitHubRepoCodeQueryService : IGitHubRepoCodeQueryService
{
    internal const int PerformanceFixtureTreeFileCount = 1_500;
    private readonly IGitHubQueryService _queryService;

    public GitHubRepoCodeQueryService(IGitHubQueryService queryService)
    {
        _queryService = queryService;
    }

    public Task<CachedResult<GitHubTree>> GetTreeAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(CreatePreviewTree()));
        }

        GitHubQuery<GitHubTree> query = CreateQuery(
            accessToken,
            userId,
            $"repos/{Escape(owner)}/{Escape(repositoryName)}/git/trees/{Escape(gitRef)}?recursive=1",
            GitReferencePolicy.CacheResourceFor(gitRef),
            Phase0GitHubJsonSerializerContext.Default.GitHubTree,
            ["repo-code", "repo-code-tree", CreateRepositoryTag(owner, repositoryName)]);
        return ExecuteAsync(query, fetchPolicy, cancellationToken);
    }

    public Task<CachedResult<GitHubRepositoryContent[]>> GetDirectoryAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string path,
        string gitRef,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(CreatePreviewDirectory(path)));
        }

        string normalizedPath = path.Trim('/');
        string relativePath = string.IsNullOrEmpty(normalizedPath)
            ? $"repos/{Escape(owner)}/{Escape(repositoryName)}/contents?ref={Escape(gitRef)}"
            : $"repos/{Escape(owner)}/{Escape(repositoryName)}/contents/{EscapePath(normalizedPath)}?ref={Escape(gitRef)}";
        GitHubQuery<GitHubRepositoryContent[]> query = CreateQuery(
            accessToken,
            userId,
            relativePath,
            GitReferencePolicy.CacheResourceFor(gitRef),
            Phase0GitHubJsonSerializerContext.Default.GitHubRepositoryContentArray,
            ["repo-code", "repo-code-directory", CreateRepositoryTag(owner, repositoryName)]);
        return ExecuteAsync(query, fetchPolicy, cancellationToken);
    }

    public Task<CachedResult<GitHubBlob>> GetBlobAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string sha,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        CancellationToken cancellationToken = default) =>
        GetBlobAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            sha,
            GitHubRequestPriority.Visible,
            fetchPolicy,
            cancellationToken);

    public Task<CachedResult<GitHubBlob>> GetBlobAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string sha,
        GitHubRequestPriority priority,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(CreatePreviewBlob(sha)));
        }

        GitHubQuery<GitHubBlob> query = CreateQuery(
            accessToken,
            userId,
            $"repos/{Escape(owner)}/{Escape(repositoryName)}/git/blobs/{Escape(sha)}",
            GitHubCachePolicy.ImmutableShaResource,
            Phase0GitHubJsonSerializerContext.Default.GitHubBlob,
            ["repo-code", "repo-code-blob", CreateRepositoryTag(owner, repositoryName), CreateBlobTag(owner, repositoryName, sha)],
            priority);
        return ExecuteAsync(query, fetchPolicy, cancellationToken);
    }

    private Task<CachedResult<T>> ExecuteAsync<T>(
        GitHubQuery<T> query,
        QueryFetchPolicy fetchPolicy,
        CancellationToken cancellationToken)
        where T : class =>
        fetchPolicy == QueryFetchPolicy.NetworkOnly
            ? _queryService.RefreshAsync(query, cancellationToken)
            : _queryService.GetAsync(query, fetchPolicy, cancellationToken);

    private static GitHubQuery<T> CreateQuery<T>(
        string accessToken,
        string userId,
        string relativePath,
        string resourceKind,
        JsonTypeInfo<T> jsonTypeInfo,
        string[] tags,
        GitHubRequestPriority priority = GitHubRequestPriority.Visible)
        where T : class
    {
        string normalizedUserId = GitHubAccountPartition.Resolve(accessToken, userId);
        return new GitHubQuery<T>(
            accessToken,
            normalizedUserId,
            HttpMethod.Get,
            relativePath,
            GitHubQueryKeys.Create(normalizedUserId, HttpMethod.Get, relativePath),
            resourceKind,
            GitHubCachePolicy.TtlForResource(resourceKind),
            jsonTypeInfo,
            tags,
            priority);
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string EscapePath(string path) =>
        string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Escape));

    private static string CreateRepositoryTag(string owner, string repositoryName) =>
        $"repo:{owner.Trim().ToLowerInvariant()}/{repositoryName.Trim().ToLowerInvariant()}";

    private static string CreateBlobTag(string owner, string repositoryName, string sha) =>
        $"{CreateRepositoryTag(owner, repositoryName)}:blob:{sha.Trim().ToLowerInvariant()}";

    private static CachedResult<T> CreateCached<T>(T value)
        where T : class =>
        new(value, CacheState.Fresh, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));

    private static readonly Lazy<string> LargePreviewSource = new(CreateLargePreviewSource);

    private const string PreviewReadme = """
        # JitHub

        A fast native GitHub workspace for Windows.

        - Cached repository navigation
        - Responsive code reading
        - Native Markdown rendering
        """;

    private const string PreviewCsv = """
        Repository,Language,Open issues,Status,Notes
        JitHub,C#,18,Active,"Native AOT, WinUI 3"
        MarkdownRenderer,C#,4,Active,"Quoted fields, sorting, and copy"
        AppDataTable,C#,0,Preview,"Two-axis virtualization"
        RepositorySvgRasterizer,C#,2,Preview,"Transparent tiles, 0.1x through 8x zoom"
        "Roadmap, 2026",Mixed,7,Planned,"A field with a comma"
        "Multiline fixture",Text,1,Active,"First line
        Second line"
        "Escaped quotes",Text,0,Complete,"Native ""rich"" preview"
        """;

    private const string PreviewSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" width="960" height="540" viewBox="0 0 960 540">
          <defs>
            <linearGradient id="panel" x1="0" y1="0" x2="1" y2="1">
              <stop offset="0" stop-color="#2f81f7"/>
              <stop offset="1" stop-color="#3fb950"/>
            </linearGradient>
            <clipPath id="roundClip"><rect x="56" y="48" width="848" height="444" rx="32"/></clipPath>
            <mask id="fadeMask">
              <rect width="960" height="540" fill="white"/>
              <circle cx="840" cy="92" r="88" fill="#808080"/>
            </mask>
            <g id="node">
              <rect width="180" height="76" rx="12" fill="#ffffff" fill-opacity="0.94"/>
              <circle cx="32" cy="38" r="13" fill="#1f883d"/>
              <path d="M58 30h92M58 46h66" stroke="#24292f" stroke-width="7" stroke-linecap="round"/>
            </g>
          </defs>
          <g clip-path="url(#roundClip)" mask="url(#fadeMask)">
            <rect x="56" y="48" width="848" height="444" fill="url(#panel)"/>
            <path d="M236 178H480M480 178L724 362M480 178L236 362" fill="none" stroke="#ffffff" stroke-opacity="0.65" stroke-width="8"/>
            <use href="#node" x="146" y="140"/>
            <use href="#node" x="390" y="140"/>
            <use href="#node" x="634" y="324"/>
            <use href="#node" x="146" y="324"/>
          </g>
          <text x="480" y="522" text-anchor="middle" font-family="Segoe UI, sans-serif" font-size="24" fill="#57606a">Native repository rendering</text>
        </svg>
        """;

    private static GitHubTree CreatePreviewTree()
    {
        List<GitHubTreeEntry> entries =
        [
            new() { Path = "README.md", Type = "blob", Sha = "preview-readme", Size = Encoding.UTF8.GetByteCount(PreviewReadme) },
            new() { Path = "data.csv", Type = "blob", Sha = "preview-csv", Size = Encoding.UTF8.GetByteCount(PreviewCsv) },
            new() { Path = "architecture.svg", Type = "blob", Sha = "preview-svg", Size = Encoding.UTF8.GetByteCount(PreviewSvg) },
            new() { Path = "src", Type = "tree", Sha = "preview-src" },
            new()
            {
                Path = "src/App.cs",
                Type = "blob",
                Sha = "preview-app",
                Size = Encoding.UTF8.GetByteCount(CreatePreviewSource())
            }
        ];

        if (IsPerformanceFixture)
        {
            entries.Add(new GitHubTreeEntry { Path = "src/generated", Type = "tree", Sha = "preview-generated" });
            int fixtureTreeFileCount = IsLargeTreePerformanceFixture
                ? PerformanceFixtureTreeFileCount
                : ProductPerformanceLargeAccountFixture.StandardRouteItemCount;
            for (int index = 0; index < fixtureTreeFileCount; index++)
            {
                entries.Add(new GitHubTreeEntry
                {
                    Path = $"src/generated/Fixture{index:D4}.cs",
                    Type = "blob",
                    Sha = $"preview-generated-{index:D4}",
                    Size = 128
                });
            }
        }

        return new GitHubTree { Sha = "preview-tree", Tree = entries.ToArray() };
    }

    private static GitHubRepositoryContent[] CreatePreviewDirectory(string path) =>
        string.Equals(path.Trim('/'), "src", StringComparison.OrdinalIgnoreCase)
            ?
            [
                new GitHubRepositoryContent
                {
                    Name = "App.cs",
                    Path = "src/App.cs",
                    Type = "file",
                    Sha = "preview-app",
                    Size = Encoding.UTF8.GetByteCount(CreatePreviewSource())
                }
            ]
            :
            [
                new GitHubRepositoryContent { Name = "src", Path = "src", Type = "dir", Sha = "preview-src" },
                new GitHubRepositoryContent { Name = "README.md", Path = "README.md", Type = "file", Sha = "preview-readme", Size = Encoding.UTF8.GetByteCount(PreviewReadme) },
                new GitHubRepositoryContent { Name = "data.csv", Path = "data.csv", Type = "file", Sha = "preview-csv", Size = Encoding.UTF8.GetByteCount(PreviewCsv) },
                new GitHubRepositoryContent { Name = "architecture.svg", Path = "architecture.svg", Type = "file", Sha = "preview-svg", Size = Encoding.UTF8.GetByteCount(PreviewSvg) }
            ];

    private static GitHubBlob CreatePreviewBlob(string sha)
    {
        string text = sha switch
        {
            "preview-readme" => PreviewReadme,
            "preview-csv" => PreviewCsv,
            "preview-svg" => PreviewSvg,
            "preview-app" => CreatePreviewSource(),
            _ => "namespace JitHub.Generated;\n\ninternal static class Fixture { }\n"
        };
        return new GitHubBlob
        {
            Sha = sha,
            Size = Encoding.UTF8.GetByteCount(text),
            Encoding = "base64",
            Content = Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
        };
    }

    private static bool IsPerformanceFixture =>
        ProductPerformanceLargeAccountFixture.IsBenchmarkEnabled ||
        string.Equals(
            Environment.GetEnvironmentVariable("JITHUB_PREVIEW_SCENARIO"),
            "repo-code-performance",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsLargeTreePerformanceFixture =>
        ProductPerformanceLargeAccountFixture.IsEnabled ||
        string.Equals(
            Environment.GetEnvironmentVariable("JITHUB_PREVIEW_SCENARIO"),
            "repo-code-performance",
            StringComparison.OrdinalIgnoreCase);

    private static string CreatePreviewSource() => IsPerformanceFixture
        ? LargePreviewSource.Value
        : "namespace JitHub;\n\npublic static class App\n{\n    public const string Experience = \"Native\";\n}\n";

    private static string CreateLargePreviewSource()
    {
        StringBuilder builder = new(112 * 1024);
        builder.AppendLine("namespace JitHub;");
        builder.AppendLine();
        builder.AppendLine("public static class App");
        builder.AppendLine("{");
        builder.AppendLine("    public const string Experience = \"Native\";");
        for (int index = 0; index < 1_650; index++)
        {
            builder.Append("    public static int Measure");
            builder.Append(index.ToString("D4"));
            builder.Append("(int value) => value + ");
            builder.Append(index);
            builder.AppendLine(";");
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    internal static string CreatePerformanceFixtureSourceForTests() => LargePreviewSource.Value;
}
