using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace JitHub.Services.Markdown;

/// <summary>
/// File-backed lifecycle coordination used only by isolated Markdown automation runs.
/// Production behavior is unchanged unless the explicit lifecycle fixture is enabled.
/// </summary>
internal static partial class MarkdownLifecycleAutomationBridge
{
    private const string FixtureVariable = "JITHUB_MARKDOWN_LIFECYCLE_FIXTURE";
    private const string TargetHostVariable = "JITHUB_MARKDOWN_LIFECYCLE_HOST";
    private const string AppReadyPathVariable = "JITHUB_MARKDOWN_APP_READY_PATH";
    private const string HostReadyPathVariable = "JITHUB_MARKDOWN_HOST_READY_PATH";
    private const string RuntimeSettingsPathVariable = "JITHUB_MARKDOWN_RUNTIME_SETTINGS_PATH";
    private const string LinkEvidencePathVariable = "JITHUB_MARKDOWN_LINK_EVIDENCE_PATH";
    private const string ImageEvidencePathVariable = "JITHUB_MARKDOWN_IMAGE_EVIDENCE_PATH";
    private const string HighContrastVariable = "JITHUB_AUTOMATION_HIGH_CONTRAST";
    private const string ResourceMapAbsentVariable = "JITHUB_AUTOMATION_RESOURCE_MAP_ABSENT";
    private const string ResourceMapEvidencePathVariable = "JITHUB_AUTOMATION_RESOURCE_MAP_EVIDENCE_PATH";

    private static readonly object SignalGate = new();
    private static string? _signaledHost;

    public static bool IsEnabled => IsOne(FixtureVariable);

    public static bool IsHighContrastEnabled => IsEnabled && IsOne(HighContrastVariable);

    public static bool IsResourceMapForcedAbsent => IsEnabled && IsOne(ResourceMapAbsentVariable);

    public static string? TargetHost => IsEnabled
        ? Environment.GetEnvironmentVariable(TargetHostVariable)
        : null;

    public static bool TargetsHost(string automationId) =>
        IsEnabled &&
        !string.IsNullOrWhiteSpace(automationId) &&
        (string.IsNullOrWhiteSpace(TargetHost) ||
         automationId.StartsWith(TargetHost, StringComparison.Ordinal));

    public static void SignalAppReady()
    {
        if (!IsEnabled)
        {
            return;
        }

        WriteSignal(
            Environment.GetEnvironmentVariable(AppReadyPathVariable),
            new LifecycleReadySignal(Environment.ProcessId, "app", DateTimeOffset.UtcNow),
            MarkdownLifecycleJsonContext.Default.LifecycleReadySignal);
    }

