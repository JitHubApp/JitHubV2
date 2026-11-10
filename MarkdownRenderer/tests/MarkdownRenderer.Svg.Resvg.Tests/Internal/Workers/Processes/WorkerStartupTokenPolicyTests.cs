using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests.Internal.Workers.Processes;

public sealed partial class WorkerStartupTokenPolicyTests
{
    [Fact]
    public async Task WorkerRefusesUnrestrictedDirectLaunchBeforeOpeningAnInputPipe()
    {
        // A well-shaped command line must not let a normally launched worker
        // open IPC before establishing the required restricted primary context.
        // The pipe does not exist, so reaching the pipe open would report a
        // pipe failure instead of the startup guard.
        await AssertStartupGuardRefusesLaunchAsync(
            "--pipe",
            $"MarkdownRenderer.StartupGuard.{Guid.NewGuid():N}",
            "--nonce",
            new string('0', 32));
    }

    // The worker checks its startup context before it parses arguments, opens
    // IPC or starts its font-catalog thread. An unrestricted launch with no or
    // malformed arguments must still fail on the startup guard: an argument
    // error would mean arguments were read first, and the guard exits before
    // any later step can run.
    [Theory]
    [InlineData("")]
    [InlineData("--pipe")]
    [InlineData("--unexpected value")]
    public async Task WorkerChecksStartupContextBeforeReadingArguments(string commandLine)
    {
        await AssertStartupGuardRefusesLaunchAsync(
            commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static async Task AssertStartupGuardRefusesLaunchAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "MarkdownRenderer.Svg.Resvg.Worker.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("The worker did not start.");
        Task<string> errors = process.StandardError.ReadToEndAsync();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(70, process.ExitCode);
            string error = await errors;
            Assert.Contains("process primary token is not low integrity", error, StringComparison.Ordinal);
            Assert.DoesNotContain("expected --", error, StringComparison.Ordinal);
            Assert.DoesNotContain("invalid arguments", error, StringComparison.Ordinal);
            Assert.DoesNotContain("pipe open failed", error, StringComparison.Ordinal);
            Assert.Empty(await output);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public void StartupTokenObjectDaclGrantsOwnerOnlyReadControl()
    {
        using WorkerObjectSecurity.SecurityDescriptorHandle descriptor =
            WorkerObjectSecurity.CreateStartupTokenDescriptor();
        bool reference = false;
        try
        {
            descriptor.DangerousAddRef(ref reference);
            nint pointer = descriptor.DangerousGetHandle();
            uint length = GetSecurityDescriptorLength(pointer);
            Assert.InRange(length, 1u, 4096u);
            byte[] bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            var security = new RawSecurityDescriptor(bytes, 0);
            Assert.True(security.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
            Assert.NotNull(security.DiscretionaryAcl);
            CommonAce ace = Assert.IsType<CommonAce>(Assert.Single(security.DiscretionaryAcl.Cast<GenericAce>()));
            Assert.Equal(AceQualifier.AccessAllowed, ace.AceQualifier);
            Assert.Equal(AceFlags.None, ace.AceFlags);
            Assert.Equal(new SecurityIdentifier("S-1-3-4"), ace.SecurityIdentifier);
            Assert.Equal(0x00020000, ace.AccessMask); // READ_CONTROL, no TOKEN_* or WRITE_* rights.
            Assert.Null(security.Owner); // Do not require a privileged owner override.
        }
        finally
        {
            if (reference)
                descriptor.DangerousRelease();
        }
    }

    [Theory]
    [InlineData(Architecture.X86, "assign,x86-token,resume")]
    [InlineData(Architecture.X64, "assign,resume")]
    [InlineData(Architecture.Arm64, "assign,resume")]
    public void BootstrapIsX86OnlyAfterJobAssignmentAndBeforeResume(Architecture architecture, string expected)
    {
        var activation = new RecordingActivation(failAt: null);

        WorkerProcessSandbox.ActivateSuspended(activation, architecture);

        Assert.Equal(expected, string.Join(',', activation.Steps));
    }

    [Theory]
    [InlineData("assign", "assign")]
    [InlineData("x86-token", "assign,x86-token")]
    public void FailedActivationStepNeverResumesTheWorker(string failAt, string expected)
    {
        var activation = new RecordingActivation(failAt);

        Assert.Throws<InvalidOperationException>(() =>
            WorkerProcessSandbox.ActivateSuspended(activation, Architecture.X86));

        Assert.Equal(expected, string.Join(',', activation.Steps));
    }

    private sealed class RecordingActivation(string? failAt) : ISuspendedWorkerActivation
    {
        public List<string> Steps { get; } = [];

        public void AssignToJob() => Record("assign");

        public void AttachX86StartupToken() => Record("x86-token");

        public void Resume() => Record("resume");

        private void Record(string step)
        {
            Steps.Add(step);
            if (step == failAt)
                throw new InvalidOperationException(step);
        }
    }

    [LibraryImport("advapi32.dll")]
    private static partial uint GetSecurityDescriptorLength(nint securityDescriptor);
}
