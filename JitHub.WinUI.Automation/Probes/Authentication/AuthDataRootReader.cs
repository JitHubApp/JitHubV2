using System.Text;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;

namespace JitHub.WinUI.Automation.Probes.Authentication;

/// <summary>
/// Reads the credentials, settings and automation markers the app writes under an automation data root.
/// </summary>
internal static class AuthDataRootReader
{
    internal static IReadOnlyDictionary<string, string> ReadAuthCredentials(string dataRoot)
    {
        string path = Path.Combine(dataRoot, "Local", "AuthLifecycle", "credentials.vault");
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return values;
        }
        foreach (string line in File.ReadLines(path))
        {
            string[] parts = line.Split('\t');
            if (parts.Length != 3)
            {
                continue;
            }
            try
            {
                values[DecodeBase64(parts[1])] = DecodeBase64(parts[2]);
            }
            catch (FormatException)
            {
            }
        }
        return values;
    }

    internal static string? ReadAuthSetting(string dataRoot, string key)
    {
        string path = Path.Combine(dataRoot, "Local", "Settings", "settings.json");
        if (!File.Exists(path))
        {
            return null;
        }
        foreach (string line in File.ReadLines(path))
        {
            int separator = line.IndexOf('|');
            if (separator <= 0)
            {
                continue;
            }
            try
            {
                if (string.Equals(DecodeBase64(line[..separator]), key, StringComparison.Ordinal))
                {
                    return DecodeBase64(line[(separator + 1)..]);
                }
            }
            catch (FormatException)
            {
            }
        }
        return null;
    }

    internal static void AssertAuthMarker(string dataRoot, string marker) =>
        _ = WaitForAuthMarkerValue(dataRoot, marker, TimeSpan.FromSeconds(8));

    internal static string WaitForAuthMarkerValue(string dataRoot, string marker, TimeSpan timeout)
    {
        string? value = null;
        WaitUntil(
            $"auth marker '{marker}'",
            () => TryReadAuthMarker(dataRoot, marker, out value),
            timeout);
        return value ?? string.Empty;
    }

    private static bool TryReadAuthMarker(string dataRoot, string marker, out string? value)
    {
        value = null;
        string path = Path.Combine(dataRoot, "Local", "AuthLifecycle", "scenario-state.ndjson");
        if (!File.Exists(path))
        {
            return false;
        }
        foreach (string line in File.ReadLines(path).Reverse())
        {
            string[] parts = line.Split('\t');
            if (parts.Length == 3 && string.Equals(parts[1], marker, StringComparison.Ordinal))
            {
                try
                {
                    value = DecodeBase64(parts[2]);
                    return true;
                }
                catch (FormatException)
                {
                    return false;
                }
            }
        }
        return false;
    }

    private static string DecodeBase64(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
}
