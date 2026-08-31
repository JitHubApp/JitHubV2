using System.Collections.Generic;
using System.Linq;

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
    bool WebsiteShowcase = false)
{
    private const int MaximumActivationArgumentLength = 32_767;
    private const int MaximumActivationArgumentCount = 128;
    private const string DefaultRepository = "JitHubApp/JitHubV2";

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
        bool websiteShowcase = false;

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
            websiteShowcase);
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
