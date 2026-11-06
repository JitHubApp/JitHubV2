namespace MarkdownRenderer.Sample.Automation;

/// <summary>
/// The driver's command line: <c>--app-path &lt;exe&gt;</c> names the sample
/// executable, and <c>--narrator-smoke</c> selects the Narrator pass instead of
/// the probe suite.
/// </summary>
internal sealed record AutomationOptions(string AppPath, bool NarratorSmoke)
{
    internal const string AppPathSwitch = "--app-path";
    internal const string NarratorSmokeSwitch = "--narrator-smoke";

    /// <summary>
    /// Parses <paramref name="args"/>. The default app path is looked up only
    /// when <c>--app-path</c> is absent.
    /// </summary>
    /// <exception cref="InvalidOperationException">No sample executable can be found.</exception>
    internal static AutomationOptions Parse(IReadOnlyList<string> args, Func<string?> findDefaultAppPath)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(findDefaultAppPath);
        string appPath = ParseAppPath(args)
            ?? findDefaultAppPath()
            ?? throw new InvalidOperationException(
                "Cannot locate MarkdownRenderer.Sample.exe. Pass --app-path <exe>.");
        bool narratorSmoke = args.Contains(NarratorSmokeSwitch, StringComparer.OrdinalIgnoreCase);
        return new AutomationOptions(appPath, narratorSmoke);
    }

    private static string? ParseAppPath(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count - 1; i++)
            if (args[i] == AppPathSwitch) return args[i + 1];
        return null;
    }
}
