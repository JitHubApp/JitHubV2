using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>Starts the packaged resvg worker executable.</summary>
/// <remarks>
/// The startup method stays one long method on purpose: its single
/// try/finally owns the pipe, the process handle and the object security
/// until the worker adopts them, and its diagnostic identity read must
/// stay open across process creation.
/// </remarks>
internal sealed class WindowsWorkerProcessLauncher(
    string workerPath,
    ResvgMarkdownSvgRendererOptions options) : IWorkerProcessLauncher
{
    public Task<WindowsWorkerProcess> StartAsync(WorkerJob job, CancellationToken cancellationToken) =>
        StartAsync(workerPath, options, job, cancellationToken);

    /// <summary>
    /// Creates the restricted pipe, starts the sandboxed worker suspended in
    /// <paramref name="job"/>, waits for its connection under the
    /// initialization deadline and adopts it as a <see cref="WindowsWorkerProcess"/>.
    /// </summary>
    /// <param name="executablePath">The packaged worker executable.</param>
    /// <param name="options">The renderer options.</param>
    /// <param name="job">The pool's kill-on-close job.</param>
    /// <param name="cancellationToken">Cancels startup.</param>
    /// <returns>The connected worker.</returns>
    private static async Task<WindowsWorkerProcess> StartAsync(
        string executablePath,
        ResvgMarkdownSvgRendererOptions options,
        WorkerJob job,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The resvg worker is packaged for Windows only.");
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("The architecture-specific resvg worker is missing.", executablePath);

        byte[] nonceBytes = RandomNumberGenerator.GetBytes(16);
        ulong nonceLow = BitConverter.ToUInt64(nonceBytes, 0);
        ulong nonceHigh = BitConverter.ToUInt64(nonceBytes, 8);
        string nonce = $"{nonceHigh:x16}{nonceLow:x16}";
        string pipeName = $"MarkdownRenderer.Resvg.{Environment.ProcessId}.{nonce}";
        using SafeTokenHandle sourceToken = WorkerProcessSandbox.OpenCurrentPrimaryTokenForWorker();
        WorkerObjectSecurity.WorkerObjectSecurityContext? objectSecurity =
            WorkerObjectSecurity.CreateForPrimaryToken(sourceToken);

        SafeProcessHandle? process = null;
        NamedPipeServerStream? pipe = null;
        string? workerExecutableSha256 = null;
        long processStartedTimestamp = 0;
        try
        {
            var pipeSecurity = new PipeSecurity();
            pipeSecurity.SetSecurityDescriptorSddlForm(
                objectSecurity.RestrictedDaclSddl,
                AccessControlSections.Access);
            pipe = NamedPipeServerStreamAcl.Create(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough,
                WorkerProtocol.ResponseSize,
                WorkerProtocol.RequestSize,
                pipeSecurity,
                HandleInheritability.None,
                (PipeAccessRights)0);

            // Keep the newly created pipe inside the same ownership scope as
            // startup so a label failure cannot leak its server handle.
            WorkerObjectSecurity.SetLowIntegrityLabel(pipe.SafePipeHandle);
            FileStream? identityFile = null;
            try
            {
                // This is diagnostic-only work. Keep the read handle open until
                // CreateProcess has loaded the image so the recorded digest
                // cannot silently refer to a replacement file at the same path.
                if (WorkerTimeoutEvents.Log.IsEnabled())
                {
                    try
                    {
                        identityFile = new FileStream(
                            executablePath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            bufferSize: 64 * 1024,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                        byte[] digest = await SHA256.HashDataAsync(identityFile, cancellationToken)
                            .ConfigureAwait(false);
                        workerExecutableSha256 = Convert.ToHexString(digest);
                    }
                    catch (IOException)
                    {
                        identityFile?.Dispose();
                        identityFile = null;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        identityFile?.Dispose();
                        identityFile = null;
                    }
                }

                processStartedTimestamp = Stopwatch.GetTimestamp();
                process = WorkerProcessSandbox.StartRestrictedSuspended(
                    executablePath,
                    pipeName,
                    nonce,
                    Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
                    job,
                    sourceToken,
                    objectSecurity);
            }
            finally
            {
                identityFile?.Dispose();
            }

            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(WorkerSchedulingPolicy.InitializationDeadline);
            try
            {
                await WorkerStartupWait.WaitForConnectionAsync(
                    token => pipe.WaitForConnectionAsync(token),
                    process,
                    startup.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                WorkerTimeoutEvents.Log.Timeout(
                    stage: 0,
                    deadlineMilliseconds: (int)WorkerSchedulingPolicy.InitializationDeadline.TotalMilliseconds,
                    workerProcessCpuMilliseconds: -1,
                    transportPhase: -1,
                    requestWriteMilliseconds: -1,
                    workerExited: -1,
                    openProgressPhase: -1,
                    workerWorkingSetKiB: -1,
                    workerPrivateCommitKiB: -1,
                    workerPageFaults: -1,
                    elapsedWallMilliseconds: WindowsWorkerProcess.GetElapsedMilliseconds(processStartedTimestamp),
                    workerInputSha256: string.Empty,
                    workerExecutableSha256: workerExecutableSha256 ?? string.Empty);
                throw new WorkerInitializationDeadlineException(
                    "The resvg worker did not complete startup before its initialization deadline.",
                    exception);
            }

            var worker = new WindowsWorkerProcess(
                process,
                pipe,
                nonceLow,
                nonceHigh,
                workerExecutableSha256,
                options.QueueCapacity,
                objectSecurity);
            process = null;
            objectSecurity = null;
            pipe = null!;
            return worker;
        }
        finally
        {
            if (process is not null)
            {
                _ = WorkerNativeMethods.TerminateProcess(process, WorkerProcessSandbox.WorkerTerminationExitCode);
                _ = WorkerNativeMethods.WaitForSingleObject(process, WorkerProcessSandbox.WorkerTerminationWaitMilliseconds);
                process.Dispose();
            }
            pipe?.Dispose();
            objectSecurity?.Dispose();
        }
    }
}
