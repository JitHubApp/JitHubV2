using System.Reflection.PortableExecutable;

namespace JitHub.WinUI.Automation.Options;

internal sealed class CaptureOptions
{
    public required string AppPath { get; init; }
    public required string OutputDirectory { get; init; }
    public required IReadOnlyList<string> Themes { get; init; }
    public required IReadOnlyList<CaptureTarget> Targets { get; init; }
    public required string RepositoryFullName { get; init; }
    public string? Probe { get; init; }
    public string? AttachProcess { get; init; }
    public string? Configuration { get; init; }
    public IReadOnlyList<string> ShowcaseIds { get; init; } = [];

    // This method stays whole because it is the single command-line parser: it reads every --flag into locals,
    // validates the app binary and checks that it is fresh, resolves target names against the one alias table, and
    // builds the CaptureOptions from those values.
    public static CaptureOptions Parse(string[] args)
    {
        string outputDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "screenshots", "winui"));
        string? appPath = null;
        string? probe = null;
        string? attachProcess = null;
        string? configuration = null;
        string repoFullName = "JitHubApp/JitHubV2";
        string[] themes = ["light", "dark"];
        string[] targetNames = ["buttons", "inputs", "segments", "navigation", "settings", "repo", "conversation", "pr-timeline", "empty", "login", "settings-page"];
        string[] showcaseIds = [];

        foreach (string arg in args)
        {
            if (arg.StartsWith("--app=", StringComparison.OrdinalIgnoreCase))
            {
                appPath = Path.GetFullPath(arg[6..]);
            }
            else if (arg.StartsWith("--out=", StringComparison.OrdinalIgnoreCase))
            {
                outputDirectory = Path.GetFullPath(arg[6..]);
            }
            else if (arg.StartsWith("--themes=", StringComparison.OrdinalIgnoreCase))
            {
                themes = arg[9..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            else if (arg.StartsWith("--targets=", StringComparison.OrdinalIgnoreCase))
            {
                targetNames = arg[10..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            else if (arg.StartsWith("--probe=", StringComparison.OrdinalIgnoreCase))
            {
                probe = arg[8..].Trim();
            }
            else if (arg.StartsWith("--showcase-ids=", StringComparison.OrdinalIgnoreCase))
            {
                showcaseIds = arg[15..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            else if (arg.StartsWith("--attach-process=", StringComparison.OrdinalIgnoreCase))
            {
                attachProcess = arg[17..].Trim();
            }
            else if (arg.StartsWith("--repo=", StringComparison.OrdinalIgnoreCase))
            {
                repoFullName = arg[7..].Trim();
            }
            else if (arg.StartsWith("--configuration=", StringComparison.OrdinalIgnoreCase))
            {
                configuration = arg[16..].Trim();
            }
        }

        appPath ??= GuessAppPath();
        if (!File.Exists(appPath))
        {
            throw new FileNotFoundException($"Could not find JitHub.WinUI app executable at '{appPath}'. Pass --app=<path>.");
        }

        EnsureAppBinaryIsFresh(appPath);

        Dictionary<string, CaptureTarget> allTargets = new(StringComparer.OrdinalIgnoreCase)
        {
            ["buttons"] = new CaptureTarget("buttons", "design-lab", "buttons", "ScenarioButtons"),
            ["inputs"] = new CaptureTarget("inputs", "design-lab", "inputs", "ScenarioInputs"),
            ["segments"] = new CaptureTarget("segments", "design-lab", "segments", "ScenarioSegments"),
            ["segmented"] = new CaptureTarget("segments", "design-lab", "segments", "ScenarioSegments"),
            ["navigation"] = new CaptureTarget("navigation", "design-lab", "navigation", "ScenarioNavigation"),
            ["settings"] = new CaptureTarget("settings", "design-lab", "settings", "ScenarioSettings"),
            ["repo"] = new CaptureTarget("repo", "design-lab", "repo", "ScenarioRepoReference"),
            ["activities"] = new CaptureTarget("activities", "design-lab", "activities", "ScenarioActivities"),
            ["activity"] = new CaptureTarget("activities", "design-lab", "activities", "ScenarioActivities"),
            ["conversation"] = new CaptureTarget("conversation", "design-lab", "conversation", "ScenarioConversation"),
            ["pr-timeline"] = new CaptureTarget("pr-timeline", "design-lab", "pr-timeline", "ScenarioPullRequestTimeline"),
            ["pull-request-timeline"] = new CaptureTarget("pr-timeline", "design-lab", "pr-timeline", "ScenarioPullRequestTimeline"),
            ["timeline"] = new CaptureTarget("pr-timeline", "design-lab", "pr-timeline", "ScenarioPullRequestTimeline"),
            ["home"] = new CaptureTarget("home", "home", null, null),
            ["shell"] = new CaptureTarget("shell", "shell", null, null),
            ["profile"] = new CaptureTarget("profile", "profile", null, null),
            ["repo-code"] = new CaptureTarget("repo-code", "repo-code", null, null),
            ["real-repo"] = new CaptureTarget("repo-code", "repo-code", null, null),
            ["repo-issues"] = new CaptureTarget("repo-issues", "repo-issues", null, null),
            ["repo-pulls"] = new CaptureTarget("repo-pulls", "repo-pulls", null, null),
            ["repo-pull-requests"] = new CaptureTarget("repo-pulls", "repo-pull-requests", null, null),
            ["repo-commits"] = new CaptureTarget("repo-commits", "repo-commits", null, null),
            ["empty"] = new CaptureTarget("empty", "design-lab", "empty", "ScenarioEmptyState"),
            ["login"] = new CaptureTarget("login", "login", null, null),
            ["settings-page"] = new CaptureTarget("settings-page", "settings", null, null)
        };

        return new CaptureOptions
        {
            AppPath = appPath,
            OutputDirectory = outputDirectory,
            Themes = themes,
            Targets = targetNames.Select(name => allTargets[name]).ToList(),
            RepositoryFullName = string.IsNullOrWhiteSpace(repoFullName) ? "JitHubApp/JitHubV2" : repoFullName,
            Probe = probe,
            AttachProcess = attachProcess,
            Configuration = configuration,
            ShowcaseIds = showcaseIds
        };
    }

    private static string GuessAppPath()
    {
        string baseDirectory = FindRepositoryRoot();
        string[] candidates =
        [
            Path.Combine(baseDirectory, "JitHub.WinUI", "bin", "x64", "Debug", "net10.0-windows10.0.26100.0", "win-x64", "publish", "JitHub.WinUI.exe"),
            Path.Combine(baseDirectory, "JitHub.WinUI", "bin", "x64", "Debug", "net10.0-windows10.0.26100.0", "win-x64", "JitHub.WinUI.exe"),
            Path.Combine(baseDirectory, "JitHub.WinUI", "bin", "x64", "Debug", "net10.0-windows10.0.26100.0", "win-x64", "AppX", "JitHub.WinUI.exe"),
            Path.Combine(baseDirectory, "JitHub.WinUI", "bin", "Debug", "net10.0-windows10.0.26100.0", "win-x64", "JitHub.WinUI.exe"),
            Path.Combine(baseDirectory, "JitHub.WinUI", "bin", "x64", "Release", "net10.0-windows10.0.26100.0", "win-x64", "publish", "JitHub.WinUI.exe")
        ];

        DateTime newestSourceWrite = GetNewestSourceWriteTimeUtc(baseDirectory);
        string? freshCandidate = candidates.FirstOrDefault(candidate =>
        {
            if (!File.Exists(candidate))
            {
                return false;
            }

            string? freshnessArtifact = GetFreshnessArtifact(candidate);
            return freshnessArtifact is not null &&
                File.GetLastWriteTimeUtc(freshnessArtifact) >= newestSourceWrite;
        });

        if (freshCandidate is not null)
        {
            return freshCandidate;
        }

        throw new InvalidOperationException(
            "No fresh JitHub executable was found for UI automation. " +
            "Run 'dotnet publish JitHub.WinUI\\JitHub.WinUI.csproj -c Debug -p:Platform=x64' " +
            "(add '-p:EnablePseudoLocalization=true' for the vnext-pseudo-localization probe) " +
            "or pass an intentional executable with --app=<path>.");
    }

    private static void EnsureAppBinaryIsFresh(string appPath)
    {
        string? freshnessArtifact = GetFreshnessArtifact(appPath);
        if (freshnessArtifact is null)
        {
            throw new InvalidOperationException(
                $"The automation app '{appPath}' is neither a managed JitHub build with an adjacent assembly " +
                "nor a native PE image without a CLR header.");
        }

        string repositoryRoot = FindRepositoryRoot();
        DateTime newestSourceWrite = GetNewestSourceWriteTimeUtc(repositoryRoot);
        DateTime artifactWrite = File.GetLastWriteTimeUtc(freshnessArtifact);
        if (artifactWrite < newestSourceWrite)
        {
            throw new InvalidOperationException(
                $"Refusing stale JitHub automation binary '{appPath}'. " +
                $"Artifact timestamp {artifactWrite:O} predates source timestamp {newestSourceWrite:O}. " +
                "Rebuild the app and pass the rebuilt executable.");
        }
    }

    private static string? GetFreshnessArtifact(string appPath)
    {
        string adjacentAssembly = Path.Combine(Path.GetDirectoryName(appPath)!, "JitHub.WinUI.dll");
        if (File.Exists(adjacentAssembly))
        {
            return adjacentAssembly;
        }

        return IsNativeAotExecutable(appPath) ? appPath : null;
    }

    private static bool IsNativeAotExecutable(string appPath)
    {
        try
        {
            using FileStream stream = File.Open(appPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using PEReader reader = new(stream);
            return reader.PEHeaders.PEHeader is not null &&
                reader.PEHeaders.CorHeader is null;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "JitHub.WinUI")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    private static DateTime GetNewestSourceWriteTimeUtc(string baseDirectory)
    {
        string[] sourceRoots =
        [
            Path.Combine(baseDirectory, "JitHub.WinUI"),
            Path.Combine(baseDirectory, "MarkdownRenderer", "src", "MarkdownRenderer")
        ];
        string[] sourceExtensions = [".cs", ".xaml", ".csproj", ".props", ".targets"];
        DateTime newest = DateTime.MinValue;

        foreach (string sourceRoot in sourceRoots.Where(Directory.Exists))
        {
            foreach (string sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
                string firstSegment = relativePath.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    2,
                    StringSplitOptions.RemoveEmptyEntries)[0];
                if (firstSegment.StartsWith("bin", StringComparison.OrdinalIgnoreCase) ||
                    firstSegment.StartsWith("obj", StringComparison.OrdinalIgnoreCase) ||
                    firstSegment.Equals("artifacts", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!sourceExtensions.Contains(Path.GetExtension(sourcePath), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                DateTime writeTime = File.GetLastWriteTimeUtc(sourcePath);
                if (writeTime > newest)
                {
                    newest = writeTime;
                }
            }
        }

        return newest;
    }
}
