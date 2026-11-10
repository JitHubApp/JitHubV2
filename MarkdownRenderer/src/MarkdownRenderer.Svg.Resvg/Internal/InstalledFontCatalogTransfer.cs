using System.Buffers.Binary;
using System.Runtime.InteropServices;
using MarkdownRenderer.Svg.Resvg.Internal.Fonts;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Transport;
using Microsoft.Win32.SafeHandles;
using static MarkdownRenderer.Svg.Resvg.Internal.Fonts.InstalledFontNativeMethods;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Owns a read-only, handle-transferred snapshot of the installed Windows
/// fonts. The worker receives the catalog table separately and borrows each
/// per-font section until it acknowledges that it has opened the snapshot.
/// </summary>
internal sealed class InstalledFontCatalogTransfer : IDisposable
{
    internal const uint CatalogMagic = 0x544E464D; // "MFNT" in little endian.
    internal const uint CatalogVersion = 1;
    internal const int CatalogHeaderBytes = 16;
    internal const int CatalogEntryBytes = 16;
    internal const int MaximumFontFiles = 4096;
    internal const long MaximumFontFileBytes = 128L * 1024 * 1024;
    internal const long MaximumMappedViewBytesX86 = 1_342_177_280L;
    internal const long MaximumMappedViewBytesWide = 4L * 1024 * 1024 * 1024;

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

        string normalizedRoot = InstalledFontFiles.NormalizePath(Path.GetFullPath(InstalledFontFiles.NormalizePath(canonicalRoot)));
        string normalizedEnumerated = InstalledFontFiles.NormalizePath(Path.GetFullPath(enumeratedPath));
        string normalizedFinal = InstalledFontFiles.NormalizePath(Path.GetFullPath(InstalledFontFiles.NormalizePath(finalHandlePath)));
        if (!InstalledFontFiles.IsLocalDrivePath(normalizedRoot) ||
            !InstalledFontFiles.IsLocalDrivePath(normalizedEnumerated) ||
            !InstalledFontFiles.IsLocalDrivePath(normalizedFinal))
        {
            return false;
        }

