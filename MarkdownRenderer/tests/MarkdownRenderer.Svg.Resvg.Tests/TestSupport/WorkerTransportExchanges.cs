using System.Buffers.Binary;
using System.Runtime.InteropServices;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Svg.Resvg.Internal;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Transport;
using Microsoft.Win32.SafeHandles;
using Xunit;
using static MarkdownRenderer.Svg.Resvg.Tests.TestSupport.WorkerTransportNative;

namespace MarkdownRenderer.Svg.Resvg.Tests.TestSupport;

/// <summary>
/// Request and response frames the pipe peer exchanges with a fixture worker,
/// and checks of the mapping handles that worker received.
/// </summary>
internal static class WorkerTransportExchanges
{
    internal static Task<WorkerResponse> StartExchange(
        WorkerTransportFixture fixture,
        SharedMemoryLease sharedMemory,
        CancellationToken cancellationToken) => fixture.Worker.ExchangeAsync(
            CreateRenderRequest(),
            TimeSpan.FromSeconds(5),
            cancellationToken,
            sharedMemory).AsTask();

    internal static WorkerRequest CreateRenderRequest() => new(
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

    internal static async Task<byte[]> ReadRequestAsync(Stream peer)
    {
        byte[] request = new byte[WorkerProtocol.RequestSize];
        await peer.ReadExactlyAsync(request).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkerProtocol.Magic, BinaryPrimitives.ReadUInt32LittleEndian(request));
        Assert.Equal(WorkerProtocol.Version, BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(4)));
        return request;
    }

    internal static byte[] CreateResponse(ReadOnlySpan<byte> request)
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

    internal static void AssertRemoteMappingMatches(
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

    internal static void AssertRemoteHandleClosed(SafeProcessHandle workerProcess, ulong remoteHandle, SafeFileHandle identity)
    {
        if (TryDuplicateRemoteHandle(workerProcess, remoteHandle, out nint duplicate))
        {
            // The child may reuse the numeric slot for an unrelated handle.
            // Test the original kernel object's identity, not its old number.
            using var current = new SafeFileHandle(duplicate, ownsHandle: true);
            Assert.False(CompareObjectHandles(current, identity));
        }
        else
        {
            // An empty slot fails with ERROR_INVALID_HANDLE. A slot the child
            // reused for an object type that cannot be duplicated (for example an
            // ETW registration) fails with ERROR_NOT_SUPPORTED. The section and
            // file objects this probe tracks always duplicate, so either error
            // proves the original object is no longer at that slot.
            int error = Marshal.GetLastPInvokeError();
            Assert.True(error is ErrorInvalidHandle or ErrorNotSupported,
                $"Expected the remote handle to be closed; DuplicateHandle returned {error}.");
        }
    }

    internal static void AssertRemoteHandleUnavailable(SafeProcessHandle workerProcess, ulong remoteHandle)
    {
        Assert.False(TryDuplicateRemoteHandle(workerProcess, remoteHandle, out nint duplicate));
        int error = Marshal.GetLastPInvokeError();
        Assert.True(error is ErrorInvalidHandle or ErrorAccessDenied,
            $"Expected the worker mapping handle to be unavailable; DuplicateHandle returned {error}.");
        if (duplicate != nint.Zero)
            Assert.True(CloseHandle(duplicate));
    }

    internal static nint DuplicateRemoteHandle(SafeProcessHandle workerProcess, ulong remoteHandle)
    {
        if (!TryDuplicateRemoteHandle(workerProcess, remoteHandle, out nint duplicate))
        {
            throw new InvalidOperationException(
                $"The duplicated worker mapping handle was not readable (Win32 error {Marshal.GetLastPInvokeError()}).");
        }

        return duplicate;
    }

    internal static bool TryDuplicateRemoteHandle(
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
}
