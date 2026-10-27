using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MarkdownRenderer.Svg.Resvg.Internal;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed partial class InstalledFontCatalogTransferTests
{
    [Fact]
    public void CreateBuildsFixedLittleEndianCatalogAndReadOnlyNonInheritableFontAndTableHandles()
    {
        using Process currentProcess = Process.GetCurrentProcess();
        using WindowsWorkerProcess.SafeTokenHandle sourceToken =
            WindowsWorkerProcess.OpenCurrentPrimaryTokenForWorker();
        using WorkerObjectSecurity.WorkerObjectSecurityContext objectSecurity =
            WorkerObjectSecurity.CreateForPrimaryToken(sourceToken);
        var transfer = InstalledFontCatalogTransfer.Create(
            currentProcess.SafeHandle,
            objectSecurity,
            generation: 0x12345678,
            CancellationToken.None);
        var fontHandles = new List<nint>();
        var fontIdentities = new List<SafeFileHandle>();
        WorkerMappingLease? tableHandle = null;
        SafeFileHandle? tableIdentity = null;
        nint rawTableHandle = nint.Zero;

        try
        {
            ReadOnlySpan<byte> table = transfer.CatalogMemory.GetSpan();
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(table[8..]);
            Assert.Equal(InstalledFontCatalogTransfer.CatalogMagic, BinaryPrimitives.ReadUInt32LittleEndian(table));
            Assert.Equal(InstalledFontCatalogTransfer.CatalogVersion, BinaryPrimitives.ReadUInt32LittleEndian(table[4..]));
            Assert.InRange(count, 1u, (uint)InstalledFontCatalogTransfer.MaximumFontFiles);
            Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(table[12..]));
            Assert.Equal(checked(InstalledFontCatalogTransfer.CatalogHeaderBytes +
                (int)count * InstalledFontCatalogTransfer.CatalogEntryBytes), transfer.CatalogLength);
            Assert.Equal(transfer.CatalogLength, table.Length);

            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> entry = table.Slice(
                    InstalledFontCatalogTransfer.CatalogHeaderBytes + i * InstalledFontCatalogTransfer.CatalogEntryBytes,
                    InstalledFontCatalogTransfer.CatalogEntryBytes);
                ulong rawHandle = BinaryPrimitives.ReadUInt64LittleEndian(entry);
                ulong byteLength = BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
                Assert.NotEqual(0ul, rawHandle);
                Assert.InRange(byteLength, 1ul, (ulong)InstalledFontCatalogTransfer.MaximumFontFileBytes);

                nint fontHandle = unchecked((nint)(nuint)rawHandle);
                fontHandles.Add(fontHandle);
                fontIdentities.Add(DuplicateIdentity(currentProcess.Handle, fontHandle));
                Assert.True(GetHandleInformation(fontHandle, out uint flags));
                Assert.Equal(0u, flags & HandleFlagInherit);
            }
            Assert.Equal(checked((int)count), fontHandles.Count);

            // Font sections are PAGE_READONLY and their duplicated handles
            // additionally grant SECTION_MAP_READ only.
            ulong firstFontLength = BinaryPrimitives.ReadUInt64LittleEndian(
                table[(InstalledFontCatalogTransfer.CatalogHeaderBytes + 8)..]);
            AssertReadOnlyView(fontHandles[0], checked((nuint)Math.Min(firstFontLength, 64ul)));

            tableHandle = transfer.CatalogMemory.DuplicateInto(currentProcess.SafeHandle, readOnly: true);
            rawTableHandle = unchecked((nint)(nuint)tableHandle.ProtocolHandle);
            tableIdentity = DuplicateIdentity(currentProcess.Handle, rawTableHandle);
            Assert.True(GetHandleInformation(rawTableHandle, out uint tableFlags));
            Assert.Equal(0u, tableFlags & HandleFlagInherit);
            AssertReadOnlyView(rawTableHandle, checked((nuint)transfer.CatalogLength));

            transfer.Dispose();
            Assert.Throws<ObjectDisposedException>(() => transfer.CatalogMemory.GetSpan());
            AssertAllHandlesClosed(fontHandles, fontIdentities);

            tableHandle.Dispose();
            tableHandle = null;
            AssertHandleClosed(rawTableHandle, tableIdentity);
        }
        finally
        {
            tableHandle?.Dispose();
            transfer.Dispose();
            foreach (SafeFileHandle identity in fontIdentities)
                identity.Dispose();
            tableIdentity?.Dispose();
        }
    }

    [Fact]
    public void ApprovedRootCheckRejectsSiblingTraversalAlternateTargetAndNetworkPaths()
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "SvgFontCatalogRoot")));
        string approvedFile = Path.Combine(root, "Sample.ttf");

        Assert.True(InstalledFontCatalogTransfer.IsExactApprovedFilePath(root, approvedFile, approvedFile));
        Assert.False(InstalledFontCatalogTransfer.IsExactApprovedFilePath(
            root,
            Path.Combine(root, "subdirectory", "Sample.ttf"),
            Path.Combine(root, "subdirectory", "Sample.ttf")));
        string traversedFile = Path.GetFullPath(Path.Combine(root, "..", "SvgFontCatalogRoot-sibling", "Sample.ttf"));
        Assert.False(InstalledFontCatalogTransfer.IsExactApprovedFilePath(root, traversedFile, traversedFile));
        Assert.False(InstalledFontCatalogTransfer.IsExactApprovedFilePath(
            root,
            approvedFile,
            Path.Combine(root + "-sibling", "Sample.ttf")));
        Assert.False(InstalledFontCatalogTransfer.IsExactApprovedFilePath(
            root,
            approvedFile,
            Path.Combine(Path.GetDirectoryName(root)!, "outside.ttf")));
        Assert.False(InstalledFontCatalogTransfer.IsExactApprovedFilePath(
            root,
            approvedFile,
            @"\\server\share\Sample.ttf"));
    }

    [Fact]
    public void CatalogFileAndAggregateAddressSpaceLimitsAreEnforcedWithViewRounding()
    {
        const long granularity = 64 * 1024;
        Assert.Equal(granularity, InstalledFontCatalogTransfer.ReserveMappedView(0, 1, pointerSize: 4));
        Assert.Equal(InstalledFontCatalogTransfer.MaximumFontFileBytes,
            InstalledFontCatalogTransfer.ReserveMappedView(0,
                InstalledFontCatalogTransfer.MaximumFontFileBytes,
                pointerSize: 8));
        Assert.Equal(InstalledFontCatalogTransfer.MaximumMappedViewBytesX86,
            InstalledFontCatalogTransfer.ReserveMappedView(
                InstalledFontCatalogTransfer.MaximumMappedViewBytesX86 - granularity,
                1,
                pointerSize: 4));
        Assert.Equal(InstalledFontCatalogTransfer.MaximumMappedViewBytesWide,
            InstalledFontCatalogTransfer.ReserveMappedView(
                InstalledFontCatalogTransfer.MaximumMappedViewBytesWide - granularity,
                1,
                pointerSize: 8));

        Assert.Throws<InstalledFontCatalogException>(() =>
            InstalledFontCatalogTransfer.ReserveMappedView(
                0,
                InstalledFontCatalogTransfer.MaximumFontFileBytes + 1,
                pointerSize: 8));
        Assert.Throws<InstalledFontCatalogException>(() =>
            InstalledFontCatalogTransfer.ReserveMappedView(
                InstalledFontCatalogTransfer.MaximumMappedViewBytesX86,
                1,
                pointerSize: 4));
        Assert.Throws<InstalledFontCatalogException>(() =>
            InstalledFontCatalogTransfer.ReserveMappedView(
                InstalledFontCatalogTransfer.MaximumMappedViewBytesWide,
                1,
                pointerSize: 8));

        InstalledFontCatalogTransfer.ValidateFileCandidateCount(InstalledFontCatalogTransfer.MaximumFontFiles);
        Assert.Throws<InstalledFontCatalogException>(() =>
            InstalledFontCatalogTransfer.ValidateFileCandidateCount(
                InstalledFontCatalogTransfer.MaximumFontFiles + 1));
    }

    private static void AssertReadOnlyView(nint handle, nuint length)
    {
        nint readView = MapViewOfFile(handle, FileMapRead, 0, 0, length);
        Assert.NotEqual(nint.Zero, readView);
        try
        {
            _ = Marshal.ReadByte(readView);
        }
        finally
        {
            Assert.True(UnmapViewOfFile(readView));
        }

        nint writeView = MapViewOfFile(handle, FileMapWrite, 0, 0, length);
        int writeError = Marshal.GetLastPInvokeError();
        if (writeView != nint.Zero)
            _ = UnmapViewOfFile(writeView);
        Assert.Equal(nint.Zero, writeView);
        Assert.Equal(ErrorAccessDenied, writeError);
    }

    private static SafeFileHandle DuplicateIdentity(nint process, nint handle)
    {
        Assert.True(DuplicateHandle(process, handle, process, out SafeFileHandle duplicate,
            0, inheritHandle: false, 2)); // DUPLICATE_SAME_ACCESS
        Assert.True(CompareObjectHandles(handle, duplicate));
        return duplicate;
    }

    private static void AssertAllHandlesClosed(IReadOnlyList<nint> handles, IReadOnlyList<SafeFileHandle> identities)
    {
        Assert.Equal(handles.Count, identities.Count);
        for (int i = 0; i < handles.Count; i++)
            AssertHandleClosed(handles[i], identities[i]);
    }

    // Windows can immediately reuse a closed numeric handle in this parallel
    // test process. Compare against a retained identity of the original section
    // so an unrelated handle cannot create either a false leak or a false pass.
    private static void AssertHandleClosed(nint handle, SafeFileHandle identity) =>
        Assert.False(CompareObjectHandles(handle, identity));

    private const uint FileMapRead = 0x0004;
    private const uint FileMapWrite = 0x0002;
    private const uint HandleFlagInherit = 0x00000001;
    private const int ErrorAccessDenied = 5;

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

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile(nint address);
}
