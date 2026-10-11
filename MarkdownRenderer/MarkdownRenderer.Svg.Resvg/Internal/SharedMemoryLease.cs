using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal sealed unsafe partial class SharedMemoryLease : MemoryManager<byte>
{
    // A protocol-v5 trailer records bounded worker progress for a timed-out
    // Open. It is outside both the source and exact raster output ranges.
    internal const int ProgressAreaBytes = 64;
    private SafeMappingHandle? _mapping;
    private byte* _pointer;
    private readonly int _exposedOffset;
    private readonly int _exposedLength;
    private readonly int _progressOffset;

    internal SharedMemoryLease(string name, SvgSourceBuffer source, int outputLength)
        : this(name, source, source?.Length ?? throw new ArgumentNullException(nameof(source)), outputLength, securityContext: null)
    {
    }

    internal SharedMemoryLease(
        string name,
        SvgSourceBuffer source,
        int outputLength,
        WorkerObjectSecurity.WorkerObjectSecurityContext securityContext)
        : this(name, source, source?.Length ?? throw new ArgumentNullException(nameof(source)), outputLength,
            securityContext ?? throw new ArgumentNullException(nameof(securityContext)))
    {
    }

    internal SharedMemoryLease(string name, int outputLength)
        : this(name, null, 0, outputLength, securityContext: null)
    {
    }

    internal SharedMemoryLease(
        string name,
        int outputLength,
        WorkerObjectSecurity.WorkerObjectSecurityContext securityContext)
        : this(name, null, 0, outputLength, securityContext ?? throw new ArgumentNullException(nameof(securityContext)))
    {
    }

    private SharedMemoryLease(
        string name,
        SvgSourceBuffer? source,
        int sourceLength,
        int outputLength,
        WorkerObjectSecurity.WorkerObjectSecurityContext? securityContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (outputLength < 0)
            throw new ArgumentOutOfRangeException(nameof(outputLength));
        if (sourceLength < 0 || source is null && sourceLength != 0)
            throw new ArgumentOutOfRangeException(nameof(sourceLength));
        ObjectDisposedException.ThrowIf(source?.IsReleased == true, source!);

        Name = name;
        int sourceArea = Align64(sourceLength);
        int progressOffset = checked(sourceArea + outputLength);
        long totalLength = checked((long)sourceArea + outputLength + ProgressAreaBytes);
        // The worker-generation context is the production path. The contextless
        // overloads are retained for isolated test fixtures; production pool
        // factories always pass the selected worker's captured primary-token
        // descriptor explicitly.
        bool ownsDescriptor = securityContext is null;
        WorkerObjectSecurity.SecurityDescriptorHandle descriptor = securityContext?.Descriptor ??
            WorkerObjectSecurity.CreateRestrictedLowIntegrityDescriptor();
        bool descriptorReferenceAdded = false;
        nint rawMapping;
        int mappingError = 0;
        try
        {
            descriptor.DangerousAddRef(ref descriptorReferenceAdded);
            var securityAttributes = new SecurityAttributes
            {
                Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
                SecurityDescriptor = descriptor.DangerousGetHandle(),
                InheritHandle = 0,
            };
            rawMapping = CreateFileMappingW(
                new nint(-1),
                ref securityAttributes,
                PageReadWrite,
                checked((uint)((ulong)totalLength >> 32)),
                checked((uint)totalLength),
                name: null);
            if (rawMapping == 0 || rawMapping == -1)
                mappingError = Marshal.GetLastPInvokeError();
        }
        finally
        {
            if (descriptorReferenceAdded)
                descriptor.DangerousRelease();
            if (ownsDescriptor)
                descriptor.Dispose();
        }

        if (rawMapping == 0 || rawMapping == -1)
            throw new System.ComponentModel.Win32Exception(
                mappingError,
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
        _exposedOffset = sourceArea;
        _exposedLength = outputLength;
        _progressOffset = progressOffset;
        try
        {
            if (source is not null)
                source.CopyTo(new Span<byte>(_pointer, sourceLength));
        }
        catch
        {
            // A source owner can be retired before this constructor captures
            // its backing. Constructor failure must release both the mapped
            // view and handle; closing only the handle does not unmap the view.
            Dispose(disposing: true);
            throw;
        }
    }

    public string Name { get; }

    // Name is only a host-side admission identity. The section is anonymous:
    // restricted primary tokens cannot reliably resolve the session object
    // namespace, even when the section DACL and integrity label permit access.
    internal WorkerMappingLease DuplicateInto(SafeProcessHandle process, bool readOnly = false) =>
        WorkerMappingLease.Create(_mapping ?? throw new ObjectDisposedException(nameof(SharedMemoryLease)), process, readOnly);

    internal int ReadOpenProgressPhase()
    {
        ObjectDisposedException.ThrowIf(_mapping is null, this);
        return Volatile.Read(ref *(_pointer + _progressOffset));
    }

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
        string? name);

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