    public static void SignalHostReady(string automationId)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(automationId))
        {
            return;
        }

        string? target = Environment.GetEnvironmentVariable(TargetHostVariable);
        if (!string.IsNullOrWhiteSpace(target) &&
            !automationId.StartsWith(target, StringComparison.Ordinal))
        {
            return;
        }

        lock (SignalGate)
        {
            if (string.Equals(_signaledHost, automationId, StringComparison.Ordinal))
            {
                return;
            }

            WriteSignal(
                Environment.GetEnvironmentVariable(HostReadyPathVariable),
                new LifecycleReadySignal(Environment.ProcessId, automationId, DateTimeOffset.UtcNow),
                MarkdownLifecycleJsonContext.Default.LifecycleReadySignal);
            _signaledHost = automationId;
        }
    }

    public static double? GetTextScaleFactor()
    {
        if (!IsEnabled)
        {
            return null;
        }

        if (TryReadRuntimeSettings(out MarkdownLifecycleRuntimeSettings? settings) &&
            settings is not null &&
            settings.TextScaleFactor > 0)
        {
            return Math.Clamp(settings.TextScaleFactor, 1, 3);
        }

        return double.TryParse(
            Environment.GetEnvironmentVariable("JITHUB_AUTOMATION_TEXT_SCALE_FACTOR"),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double scale)
                ? Math.Clamp(scale, 1, 3)
                : null;
    }

    public static int GetRuntimeSettingsRevision() =>
        IsEnabled &&
        TryReadRuntimeSettings(out MarkdownLifecycleRuntimeSettings? settings) &&
        settings is not null
            ? settings.Revision
            : 0;

    private static bool TryReadRuntimeSettings(out MarkdownLifecycleRuntimeSettings? settings)
    {
        settings = null;
        string? path = Environment.GetEnvironmentVariable(RuntimeSettingsPathVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            settings = JsonSerializer.Deserialize(
                stream,
                MarkdownLifecycleJsonContext.Default.RuntimeSettings);
            return settings is not null;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static void SignalResourceMapFallback(string fallback)
    {
        if (!IsResourceMapForcedAbsent)
        {
            return;
        }

        WriteSignal(
            Environment.GetEnvironmentVariable(ResourceMapEvidencePathVariable),
            new ResourceMapFallbackSignal(Environment.ProcessId, fallback, DateTimeOffset.UtcNow),
            MarkdownLifecycleJsonContext.Default.ResourceMapFallbackSignal);
    }

    public static bool RecordLinkRoute(string automationId, Uri uri, string disposition)
    {
        if (!TargetsHost(automationId) || string.IsNullOrWhiteSpace(disposition))
        {
            return false;
        }

        string? path = Environment.GetEnvironmentVariable(LinkEvidencePathVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        lock (SignalGate)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                string entry = JsonSerializer.Serialize(
                    new LinkRouteSignal(
                        Environment.ProcessId,
                        automationId,
                        disposition,
                        uri.AbsoluteUri,
                        DateTimeOffset.UtcNow),
                    MarkdownLifecycleJsonContext.Default.LinkRouteSignal);
                File.AppendAllText(fullPath, entry + Environment.NewLine);
                return true;
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
    }

    public static void RecordImageUnavailable(
        string automationId,
        string source,
        MarkdownRenderer.Images.MarkdownImageUnavailableReason reason)
    {
        if (!TargetsHost(automationId))
        {
            return;
        }

        string? path = Environment.GetEnvironmentVariable(ImageEvidencePathVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (SignalGate)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                string entry = JsonSerializer.Serialize(
                    new ImageUnavailableSignal(
                        Environment.ProcessId,
                        automationId,
                        source,
                        reason.ToString(),
                        DateTimeOffset.UtcNow),
                    MarkdownLifecycleJsonContext.Default.ImageUnavailableSignal);
                File.AppendAllText(fullPath, entry + Environment.NewLine);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool IsOne(string variable) => string.Equals(
        Environment.GetEnvironmentVariable(variable),
        "1",
        StringComparison.Ordinal);

    private static void WriteSignal<T>(string? path, T signal, JsonTypeInfo<T> jsonTypeInfo)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            string temporaryPath = fullPath + $".{Environment.ProcessId}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(signal, jsonTypeInfo));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record LifecycleReadySignal(int ProcessId, string Stage, DateTimeOffset Timestamp);

    private sealed record ResourceMapFallbackSignal(int ProcessId, string Fallback, DateTimeOffset Timestamp);

    private sealed record LinkRouteSignal(
        int ProcessId,
        string Host,
        string Disposition,
        string Uri,
        DateTimeOffset Timestamp);

    private sealed record ImageUnavailableSignal(
        int ProcessId,
        string Host,
        string Source,
        string Reason,
        DateTimeOffset Timestamp);

    private sealed record MarkdownLifecycleRuntimeSettings(double TextScaleFactor, int Revision);

    [JsonSerializable(typeof(LifecycleReadySignal), TypeInfoPropertyName = "LifecycleReadySignal")]
    [JsonSerializable(typeof(ResourceMapFallbackSignal), TypeInfoPropertyName = "ResourceMapFallbackSignal")]
    [JsonSerializable(typeof(LinkRouteSignal), TypeInfoPropertyName = "LinkRouteSignal")]
    [JsonSerializable(typeof(ImageUnavailableSignal), TypeInfoPropertyName = "ImageUnavailableSignal")]
    [JsonSerializable(typeof(MarkdownLifecycleRuntimeSettings), TypeInfoPropertyName = "RuntimeSettings")]
    private sealed partial class MarkdownLifecycleJsonContext : JsonSerializerContext
    {
    }
}
