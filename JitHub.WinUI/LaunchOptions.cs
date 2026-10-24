using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace JitHub.WinUI;

internal sealed record LaunchOptions(
    string? Page = null,
    string? Scenario = null,
    string? Theme = null,
    string? Palette = null,
    string? Repository = null,
    string? Branch = null,
    bool MarkdownLifecycleFixture = false,
    string? MarkdownLifecycleHost = null,
    string? MarkdownCorpusPath = null,
    bool ReadmeProductionAudit = false,
    bool WebsiteShowcase = false,
    bool OfflineSameByteReadmeAudit = false)
{
    private const int MaximumActivationArgumentLength = 32_767;
    private const int MaximumActivationArgumentCount = 128;
    private const string DefaultRepository = "JitHubApp/JitHubV2";
    private const long MaximumPinnedManifestBytes = 4 * 1024 * 1024;
    private const long MaximumPinnedReadmeBytes = 16 * 1024 * 1024;
    private const string PinnedManifestPathVariable = "JITHUB_README_AUDIT_PINNED_MANIFEST_PATH";
    private const string PinnedManifestRankVariable = "JITHUB_README_AUDIT_PINNED_MANIFEST_RANK";
    private const string PinnedManifestSha256Variable = "JITHUB_README_AUDIT_PINNED_MANIFEST_SHA256";
    private const string SameByteReadmeAbsentVariable = "JITHUB_README_AUDIT_SAME_BYTE_README_ABSENT";

    public bool HasPageOverride => !string.IsNullOrWhiteSpace(Page);

    public bool IsRepositoryPageOverride =>
        Page is not null &&
        (Page.Equals("repo", System.StringComparison.OrdinalIgnoreCase) ||
         Page.Equals("repo-code", System.StringComparison.OrdinalIgnoreCase) ||
         Page.Equals("repo-issues", System.StringComparison.OrdinalIgnoreCase) ||
         Page.Equals("repo-pulls", System.StringComparison.OrdinalIgnoreCase) ||
         Page.Equals("repo-pull-requests", System.StringComparison.OrdinalIgnoreCase) ||
         Page.Equals("repo-commits", System.StringComparison.OrdinalIgnoreCase));

    public bool IsPublicPreviewOverride =>
        IsRepositoryPageOverride ||
        (Page is not null &&
         (Page.Equals("home", System.StringComparison.OrdinalIgnoreCase) ||
          Page.Equals("shell", System.StringComparison.OrdinalIgnoreCase) ||
          Page.Equals("my-issues", System.StringComparison.OrdinalIgnoreCase) ||
          Page.Equals("my-pull-requests", System.StringComparison.OrdinalIgnoreCase) ||
          Page.Equals("profile", System.StringComparison.OrdinalIgnoreCase) ||
          Page.Equals("repositories", System.StringComparison.OrdinalIgnoreCase) ||
          Page.Equals("settings", System.StringComparison.OrdinalIgnoreCase) ||
          Page.Equals("notifications", System.StringComparison.OrdinalIgnoreCase) ||
          Page.Equals("stars", System.StringComparison.OrdinalIgnoreCase) ||
          Page.Equals("gists", System.StringComparison.OrdinalIgnoreCase)));

    public string RepositoryFullName =>
        string.IsNullOrWhiteSpace(Repository) ? DefaultRepository : Repository.Trim();

    internal string ResolvePreviewAccessToken(string publicAccessToken)
    {
        if (OfflineSameByteReadmeAudit)
        {
            ThrowIfOfflineAuditCredentialsPresent();
            return publicAccessToken;
        }

        if (!ReadmeProductionAudit)
        {
            return publicAccessToken;
        }

        string? token = System.Environment.GetEnvironmentVariable("JITHUB_README_AUDIT_GITHUB_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new System.InvalidOperationException(
                "README production audit requires JITHUB_README_AUDIT_GITHUB_TOKEN.");
        }

        return token.Trim();
    }

    internal long ResolveReadmeAuditAccountId()
    {
        if (OfflineSameByteReadmeAudit)
        {
            throw new System.InvalidOperationException(
                "The offline same-byte README audit is anonymous and has no authenticated account partition.");
        }

        if (!ReadmeProductionAudit)
        {
            throw new System.InvalidOperationException(
                "A README audit account partition is only available during a production README audit.");
        }

        string? rawAccountId = System.Environment.GetEnvironmentVariable(
            "JITHUB_README_AUDIT_GITHUB_ACCOUNT_ID");
        if (!long.TryParse(
                rawAccountId,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out long accountId) ||
            accountId <= 0)
        {
            throw new System.InvalidOperationException(
                "README production audit requires a stable positive " +
                "JITHUB_README_AUDIT_GITHUB_ACCOUNT_ID cache partition.");
        }

        return accountId;
    }

    internal OfflineSameByteReadmeAuditPin? ValidateOfflineSameByteReadmeAuditPin()
    {
        if (!OfflineSameByteReadmeAudit)
            return null;

        if (!ReadmeProductionAudit ||
            Page is null || !Page.Equals("repo-code", System.StringComparison.OrdinalIgnoreCase) ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                Branch ?? string.Empty,
                "^[0-9a-fA-F]{40}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            throw new System.InvalidOperationException(
                "Offline same-byte README replay requires --readme-production-audit, --page=repo-code, and a pinned commit SHA.");
        }

        ThrowIfOfflineAuditCredentialsPresent();

        string manifestPath = System.Environment.GetEnvironmentVariable(PinnedManifestPathVariable)
            ?? throw new System.InvalidOperationException(
                $"Offline same-byte README replay requires {PinnedManifestPathVariable}.");
        string manifestRankText = System.Environment.GetEnvironmentVariable(PinnedManifestRankVariable)
            ?? throw new System.InvalidOperationException(
                $"Offline same-byte README replay requires {PinnedManifestRankVariable}.");
        string expectedManifestSha256 = System.Environment.GetEnvironmentVariable(PinnedManifestSha256Variable)
            ?? throw new System.InvalidOperationException(
                $"Offline same-byte README replay requires {PinnedManifestSha256Variable}.");
        if (!int.TryParse(
                manifestRankText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int rank) || rank is < 1 or > 500 ||
            !IsHex(expectedManifestSha256, 64))
        {
            throw new InvalidDataException("Offline same-byte README pin metadata is malformed.");
        }

        FileInfo manifestInfo = new(manifestPath);
        if (!manifestInfo.Exists || manifestInfo.Length is <= 0 or > MaximumPinnedManifestBytes ||
            (manifestInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Offline same-byte README manifest is missing, redirected, or oversized.");
        }

        byte[] manifestBytes = File.ReadAllBytes(manifestInfo.FullName);
        if (manifestBytes.LongLength != manifestInfo.Length ||
            !string.Equals(
                System.Convert.ToHexString(SHA256.HashData(manifestBytes)),
                expectedManifestSha256,
                System.StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Offline same-byte README manifest changed after its pin was prepared.");
        }

        using JsonDocument document = JsonDocument.Parse(
            manifestBytes,
            new JsonDocumentOptions { MaxDepth = 32 });
        JsonElement root = document.RootElement;
        if (!TryGetPropertyIgnoreCase(root, "schemaVersion", out JsonElement schema) ||
            schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int schemaVersion) || schemaVersion != 2 ||
            !TryGetPropertyIgnoreCase(root, "repositories", out JsonElement repositories) ||
            repositories.ValueKind != JsonValueKind.Array || repositories.GetArrayLength() != 500)
        {
            throw new InvalidDataException("Offline same-byte replay requires the complete schema-v2 top-500 manifest.");
        }

        bool[] ranks = new bool[501];
        JsonElement selectedRepository = default;
        foreach (JsonElement repository in repositories.EnumerateArray())
        {
            if (!TryGetPropertyIgnoreCase(repository, "rank", out JsonElement rankElement) ||
                rankElement.ValueKind != JsonValueKind.Number || !rankElement.TryGetInt32(out int itemRank) ||
                itemRank is < 1 or > 500 || ranks[itemRank])
            {
                throw new InvalidDataException("Offline pinned manifest ranks must be unique and exactly 1..500.");
            }

            ranks[itemRank] = true;
            if (itemRank == rank)
                selectedRepository = repository;
        }

        if (ranks.Skip(1).Any(static present => !present) || selectedRepository.ValueKind != JsonValueKind.Object ||
            !TryGetString(selectedRepository, "fullName", out string? fullName) ||
            !TryGetString(selectedRepository, "commitSha", out string? commitSha) ||
            !string.Equals(fullName, RepositoryFullName, System.StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(commitSha, Branch, System.StringComparison.OrdinalIgnoreCase) ||
            !TryGetPropertyIgnoreCase(selectedRepository, "readme", out JsonElement readme) ||
            readme.ValueKind != JsonValueKind.Object ||
            !TryGetPropertyIgnoreCase(readme, "available", out JsonElement availableElement) ||
            (availableElement.ValueKind != JsonValueKind.True && availableElement.ValueKind != JsonValueKind.False) ||
            !TryGetPropertyIgnoreCase(readme, "byteSize", out JsonElement byteSizeElement) ||
            byteSizeElement.ValueKind != JsonValueKind.Number || !byteSizeElement.TryGetInt64(out long byteSize) ||
            !TryGetString(readme, "path", out string? readmePath) ||
            !TryGetString(readme, "sha", out string? readmeSha))
        {
            throw new InvalidDataException("Offline pinned README identity does not match the requested repository and commit.");
        }

        bool readmeAvailable = availableElement.GetBoolean();
        string? absentMarker = System.Environment.GetEnvironmentVariable(SameByteReadmeAbsentVariable);
        string? corpusPath = System.Environment.GetEnvironmentVariable("JITHUB_README_AUDIT_SAME_BYTE_CORPUS");
        string? corpusReadmeSha = System.Environment.GetEnvironmentVariable("JITHUB_README_AUDIT_SAME_BYTE_README_SHA");
        if (readmeAvailable)
        {
            if (byteSize is <= 0 or > MaximumPinnedReadmeBytes ||
                !IsHex(readmeSha, 40) || string.IsNullOrWhiteSpace(readmePath) ||
                !JitHub.Services.CodeViewer.FilePreviewResolver.IsGitHubReadmePath(readmePath) ||
                readmePath.StartsWith("/", System.StringComparison.Ordinal) ||
                readmePath.Contains('\\') || readmePath.Contains(':') ||
                readmePath.Split('/').Any(static segment => segment is "" or "." or "..") ||
                absentMarker is not null || string.IsNullOrWhiteSpace(corpusPath) ||
                !string.Equals(corpusReadmeSha, readmeSha, System.StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Offline same-byte README corpus does not match the pinned available README.");
            }

            string fullCorpusPath = Path.GetFullPath(corpusPath);
            if (!Directory.Exists(fullCorpusPath) ||
                !File.Exists(Path.Combine(fullCorpusPath, "manifest.json")))
            {
                throw new InvalidDataException("Offline same-byte README corpus is missing its manifest.");
            }

            return new OfflineSameByteReadmeAuditPin(
                rank, RepositoryFullName, Branch!, true, readmePath, readmeSha!, byteSize,
                Path.GetFullPath(manifestInfo.FullName), expectedManifestSha256.ToUpperInvariant());
        }

        if (byteSize != 0 || !string.IsNullOrEmpty(readmePath) || !string.IsNullOrEmpty(readmeSha) ||
            absentMarker != "1" || !string.IsNullOrWhiteSpace(corpusPath) ||
            !string.IsNullOrWhiteSpace(corpusReadmeSha))
        {
            throw new InvalidDataException("Offline absent-README replay is not authorized by consistent pinned manifest metadata.");
        }

        return new OfflineSameByteReadmeAuditPin(
            rank, RepositoryFullName, Branch!, false, string.Empty, string.Empty, 0,
            Path.GetFullPath(manifestInfo.FullName), expectedManifestSha256.ToUpperInvariant());
    }

    public static LaunchOptions Parse(string[]? args, string? activationArguments = null)
    {
        string? page = null;
        string? scenario = null;
        string? theme = null;
        string? palette = null;
        string? repository = null;
        string? branch = null;
        bool markdownLifecycleFixture = false;
        string? markdownLifecycleHost = null;
        string? markdownCorpusPath = null;
        bool readmeProductionAudit = false;
        bool websiteShowcase = false;
        bool offlineSameByteReadmeAudit = false;

        IEnumerable<string> effectiveArguments = TokenizeActivationArguments(activationArguments)
            .Concat(args ?? []);
        foreach (string rawArg in effectiveArguments)
        {
            string arg = rawArg.Trim();
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }

            if (string.Equals(arg, "--design-lab", System.StringComparison.OrdinalIgnoreCase))
            {
                page = "design-lab";
                continue;
            }

            if (arg.StartsWith("--page=", System.StringComparison.OrdinalIgnoreCase))
            {
                page = arg[7..].Trim();
                continue;
            }

            if (arg.StartsWith("--scenario=", System.StringComparison.OrdinalIgnoreCase))
            {
                scenario = arg[11..].Trim();
                continue;
            }

            if (arg.StartsWith("--theme=", System.StringComparison.OrdinalIgnoreCase))
            {
                theme = arg[8..].Trim();
                continue;
            }

            if (arg.StartsWith("--palette=", System.StringComparison.OrdinalIgnoreCase))
            {
                palette = arg[10..].Trim();
                continue;
            }

            if (arg.StartsWith("--repo=", System.StringComparison.OrdinalIgnoreCase))
            {
                repository = arg[7..].Trim();
                continue;
            }

            if (arg.StartsWith("--repository=", System.StringComparison.OrdinalIgnoreCase))
            {
                repository = arg[13..].Trim();
                continue;
            }

            if (arg.StartsWith("--branch=", System.StringComparison.OrdinalIgnoreCase))
            {
                branch = arg[9..].Trim();
                continue;
            }

            if (string.Equals(arg, "--markdown-lifecycle-fixture", System.StringComparison.OrdinalIgnoreCase))
            {
                markdownLifecycleFixture = true;
                continue;
            }

            if (string.Equals(arg, "--readme-production-audit", System.StringComparison.OrdinalIgnoreCase))
            {
                readmeProductionAudit = true;
                continue;
            }

            if (string.Equals(arg, "--readme-offline-same-byte-audit", System.StringComparison.OrdinalIgnoreCase))
            {
                offlineSameByteReadmeAudit = true;
                continue;
            }

            if (string.Equals(arg, "--website-showcase", System.StringComparison.OrdinalIgnoreCase))
            {
                websiteShowcase = true;
                continue;
            }

            const string markdownHostPrefix = "--markdown-lifecycle-host=";
            if (arg.StartsWith(markdownHostPrefix, System.StringComparison.OrdinalIgnoreCase))
            {
                markdownLifecycleHost = arg[markdownHostPrefix.Length..].Trim();
                continue;
            }

            const string markdownCorpusPrefix = "--markdown-corpus=";
            if (arg.StartsWith(markdownCorpusPrefix, System.StringComparison.OrdinalIgnoreCase))
            {
                markdownCorpusPath = arg[markdownCorpusPrefix.Length..].Trim();
            }
        }

        page ??= System.Environment.GetEnvironmentVariable("JITHUB_PREVIEW_PAGE");
        scenario ??= System.Environment.GetEnvironmentVariable("JITHUB_PREVIEW_SCENARIO");
        theme ??= System.Environment.GetEnvironmentVariable("JITHUB_PREVIEW_THEME");
        palette ??= System.Environment.GetEnvironmentVariable("JITHUB_PREVIEW_PALETTE");
        repository ??= System.Environment.GetEnvironmentVariable("JITHUB_PREVIEW_REPOSITORY");
        branch ??= System.Environment.GetEnvironmentVariable("JITHUB_PREVIEW_BRANCH");

        return new LaunchOptions(
            page,
            scenario,
            theme,
            palette,
            repository,
            branch,
            markdownLifecycleFixture,
            markdownLifecycleHost,
            markdownCorpusPath,
            readmeProductionAudit,
            websiteShowcase,
            offlineSameByteReadmeAudit);
    }

    private static void ThrowIfOfflineAuditCredentialsPresent()
    {
        if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("JITHUB_README_AUDIT_GITHUB_TOKEN")) ||
            !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("JITHUB_README_AUDIT_GITHUB_ACCOUNT_ID")))
        {
            throw new System.InvalidOperationException(
                "Offline same-byte README replay refuses inherited GitHub audit token or account credentials.");
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetString(JsonElement element, string name, out string? value)
    {
        if (TryGetPropertyIgnoreCase(element, name, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return value is not null;
        }

        value = null;
        return false;
    }

    private static bool IsHex(string? value, int length)
    {
        if (value is null || value.Length != length)
            return false;
        foreach (char character in value)
        {
            if (!((character >= '0' && character <= '9') ||
                (character >= 'a' && character <= 'f') ||
                (character >= 'A' && character <= 'F')))
                return false;
        }
        return true;
    }

    internal static IReadOnlyList<string> TokenizeActivationArguments(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || commandLine.Length > MaximumActivationArgumentLength)
        {
            return [];
        }

        List<string> arguments = [];
        int index = 0;
        while (index < commandLine.Length && arguments.Count < MaximumActivationArgumentCount)
        {
            while (index < commandLine.Length && char.IsWhiteSpace(commandLine[index]))
            {
                index++;
            }

            if (index >= commandLine.Length)
            {
                break;
            }

            System.Text.StringBuilder argument = new();
            bool inQuotes = false;
            bool started = false;
            while (index < commandLine.Length)
            {
                char current = commandLine[index];
                if (!inQuotes && char.IsWhiteSpace(current))
                {
                    break;
                }

                if (current == '\\')
                {
                    int slashStart = index;
                    while (index < commandLine.Length && commandLine[index] == '\\')
                    {
                        index++;
                    }

                    int slashCount = index - slashStart;
                    if (index < commandLine.Length && commandLine[index] == '"')
                    {
                        argument.Append('\\', slashCount / 2);
                        if (slashCount % 2 == 0)
                        {
                            inQuotes = !inQuotes;
                        }
                        else
                        {
                            argument.Append('"');
                        }

                        index++;
                    }
                    else
                    {
                        argument.Append('\\', slashCount);
                    }

                    started = true;
                    continue;
                }

                if (current == '"')
                {
                    if (inQuotes && index + 1 < commandLine.Length && commandLine[index + 1] == '"')
                    {
                        argument.Append('"');
                        index += 2;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                        index++;
                    }

                    started = true;
                    continue;
                }

                argument.Append(current);
                started = true;
                index++;
            }

            if (started)
            {
                arguments.Add(argument.ToString());
            }

            while (index < commandLine.Length && char.IsWhiteSpace(commandLine[index]))
            {
                index++;
            }
        }

        return arguments;
    }
}

internal sealed record OfflineSameByteReadmeAuditPin(
    int Rank,
    string Repository,
    string CommitSha,
    bool ReadmeAvailable,
    string ReadmePath,
    string ReadmeGitBlobSha1,
    long ReadmeByteSize,
    string PinnedManifestPath,
    string PinnedManifestSha256);
