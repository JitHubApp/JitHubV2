using System.Diagnostics;
using System.Runtime.InteropServices;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed partial class WorkerMappingLeaseTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    public void RasterSectionKeepsSourceOutputPaddingAndProgressSeparate(int sourceLength)
    {
        byte[] source = Enumerable.Range(0, sourceLength).Select(index => (byte)(index + 1)).ToArray();
        byte[] expected = source.ToArray();
        using var security = new WorkerObjectSecurity.WorkerObjectSecurityContext(
            WorkerObjectSecurity.CreateRestrictedDaclSddl(),
            WorkerObjectSecurity.CreateRestrictedLowIntegrityDescriptor());
        using var memory = new SharedMemoryLease("raster-section-layout", source.AsMemory(), 16, security);
        source.AsSpan().Fill(0xAA); // The admitted section is a copy, never caller-memory aliasing.
        Assert.Equal(16, memory.Memory.Length);
        Assert.Equal(-1, memory.GetSpan().IndexOfAnyExcept((byte)0));
        Assert.Equal(0, memory.ReadOpenProgressPhase());

        using Process process = Process.GetCurrentProcess();
        using WorkerMappingLease duplicated = memory.DuplicateInto(process.SafeHandle);
        int outputOffset = (sourceLength + 63) & ~63;
        int totalBytes = outputOffset + 16 + SharedMemoryLease.ProgressAreaBytes;
        nint mapped = MapViewOfFile(unchecked((nint)(nuint)duplicated.ProtocolHandle),
            FileMapRead | FileMapWrite, 0, 0, checked((nuint)totalBytes));
        Assert.NotEqual(nint.Zero, mapped);
        try
        {
            byte[] section = new byte[totalBytes];
            Marshal.Copy(mapped, section, 0, section.Length);
            Assert.Equal(expected, section.AsSpan(0, sourceLength).ToArray());
            Assert.Equal(-1, section.AsSpan(sourceLength).IndexOfAnyExcept((byte)0));
            memory.GetSpan().Fill(0x7B);
            Assert.Equal((byte)0x7B, Marshal.ReadByte(mapped, outputOffset));
            Assert.Equal((byte)0, Marshal.ReadByte(mapped, outputOffset + 16));
            Marshal.WriteByte(mapped, outputOffset + 16, 9);
            Assert.Equal(9, memory.ReadOpenProgressPhase());
            Assert.Equal((byte)0x7B, memory.GetSpan()[^1]);
            Assert.Equal(expected[^1], Marshal.ReadByte(mapped, sourceLength - 1));
        }
        finally
        {
            Assert.True(UnmapViewOfFile(mapped));
        }
    }
}
