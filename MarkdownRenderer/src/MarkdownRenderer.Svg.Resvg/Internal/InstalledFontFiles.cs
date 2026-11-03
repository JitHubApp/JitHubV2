using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static MarkdownRenderer.Svg.Resvg.Internal.InstalledFontNativeMethods;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Win32 access to installed-font files for the catalog transfer: opening
/// approved directories and font files without following reparse points,
/// read-only sections under the worker descriptor, handle identity and final
/// path verification, and the path forms the approval checks compare.
/// </summary>
internal static class InstalledFontFiles
{
    private const int FileNameBufferInitialChars = 512;
    private const int FileNameBufferMaximumChars = 32768;

    internal static SafeFileHandle OpenDirectory(string path, bool optional)
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

    internal static SafeFileHandle OpenFontFile(string path)
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

    internal static SafeFileMappingHandle CreateReadOnlySection(
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

    internal static ByHandleFileInformation GetFileInformation(SafeFileHandle file, string path)
    {
        if (GetFileInformationByHandle(file, out ByHandleFileInformation information))
            return information;

        int error = Marshal.GetLastPInvokeError();
        throw new InstalledFontCatalogException(
            InstalledFontCatalogFailure.CatalogUnavailable,
            "An installed-font handle could not be inspected safely.",
            new Win32Exception(error, path));
    }

    internal static long GetFileLength(ByHandleFileInformation information)
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

    internal static unsafe string GetFinalPath(SafeFileHandle handle)
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

    internal static bool IsSupportedFontExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc" or ".otc";

    internal static bool IsLocalDrivePath(string path) =>
        path.Length >= 3 &&
        ((path[0] is >= 'A' and <= 'Z') || (path[0] is >= 'a' and <= 'z')) &&
        path[1] == ':' &&
        Path.IsPathFullyQualified(path) &&
        (path[2] == Path.DirectorySeparatorChar || path[2] == Path.AltDirectorySeparatorChar) &&
        !path.StartsWith("\\\\", StringComparison.Ordinal);

    internal static string NormalizePath(string path)
    {
        if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            return "\\\\" + path[8..];
        if (path.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase))
            return path[4..];
        return path;
    }

    internal static string TrimTrailingSeparator(string path) => Path.TrimEndingDirectorySeparator(path);
}
