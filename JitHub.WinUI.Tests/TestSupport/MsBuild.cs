using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace JitHub.WinUI.Tests.TestSupport;

/// <summary>
/// Runs MSBuild from the .NET SDK that runs these tests, so a build-definition
/// contract checks what MSBuild computes (imports, conditions, inherited
/// properties, central package versions) instead of how a project file spells it.
/// </summary>
internal static class MsBuild
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>Evaluates <paramref name="projectPath"/> without running a target.</summary>
    public static MsBuildEvaluation Evaluate(
        string projectPath,
        IReadOnlyDictionary<string, string> globalProperties,
        IEnumerable<string> properties,
        IEnumerable<string> itemTypes)
    {
        // With exactly one -getProperty and nothing else MSBuild prints the bare
        // value; also asking for the project name keeps the output JSON.
        List<string> arguments = [.. properties.Append("MSBuildProjectName").Distinct().Select(static name => "-getProperty:" + name)];
        arguments.AddRange(itemTypes.Select(static type => "-getItem:" + type));

        using JsonDocument output = Run(projectPath, globalProperties, arguments, Path.GetDirectoryName(projectPath)!);
        JsonElement root = output.RootElement;
        Dictionary<string, string> evaluatedProperties = new(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("Properties", out JsonElement propertyValues))
        {
            foreach (JsonProperty property in propertyValues.EnumerateObject())
                evaluatedProperties[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        Dictionary<string, IReadOnlyList<MsBuildItem>> evaluatedItems = new(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("Items", out JsonElement itemValues))
        {
            foreach (JsonProperty itemType in itemValues.EnumerateObject())
                evaluatedItems[itemType.Name] = ReadItems(itemType.Value);
        }

        return new MsBuildEvaluation(evaluatedProperties, evaluatedItems);
    }

    /// <summary>
    /// Runs <paramref name="target"/> and returns the items it returns. A probe
    /// project outside the repository passes the repository as
    /// <paramref name="workingDirectory"/> so its global.json picks the SDK.
    /// </summary>
    public static IReadOnlyList<MsBuildItem> GetTargetOutputs(
        string projectPath,
        string target,
        IReadOnlyDictionary<string, string> globalProperties,
        string? workingDirectory = null)
    {
        using JsonDocument output = Run(
            projectPath,
            globalProperties,
            ["-t:" + target, "-getTargetResult:" + target],
            workingDirectory ?? Path.GetDirectoryName(projectPath)!);
        JsonElement result = output.RootElement.GetProperty("TargetResults").GetProperty(target);
        Assert.Equal("Success", result.GetProperty("Result").GetString());
        return ReadItems(result.GetProperty("Items"));
    }

    private static MsBuildItem[] ReadItems(JsonElement items) =>
        items.EnumerateArray()
            .Select(static item => new MsBuildItem(
                item.GetProperty("Identity").GetString()!,
                item.EnumerateObject()
                    .Where(static metadata => metadata.Value.ValueKind == JsonValueKind.String)
                    .ToDictionary(
                        static metadata => metadata.Name,
                        static metadata => metadata.Value.GetString()!,
                        StringComparer.OrdinalIgnoreCase)))
            .ToArray();

    private static JsonDocument Run(
        string projectPath,
        IReadOnlyDictionary<string, string> globalProperties,
        IEnumerable<string> queryArguments,
        string workingDirectory)
    {
        ProcessStartInfo start = new(DotNetHost())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // The global.json above the working directory picks the SDK, as it
            // does for a command-line build.
            WorkingDirectory = workingDirectory,
        };
        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add(projectPath);
        start.ArgumentList.Add("-nologo");
        start.ArgumentList.Add("-nodeReuse:false");
        foreach ((string name, string value) in globalProperties)
            start.ArgumentList.Add($"-property:{name}={value}");
        foreach (string argument in queryArguments)
            start.ArgumentList.Add(argument);

        // The test host can inherit the paths of the MSBuild that built it; the
        // child must resolve its own SDK the way a command-line build does.
        foreach (string inherited in new[] { "MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBUILD_EXE_PATH" })
            start.Environment.Remove(inherited);
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(Timeout))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"MSBuild did not finish evaluating {projectPath} within {Timeout}.");
        }

        Assert.True(
            process.ExitCode == 0,
            $"MSBuild exited {process.ExitCode} for {projectPath}:{Environment.NewLine}{output.Result}{error.Result}");
        return JsonDocument.Parse(output.Result);
    }

    // DOTNET_HOST_PATH is the muxer that launched this run; DOTNET_ROOT and then
    // PATH are the fallbacks a developer shell provides.
    private static string DotNetHost()
    {
        string? host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(host) && File.Exists(host))
            return host;

        string? root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "dotnet.exe")))
            return Path.Combine(root, "dotnet.exe");

        return "dotnet";
    }
}
