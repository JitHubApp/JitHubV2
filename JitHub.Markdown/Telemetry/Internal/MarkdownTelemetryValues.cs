namespace JitHub.Markdown.Telemetry.Internal;

/// <summary>
/// The identifier-free telemetry dimension values Markdown hosts emit. They are
/// wire values: each one is registered in the app's telemetry emitter catalog,
/// which drops values it does not know.
/// </summary>
internal static class MarkdownTelemetryValues
{
    /// <summary>The <c>action</c> values.</summary>
    internal static class Actions
    {
        internal const string CopyCode = "copy_code";
        internal const string CopySelection = "copy_selection";
        internal const string LoadRemoteImages = "load_remote_images";
        internal const string OpenLink = "open_link";
        internal const string Render = "render";
        internal const string Retry = "retry";
        internal const string ToggleDetails = "toggle_details";
    }

    /// <summary>The <c>result</c> values.</summary>
    internal static class Results
    {
        internal const string Collapsed = "collapsed";
        internal const string Deferred = "deferred";
        internal const string Error = "error";
        internal const string Expanded = "expanded";
        internal const string Failed = "failed";
        internal const string Launched = "launched";
        internal const string Queued = "queued";
        internal const string Rejected = "rejected";
        internal const string Success = "success";
        internal const string Unavailable = "unavailable";
    }

    /// <summary>The <c>error_kind</c> values.</summary>
    internal static class ErrorKinds
    {
        internal const string Launch = "launch";
        internal const string Unexpected = "unexpected";
    }

    /// <summary>The <c>source</c> values.</summary>
    internal static class Sources
    {
        internal const string User = "user";
    }
}
