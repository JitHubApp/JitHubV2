using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed partial class WorkerMappingLeaseTests
{
    [Fact]
    public void DuplicateIntoCreatesNonInheritableReadWriteHandleOwnedByLease()
    {
        using Process currentProcess = Process.GetCurrentProcess();
        using var sharedMemory = new SharedMemoryLease("same-logical-id", outputLength: 32);
        Span<byte> source = sharedMemory.GetSpan();
        source[3] = 0x31;
        source[19] = 0x52;

        using WorkerMappingLease workerMapping = sharedMemory.DuplicateInto(currentProcess.SafeHandle);
        nint transferredHandle = unchecked((nint)(nuint)workerMapping.ProtocolHandle);
        using SafeFileHandle identity = DuplicateIdentity(transferredHandle);

        Assert.NotEqual(nint.Zero, transferredHandle);
        Assert.True(GetHandleInformation(transferredHandle, out uint flags));
        Assert.Equal(0u, flags & HandleFlagInherit);

        nint view = MapViewOfFile(
            transferredHandle,
            FileMapRead | FileMapWrite,
            0,
            0,
            bytesToMap: 32);
        Assert.NotEqual(nint.Zero, view);
        try
        {
            Assert.Equal((byte)0x31, Marshal.ReadByte(view, 3));
            Assert.Equal((byte)0x52, Marshal.ReadByte(view, 19));

            Marshal.WriteByte(view, 11, 0xA7);
            Assert.Equal((byte)0xA7, sharedMemory.GetSpan()[11]);
        }
        finally
        {
            Assert.True(UnmapViewOfFile(view));
        }

        workerMapping.Dispose();

        Assert.False(CompareObjectHandles(transferredHandle, identity));
        Assert.Equal((byte)0x31, sharedMemory.GetSpan()[3]);
    }

    [Fact]
    public void SharedMemoryLogicalNamesDoNotAliasKernelObjects()
    {
        using var first = new SharedMemoryLease("same-logical-id", outputLength: 8);
        using var second = new SharedMemoryLease("same-logical-id", outputLength: 8);

        first.GetSpan().Fill(0x11);
        second.GetSpan().Fill(0x22);

        Assert.Equal(first.Name, second.Name);
        Assert.All(first.GetSpan().ToArray(), value => Assert.Equal((byte)0x11, value));
        Assert.All(second.GetSpan().ToArray(), value => Assert.Equal((byte)0x22, value));
    }

    [Fact]
    public void WorkerMappingHandleRemainsValidAfterHostMappingOwnerIsDisposed()
    {
        using Process currentProcess = Process.GetCurrentProcess();
        var sharedMemory = new SharedMemoryLease("owner-disposal", outputLength: 16);
        sharedMemory.GetSpan()[5] = 0x6C;
        WorkerMappingLease workerMapping = sharedMemory.DuplicateInto(currentProcess.SafeHandle);
        nint transferredHandle = unchecked((nint)(nuint)workerMapping.ProtocolHandle);
        using SafeFileHandle identity = DuplicateIdentity(transferredHandle);

        ((IDisposable)sharedMemory).Dispose();
        using (workerMapping)
        {
            nint view = MapViewOfFile(
                transferredHandle,
                FileMapRead | FileMapWrite,
                0,
                0,
                bytesToMap: 16);
            Assert.NotEqual(nint.Zero, view);
            try
            {
                Assert.Equal((byte)0x6C, Marshal.ReadByte(view, 5));
                Marshal.WriteByte(view, 9, 0xD4);
            }
            finally
            {
                Assert.True(UnmapViewOfFile(view));
            }
        }

        Assert.False(CompareObjectHandles(transferredHandle, identity));
    }

    [Fact]
    public void MappingLeasePinsTargetProcessHandleUntilRemoteHandleIsClosed()
    {
        Process currentProcess = Process.GetCurrentProcess();
        try
        {
            using var sharedMemory = new SharedMemoryLease("pinned-process", outputLength: 8);
            WorkerMappingLease workerMapping = sharedMemory.DuplicateInto(currentProcess.SafeHandle);
            nint transferredHandle = unchecked((nint)(nuint)workerMapping.ProtocolHandle);
            using SafeFileHandle identity = DuplicateIdentity(transferredHandle);

            currentProcess.SafeHandle.Dispose();
            workerMapping.Dispose();

            Assert.False(CompareObjectHandles(transferredHandle, identity));
        }
        finally
        {
            currentProcess.Dispose();
        }
    }

    [Fact]
    public void ProtocolV10CarriesOnlyTheFixedRawHandleAndZeroReservedNameRegion()
    {
        const ulong mappingHandle = 0x1234_5678_9ABC_DEF0;
        byte[] frame = WorkerProtocol.Encode(CreateRequest(WorkerOperation.Open, mappingHandle));

        Assert.Equal((ushort)10, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4)));
        Assert.Equal(mappingHandle, BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(240)));
        Assert.Equal(-1, frame.AsSpan(248, 120).IndexOfAnyExcept((byte)0));
        Assert.Equal(-1, frame.AsSpan(170, 6).IndexOfAnyExcept((byte)0));
    }

    [Fact]
    public void ProtocolEncoderReusableFrameMatchesAllocatedFramesAndClearsPriorFields()
    {
        byte[] reusableFrame = new byte[WorkerProtocol.RequestSize];
        reusableFrame.AsSpan().Fill(0xA5);

        WorkerRequest render = CreateRequest(WorkerOperation.Render, 0x1020_3040ul) with
        {
            ContentHash = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray(),
        };
        byte[] expectedRender = WorkerProtocol.Encode(render);
        WorkerProtocol.Encode(render, reusableFrame);
        Assert.Equal(expectedRender, reusableFrame);

        // A control request leaves many data-operation fields unused. Reusing
        // a previously populated frame must keep all those bytes zero.
        WorkerRequest hello = CreateRequest(WorkerOperation.Hello, 0);
        byte[] expectedHello = WorkerProtocol.Encode(hello);
        WorkerProtocol.Encode(hello, reusableFrame);
        Assert.Equal(expectedHello, reusableFrame);
        Assert.Equal(-1, reusableFrame.AsSpan(32, 36).IndexOfAnyExcept((byte)0));
        Assert.Equal(-1, reusableFrame.AsSpan(248, 120).IndexOfAnyExcept((byte)0));

        Assert.Throws<InvalidOperationException>(() =>
            WorkerProtocol.Encode(CreateRequest(WorkerOperation.Open, 0), reusableFrame));
        Assert.Equal(-1, reusableFrame.AsSpan().IndexOfAnyExcept((byte)0));
    }

    private static WorkerRequest CreateRequest(WorkerOperation operation, ulong mappingHandle) => new(
        operation,
        RequestId: 7,
        NonceLow: 11,
        NonceHigh: 13,
        MappingHandle: mappingHandle,
        ContentHash: new byte[32],
        SourceLength: operation == WorkerOperation.Open ? 4 : 0,
        OutputLength: operation == WorkerOperation.Render ? 64 : 0,
        TargetWidth: operation == WorkerOperation.Render ? 4 : 0,
        TargetHeight: operation == WorkerOperation.Render ? 4 : 0,
        Tile: null,
        Locale: "en",
        ColorScheme: MarkdownSvgColorScheme.Light,
        SemanticColor: null,
        PixelFormat: MarkdownSvgPixelFormat.Rgba8Premultiplied,
        FontGeneration: 0,
        ParsedResourceCacheBytes: 1024,
        DocumentId: operation is WorkerOperation.Open or WorkerOperation.Render ? 9ul : 0ul,
        Options: new ResvgMarkdownSvgRendererOptions());

    private const uint FileMapRead = 0x0004;
    private const uint FileMapWrite = 0x0002;
    private const uint HandleFlagInherit = 0x00000001;

    // A closed numeric slot can be reused immediately by a parallel test.
    // Compare the retained original section identity, not merely slot validity.
    private static SafeFileHandle DuplicateIdentity(nint handle)
    {
        Assert.True(DuplicateHandle(new nint(-1), handle, new nint(-1), out SafeFileHandle duplicate,
            0, false, 2));
        Assert.True(CompareObjectHandles(handle, duplicate));
        return duplicate;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(nint sourceProcess, nint sourceHandle, nint targetProcess,
        out SafeFileHandle targetHandle, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [LibraryImport("kernelbase.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CompareObjectHandles(nint first, SafeFileHandle second);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetHandleInformation(nint handle, out uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint MapViewOfFile(
        nint mapping,
        uint desiredAccess,
        uint fileOffsetHigh,
        uint fileOffsetLow,
        nuint bytesToMap);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile(nint address);
}
