using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using MarkdownRenderer.Svg.Resvg.Internal.Diagnostics;
using MarkdownRenderer.Svg.Resvg.Internal.Fonts;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Transport;
using Microsoft.Win32.SafeHandles;
using static MarkdownRenderer.Svg.Resvg.Tests.WorkerTransportNative;

namespace MarkdownRenderer.Svg.Resvg.Tests;

/// <summary>
/// A <see cref="WindowsWorkerProcess"/> connected to a pipe peer the test
/// drives, over a blocking child process whose handles the test can inspect.
/// </summary>
internal sealed class WorkerTransportFixture : IAsyncDisposable
{
    private readonly Process _child;
    private readonly NamedPipeServerStream _server;
    private bool _disposed;

    private WorkerTransportFixture(
        Process child,
        SafeProcessHandle inspectionProcess,
        NamedPipeServerStream server,
        NamedPipeClientStream peer,
        WindowsWorkerProcess worker,
        WorkerExchangeFrames frames)
    {
        _child = child;
        InspectionProcess = inspectionProcess;
        _server = server;
        Peer = peer;
        Worker = worker;
        Frames = frames;
    }

    internal Process Child => _child;
    internal SafeProcessHandle InspectionProcess { get; }
    internal NamedPipeClientStream Peer { get; }
    internal WindowsWorkerProcess Worker { get; }

    /// <summary>The request and response frames the worker reuses for every exchange.</summary>
    internal WorkerExchangeFrames Frames { get; }

    internal static async Task<WorkerTransportFixture> CreateAsync(
        bool allowTermination = true,
        Func<IWorkerFontCatalogHost, WorkerFontCatalogSupplier>? fontCatalogSupplierFactory = null)
    {
        Process child = StartBlockingChild();
        SafeProcessHandle? inspectionProcess = null;
        SafeProcessHandle? workerProcess = null;
        NamedPipeServerStream? server = null;
        NamedPipeClientStream? peer = null;
        WindowsWorkerProcess? worker = null;
        try
        {
            ProcessAccess access = ProcessAccess.DuplicateHandle | ProcessAccess.Synchronize |
                ProcessAccess.QueryLimitedInformation | ProcessAccess.Terminate;
            inspectionProcess = OpenProcess(access, inheritHandle: false, checked((uint)child.Id));
            if (inspectionProcess.IsInvalid)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Could not open the worker test process.");
            workerProcess = DuplicateCurrentProcessHandle(inspectionProcess, allowTermination);

            string pipeName = $"MarkdownRenderer.TransportFault.{Guid.NewGuid():N}";
            server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            peer = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            var frames = new WorkerExchangeFrames();
            worker = CreateConnectedWorker(workerProcess, server, frames, fontCatalogSupplierFactory);
            workerProcess = null;

            Task serverConnection = server.WaitForConnectionAsync();
            await peer.ConnectAsync(5_000).WaitAsync(TimeSpan.FromSeconds(5));
            await serverConnection.WaitAsync(TimeSpan.FromSeconds(5));

            var fixture = new WorkerTransportFixture(
                child,
                inspectionProcess,
                server,
                peer,
                worker,
                frames);
            inspectionProcess = null;
            server = null;
            peer = null;
            worker = null;
            return fixture;
        }
        catch
        {
            try
            {
                worker?.DisposeForRestart();
            }
            catch
            {
                try
                {
                    worker?.Dispose();
                }
                catch
                {
                    // The child is explicitly killed below even if worker
                    // transport cleanup itself failed.
                }
            }
            workerProcess?.Dispose();
            peer?.Dispose();
            server?.Dispose();
            inspectionProcess?.Dispose();
            KillChild(child);
            child.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;

        Peer.Dispose();
        try
        {
            Worker.DisposeForRestart();
        }
        catch
        {
            try
            {
                Worker.Dispose();
            }
            catch
            {
                // The child is explicitly killed below even if worker
                // transport cleanup itself failed.
            }
        }
        _server.Dispose();
        KillChild(_child);
        InspectionProcess.Dispose();
        _child.Dispose();
        return ValueTask.CompletedTask;
    }

    private static WindowsWorkerProcess CreateConnectedWorker(
        SafeProcessHandle process,
        NamedPipeServerStream pipe,
        WorkerExchangeFrames frames,
        Func<IWorkerFontCatalogHost, WorkerFontCatalogSupplier>? fontCatalogSupplierFactory)
    {
        using SafeTokenHandle primaryToken =
            WorkerProcessSandbox.OpenCurrentPrimaryTokenForWorker();
        var objectSecurity = WorkerObjectSecurity.CreateForPrimaryToken(primaryToken);
        try
        {
            return new WindowsWorkerProcess(
                process,
                pipe,
                0x1122_3344_5566_7788ul,
                0x8877_6655_4433_2211ul,
                workerExecutableSha256: null,
                queueCapacity: 4,
                objectSecurity,
                ResvgEventSource.Log.AllocateWorkerInstanceId(),
                fontCatalogSupplierFactory,
                frames);
        }
        catch
        {
            objectSecurity.Dispose();
            throw;
        }
    }

    private static Process StartBlockingChild()
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("127.0.0.1");
        Process child = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the worker test child.");
        if (child.HasExited)
        {
            child.Dispose();
            throw new InvalidOperationException("The worker test child exited before it could be exercised.");
        }

        return child;
    }

    private static SafeProcessHandle DuplicateCurrentProcessHandle(
        SafeProcessHandle process,
        bool allowTermination)
    {
        ProcessAccess workerAccess = ProcessAccess.DuplicateHandle | ProcessAccess.Synchronize |
            ProcessAccess.QueryLimitedInformation;
        if (allowTermination)
            workerAccess |= ProcessAccess.Terminate;
        using Process currentProcess = Process.GetCurrentProcess();
        if (!DuplicateHandle(
                currentProcess.SafeHandle,
                process.DangerousGetHandle(),
                GetCurrentProcess(),
                out nint duplicate,
                desiredAccess: (uint)workerAccess,
                inheritHandle: false,
                options: 0))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Could not duplicate the worker test process handle.");
        }

        return new SafeProcessHandle(duplicate, ownsHandle: true);
    }

    private static void KillChild(Process child)
    {
        try
        {
            if (!child.HasExited)
                child.Kill(entireProcessTree: true);
            child.WaitForExit(5_000);
        }
        catch (InvalidOperationException)
        {
            // The child has already exited or its process handle was closed.
        }
    }
}
