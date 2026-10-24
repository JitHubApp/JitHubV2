using System.Globalization;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

/// <summary>
/// Emits bounded, privacy-safe terminal evidence to CI logs. Free-form case
/// failures, exception messages, README content, asset URLs, and local paths
/// must remain out of this output.
/// </summary>
internal static class ReadmeAuditTerminalDiagnostic
{
    internal static ReadmeAuditNativeFailureDiagnostic CreateNativeFailureDiagnostic(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        ReadmeAuditNativeFailureCategory category = exception switch
        {
            TimeoutException => ReadmeAuditNativeFailureCategory.Timeout,
            InvalidDataException => ReadmeAuditNativeFailureCategory.Protocol,
            IOException => ReadmeAuditNativeFailureCategory.Io,
            Win32Exception => ReadmeAuditNativeFailureCategory.Win32,
            COMException => ReadmeAuditNativeFailureCategory.Interop,
            _ => ReadmeAuditNativeFailureCategory.Other,
        };

        return new ReadmeAuditNativeFailureDiagnostic(category, unchecked((uint)exception.HResult));
    }

    internal static string CreateFailureSummary(
        int rank,
        bool browserCaptured,
        bool nativeCaptured,
        bool infrastructureFailure,
        string readinessProgressPath,
        ReadmeAuditNativeFailureDiagnostic? nativeFailure = null)
    {
        string summary = "README audit failure rank=" + rank.ToString(CultureInfo.InvariantCulture) +
            " browserCaptured=" + (browserCaptured ? "true" : "false") +
            " nativeCaptured=" + (nativeCaptured ? "true" : "false") +
            " infrastructureFailure=" + (infrastructureFailure ? "true" : "false");

        // Native failure details are useful only when no NativeAuditResult was
        // produced. Keep this terminal channel to an allow-listed category and
        // numeric HRESULT; exception messages and stacks can contain paths,
        // URLs, or README-derived content and must remain in the private case
        // artifact only.
        if (!nativeCaptured && nativeFailure is ReadmeAuditNativeFailureDiagnostic failure)
            summary += "; " + failure.ToTerminalText();

        return summary + "; " + NativeReadinessProgressDiagnostic.ReadLastSummary(readinessProgressPath);
    }
}

internal enum ReadmeAuditNativeFailureCategory : byte
{
    Other,
    Timeout,
    Io,
    Protocol,
    Win32,
    Interop,
}

internal readonly record struct ReadmeAuditNativeFailureDiagnostic(
    ReadmeAuditNativeFailureCategory Category,
    uint HResult)
{
    internal string ToTerminalText()
    {
        string category = Category switch
        {
            ReadmeAuditNativeFailureCategory.Timeout => "timeout",
            ReadmeAuditNativeFailureCategory.Io => "io",
            ReadmeAuditNativeFailureCategory.Protocol => "protocol",
            ReadmeAuditNativeFailureCategory.Win32 => "win32",
            ReadmeAuditNativeFailureCategory.Interop => "interop",
            _ => "other",
        };

        return "nativeFailure category=" + category +
            " hresult=0x" + HResult.ToString("X8", CultureInfo.InvariantCulture);
    }
}
