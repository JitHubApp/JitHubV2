using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace JitHub.Services.Markdown;

/// <summary>
/// File-backed lifecycle coordination used only by isolated Markdown automation runs.
/// Production behavior is unchanged unless the explicit lifecycle fixture is enabled.
/// </summary>
/// <remarks>
/// The app creates one instance from its launch options (see <c>App.MarkdownAutomation</c>)
/// and passes it to the automation hooks. Environment variables are read on each
/// query, as the harness may set them after launch; the reader is injectable so
/// the behavior can be checked without touching the process environment.
/// </remarks>
internal sealed class MarkdownLifecycleAutomationBridge
{
    private const string FixtureVariable = "JITHUB_MARKDOWN_LIFECYCLE_FIXTURE";
    private const string TargetHostVariable = "JITHUB_MARKDOWN_LIFECYCLE_HOST";
    private const string AppReadyPathVariable = "JITHUB_MARKDOWN_APP_READY_PATH";
    private const string HostReadyPathVariable = "JITHUB_MARKDOWN_HOST_READY_PATH";
    private const string RuntimeSettingsPathVariable = "JITHUB_MARKDOWN_RUNTIME_SETTINGS_PATH";
    private const string LinkEvidencePathVariable = "JITHUB_MARKDOWN_LINK_EVIDENCE_PATH";
    private const string ImageEvidencePathVariable = "JITHUB_MARKDOWN_IMAGE_EVIDENCE_PATH";
    private const string RenderFailureEvidencePathVariable = "JITHUB_MARKDOWN_RENDER_FAILURE_EVIDENCE_PATH";
    private const string HighContrastVariable = "JITHUB_AUTOMATION_HIGH_CONTRAST";
    private const string ResourceMapAbsentVariable = "JITHUB_AUTOMATION_RESOURCE_MAP_ABSENT";
    private const string ResourceMapEvidencePathVariable = "JITHUB_AUTOMATION_RESOURCE_MAP_EVIDENCE_PATH";
    private const string TextScaleFactorVariable = "JITHUB_AUTOMATION_TEXT_SCALE_FACTOR";

    private readonly object _signalGate = new();
    private readonly bool _launchFixtureEnabled;
    private readonly string? _launchTargetHost;
    private readonly Func<string, string?> _environment;
    private readonly int _processId;
    private string? _signaledHost;

    /// <summary>Creates a bridge that reads the process environment.</summary>
    /// <param name="launchFixtureEnabled">Whether the launch arguments enabled the lifecycle fixture.</param>
    /// <param name="launchTargetHost">The host prefix named by the launch arguments, if any.</param>
    internal MarkdownLifecycleAutomationBridge(bool launchFixtureEnabled, string? launchTargetHost)
        : this(launchFixtureEnabled, launchTargetHost, Environment.GetEnvironmentVariable, Environment.ProcessId)
    {
    }

    /// <param name="launchFixtureEnabled">Whether the launch arguments enabled the lifecycle fixture.</param>
    /// <param name="launchTargetHost">The host prefix named by the launch arguments, if any.</param>
    /// <param name="environment">Reads one environment variable.</param>
    /// <param name="processId">The process id written into evidence files.</param>
    internal MarkdownLifecycleAutomationBridge(
        bool launchFixtureEnabled,
        string? launchTargetHost,
        Func<string, string?> environment,
        int processId)
    {
        _launchFixtureEnabled = launchFixtureEnabled;
        _launchTargetHost = string.IsNullOrWhiteSpace(launchTargetHost) ? null : launchTargetHost.Trim();
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _processId = processId;
    }

    public bool IsEnabled => _launchFixtureEnabled || IsOne(FixtureVariable);

    public bool IsHighContrastEnabled => IsEnabled && IsOne(HighContrastVariable);

    public bool IsResourceMapForcedAbsent => IsEnabled && IsOne(ResourceMapAbsentVariable);

    public string? TargetHost => IsEnabled
        ? _launchTargetHost ?? _environment(TargetHostVariable)
        : null;

    public bool TargetsHost(string automationId) =>
        IsEnabled &&
        !string.IsNullOrWhiteSpace(automationId) &&
        (string.IsNullOrWhiteSpace(TargetHost) ||
         automationId.StartsWith(TargetHost, StringComparison.Ordinal));

    public void SignalAppReady()
    {
        if (!IsEnabled)
        {
            return;
        }

        WriteSignal(
            _environment(AppReadyPathVariable),
            new MarkdownLifecycleJsonContext.ReadySignal(_processId, "app", DateTimeOffset.UtcNow),
            MarkdownLifecycleJsonContext.Default.LifecycleReadySignal);
    }

