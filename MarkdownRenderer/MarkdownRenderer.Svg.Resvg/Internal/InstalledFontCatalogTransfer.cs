using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Owns a read-only, handle-transferred snapshot of the installed Windows
/// fonts. The worker receives the catalog table separately and borrows each
/// per-font section until it acknowledges that it has opened the snapshot.
/// </summary>
internal sealed partial class InstalledFontCatalogTransfer : IDisposable
{
    internal const uint CatalogMagic = 0x544E464D; // "MFNT" in little endian.
    internal const uint CatalogVersion = 1;
    internal const int CatalogHeaderBytes = 16;
    internal const int CatalogEntryBytes = 16;
    internal const int MaximumFontFiles = 4096;
    internal const long MaximumFontFileBytes = 128L * 1024 * 1024;
    internal const long MaximumMappedViewBytesX86 = 1_342_177_280L;
    internal const long MaximumMappedViewBytesWide = 4L * 1024 * 1024 * 1024;

    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint PageReadOnly = 0x00000002;
    private const uint FileNameNormalized = 0x00000000;
    private const uint VolumeNameDos = 0x00000000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int FileNameBufferInitialChars = 512;
    private const int FileNameBufferMaximumChars = 32768;
    private const long AllocationGranularity = 64 * 1024;

    private readonly List<FontSectionLease> _fontSections = [];
    private SharedMemoryLease? _catalogMemory;
    private int _disposed;

    private InstalledFontCatalogTransfer()
    {
    }

    internal SharedMemoryLease CatalogMemory =>
        _catalogMemory ?? throw new ObjectDisposedException(nameof(InstalledFontCatalogTransfer));

    internal int CatalogLength { get; private set; }

    /// <summary>
    /// Creates the immutable font catalog and duplicates each file-backed
    /// section into the worker with SECTION_MAP_READ only. Call from a worker
    /// thread; the worker protocol owns ACK timing and this transfer must stay
    /// alive until that ACK is received.
    /// </summary>
    internal static InstalledFontCatalogTransfer Create(
        SafeProcessHandle worker,
        WorkerObjectSecurity.WorkerObjectSecurityContext objectSecurity,
        long generation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(objectSecurity);
        if (worker.IsClosed || worker.IsInvalid)
            throw new ArgumentException("The SVG worker process handle is not open.", nameof(worker));

        if (generation < 0 || generation > uint.MaxValue)
        {
            throw new InstalledFontCatalogException(
                InstalledFontCatalogFailure.InvalidGeneration,
                "The installed-font catalog generation is outside the protocol range.");
        }

        var transfer = new InstalledFontCatalogTransfer();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnumerateAndMapFonts(worker, transfer, objectSecurity, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            transfer.CreateCatalogTable(checked((uint)generation), objectSecurity);
            return transfer;
        }
        catch (OperationCanceledException)
        {
            transfer.Dispose();
            throw;
        }
        catch (OutOfMemoryException)
        {
            transfer.Dispose();
            throw;
        }
        catch (InstalledFontCatalogException)
        {
            transfer.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            transfer.Dispose();
            throw new InstalledFontCatalogException(
                InstalledFontCatalogFailure.CatalogUnavailable,
                "The installed Windows font catalog could not be captured completely.",
                ex);
        }
    }

