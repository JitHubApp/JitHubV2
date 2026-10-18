namespace JitHub.WinUI;

internal sealed record LaunchOptions(string? Page = null, string? Scenario = null, string? Theme = null)
{
    public bool HasPageOverride => !string.IsNullOrWhiteSpace(Page);

    public static LaunchOptions Parse(string[]? args)
    {
        string? page = null;
        string? scenario = null;
        string? theme = null;

        if (args is null)
        {
            return new LaunchOptions();
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
            }
        }

        return new LaunchOptions(page, scenario, theme);
    }
}