    public void SignalHostReady(string automationId)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(automationId))
        {
            return;
        }

        string? target = TargetHost;
        if (!string.IsNullOrWhiteSpace(target) &&
            !automationId.StartsWith(target, StringComparison.Ordinal))
        {
            return;
        }

        lock (_signalGate)
        {
            if (string.Equals(_signaledHost, automationId, StringComparison.Ordinal))
            {
                return;
            }

            WriteSignal(
                _environment(HostReadyPathVariable),
                new MarkdownLifecycleJsonContext.ReadySignal(_processId, automationId, DateTimeOffset.UtcNow),
                MarkdownLifecycleJsonContext.Default.LifecycleReadySignal);
            _signaledHost = automationId;
        }
    }

    public double? GetTextScaleFactor()
    {
        if (!IsEnabled)
        {
            return null;
        }

        if (TryReadRuntimeSettings(out MarkdownLifecycleJsonContext.RuntimeSettingsFile? settings) &&
            settings is not null &&
            settings.TextScaleFactor > 0)
        {
            return Math.Clamp(settings.TextScaleFactor, 1, 3);
        }

        return double.TryParse(
            _environment(TextScaleFactorVariable),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double scale)
                ? Math.Clamp(scale, 1, 3)
                : null;
    }

    public int GetRuntimeSettingsRevision() =>
        IsEnabled &&
        TryReadRuntimeSettings(out MarkdownLifecycleJsonContext.RuntimeSettingsFile? settings) &&
        settings is not null
            ? settings.Revision
            : 0;

    public void SignalResourceMapFallback(string fallback)
    {
        if (!IsResourceMapForcedAbsent)
        {
            return;
        }

        WriteSignal(
            _environment(ResourceMapEvidencePathVariable),
            new MarkdownLifecycleJsonContext.ResourceMapSignal(_processId, fallback, DateTimeOffset.UtcNow),
            MarkdownLifecycleJsonContext.Default.ResourceMapFallbackSignal);
    }

    public bool RecordLinkRoute(string automationId, Uri uri, string disposition)
    {
        if (!TargetsHost(automationId) || string.IsNullOrWhiteSpace(disposition))
        {
            return false;
        }

        string? path = _environment(LinkEvidencePathVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string entry = JsonSerializer.Serialize(
            new MarkdownLifecycleJsonContext.LinkRouteEntry(
                _processId,
                automationId,
                disposition,
                uri.AbsoluteUri,
                DateTimeOffset.UtcNow),
            MarkdownLifecycleJsonContext.Default.LinkRouteSignal);
        return TryWriteEvidence(path, entry, append: true);
    }

    public void RecordImageUnavailable(
        string automationId,
        string source,
        MarkdownRenderer.Images.MarkdownImageUnavailableReason reason)
    {
        if (!TargetsHost(automationId))
        {
            return;
        }

        string? path = _environment(ImageEvidencePathVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string entry = JsonSerializer.Serialize(
            new MarkdownLifecycleJsonContext.ImageUnavailableEntry(
                _processId,
                automationId,
                source,
                reason.ToString(),
                DateTimeOffset.UtcNow),
            MarkdownLifecycleJsonContext.Default.ImageUnavailableSignal);
        _ = TryWriteEvidence(path, entry, append: true);
    }

    public void RecordRenderFailure(string automationId, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!TargetsHost(automationId))
        {
            return;
        }

        string? path = _environment(RenderFailureEvidencePathVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _ = TryWriteEvidence(path, exception.ToString(), append: false);
    }

    private bool TryReadRuntimeSettings(out MarkdownLifecycleJsonContext.RuntimeSettingsFile? settings)
    {
        settings = null;
        string? path = _environment(RuntimeSettingsPathVariable);
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // The harness rewrites this file while the app polls it. A partial
            // or locked read means "no new settings yet"; the next poll retries.
            return false;
        }
    }

    /// <summary>Appends a line to, or replaces, an evidence file. Returns whether it was written.</summary>
    private bool TryWriteEvidence(string path, string content, bool append)
    {
        lock (_signalGate)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                if (append)
                {
                    File.AppendAllText(fullPath, content + Environment.NewLine);
                }
                else
                {
                    File.WriteAllText(fullPath, content);
                }

                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Evidence is best effort: the harness reports missing evidence as
                // a failed check, and the product must keep running regardless.
                return false;
            }
        }
    }

    private bool IsOne(string variable) => string.Equals(_environment(variable), "1", StringComparison.Ordinal);

    private void WriteSignal<T>(string? path, T signal, JsonTypeInfo<T> jsonTypeInfo)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            string temporaryPath = fullPath + $".{_processId}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(signal, jsonTypeInfo));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Readiness signals are best effort: the harness times out and reports
            // the missing signal, and the product must keep running regardless.
        }
    }
}
