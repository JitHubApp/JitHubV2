using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ReadmeAuditTerminalDiagnosticTests
{
    [Fact]
    public void KnownNativeExceptionFamiliesUseOnlyAllowListedCategoryAndNumericHResult()
    {
        AssertCategory(new TimeoutException("private path C:\\runner\\readme.md"), "timeout");
        AssertCategory(new IOException("private URL https://example.invalid/private"), "io");
        AssertCategory(new InvalidDataException("README excerpt must not reach CI"), "protocol");
        AssertCategory(new Win32Exception(5, "private account name"), "win32");
        AssertCategory(new COMException("private native message", unchecked((int)0x80004005)), "interop");
    }

    [Fact]
    public void UnknownExceptionIsSanitizedWithoutItsTypeOrMessage()
    {
        const string privateMessage = "C:\\users\\secret\\README.md https://example.invalid/token";
        ReadmeAuditNativeFailureDiagnostic diagnostic =
            global::ReadmeAuditTerminalDiagnostic.CreateNativeFailureDiagnostic(
                new PrivateFixtureException(privateMessage));

        string output = diagnostic.ToTerminalText();

        Assert.StartsWith("nativeFailure category=other hresult=0x", output, StringComparison.Ordinal);
        Assert.DoesNotContain(privateMessage, output, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(PrivateFixtureException), output, StringComparison.Ordinal);
        Assert.Matches("^nativeFailure category=other hresult=0x[0-9A-F]{8}$", output);
    }

    [Fact]
    public void FailureSummaryEmitsNativeTokenOnlyWhenNativeCaptureIsAbsent()
    {
        ReadmeAuditNativeFailureDiagnostic diagnostic =
            global::ReadmeAuditTerminalDiagnostic.CreateNativeFailureDiagnostic(
                new TimeoutException("private timeout detail"));
        string withoutNativeCapture = global::ReadmeAuditTerminalDiagnostic.CreateFailureSummary(
            rank: 76,
            browserCaptured: true,
            nativeCaptured: false,
            infrastructureFailure: false,
            readinessProgressPath: string.Empty,
            nativeFailure: diagnostic);
        string withNativeCapture = global::ReadmeAuditTerminalDiagnostic.CreateFailureSummary(
            rank: 76,
            browserCaptured: true,
            nativeCaptured: true,
            infrastructureFailure: false,
            readinessProgressPath: string.Empty,
            nativeFailure: diagnostic);

        Assert.Contains("nativeFailure category=timeout", withoutNativeCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("nativeFailure", withNativeCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("private timeout detail", withoutNativeCapture, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingBoundedProgressReasonRemainsInFailureSummary()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ReadmeAuditTerminalDiagnosticTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string progressPath = Path.Combine(directory, "progress.ndjson");
        try
        {
            File.WriteAllText(progressPath,
                "{\"Stage\":\"polling\",\"Reason\":\"visible-images-loading\",\"Generation\":7,\"CurrentGeneration\":7}\n");

            string output = global::ReadmeAuditTerminalDiagnostic.CreateFailureSummary(
                rank: 76,
                browserCaptured: false,
                nativeCaptured: false,
                infrastructureFailure: false,
                readinessProgressPath: progressPath,
                nativeFailure: new ReadmeAuditNativeFailureDiagnostic(
                    ReadmeAuditNativeFailureCategory.Timeout,
                    0x80131505));

            Assert.Contains("readinessProgress stage=polling reason=visible-images-loading", output, StringComparison.Ordinal);
            Assert.Contains("nativeFailure category=timeout hresult=0x80131505", output, StringComparison.Ordinal);
            Assert.DoesNotContain(directory, output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SuccessDiagnosticIsUnchangedWhenNoFailureTokenIsProvided()
    {
        string original = global::ReadmeAuditTerminalDiagnostic.CreateFailureSummary(
            rank: 12,
            browserCaptured: true,
            nativeCaptured: true,
            infrastructureFailure: false,
            readinessProgressPath: string.Empty);
        string withIgnoredToken = global::ReadmeAuditTerminalDiagnostic.CreateFailureSummary(
            rank: 12,
            browserCaptured: true,
            nativeCaptured: true,
            infrastructureFailure: false,
            readinessProgressPath: string.Empty,
            nativeFailure: new ReadmeAuditNativeFailureDiagnostic(
                ReadmeAuditNativeFailureCategory.Io,
                0x8007001F));

        Assert.Equal(original, withIgnoredToken);
        Assert.Equal(
            "README audit failure rank=12 browserCaptured=true nativeCaptured=true infrastructureFailure=false; readinessProgress=unavailable",
            original);
    }

    private static void AssertCategory(Exception exception, string expectedCategory)
    {
        string output = global::ReadmeAuditTerminalDiagnostic
            .CreateNativeFailureDiagnostic(exception)
            .ToTerminalText();

        Assert.StartsWith($"nativeFailure category={expectedCategory} hresult=0x", output, StringComparison.Ordinal);
        Assert.Matches($"^nativeFailure category={expectedCategory} hresult=0x[0-9A-F]{{8}}$", output);
        Assert.DoesNotContain(exception.Message, output, StringComparison.Ordinal);
        Assert.DoesNotContain(exception.GetType().FullName!, output, StringComparison.Ordinal);
    }

    private sealed class PrivateFixtureException : Exception
    {
        internal PrivateFixtureException(string message) : base(message) { }
    }
}
