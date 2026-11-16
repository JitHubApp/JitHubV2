using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Tests.TestSupport;

/// <summary>The Win32 handle and mapping calls the worker transport tests use to inspect the child.</summary>
internal static partial class WorkerTransportNative
{
    [Flags]
    internal enum ProcessAccess : uint
    {
        DuplicateHandle = 0x0040,
        QueryLimitedInformation = 0x1000,
        Synchronize = 0x00100000,
        Terminate = 0x0001,
    }

    internal const uint FileMapRead = 0x0004;
    internal const uint FileMapWrite = 0x0002;
    internal const uint DuplicateSameAccess = 0x00000002;
    internal const int ErrorInvalidHandle = 6;
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorNotSupported = 50;

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial SafeProcessHandle OpenProcess(ProcessAccess desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DuplicateHandle(
        SafeProcessHandle sourceProcess,
        nint sourceHandle,
        nint targetProcess,
        out nint targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint MapViewOfFile(nint mapping, uint desiredAccess, uint offsetHigh, uint offsetLow, nuint bytes);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnmapViewOfFile(nint address);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("kernelbase.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CompareObjectHandles(SafeFileHandle first, SafeFileHandle second);
}
