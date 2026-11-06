using System.Diagnostics;
using System.IO.Pipes;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class WorkerStartupWaitTests
{
    [Fact]
    public async Task PipeConnectionWinsAndDoesNotWaitForLiveWorkerExit()
    {
        using Process currentProcess = Process.GetCurrentProcess();
        string pipeName = $"MarkdownRenderer.StartupWait.{Guid.NewGuid():N}";
        using var server = CreatePipeServer(pipeName);
        using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        Task clientConnection = client.ConnectAsync(5_000);

        var stopwatch = Stopwatch.StartNew();
        await WorkerStartupWait.WaitForConnectionAsync(
            token => server.WaitForConnectionAsync(token),
            currentProcess.SafeHandle,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        await clientConnection.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ExitedWorkerFailsPromptlyWithNativeExitCode()
    {
        using Process child = StartCommand("exit 37");
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        string pipeName = $"MarkdownRenderer.StartupExit.{Guid.NewGuid():N}";
        using var server = CreatePipeServer(pipeName);
        var stopwatch = Stopwatch.StartNew();

        WorkerProtocolException exception = await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await WorkerStartupWait.WaitForConnectionAsync(
                token => server.WaitForConnectionAsync(token),
                child.SafeHandle,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.Contains("native exit code 0x00000025", exception.Message, StringComparison.Ordinal);
        Assert.Equal(37u, Assert.IsType<WorkerStartupExitCodeException>(exception.InnerException).NativeExitCode);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CallerCancellationIsPreservedAndDrainsPipeWait()
    {
        using Process currentProcess = Process.GetCurrentProcess();
        using var cancellation = new CancellationTokenSource();
        string pipeName = $"MarkdownRenderer.StartupCancel.{Guid.NewGuid():N}";
        using var server = CreatePipeServer(pipeName);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await WorkerStartupWait.WaitForConnectionAsync(
                token => server.WaitForConnectionAsync(token),
                currentProcess.SafeHandle,
                cancellation.Token).WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task StartupDeadlineCancellationIsPreservedAndDrainsPipeWait()
    {
        using Process currentProcess = Process.GetCurrentProcess();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        string pipeName = $"MarkdownRenderer.StartupDeadline.{Guid.NewGuid():N}";
        using var server = CreatePipeServer(pipeName);
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await WorkerStartupWait.WaitForConnectionAsync(
                token => server.WaitForConnectionAsync(token),
                currentProcess.SafeHandle,
                deadline.Token).WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.True(deadline.IsCancellationRequested);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ConnectionAndExitCallbackRaceCanBeDisposedRepeatedly()
    {
        using Process child = StartCommand("exit 41");
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // A signaled process queues its registered callback immediately. Race
        // that callback against an already-completed connection repeatedly to
        // exercise both unregister and callback-drain ownership paths.
        for (int index = 0; index < 32; index++)
        {
            try
            {
                await WorkerStartupWait.WaitForConnectionAsync(
                    static _ => Task.CompletedTask,
                    child.SafeHandle,
                    CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (WorkerProtocolException exception)
            {
                Assert.Contains("native exit code 0x00000029", exception.Message, StringComparison.Ordinal);
                Assert.Equal(41u, Assert.IsType<WorkerStartupExitCodeException>(exception.InnerException).NativeExitCode);
            }
        }
    }

    private static NamedPipeServerStream CreatePipeServer(string pipeName) => new(
        pipeName,
        PipeDirection.InOut,
        maxNumberOfServerInstances: 1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous);

    private static Process StartCommand(string command)
    {
        string commandInterpreter = Environment.GetEnvironmentVariable("ComSpec") ??
            Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var startInfo = new ProcessStartInfo(commandInterpreter)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(command);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The child process did not start.");
    }
}
