using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed partial class WorkerStartupTokenPolicyTests
{
    [Fact]
    public async Task WorkerRefusesUnrestrictedDirectLaunchBeforeOpeningAnInputPipe()
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "MarkdownRenderer.Svg.Resvg.Worker.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        // A well-shaped command line must not let a normally launched worker
        // open IPC before establishing the required restricted primary context.
        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add($"MarkdownRenderer.StartupGuard.{Guid.NewGuid():N}");
        start.ArgumentList.Add("--nonce");
        start.ArgumentList.Add(new string('0', 32));
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("The worker did not start.");
        Task<string> errors = process.StandardError.ReadToEndAsync();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(70, process.ExitCode);
            Assert.Contains("process primary token is not low integrity", await errors, StringComparison.Ordinal);
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

    [Fact]
    public void WorkerDropsStartupContextBeforeArgumentsIpcAndBackgroundThreads()
    {
        string main = ReadSource("native", "src", "main.rs").Replace("\r\n", "\n");
        Assert.Contains("fn run() -> Result<(), String> {\n    sandbox_startup::enter_restricted_execution()?;",
            main, StringComparison.Ordinal);
        int guard = main.IndexOf("    sandbox_startup::enter_restricted_execution()?;", StringComparison.Ordinal);
        int mitigation = main.IndexOf("    apply_process_mitigations()?;", StringComparison.Ordinal);
        int args = main.IndexOf("    let (pipe_name,", StringComparison.Ordinal);
        int pipe = main.IndexOf("    let mut pipe = OpenOptions::new()", StringComparison.Ordinal);
        int fonts = main.IndexOf("    let font_catalog = FontCatalogCoordinator::start()?;", StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < mitigation && mitigation < args && args < pipe && pipe < fonts);
    }

    private static string ReadSource(params string[] path)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string provider = Path.Combine(directory.FullName, "MarkdownRenderer", "src", "MarkdownRenderer.Svg.Resvg");
            string fullPath = Path.Combine([provider, .. path]);
            if (File.Exists(fullPath))
                return File.ReadAllText(fullPath);
        }
        throw new FileNotFoundException("The worker startup source contract was not found.");
    }

    [LibraryImport("advapi32.dll")]
    private static partial uint GetSecurityDescriptorLength(nint securityDescriptor);
}
