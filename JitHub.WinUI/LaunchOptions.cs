namespace JitHub.WinUI;

internal sealed record LaunchOptions(
    string? Page = null,
    string? Scenario = null,
    string? Theme = null,
    string? Repository = null,
    string? Branch = null)
{
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
        (Page is not null && Page.Equals("home", System.StringComparison.OrdinalIgnoreCase));

    public string RepositoryFullName =>
        string.IsNullOrWhiteSpace(Repository) ? DefaultRepository : Repository.Trim();

    public static LaunchOptions Parse(string[]? args)
    {
        string? page = null;
        string? scenario = null;
        string? theme = null;
        string? repository = null;
        string? branch = null;

        if (args is null)
        {
            args = [];
        }

        foreach (string rawArg in args)
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
            }
        }

        page ??= System.Environment.GetEnvironmentVariable("JITHUB_PREVIEW_PAGE");
        scenario ??= System.Environment.GetEnvironmentVariable("JITHUB_PREVIEW_SCENARIO");
        theme ??= System.Environment.GetEnvironmentVariable("JITHUB_PREVIEW_THEME");
        repository ??= System.Environment.GetEnvironmentVariable("JITHUB_PREVIEW_REPOSITORY");
        branch ??= System.Environment.GetEnvironmentVariable("JITHUB_PREVIEW_BRANCH");

        return new LaunchOptions(page, scenario, theme, repository, branch);
    }
}