    /// <summary>Tests whether one enumerated file still names the exact file opened beneath its approved root.</summary>
    internal static bool IsExactApprovedFilePath(
        string canonicalRoot,
        string enumeratedPath,
        string finalHandlePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(enumeratedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalHandlePath);

        string normalizedRoot = NormalizePath(Path.GetFullPath(NormalizePath(canonicalRoot)));
        string normalizedEnumerated = NormalizePath(Path.GetFullPath(enumeratedPath));
        string normalizedFinal = NormalizePath(Path.GetFullPath(NormalizePath(finalHandlePath)));
        if (!IsLocalDrivePath(normalizedRoot) ||
            !IsLocalDrivePath(normalizedEnumerated) ||
            !IsLocalDrivePath(normalizedFinal))
        {
            return false;
        }

        string? enumeratedParent = Path.GetDirectoryName(normalizedEnumerated);
        string? finalParent = Path.GetDirectoryName(normalizedFinal);
        string rootWithoutTrailingSeparator = TrimTrailingSeparator(normalizedRoot);
        return string.Equals(TrimTrailingSeparator(enumeratedParent ?? string.Empty),
                   rootWithoutTrailingSeparator, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(TrimTrailingSeparator(finalParent ?? string.Empty),
                   rootWithoutTrailingSeparator, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileName(normalizedEnumerated), Path.GetFileName(normalizedFinal),
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns the next aggregate reservation or fails before any over-budget section is created.</summary>
    internal static long ReserveMappedView(long currentBytes, long fontFileBytes, int pointerSize)
    {
        if (currentBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(currentBytes));
        if (pointerSize is not (4 or 8))
            throw new ArgumentOutOfRangeException(nameof(pointerSize));
        if (fontFileBytes <= 0 || fontFileBytes > MaximumFontFileBytes)
        {
            throw new InstalledFontCatalogException(
                InstalledFontCatalogFailure.ResourceLimit,
                "An installed font is empty or exceeds the per-file byte limit.");
        }

        long roundedBytes = checked((fontFileBytes + AllocationGranularity - 1) & ~(AllocationGranularity - 1));
        long maximumBytes = pointerSize == 4 ? MaximumMappedViewBytesX86 : MaximumMappedViewBytesWide;
        if (currentBytes > maximumBytes || roundedBytes > maximumBytes - currentBytes)
        {
            throw new InstalledFontCatalogException(
                InstalledFontCatalogFailure.ResourceLimit,
                "The installed font catalog exceeds the worker address-space budget.");
        }

        return currentBytes + roundedBytes;
    }

    internal static void ValidateFileCandidateCount(int candidateCount)
    {
        if (candidateCount < 0 || candidateCount > MaximumFontFiles)
        {
            throw new InstalledFontCatalogException(
                InstalledFontCatalogFailure.ResourceLimit,
                "The installed font catalog exceeds the file-count limit.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        for (int i = _fontSections.Count - 1; i >= 0; i--)
        {
            try
            {
                _fontSections[i].RemoteHandle.Dispose();
            }
            catch
            {
                // Continue releasing the remaining remote mappings and host sections.
            }
        }

        for (int i = _fontSections.Count - 1; i >= 0; i--)
        {
            try
            {
                _fontSections[i].Section.Dispose();
            }
            catch
            {
                // A failed close must not strand the rest of the catalog handles.
            }
        }
        _fontSections.Clear();
        SharedMemoryLease? catalogMemory = Interlocked.Exchange(ref _catalogMemory, null);
        if (catalogMemory is not null)
        {
            try
            {
                ((IDisposable)catalogMemory).Dispose();
            }
            catch
            {
                // Cleanup remains best-effort for every owner after a native close failure.
            }
        }
    }

    private static void EnumerateAndMapFonts(
        SafeProcessHandle worker,
        InstalledFontCatalogTransfer transfer,
        WorkerObjectSecurity.WorkerObjectSecurityContext objectSecurity,
        CancellationToken cancellationToken)
    {
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windowsDirectory))
        {
            throw new InstalledFontCatalogException(
                InstalledFontCatalogFailure.CatalogUnavailable,
                "The Windows directory could not be resolved for installed-font discovery.");
        }

        string machineFonts = Path.GetFullPath(Path.Combine(windowsDirectory, "Fonts"));
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string? userFonts = string.IsNullOrWhiteSpace(localAppData)
            ? null
            : Path.GetFullPath(Path.Combine(localAppData, "Microsoft", "Windows", "Fonts"));

        var visitedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long aggregateViewBytes = 0;
        int candidateCount = 0;
        var fileIdentities = new HashSet<FileIdentity>();

        ProcessFontRoot(machineFonts, optional: false);
        visitedRoots.Add(NormalizePath(machineFonts));
        if (userFonts is not null)
        {
            string normalizedUserFonts = NormalizePath(userFonts);
            if (visitedRoots.Add(normalizedUserFonts))
                ProcessFontRoot(userFonts, optional: true);
        }

        if (transfer._fontSections.Count == 0)
        {
            throw new InstalledFontCatalogException(
                InstalledFontCatalogFailure.EmptyCatalog,
                "No supported installed Windows font files were available to the isolated renderer.");
        }

        void ProcessFontRoot(string rootPath, bool optional)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalizedRoot = NormalizePath(Path.GetFullPath(rootPath));
            if (!IsLocalDrivePath(normalizedRoot))
            {
                throw new InstalledFontCatalogException(
                    InstalledFontCatalogFailure.UnsafePath,
                    "An installed-font root did not resolve to a local drive path.");
            }

            using SafeFileHandle rootHandle = OpenDirectory(rootPath, optional);
            if (rootHandle.IsInvalid)
                return;

            ByHandleFileInformation rootInformation = GetFileInformation(rootHandle, rootPath);
            if ((rootInformation.FileAttributes & FileAttributeDirectory) == 0 ||
                (rootInformation.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new InstalledFontCatalogException(
                    InstalledFontCatalogFailure.UnsafePath,
                    "An installed-font root is not a plain directory.");
            }

            string finalRoot = NormalizePath(GetFinalPath(rootHandle));
            if (!IsLocalDrivePath(finalRoot) ||
                !string.Equals(TrimTrailingSeparator(finalRoot), TrimTrailingSeparator(normalizedRoot),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InstalledFontCatalogException(
                    InstalledFontCatalogFailure.UnsafePath,
                    "An installed-font root changed identity while it was opened.");
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(rootPath, "*", new EnumerationOptions
                {
                    AttributesToSkip = 0,
                    IgnoreInaccessible = false,
                    RecurseSubdirectories = false,
                    ReturnSpecialDirectories = false,
                });
                foreach (string filePath in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    candidateCount = checked(candidateCount + 1);
                    ValidateFileCandidateCount(candidateCount);
                    if (!IsSupportedFontExtension(filePath))
                        continue;
                    string enumeratedPath = NormalizePath(Path.GetFullPath(filePath));
                    if (!string.Equals(TrimTrailingSeparator(Path.GetDirectoryName(enumeratedPath) ?? string.Empty),
                            TrimTrailingSeparator(finalRoot), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InstalledFontCatalogException(
                            InstalledFontCatalogFailure.UnsafePath,
                            "A font enumeration result escaped its approved root.");
                    }

                    using SafeFileHandle fontFile = OpenFontFile(filePath);
                    ByHandleFileInformation fileInformation = GetFileInformation(fontFile, filePath);
                    if ((fileInformation.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
                    {
                        throw new InstalledFontCatalogException(
                            InstalledFontCatalogFailure.UnsafePath,
                            "An enumerated font file is a directory or reparse point.");
                    }

                    string finalFilePath = GetFinalPath(fontFile);
                    if (!IsExactApprovedFilePath(finalRoot, enumeratedPath, finalFilePath))
                    {
                        throw new InstalledFontCatalogException(
                            InstalledFontCatalogFailure.UnsafePath,
                            "An opened font file did not retain its exact approved-root identity.");
                    }

                    long fileBytes = GetFileLength(fileInformation);
                    ulong fileIndex = ((ulong)fileInformation.FileIndexHigh << 32) | fileInformation.FileIndexLow;
                    var identity = new FileIdentity(fileInformation.VolumeSerialNumber, fileIndex);
                    if (fileIndex != 0 && !fileIdentities.Add(identity))
                        continue;

                    long nextAggregate = ReserveMappedView(aggregateViewBytes, fileBytes, IntPtr.Size);

                    cancellationToken.ThrowIfCancellationRequested();
                    SafeFileMappingHandle section = CreateReadOnlySection(fontFile, filePath, objectSecurity);
                    WorkerMappingLease? remote = null;
                    try
                    {
                        remote = WorkerMappingLease.Create(section, worker, readOnly: true);
                        transfer._fontSections.Add(new FontSectionLease(section, remote, checked((ulong)fileBytes)));
                        aggregateViewBytes = nextAggregate;
                        section = null!;
                        remote = null;
                    }
                    finally
                    {
                        remote?.Dispose();
                        section?.Dispose();
                    }
                }
            }
            catch (InstalledFontCatalogException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new InstalledFontCatalogException(
                    InstalledFontCatalogFailure.CatalogUnavailable,
                    "An installed-font directory could not be enumerated completely.",
                    ex);
            }
        }
    }

    private void CreateCatalogTable(
        uint generation,
        WorkerObjectSecurity.WorkerObjectSecurityContext objectSecurity)
    {
        int tableLength = checked(CatalogHeaderBytes + _fontSections.Count * CatalogEntryBytes);
        _catalogMemory = new SharedMemoryLease(
            "svg-font-catalog",
            outputLength: tableLength,
            securityContext: objectSecurity);
        CatalogLength = tableLength;
        Span<byte> table = _catalogMemory.GetSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(table, CatalogMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(table[4..], CatalogVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(table[8..], checked((uint)_fontSections.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(table[12..], generation);

        for (int i = 0; i < _fontSections.Count; i++)
        {
            FontSectionLease entry = _fontSections[i];
            Span<byte> encoded = table.Slice(CatalogHeaderBytes + i * CatalogEntryBytes, CatalogEntryBytes);
            BinaryPrimitives.WriteUInt64LittleEndian(encoded, entry.RemoteHandle.ProtocolHandle);
            BinaryPrimitives.WriteUInt64LittleEndian(encoded[8..], entry.ByteLength);
        }
    }

    private static SafeFileHandle OpenDirectory(string path, bool optional)
    {
        nint rawHandle = CreateFileW(
            path,
            FileReadAttributes,
            ShareRead | ShareWrite,
            0,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            0);
        if (rawHandle != 0 && rawHandle != new nint(-1))
            return new SafeFileHandle(rawHandle, ownsHandle: true);

        int error = Marshal.GetLastPInvokeError();
        if (optional && error is ErrorFileNotFound or ErrorPathNotFound)
            return new SafeFileHandle(new nint(-1), ownsHandle: false);
        throw new InstalledFontCatalogException(
            InstalledFontCatalogFailure.CatalogUnavailable,
            "An approved installed-font directory could not be opened.",
            new Win32Exception(error));
    }

    private static SafeFileHandle OpenFontFile(string path)
    {
        // Sharing read-only prevents a concurrently opened writer from changing
        // the bytes after the handle identity and section length are validated.
        nint rawHandle = CreateFileW(
            path,
            GenericRead,
            ShareRead,
            0,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagSequentialScan,
            0);
        if (rawHandle != 0 && rawHandle != new nint(-1))
            return new SafeFileHandle(rawHandle, ownsHandle: true);

        int error = Marshal.GetLastPInvokeError();
        throw new InstalledFontCatalogException(
            InstalledFontCatalogFailure.CatalogUnavailable,
            "An enumerated installed font could not be read.",
            new Win32Exception(error));
    }

    private static SafeFileMappingHandle CreateReadOnlySection(
        SafeFileHandle file,
        string path,
        WorkerObjectSecurity.WorkerObjectSecurityContext objectSecurity)
    {
        WorkerObjectSecurity.SecurityDescriptorHandle descriptor = objectSecurity.Descriptor;
        bool descriptorReferenceAdded = false;
        nint rawMapping;
        int mappingError = 0;
        try
        {
            descriptor.DangerousAddRef(ref descriptorReferenceAdded);
            var attributes = new SecurityAttributes
            {
                Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
                SecurityDescriptor = descriptor.DangerousGetHandle(),
                InheritHandle = 0,
            };
            rawMapping = CreateFileMappingW(file, ref attributes, PageReadOnly, 0, 0, null);
            if (rawMapping == 0 || rawMapping == new nint(-1))
                mappingError = Marshal.GetLastPInvokeError();
        }
        finally
        {
            if (descriptorReferenceAdded)
                descriptor.DangerousRelease();
        }

        if (rawMapping != 0 && rawMapping != new nint(-1))
            return new SafeFileMappingHandle(rawMapping);

        throw new InstalledFontCatalogException(
            InstalledFontCatalogFailure.MappingFailed,
            "An installed font could not be represented by a read-only file mapping.",
            new Win32Exception(mappingError, path));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    private static ByHandleFileInformation GetFileInformation(SafeFileHandle file, string path)
    {
        if (GetFileInformationByHandle(file, out ByHandleFileInformation information))
            return information;

        int error = Marshal.GetLastPInvokeError();
        throw new InstalledFontCatalogException(
            InstalledFontCatalogFailure.CatalogUnavailable,
            "An installed-font handle could not be inspected safely.",
            new Win32Exception(error, path));
    }

    private static long GetFileLength(ByHandleFileInformation information)
    {
        ulong length = ((ulong)information.FileSizeHigh << 32) | information.FileSizeLow;
        if (length > long.MaxValue)
        {
            throw new InstalledFontCatalogException(
                InstalledFontCatalogFailure.ResourceLimit,
                "An installed font has an invalid file length.");
        }

        return (long)length;
    }

    private static unsafe string GetFinalPath(SafeFileHandle handle)
    {
        int capacity = FileNameBufferInitialChars;
        while (true)
        {
            char[] buffer = new char[capacity];
            uint length;
            fixed (char* pointer = buffer)
            {
                length = GetFinalPathNameByHandleW(
                    handle,
                    pointer,
                    checked((uint)buffer.Length),
                    FileNameNormalized | VolumeNameDos);
            }

            if (length == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                throw new InstalledFontCatalogException(
                    InstalledFontCatalogFailure.CatalogUnavailable,
                    "An installed-font handle's final path could not be verified.",
                    new Win32Exception(error));
            }

            if (length < buffer.Length)
                return NormalizePath(new string(buffer, 0, checked((int)length)));
            if (length > FileNameBufferMaximumChars)
            {
                throw new InstalledFontCatalogException(
                    InstalledFontCatalogFailure.UnsafePath,
                    "An installed-font path exceeded the Windows path bound.");
            }

            capacity = checked((int)length + 1);
        }
    }

    private static bool IsSupportedFontExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc" or ".otc";

    private static bool IsLocalDrivePath(string path) =>
        path.Length >= 3 &&
        ((path[0] is >= 'A' and <= 'Z') || (path[0] is >= 'a' and <= 'z')) &&
        path[1] == ':' &&
        Path.IsPathFullyQualified(path) &&
        (path[2] == Path.DirectorySeparatorChar || path[2] == Path.AltDirectorySeparatorChar) &&
        !path.StartsWith("\\\\", StringComparison.Ordinal);

    private static string NormalizePath(string path)
    {
        if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            return "\\\\" + path[8..];
        if (path.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase))
            return path[4..];
        return path;
    }

    private static string TrimTrailingSeparator(string path) => Path.TrimEndingDirectorySeparator(path);

    private sealed class FontSectionLease(
        SafeFileMappingHandle section,
        WorkerMappingLease remoteHandle,
        ulong byteLength)
    {
        internal SafeFileMappingHandle Section { get; } = section;
        internal WorkerMappingLease RemoteHandle { get; } = remoteHandle;
        internal ulong ByteLength { get; } = byteLength;
    }

    private readonly struct FileIdentity : IEquatable<FileIdentity>
    {
        private readonly uint _volumeSerial;
        private readonly ulong _fileIndex;

        internal FileIdentity(uint volumeSerial, ulong fileIndex)
        {
            _volumeSerial = volumeSerial;
            _fileIndex = fileIndex;
        }

        public bool Equals(FileIdentity other) =>
            _volumeSerial == other._volumeSerial && _fileIndex == other._fileIndex;

        public override bool Equals(object? obj) => obj is FileIdentity other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(_volumeSerial, _fileIndex);
    }

    private sealed class SafeFileMappingHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeFileMappingHandle(nint value) : base(ownsHandle: true) => SetHandle(value);

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileMappingW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFileMappingW(
        SafeFileHandle file,
        ref SecurityAttributes attributes,
        uint protect,
        uint maximumSizeHigh,
        uint maximumSizeLow,
        string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        char* path,
        uint pathChars,
        uint flags);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}

internal enum InstalledFontCatalogFailure
{
    CatalogUnavailable,
    EmptyCatalog,
    InvalidGeneration,
    MappingFailed,
    ResourceLimit,
    UnsafePath,
}

internal sealed class InstalledFontCatalogException : Exception
{
    internal InstalledFontCatalogException(
        InstalledFontCatalogFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    internal InstalledFontCatalogFailure Failure { get; }
}
