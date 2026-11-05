using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>Starts the packaged resvg worker executable.</summary>
/// <remarks>
/// The startup method keeps the single try/finally that owns the pipe, the
/// process handle and the object security until the worker adopts them; its
/// steps create, read or report but never keep those handles. The diagnostic
/// identity read stays open across process creation. It stays near 90 lines
/// on purpose: the remaining body is the ownership sequence itself (label,
/// identity read, process creation, connection wait, adoption), and an async
/// step cannot take the handle locals by reference, so moving any of it would
/// let a failure between assignment and the outer finally leak a handle.
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

        (ulong nonceLow, ulong nonceHigh, string nonce) = CreateNonce();
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
            pipe = CreateRestrictedPipe(pipeName, objectSecurity);

            // Keep the newly created pipe inside the same ownership scope as
            // startup so a label failure cannot leak its server handle.
            WorkerObjectSecurity.SetLowIntegrityLabel(pipe.SafePipeHandle);
            (process, workerExecutableSha256, processStartedTimestamp) = await StartSuspendedAsync(
                executablePath,
                pipeName,
                nonce,
                job,
                sourceToken,
                objectSecurity,
                cancellationToken).ConfigureAwait(false);
            await WaitForConnectionAsync(
                pipe,
                process,
                processStartedTimestamp,
                workerExecutableSha256,
                cancellationToken).ConfigureAwait(false);

            var worker = new WindowsWorkerProcess(
                process,
                pipe,
                nonceLow,
                nonceHigh,
                workerExecutableSha256,
                options.QueueCapacity,
                objectSecurity,
                ResvgEventSource.Log.AllocateWorkerInstanceId());
            process = null;
            objectSecurity = null;
            pipe = null!;
            return worker;
        }
        finally
        {
            if (process is not null)
                TerminateUnadoptedProcess(process);
            pipe?.Dispose();
            objectSecurity?.Dispose();
        }
    }

    /// <summary>
    /// Starts the restricted worker suspended in <paramref name="job"/>. The
    /// created process handle is the last step, so a failure leaves nothing for
    /// the caller to terminate.
    /// </summary>
    /// <returns>The suspended process, the executable digest when timeout events are enabled, and the start timestamp.</returns>
    private static async Task<(SafeProcessHandle Process, string? ExecutableSha256, long StartedTimestamp)> StartSuspendedAsync(
        string executablePath,
        string pipeName,
        string nonce,
        WorkerJob job,
        SafeTokenHandle sourceToken,
        WorkerObjectSecurity.WorkerObjectSecurityContext objectSecurity,
        CancellationToken cancellationToken)
    {
        FileStream? identityFile = null;
        string? workerExecutableSha256 = null;
        try
        {
            // This is diagnostic-only work. Keep the read handle open until
            // CreateProcess has loaded the image so the recorded digest
            // cannot silently refer to a replacement file at the same path.
            if (WorkerTimeoutEvents.Log.IsEnabled())
            {
                (identityFile, workerExecutableSha256) =
                    await OpenExecutableIdentityAsync(executablePath, cancellationToken).ConfigureAwait(false);
            }

            long processStartedTimestamp = Stopwatch.GetTimestamp();
            SafeProcessHandle process = WorkerProcessSandbox.StartRestrictedSuspended(
                executablePath,
                pipeName,
                nonce,
                Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
                job,
                sourceToken,
                objectSecurity);
            return (process, workerExecutableSha256, processStartedTimestamp);
        }
        finally
        {
            identityFile?.Dispose();
        }
    }

    /// <summary>
    /// Waits for the started worker to connect to <paramref name="pipe"/>
    /// within the initialization deadline, reporting a deadline miss.
    /// </summary>
    private static async Task WaitForConnectionAsync(
        NamedPipeServerStream pipe,
        SafeProcessHandle process,
        long processStartedTimestamp,
        string? workerExecutableSha256,
        CancellationToken cancellationToken)
    {
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
            throw ReportInitializationDeadline(processStartedTimestamp, workerExecutableSha256, exception);
        }
    }

    /// <summary>Draws the per-worker nonce that names the pipe and authenticates the worker.</summary>
    /// <returns>The nonce halves and their hexadecimal form.</returns>
    private static (ulong Low, ulong High, string Text) CreateNonce()
    {
        byte[] nonceBytes = RandomNumberGenerator.GetBytes(16);
        ulong nonceLow = BitConverter.ToUInt64(nonceBytes, 0);
        ulong nonceHigh = BitConverter.ToUInt64(nonceBytes, 8);
        return (nonceLow, nonceHigh, $"{nonceHigh:x16}{nonceLow:x16}");
    }

    /// <summary>
    /// Creates the single-instance server pipe with the worker's restricted
    /// DACL. The caller owns the returned pipe as soon as it returns.
    /// </summary>
    /// <param name="pipeName">The nonce-qualified pipe name.</param>
    /// <param name="objectSecurity">The worker's object security context.</param>
    /// <returns>The unconnected server pipe.</returns>
    private static NamedPipeServerStream CreateRestrictedPipe(
        string pipeName,
        WorkerObjectSecurity.WorkerObjectSecurityContext objectSecurity)
    {
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.SetSecurityDescriptorSddlForm(
            objectSecurity.RestrictedDaclSddl,
            AccessControlSections.Access);
        return NamedPipeServerStreamAcl.Create(
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
    }

    /// <summary>
    /// Opens the worker executable for reading and hashes it for diagnostics.
    /// An unreadable file yields no identity; any other failure, including
    /// cancellation, disposes the handle and propagates.
    /// </summary>
    /// <param name="executablePath">The packaged worker executable.</param>
    /// <param name="cancellationToken">Cancels startup.</param>
    /// <returns>The still-open read handle and its SHA-256, or neither.</returns>
    private static async ValueTask<(FileStream? File, string? Sha256)> OpenExecutableIdentityAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        FileStream? identityFile = null;
        bool returned = false;
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
            string sha256 = Convert.ToHexString(digest);
            returned = true;
            return (identityFile, sha256);
        }
        catch (IOException)
        {
            return (null, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, null);
        }
        finally
        {
            if (!returned)
                identityFile?.Dispose();
        }
    }

    /// <summary>Records the startup timeout and creates the deadline failure.</summary>
    /// <param name="processStartedTimestamp">When process creation began.</param>
    /// <param name="workerExecutableSha256">The diagnostic executable digest, if read.</param>
    /// <param name="exception">The startup cancellation.</param>
    /// <returns>The exception the caller throws.</returns>
    private static WorkerInitializationDeadlineException ReportInitializationDeadline(
        long processStartedTimestamp,
        string? workerExecutableSha256,
        OperationCanceledException exception)
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
            elapsedWallMilliseconds: WorkerProcessCounters.GetElapsedMilliseconds(processStartedTimestamp),
            workerInputSha256: string.Empty,
            workerExecutableSha256: workerExecutableSha256 ?? string.Empty);
        return new WorkerInitializationDeadlineException(
            "The resvg worker did not complete startup before its initialization deadline.",
            exception);
    }

    /// <summary>Terminates and releases a worker that startup did not hand over.</summary>
    /// <param name="process">The unadopted worker process.</param>
    private static void TerminateUnadoptedProcess(SafeProcessHandle process)
    {
        _ = WorkerNativeMethods.TerminateProcess(process, WorkerProcessSandbox.WorkerTerminationExitCode);
        _ = WorkerNativeMethods.WaitForSingleObject(process, WorkerProcessSandbox.WorkerTerminationWaitMilliseconds);
        process.Dispose();
    }
}
