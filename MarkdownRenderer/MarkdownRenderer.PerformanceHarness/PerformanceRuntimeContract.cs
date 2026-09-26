using System.Collections;
using System.Text.Json;

namespace MarkdownRenderer.PerformanceHarness;

internal static class PerformanceRuntimeContract
{
    private const string TieredCompilation = "System.Runtime.TieredCompilation";
    private const string TieredPgo = "System.Runtime.TieredPGO";
    private const string ConcurrentGc = "System.GC.Concurrent";
    private const string ReadyToRun = "System.Runtime.ReadyToRun";

    internal static RuntimeConfigurationEvidence Capture(string runtimeConfigPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeConfigPath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(runtimeConfigPath));
        JsonElement properties = document.RootElement
            .GetProperty("runtimeOptions")
            .GetProperty("configProperties");

        bool tieredCompilation = ReadBoolean(properties, TieredCompilation);
        bool tieredPgo = ReadBoolean(properties, TieredPgo);
        bool concurrentGc = ReadBoolean(properties, ConcurrentGc);
        bool readyToRun = ReadBoolean(properties, ReadyToRun);
        List<string> overrides = GetRuntimeOverrideEnvironmentVariables();

        return new RuntimeConfigurationEvidence
        {
            Policy = PerformanceMeasurementContract.RuntimeConfigurationPolicy,
            TieredCompilationEnabled = tieredCompilation,
            TieredPgoEnabled = tieredPgo,
            ConcurrentGcEnabled = concurrentGc,
            ReadyToRunEnabled = readyToRun,
            OverrideEnvironmentVariables = overrides,
            Passed = !tieredCompilation &&
                     !tieredPgo &&
                     !concurrentGc &&
                     !readyToRun &&
                     overrides.Count == 0,
        };
    }

    internal static bool IsValid(RuntimeConfigurationEvidence? evidence)
        => evidence is not null &&
           evidence.Policy == PerformanceMeasurementContract.RuntimeConfigurationPolicy &&
           !evidence.TieredCompilationEnabled &&
           !evidence.TieredPgoEnabled &&
           !evidence.ConcurrentGcEnabled &&
           !evidence.ReadyToRunEnabled &&
           evidence.OverrideEnvironmentVariables is { Count: 0 } &&
           evidence.Passed;

    private static bool ReadBoolean(JsonElement properties, string name)
    {
        if (!properties.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException(
                $"Runtime configuration must explicitly declare boolean '{name}'.");
        }

        return value.GetBoolean();
    }

    private static List<string> GetRuntimeOverrideEnvironmentVariables()
    {
        var names = new List<string>();
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is not string name)
                continue;
            if (name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }
}
