using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace JitHub.Services.Markdown;

/// <summary>
/// File-backed coordination for isolated Markdown lifecycle and production-content audits.
/// Production behavior is unchanged unless an explicit automation launch mode is enabled.
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
    private const string ImageResolutionEvidencePathVariable = "JITHUB_MARKDOWN_IMAGE_RESOLUTION_EVIDENCE_PATH";
    private const string RenderFailureEvidencePathVariable = "JITHUB_MARKDOWN_RENDER_FAILURE_EVIDENCE_PATH";
    private const string RenderCompleteEvidencePathVariable = "JITHUB_MARKDOWN_RENDER_COMPLETE_EVIDENCE_PATH";
    private const string CaptureRequestPathVariable = "JITHUB_MARKDOWN_CAPTURE_REQUEST_PATH";
    private const string CaptureResponsePathVariable = "JITHUB_MARKDOWN_CAPTURE_RESPONSE_PATH";
    private const string HighContrastVariable = "JITHUB_AUTOMATION_HIGH_CONTRAST";
    private const string ResourceMapAbsentVariable = "JITHUB_AUTOMATION_RESOURCE_MAP_ABSENT";
    private const string ResourceMapEvidencePathVariable = "JITHUB_AUTOMATION_RESOURCE_MAP_EVIDENCE_PATH";

    private static readonly object SignalGate = new();
    private static string? _signaledHost;
    private static bool _launchFixtureEnabled;
    private static bool _productionAuditEnabled;
    private static string? _launchTargetHost;

    public static bool IsEnabled => _launchFixtureEnabled || IsOne(FixtureVariable);

    public static bool IsEvidenceEnabled => IsEnabled || _productionAuditEnabled;

    public static bool IsHighContrastEnabled => IsEnabled && IsOne(HighContrastVariable);

    public static bool IsResourceMapForcedAbsent => IsEnabled && IsOne(ResourceMapAbsentVariable);

    public static string? TargetHost => IsEvidenceEnabled
        ? _launchTargetHost ?? Environment.GetEnvironmentVariable(TargetHostVariable)
        : null;

    public static void ConfigureLaunchOptions(
        bool fixtureEnabled,
        string? targetHost,
        bool productionAuditEnabled = false)
    {
        _launchFixtureEnabled = fixtureEnabled;
        _productionAuditEnabled = productionAuditEnabled;
        _launchTargetHost = string.IsNullOrWhiteSpace(targetHost) ? null : targetHost.Trim();
    }

    public static bool TargetsHost(string automationId) =>
        IsEvidenceEnabled &&
        !string.IsNullOrWhiteSpace(automationId) &&
        (string.IsNullOrWhiteSpace(TargetHost) ||
         automationId.StartsWith(TargetHost, StringComparison.Ordinal));

    public static void SignalAppReady()
    {
        if (!IsEvidenceEnabled)
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
        if (!IsEvidenceEnabled || string.IsNullOrWhiteSpace(automationId))
        {
            return;
        }

        string? target = TargetHost;
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
        MarkdownRenderer.Images.MarkdownImageUnavailableReason reason,
        MarkdownRenderer.Images.MarkdownSvgFailureReason? svgFailureReason = null)
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
                        svgFailureReason?.ToString(),
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

    public static void RecordImageResolution(
        string source,
        MarkdownRenderer.Images.MarkdownImageResolution resolution)
    {
        if (!IsEvidenceEnabled)
        {
            return;
        }

        string? path = Environment.GetEnvironmentVariable(ImageResolutionEvidencePathVariable);
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
                    new ImageResolutionSignal(
                        Environment.ProcessId,
                        source,
                        resolution.IsHandled,
                        resolution.Asset is not null,
                        resolution.Asset?.Bytes.Length ?? 0,
                        resolution.Asset?.ContentType,
                        resolution.Asset?.ResolvedUri?.AbsoluteUri,
                        resolution.UnavailableReason.ToString(),
                        DateTimeOffset.UtcNow),
                    MarkdownLifecycleJsonContext.Default.ImageResolutionSignal);
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

    public static void RecordRenderFailure(string automationId, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!TargetsHost(automationId))
        {
            return;
        }

        RecordAuditFailure("markdown-render", exception);
    }

    /// <summary>
    /// Records a production-audit failure that prevents the targeted Markdown
    /// surface from being created. This is inert outside explicit automation
    /// launches and preserves the original exception for the audit report.
    /// </summary>
    public static void RecordAuditFailure(string stage, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentNullException.ThrowIfNull(exception);
        if (!IsEvidenceEnabled)
        {
            return;
        }

        string? path = Environment.GetEnvironmentVariable(RenderFailureEvidencePathVariable);
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
                File.WriteAllText(fullPath, $"Stage: {stage}{Environment.NewLine}{exception}");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static void RecordRenderComplete(string automationId)
    {
        if (!TargetsHost(automationId))
        {
            return;
        }

        WriteSignal(
            Environment.GetEnvironmentVariable(RenderCompleteEvidencePathVariable),
            new RenderCompleteSignal(Environment.ProcessId, automationId, DateTimeOffset.UtcNow),
            MarkdownLifecycleJsonContext.Default.RenderCompleteSignal);
    }

    public static bool TryReadCaptureRequest(
        string automationId,
        out MarkdownAuditCaptureRequest? request)
    {
        request = null;
        if (!TargetsHost(automationId))
            return false;

        string? path = Environment.GetEnvironmentVariable(CaptureRequestPathVariable);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        try
        {
            using FileStream stream = new(
                Path.GetFullPath(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            request = JsonSerializer.Deserialize(
                stream,
                MarkdownLifecycleJsonContext.Default.MarkdownAuditCaptureRequest);
            return request is { RequestId.Length: > 0 };
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

    public static void RecordCaptureResponse(
        string requestId,
        bool succeeded,
        int width,
        int height,
        double documentTop,
        string? error)
    {
        WriteSignal(
            Environment.GetEnvironmentVariable(CaptureResponsePathVariable),
            new MarkdownAuditCaptureResponse(
                requestId,
                succeeded,
                width,
                height,
                documentTop,
                error),
            MarkdownLifecycleJsonContext.Default.MarkdownAuditCaptureResponse);
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
        string? SvgFailureReason,
        DateTimeOffset Timestamp);

    private sealed record ImageResolutionSignal(
        int ProcessId,
        string Source,
        bool IsHandled,
        bool HasAsset,
        int ByteLength,
        string? ContentType,
        string? ResolvedUri,
        string Reason,
        DateTimeOffset Timestamp);

    private sealed record RenderCompleteSignal(int ProcessId, string Host, DateTimeOffset Timestamp);

    internal sealed record MarkdownAuditCaptureRequest(
        string RequestId,
        string? OutputPath,
        bool Save);

    private sealed record MarkdownAuditCaptureResponse(
        string RequestId,
        bool Succeeded,
        int Width,
        int Height,
        double DocumentTop,
        string? Error);

    private sealed record MarkdownLifecycleRuntimeSettings(double TextScaleFactor, int Revision);

    [JsonSerializable(typeof(LifecycleReadySignal), TypeInfoPropertyName = "LifecycleReadySignal")]
    [JsonSerializable(typeof(ResourceMapFallbackSignal), TypeInfoPropertyName = "ResourceMapFallbackSignal")]
    [JsonSerializable(typeof(LinkRouteSignal), TypeInfoPropertyName = "LinkRouteSignal")]
    [JsonSerializable(typeof(ImageUnavailableSignal), TypeInfoPropertyName = "ImageUnavailableSignal")]
    [JsonSerializable(typeof(ImageResolutionSignal), TypeInfoPropertyName = "ImageResolutionSignal")]
    [JsonSerializable(typeof(RenderCompleteSignal), TypeInfoPropertyName = "RenderCompleteSignal")]
    [JsonSerializable(typeof(MarkdownAuditCaptureRequest), TypeInfoPropertyName = "MarkdownAuditCaptureRequest")]
    [JsonSerializable(typeof(MarkdownAuditCaptureResponse), TypeInfoPropertyName = "MarkdownAuditCaptureResponse")]
    [JsonSerializable(typeof(MarkdownLifecycleRuntimeSettings), TypeInfoPropertyName = "RuntimeSettings")]
    private sealed partial class MarkdownLifecycleJsonContext : JsonSerializerContext
    {
    }
}
