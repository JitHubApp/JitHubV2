using System;
using System.Text.Json.Serialization;

namespace JitHub.Services.Markdown;

/// <summary>
/// AOT-safe JSON metadata for the files <see cref="MarkdownLifecycleAutomationBridge"/>
/// exchanges with the lifecycle automation harness. The nested records are the
/// wire shapes; the harness reads them by property name.
/// </summary>
/// <remarks><c>partial</c> because the System.Text.Json source generator completes it.</remarks>
[JsonSerializable(typeof(MarkdownLifecycleJsonContext.ReadySignal), TypeInfoPropertyName = "LifecycleReadySignal")]
[JsonSerializable(typeof(MarkdownLifecycleJsonContext.ResourceMapSignal), TypeInfoPropertyName = "ResourceMapFallbackSignal")]
[JsonSerializable(typeof(MarkdownLifecycleJsonContext.LinkRouteEntry), TypeInfoPropertyName = "LinkRouteSignal")]
[JsonSerializable(typeof(MarkdownLifecycleJsonContext.ImageUnavailableEntry), TypeInfoPropertyName = "ImageUnavailableSignal")]
[JsonSerializable(typeof(MarkdownLifecycleJsonContext.RuntimeSettingsFile), TypeInfoPropertyName = "RuntimeSettings")]
internal sealed partial class MarkdownLifecycleJsonContext : JsonSerializerContext
{
    internal sealed record ReadySignal(int ProcessId, string Stage, DateTimeOffset Timestamp);

    internal sealed record ResourceMapSignal(int ProcessId, string Fallback, DateTimeOffset Timestamp);

    internal sealed record LinkRouteEntry(
        int ProcessId,
        string Host,
        string Disposition,
        string Uri,
        DateTimeOffset Timestamp);

    internal sealed record ImageUnavailableEntry(
        int ProcessId,
        string Host,
        string Source,
        string Reason,
        DateTimeOffset Timestamp);

    internal sealed record RuntimeSettingsFile(double TextScaleFactor, int Revision);
}