        string? enumeratedParent = Path.GetDirectoryName(normalizedEnumerated);
        string? finalParent = Path.GetDirectoryName(normalizedFinal);
        string rootWithoutTrailingSeparator = InstalledFontFiles.TrimTrailingSeparator(normalizedRoot);
        return string.Equals(InstalledFontFiles.TrimTrailingSeparator(enumeratedParent ?? string.Empty),
                   rootWithoutTrailingSeparator, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(InstalledFontFiles.TrimTrailingSeparator(finalParent ?? string.Empty),
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
        var enumeration = new InstalledFontEnumeration();

        MapFontRoot(machineFonts, optional: false, worker, transfer, objectSecurity, ref enumeration, cancellationToken);
        visitedRoots.Add(InstalledFontFiles.NormalizePath(machineFonts));
        if (userFonts is not null)
        {
            string normalizedUserFonts = InstalledFontFiles.NormalizePath(userFonts);
            if (visitedRoots.Add(normalizedUserFonts))
                MapFontRoot(userFonts, optional: true, worker, transfer, objectSecurity, ref enumeration, cancellationToken);
        }

        if (transfer._fontSections.Count == 0)
        {
            throw new InstalledFontCatalogException(
                InstalledFontCatalogFailure.EmptyCatalog,
                "No supported installed Windows font files were available to the isolated renderer.");
        }
    }

    /// <summary>
    /// Opens one approved font root, proves it is a plain local directory that
    /// kept its identity, and maps each supported file beneath it. Any failure
    /// other than a catalog failure or cancellation means the root could not be
    /// enumerated completely.
    /// </summary>
    private static void MapFontRoot(
        string rootPath,
        bool optional,
        SafeProcessHandle worker,
        InstalledFontCatalogTransfer transfer,
        WorkerObjectSecurity.WorkerObjectSecurityContext objectSecurity,
        ref InstalledFontEnumeration enumeration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string normalizedRoot = InstalledFontFiles.NormalizePath(Path.GetFullPath(rootPath));
        if (!InstalledFontFiles.IsLocalDrivePath(normalizedRoot))
        {
            throw new InstalledFontCatalogException(
                InstalledFontCatalogFailure.UnsafePath,
                "An installed-font root did not resolve to a local drive path.");
        }

        using SafeFileHandle rootHandle = InstalledFontFiles.OpenDirectory(rootPath, optional);
        if (rootHandle.IsInvalid)
            return;

        string finalRoot = GetApprovedRootFinalPath(rootHandle, rootPath, normalizedRoot);
        try
        {
            IEnumerable<string> files = Directory.EnumerateFiles(rootPath, "*", new EnumerationOptions
            {
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
            });
            foreach (string filePath in files)
            {
                MapFontFile(filePath, finalRoot, worker, transfer, objectSecurity, ref enumeration, cancellationToken);
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

    /// <summary>
    /// Requires an opened font root to be a plain directory whose final path
    /// is the local path that was requested.
    /// </summary>
    /// <returns>The root's normalized final path.</returns>
    private static string GetApprovedRootFinalPath(SafeFileHandle rootHandle, string rootPath, string normalizedRoot)
    {
        ByHandleFileInformation rootInformation = InstalledFontFiles.GetFileInformation(rootHandle, rootPath);
        if ((rootInformation.FileAttributes & FileAttributeDirectory) == 0 ||
            (rootInformation.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new InstalledFontCatalogException(
                InstalledFontCatalogFailure.UnsafePath,
                "An installed-font root is not a plain directory.");
        }

        string finalRoot = InstalledFontFiles.NormalizePath(InstalledFontFiles.GetFinalPath(rootHandle));
        if (!InstalledFontFiles.IsLocalDrivePath(finalRoot) ||
            !string.Equals(InstalledFontFiles.TrimTrailingSeparator(finalRoot), InstalledFontFiles.TrimTrailingSeparator(normalizedRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InstalledFontCatalogException(
                InstalledFontCatalogFailure.UnsafePath,
                "An installed-font root changed identity while it was opened.");
        }

        return finalRoot;
    }

    /// <summary>
    /// Counts one enumerated entry and, for a supported font file that is
    /// exactly the file beneath the approved root and not yet mapped, maps a
    /// read-only section of it into the worker.
    /// </summary>
    private static void MapFontFile(
        string filePath,
        string finalRoot,
        SafeProcessHandle worker,
        InstalledFontCatalogTransfer transfer,
        WorkerObjectSecurity.WorkerObjectSecurityContext objectSecurity,
        ref InstalledFontEnumeration enumeration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        enumeration.CandidateCount = checked(enumeration.CandidateCount + 1);
        ValidateFileCandidateCount(enumeration.CandidateCount);
        if (!InstalledFontFiles.IsSupportedFontExtension(filePath))
            return;
        string enumeratedPath = InstalledFontFiles.NormalizePath(Path.GetFullPath(filePath));
        if (!string.Equals(InstalledFontFiles.TrimTrailingSeparator(Path.GetDirectoryName(enumeratedPath) ?? string.Empty),
                InstalledFontFiles.TrimTrailingSeparator(finalRoot), StringComparison.OrdinalIgnoreCase))
        {
            throw new InstalledFontCatalogException(
                InstalledFontCatalogFailure.UnsafePath,
                "A font enumeration result escaped its approved root.");
        }

        using SafeFileHandle fontFile = InstalledFontFiles.OpenFontFile(filePath);
        ByHandleFileInformation fileInformation = InstalledFontFiles.GetFileInformation(fontFile, filePath);
        if ((fileInformation.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
        {
            throw new InstalledFontCatalogException(
                InstalledFontCatalogFailure.UnsafePath,
                "An enumerated font file is a directory or reparse point.");
        }

        string finalFilePath = InstalledFontFiles.GetFinalPath(fontFile);
        if (!IsExactApprovedFilePath(finalRoot, enumeratedPath, finalFilePath))
        {
            throw new InstalledFontCatalogException(
                InstalledFontCatalogFailure.UnsafePath,
                "An opened font file did not retain its exact approved-root identity.");
        }

        long fileBytes = InstalledFontFiles.GetFileLength(fileInformation);
        ulong fileIndex = ((ulong)fileInformation.FileIndexHigh << 32) | fileInformation.FileIndexLow;
        var identity = new FontFileIdentity(fileInformation.VolumeSerialNumber, fileIndex);
        if (fileIndex != 0 && !enumeration.FileIdentities.Add(identity))
            return;

        long nextAggregate = ReserveMappedView(enumeration.AggregateViewBytes, fileBytes, IntPtr.Size);

        cancellationToken.ThrowIfCancellationRequested();
        SafeFileMappingHandle section = InstalledFontFiles.CreateReadOnlySection(fontFile, filePath, objectSecurity);
        WorkerMappingLease? remote = null;
        try
        {
            remote = WorkerMappingLease.Create(section, worker, readOnly: true);
            transfer._fontSections.Add(new FontSectionLease(section, remote, checked((ulong)fileBytes)));
            enumeration.AggregateViewBytes = nextAggregate;
            section = null!;
            remote = null;
        }
        finally
        {
            remote?.Dispose();
            section?.Dispose();
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

}
