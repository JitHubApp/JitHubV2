using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal sealed unsafe partial class SharedMemoryLease : MemoryManager<byte>
{
    private SafeMappingHandle? _mapping;
    private byte* _pointer;
    private readonly int _exposedOffset;
    private readonly int _exposedLength;

    public SharedMemoryLease(string name, byte[] source, int outputLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(source);
        if (outputLength < 0)
            throw new ArgumentOutOfRangeException(nameof(outputLength));

        Name = name;
        int sourceArea = Align64(source.Length);
        long totalLength = checked((long)sourceArea + outputLength);
        using WorkerObjectSecurity.SecurityDescriptorHandle descriptor =
            WorkerObjectSecurity.CreateRestrictedLowIntegrityDescriptor();
        var securityAttributes = new SecurityAttributes
        {
            Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
            SecurityDescriptor = descriptor.DangerousGetHandle(),
            InheritHandle = 0,
        };
        nint rawMapping = CreateFileMappingW(
            new nint(-1),
            ref securityAttributes,
            PageReadWrite,
            checked((uint)((ulong)totalLength >> 32)),
            checked((uint)totalLength),
            name);
        if (rawMapping == 0 || rawMapping == -1)
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastPInvokeError(),
                "The restricted SVG shared-memory object could not be created.");

        _mapping = new SafeMappingHandle(rawMapping);
        nint view = MapViewOfFile(
            _mapping,
            FileMapAllAccess,
            0,
            0,
            checked((nuint)totalLength));
        if (view == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            _mapping.Dispose();
            _mapping = null;
            throw new System.ComponentModel.Win32Exception(
                error,
                "The restricted SVG shared-memory view could not be created.");
        }

        _pointer = (byte*)view;
        source.CopyTo(new Span<byte>(_pointer, source.Length));
        _exposedOffset = sourceArea;
        _exposedLength = outputLength;
    }

    public string Name { get; }

    public override Span<byte> GetSpan()
    {
        ObjectDisposedException.ThrowIf(_mapping is null, this);
        return new Span<byte>(_pointer + _exposedOffset, _exposedLength);
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_mapping is null, this);
        ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
        if (elementIndex > _exposedLength)
            throw new ArgumentOutOfRangeException(nameof(elementIndex));
        return new MemoryHandle(_pointer + _exposedOffset + elementIndex, default, this);
    }

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
        byte* pointer = _pointer;
        _pointer = null;
        if (pointer != null)
        {
            _ = UnmapViewOfFile((nint)pointer);
        }
        Interlocked.Exchange(ref _mapping, null)?.Dispose();
    }

    private static int Align64(int value) => checked((value + 63) & ~63);

    private const uint PageReadWrite = 0x00000004;
    private const uint FileMapAllAccess = 0x000F001F;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    private sealed class SafeMappingHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeMappingHandle(nint handle)
            : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileMappingW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFileMappingW(
        nint file,
        ref SecurityAttributes attributes,
        uint protect,
        uint maximumSizeHigh,
        uint maximumSizeLow,
        string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint MapViewOfFile(
        SafeMappingHandle mapping,
        uint desiredAccess,
        uint fileOffsetHigh,
        uint fileOffsetLow,
        nuint bytesToMap);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile(nint address);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
