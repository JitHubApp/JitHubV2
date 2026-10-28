using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>Host-owned, non-inheritable data-only mapping handle in one worker.</summary>
internal sealed partial class WorkerMappingLease : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeProcessHandle? _process;

    private WorkerMappingLease() : base(ownsHandle: true) { }

    internal ulong ProtocolHandle => checked((ulong)(nuint)handle);

    internal static WorkerMappingLease Create(
        SafeHandle mapping,
        SafeProcessHandle process,
        bool readOnly = false)
    {
        var lease = new WorkerMappingLease();
        bool added = false;
        try
        {
            // Disposal may terminate the worker while an exchange is active.
            // Pin its native handle until our remote handle has been reclaimed.
            process.DangerousAddRef(ref added);
            if (!DuplicateHandle(new nint(-1), mapping, process.DangerousGetHandle(),
                    out nint remote, readOnly ? SectionMapRead : SectionMapRead | SectionMapWrite,
                    inherit: false, options: 0))
            {
                throw new WorkerProtocolException("The SVG shared-memory handle could not be transferred.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }
            lease._process = process;
            lease.SetHandle(remote);
            added = false;
            return lease;
        }
        finally
        {
            if (added)
                process.DangerousRelease();
        }
    }

    private const uint SectionMapRead = 0x00000004;
    private const uint SectionMapWrite = 0x00000002;

    protected override bool ReleaseHandle()
    {
        try
        {
            // The worker borrows this handle; it only unmaps its own view.
            // DUPLICATE_CLOSE_SOURCE with a null target closes the remote
            // handle without introducing a second host handle. A terminated
            // worker has already closed all its handles.
            _ = CloseRemoteHandle(_process!.DangerousGetHandle(), handle, 0,
                out _, 0, false, 1);
            return true;
        }
        finally
        {
            _process!.DangerousRelease();
            _process = null;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(nint sourceProcess, SafeHandle source,
        nint targetProcess, out nint target, uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [LibraryImport("kernel32.dll", EntryPoint = "DuplicateHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseRemoteHandle(nint sourceProcess, nint source,
        nint targetProcess, out nint target, uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
}
