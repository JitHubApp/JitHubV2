using JitHub.Services;

internal sealed record ProductPerformanceGateOptions(
    string Command,
    string? AppPath,
    string OutputPath,
    string ArtifactsPath,
    string Configuration,
    int Iterations,
    IReadOnlyList<ProductPerformanceFixture>? Fixtures,
    IReadOnlyList<string>? Routes,
    string Repository)
{
    public static ProductPerformanceGateOptions Parse(string[] args)
    {
        string command = args.FirstOrDefault(static argument => !argument.StartsWith("--", StringComparison.Ordinal))
            ?.Trim().ToLowerInvariant() ?? "run";
        string? appPath = null;
        string outputPath = Path.Combine("artifacts", "performance", "product-performance-report.json");
        string artifactsPath = Path.Combine("artifacts", "performance", "runs");
        string configuration = "Release";
        int iterations = 10;
        List<ProductPerformanceFixture>? fixtures = null;
        List<string>? routes = null;
        string repository = "JitHubApp/JitHubV2";

        foreach (string argument in args.Where(static argument => argument.StartsWith("--", StringComparison.Ordinal)))
        {
            if (TryValue(argument, "--app=", out string? value))
            {
                appPath = Path.GetFullPath(value);
            }
            else if (TryValue(argument, "--output=", out value))
            {
                outputPath = Path.GetFullPath(value);
            }
            else if (TryValue(argument, "--artifacts=", out value))
            {
                artifactsPath = Path.GetFullPath(value);
            }
            else if (TryValue(argument, "--configuration=", out value))
            {
                configuration = value;
            }
            else if (TryValue(argument, "--iterations=", out value) &&
                     int.TryParse(value, out int parsedIterations))
            {
                iterations = parsedIterations;
            }
            else if (TryValue(argument, "--fixtures=", out value))
            {
                fixtures = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(static fixture => Enum.Parse<ProductPerformanceFixture>(fixture, ignoreCase: true))
                    .ToList();
            }
            else if (TryValue(argument, "--routes=", out value))
            {
                routes = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .ToList();
            }
            else if (TryValue(argument, "--repo=", out value))
            {
                repository = value;
            }
            else
            {
                throw new ArgumentException($"Unknown performance argument '{argument}'.");
            }
        }

        if (command is not ("run" or "gate" or "plan"))
        {
            throw new ArgumentException($"Unknown performance command '{command}'. Use run, gate, or plan.");
        }

        if (command == "run" && string.IsNullOrWhiteSpace(appPath))
        {
            throw new ArgumentException("The run command requires --app=<path>.");
        }

        if (command == "run" && !File.Exists(appPath))
        {
            throw new FileNotFoundException("The JitHub executable was not found.", appPath);
        }

        string normalizedRepository = repository.Trim();
        string[] repositorySegments = normalizedRepository.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (repositorySegments.Length != 2 ||
            repositorySegments.Any(static segment => segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new ArgumentException("--repo must be an owner/repository pair.");
        }

        return new ProductPerformanceGateOptions(
            command,
            appPath,
            Path.GetFullPath(outputPath),
            Path.GetFullPath(artifactsPath),
            string.IsNullOrWhiteSpace(configuration) ? "Release" : configuration.Trim(),
            iterations,
            fixtures,
            routes,
            normalizedRepository);
    }

    private static bool TryValue(string argument, string prefix, out string value)
    {
        if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = argument[prefix.Length..].Trim();
            if (value.Length == 0)
            {
                throw new ArgumentException($"'{prefix}' requires a value.");
            }

            return true;
        }

        value = string.Empty;
        return false;
    }
}
