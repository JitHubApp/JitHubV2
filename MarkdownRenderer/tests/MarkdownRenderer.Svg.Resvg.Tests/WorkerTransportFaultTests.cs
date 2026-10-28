using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed partial class WorkerTransportFaultTests
{
    [Fact]
    public async Task PoolShutdownWaitsForFontOwnersOfAnAlreadyDetachedWorker()
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync();
        using var pool = new WorkerPool(new ResvgMarkdownSvgRendererOptions());
        var preparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkerObjectSecurity.WorkerObjectSecurityContext objectSecurity = fixture.Worker.ObjectSecurity;
        typeof(WindowsWorkerProcess).GetField("_fontPreparationTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(fixture.Worker, preparation.Task);
        var owners = (WorkerLifetimeRegistry<WindowsWorkerProcess>)typeof(WorkerPool)
            .GetField("_workerLifetimes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!;
        using var startup = owners.Reserve();
        startup.Attach(fixture.Worker);
        object primary = typeof(WorkerPool).GetField("_primary", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(pool)!;
        primary.GetType().GetProperty("Worker")!.SetValue(primary, fixture.Worker);

        Task? shutdown = null;
        try
        {
            pool.InvalidateWorker(fixture.Worker.InstanceId);
            Assert.Null(primary.GetType().GetProperty("Worker")!.GetValue(primary));
            Assert.True(fixture.Child.HasExited);
            Assert.Same(objectSecurity, fixture.Worker.ObjectSecurity);
            shutdown = pool.DisposeAsync().AsTask();
            Assert.False(shutdown.IsCompleted);
        }
        finally
        {
            preparation.TrySetResult();
        }
        await shutdown!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<ObjectDisposedException>(() => _ = fixture.Worker.ObjectSecurity);
    }

    [Fact]
    public async Task AsyncDisposalWaitsForExactWorkerExitAfterOrdinaryDisposal()
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync(allowTermination: false);
        fixture.Worker.Dispose();
        Task shutdown = fixture.Worker.DisposeAsync().AsTask();
        Assert.False(shutdown.IsCompleted);
        Assert.False(fixture.Child.HasExited);
        fixture.Child.Kill();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Child.HasExited);
        await fixture.Worker.DisposeAsync();
    }

    [Fact]
    public async Task AsyncDisposalFailsInsteadOfCertifyingAnUnterminatedWorker()
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync(allowTermination: false);
        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await fixture.Worker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.False(fixture.Child.HasExited);
    }

    [Fact]
    public async Task AsyncDisposalCompletesOnlyAfterWorkerHasExited()
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync();
        await fixture.Worker.DisposeAsync();
        Assert.True(fixture.Child.HasExited);
    }

    [Fact]
    public async Task SuccessfulExchangeClosesRemoteMappingHandleBeforeReturning()
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync();
        using var sharedMemory = new SharedMemoryLease("remote-close", outputLength: 16);
        sharedMemory.GetSpan().Fill(0x4A);
        Task<WorkerResponse> exchange = StartExchange(fixture, sharedMemory, CancellationToken.None);

        byte[] request = await ReadRequestAsync(fixture.Peer);
        ulong remoteHandle = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(240));
        Assert.NotEqual(0ul, remoteHandle);
        using var mappingIdentity = new SafeFileHandle(DuplicateRemoteHandle(fixture.InspectionProcess, remoteHandle), ownsHandle: true);
        AssertRemoteMappingMatches(fixture.InspectionProcess, remoteHandle, sharedMemory.GetSpan());

        await fixture.Peer.WriteAsync(CreateResponse(request));
        await fixture.Peer.FlushAsync();
        WorkerResponse response = await exchange.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkerStatus.Ok, response.Status);
        AssertRemoteHandleClosed(fixture.InspectionProcess, remoteHandle, mappingIdentity);
        Assert.False(fixture.Child.HasExited);
    }

    [Theory]
    [InlineData(TransportFault.WrongRequestId)]
    [InlineData(TransportFault.MalformedResponse)]
    [InlineData(TransportFault.PartialResponse)]
    [InlineData(TransportFault.PipeLoss)]
    public async Task InvalidOrLostTransportRetiresWorkerAndReclaimsMapping(TransportFault fault)
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync();
        using var sharedMemory = new SharedMemoryLease($"fault-{fault}", outputLength: 16);
        sharedMemory.GetSpan().Fill(0x6D);
        Task<WorkerResponse> exchange = StartExchange(fixture, sharedMemory, CancellationToken.None);

        byte[] request = await ReadRequestAsync(fixture.Peer);
        ulong remoteHandle = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(240));
        Assert.NotEqual(0ul, remoteHandle);
        AssertRemoteMappingMatches(fixture.InspectionProcess, remoteHandle, sharedMemory.GetSpan());

        if (fault is TransportFault.WrongRequestId or TransportFault.MalformedResponse)
        {
            byte[] response = CreateResponse(request);
            if (fault == TransportFault.WrongRequestId)
            {
                ulong requestId = BinaryPrimitives.ReadUInt64LittleEndian(response.AsSpan(8));
                BinaryPrimitives.WriteUInt64LittleEndian(response.AsSpan(8), checked(requestId + 1));
            }
            else
            {
                response[73] = 0x80;
            }

            await fixture.Peer.WriteAsync(response);
            await fixture.Peer.FlushAsync();
        }
        else if (fault == TransportFault.PartialResponse)
        {
            byte[] response = CreateResponse(request);
            await fixture.Peer.WriteAsync(response.AsMemory(0, 31));
            await fixture.Peer.FlushAsync();
            fixture.Peer.Dispose();
        }
        else
        {
            fixture.Peer.Dispose();
        }

        await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await exchange.WaitAsync(TimeSpan.FromSeconds(8)));

        await fixture.Child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Child.HasExited);
        AssertRemoteHandleUnavailable(fixture.InspectionProcess, remoteHandle);
        Assert.Equal((byte)0x6D, sharedMemory.GetSpan()[0]);
    }

    [Fact]
    public async Task CallerCancellationDuringHungExchangeRetiresWorkerAndReclaimsMapping()
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync();
        using var sharedMemory = new SharedMemoryLease("cancel-hang", outputLength: 16);
        sharedMemory.GetSpan().Fill(0x7E);
        using var cancellation = new CancellationTokenSource();
        Task<WorkerResponse> exchange = StartExchange(fixture, sharedMemory, cancellation.Token);

        byte[] request = await ReadRequestAsync(fixture.Peer);
        ulong remoteHandle = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(240));
        Assert.NotEqual(0ul, remoteHandle);
        AssertRemoteMappingMatches(fixture.InspectionProcess, remoteHandle, sharedMemory.GetSpan());

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await exchange.WaitAsync(TimeSpan.FromSeconds(8)));

        await fixture.Child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Child.HasExited);
        AssertRemoteHandleUnavailable(fixture.InspectionProcess, remoteHandle);
    }

    [Fact]
    public async Task DeadlineDuringHungExchangeRetiresWorkerAndReclaimsMapping()
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync();
        using var sharedMemory = new SharedMemoryLease("deadline-hang", outputLength: 16);
        Task<WorkerResponse> exchange = fixture.Worker.ExchangeAsync(
            CreateRenderRequest(),
            TimeSpan.FromMilliseconds(150),
            CancellationToken.None,
            sharedMemory).AsTask();

        byte[] request = await ReadRequestAsync(fixture.Peer);
        ulong remoteHandle = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(240));
        Assert.NotEqual(0ul, remoteHandle);

        await Assert.ThrowsAsync<WorkerDeadlineException>(async () =>
            await exchange.WaitAsync(TimeSpan.FromSeconds(8)));

        await fixture.Child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Child.HasExited);
        AssertRemoteHandleUnavailable(fixture.InspectionProcess, remoteHandle);
    }

    [Fact]
    public async Task ConcurrentRestartDisposeWaitsAfterOrdinaryDisposeAndClosesRemoteMapping()
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync(allowTermination: false);
        using var sharedMemory = new SharedMemoryLease("concurrent-dispose", outputLength: 16);
        sharedMemory.GetSpan().Fill(0x3C);
        Task<WorkerResponse> exchange = StartExchange(fixture, sharedMemory, CancellationToken.None);
        byte[] request = await ReadRequestAsync(fixture.Peer);
        ulong remoteHandle = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(240));
        Assert.NotEqual(0ul, remoteHandle);
        using var mappingIdentity = new SafeFileHandle(DuplicateRemoteHandle(fixture.InspectionProcess, remoteHandle), ownsHandle: true);
        AssertRemoteMappingMatches(fixture.InspectionProcess, remoteHandle, sharedMemory.GetSpan());

        var ordinaryDispose = Stopwatch.StartNew();
        fixture.Worker.Dispose();
        Assert.True(ordinaryDispose.Elapsed < TimeSpan.FromSeconds(1),
            $"Ordinary Dispose must not block while the registered process wait is pending; observed {ordinaryDispose.Elapsed}.");
        Assert.False(fixture.Child.HasExited);

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await exchange.WaitAsync(TimeSpan.FromSeconds(8)));

        // The worker-owned process handle intentionally lacks PROCESS_TERMINATE.
        // The registered duplicate must remain usable after Dispose() starts
        // shutdown. Exchange's restart disposal waits the full bound, then
        // closes the still-live worker's remote mapping handle.
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(2.5),
            $"Expected the bounded restart wait after ordinary disposal; observed {stopwatch.Elapsed}.");
        Assert.False(fixture.Child.HasExited);
        AssertRemoteHandleClosed(fixture.InspectionProcess, remoteHandle, mappingIdentity);
    }

    private static Task<WorkerResponse> StartExchange(
        WorkerTransportFixture fixture,
        SharedMemoryLease sharedMemory,
        CancellationToken cancellationToken) => fixture.Worker.ExchangeAsync(
            CreateRenderRequest(),
            TimeSpan.FromSeconds(5),
            cancellationToken,
            sharedMemory).AsTask();

    private static WorkerRequest CreateRenderRequest() => new(
        WorkerOperation.Render,
        RequestId: 73,
        NonceLow: 0x1122_3344_5566_7788,
        NonceHigh: 0x8877_6655_4433_2211,
        MappingHandle: 0,
        ContentHash: new byte[32],
        SourceLength: 0,
        OutputLength: 16,
        TargetWidth: 2,
        TargetHeight: 2,
        Tile: null,
        Locale: "en-US",
        ColorScheme: MarkdownSvgColorScheme.Light,
        SemanticColor: null,
        PixelFormat: MarkdownSvgPixelFormat.Rgba8Premultiplied,
        FontGeneration: 0,
        ParsedResourceCacheBytes: 1024,
        DocumentId: 9,
        Options: new ResvgMarkdownSvgRendererOptions());

    private static async Task<byte[]> ReadRequestAsync(Stream peer)
    {
        byte[] request = new byte[WorkerProtocol.RequestSize];
        await peer.ReadExactlyAsync(request).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkerProtocol.Magic, BinaryPrimitives.ReadUInt32LittleEndian(request));
        Assert.Equal(WorkerProtocol.Version, BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(4)));
        return request;
    }

    private static byte[] CreateResponse(ReadOnlySpan<byte> request)
    {
        byte[] response = new byte[WorkerProtocol.ResponseSize];
        BinaryPrimitives.WriteUInt32LittleEndian(response, WorkerProtocol.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(4), WorkerProtocol.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(6), (ushort)WorkerStatus.Ok);
        request.Slice(8, 24).CopyTo(response.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(56), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(60), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(64), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(68), 2);
        response[72] = 1;
        return response;
    }

    private static void AssertRemoteMappingMatches(
        SafeProcessHandle workerProcess,
        ulong remoteHandle,
        ReadOnlySpan<byte> hostBytes)
    {
        nint localHandle = DuplicateRemoteHandle(workerProcess, remoteHandle);
        Assert.NotEqual(nint.Zero, localHandle);
        try
        {
            nint view = MapViewOfFile(localHandle, FileMapRead | FileMapWrite, 0, 0, checked((nuint)hostBytes.Length));
            Assert.NotEqual(nint.Zero, view);
            try
            {
                for (int index = 0; index < hostBytes.Length; index++)
                    Assert.Equal(hostBytes[index], Marshal.ReadByte(view, index));

                Marshal.WriteByte(view, 7, 0xA5);
                Assert.Equal((byte)0xA5, hostBytes[7]);
            }
            finally
            {
                Assert.True(UnmapViewOfFile(view));
            }
        }
        finally
        {
            Assert.True(CloseHandle(localHandle));
        }
    }

    private static void AssertRemoteHandleClosed(SafeProcessHandle workerProcess, ulong remoteHandle, SafeFileHandle identity)
    {
        if (TryDuplicateRemoteHandle(workerProcess, remoteHandle, out nint duplicate))
        {
            // The child may reuse the numeric slot for an unrelated handle.
            // Test the original kernel object's identity, not its old number.
            using var current = new SafeFileHandle(duplicate, ownsHandle: true);
            Assert.False(CompareObjectHandles(current, identity));
        }
        else
            Assert.Equal(ErrorInvalidHandle, Marshal.GetLastPInvokeError());
    }

    private static void AssertRemoteHandleUnavailable(SafeProcessHandle workerProcess, ulong remoteHandle)
    {
        Assert.False(TryDuplicateRemoteHandle(workerProcess, remoteHandle, out nint duplicate));
        int error = Marshal.GetLastPInvokeError();
        Assert.True(error is ErrorInvalidHandle or ErrorAccessDenied,
            $"Expected the worker mapping handle to be unavailable; DuplicateHandle returned {error}.");
        if (duplicate != nint.Zero)
            Assert.True(CloseHandle(duplicate));
    }

    private static nint DuplicateRemoteHandle(SafeProcessHandle workerProcess, ulong remoteHandle)
    {
        if (!TryDuplicateRemoteHandle(workerProcess, remoteHandle, out nint duplicate))
        {
            throw new InvalidOperationException(
                $"The duplicated worker mapping handle was not readable (Win32 error {Marshal.GetLastPInvokeError()}).");
        }

        return duplicate;
    }

    private static bool TryDuplicateRemoteHandle(
        SafeProcessHandle workerProcess,
        ulong remoteHandle,
        out nint duplicate)
    {
        bool succeeded = DuplicateHandle(
            workerProcess,
            unchecked((nint)(nuint)remoteHandle),
            GetCurrentProcess(),
            out duplicate,
            desiredAccess: 0,
            inheritHandle: false,
            options: DuplicateSameAccess);
        if (!succeeded)
            duplicate = nint.Zero;
        return succeeded;
    }

    private static WindowsWorkerProcess CreateWorkerForTest(
        SafeProcessHandle process,
        NamedPipeServerStream pipe)
    {
        ConstructorInfo constructor = typeof(WindowsWorkerProcess).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [
                typeof(SafeProcessHandle),
                typeof(NamedPipeServerStream),
                typeof(ulong),
                typeof(ulong),
                typeof(string),
                typeof(int),
                typeof(WorkerObjectSecurity.WorkerObjectSecurityContext),
            ],
            modifiers: null) ?? throw new InvalidOperationException("The internal worker constructor was not found.");
        using WindowsWorkerProcess.SafeTokenHandle primaryToken =
            WindowsWorkerProcess.OpenCurrentPrimaryTokenForWorker();
        var objectSecurity = WorkerObjectSecurity.CreateForPrimaryToken(primaryToken);
        try
        {
            return (WindowsWorkerProcess)constructor.Invoke(
            [
                process,
                pipe,
                0x1122_3344_5566_7788ul,
                0x8877_6655_4433_2211ul,
                null,
                4,
                objectSecurity,
            ]);
        }
        catch
        {
            objectSecurity.Dispose();
            throw;
        }
    }

    public enum TransportFault
    {
        WrongRequestId,
        MalformedResponse,
        PartialResponse,
        PipeLoss,
    }

    [Flags]
    private enum ProcessAccess : uint
    {
        DuplicateHandle = 0x0040,
        QueryLimitedInformation = 0x1000,
        Synchronize = 0x00100000,
        Terminate = 0x0001,
    }

    private const uint FileMapRead = 0x0004;
    private const uint FileMapWrite = 0x0002;
    private const uint DuplicateSameAccess = 0x00000002;
    private const int ErrorInvalidHandle = 6;
    private const int ErrorAccessDenied = 5;

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(ProcessAccess desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(
        SafeProcessHandle sourceProcess,
        nint sourceHandle,
        nint targetProcess,
        out nint targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint MapViewOfFile(nint mapping, uint desiredAccess, uint offsetHigh, uint offsetLow, nuint bytes);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile(nint address);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernelbase.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CompareObjectHandles(SafeFileHandle first, SafeFileHandle second);

    private sealed class WorkerTransportFixture : IAsyncDisposable
    {
        private readonly Process _child;
        private readonly NamedPipeServerStream _server;
        private bool _disposed;

        private WorkerTransportFixture(
            Process child,
            SafeProcessHandle inspectionProcess,
            NamedPipeServerStream server,
            NamedPipeClientStream peer,
            WindowsWorkerProcess worker)
        {
            _child = child;
            InspectionProcess = inspectionProcess;
            _server = server;
            Peer = peer;
            Worker = worker;
        }

        internal Process Child => _child;
        internal SafeProcessHandle InspectionProcess { get; }
        internal NamedPipeClientStream Peer { get; }
        internal WindowsWorkerProcess Worker { get; }

        internal static async Task<WorkerTransportFixture> CreateAsync(bool allowTermination = true)
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
                worker = CreateWorkerForTest(workerProcess, server);
                workerProcess = null;

                Task serverConnection = server.WaitForConnectionAsync();
                await peer.ConnectAsync(5_000).WaitAsync(TimeSpan.FromSeconds(5));
                await serverConnection.WaitAsync(TimeSpan.FromSeconds(5));

                var fixture = new WorkerTransportFixture(
                    child,
                    inspectionProcess,
                    server,
                    peer,
                    worker);
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
}
