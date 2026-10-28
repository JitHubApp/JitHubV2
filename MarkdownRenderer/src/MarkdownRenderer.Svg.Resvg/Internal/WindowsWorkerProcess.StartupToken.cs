using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal sealed partial class WindowsWorkerProcess
{
    /// <summary>
    /// Allows trusted WOW64 initialization to read the Windows directory while
    /// leaving the process primary token RC-only and low-integrity throughout.
    /// </summary>
    /// <remarks>
    /// The signed/host-selected worker must drop this initial-thread context as
    /// its first action, before arguments, IPC, document data, fonts or threads.
    /// Other threads always start under the restricted process primary token.
    /// There is no in-process or relaxed-primary fallback if startup fails.
    /// </remarks>
    private static unsafe void AttachX86StartupToken(
        SafeWaitHandle suspendedThread,
        SafeTokenHandle sourceToken,
        WorkerObjectSecurity.WorkerObjectSecurityContext objectSecurity)
    {
        byte[] restrictedCodeSid = GetSidBytes("S-1-5-12");
        byte[] usersSid = GetSidBytes("S-1-5-32-545");
        fixed (byte* restrictedCode = restrictedCodeSid)
        fixed (byte* users = usersSid)
        {
            SidAndAttributes* restrictingSids = stackalloc SidAndAttributes[2];
            restrictingSids[0] = new SidAndAttributes { Sid = (nint)restrictedCode };
            restrictingSids[1] = new SidAndAttributes { Sid = (nint)users };
            if (!CreateRestrictedToken(sourceToken, DisableMaxPrivilege,
                    0, 0, 0, 0, 2, (nint)restrictingSids, out SafeTokenHandle primary))
                throw new Win32Exception(Marshal.GetLastPInvokeError(),
                    "The x86 SVG worker startup token could not be created.");

            using (primary)
            {
                SetLowIntegrity(primary);
                SetRestrictedDefaultDacl(primary, objectSecurity);
                using WorkerObjectSecurity.SecurityDescriptorHandle descriptor =
                    WorkerObjectSecurity.CreateStartupTokenDescriptor();
                bool descriptorReference = false;
                try
                {
                    descriptor.DangerousAddRef(ref descriptorReference);
                    var attributes = new StartupTokenSecurityAttributes
                    {
                        Length = checked((uint)sizeof(StartupTokenSecurityAttributes)),
                        SecurityDescriptor = descriptor.DangerousGetHandle(),
                        InheritHandle = 0,
                    };
                    // No TOKEN_DUPLICATE/WRITE_DAC/WRITE_OWNER is retained even
                    // by the broker. Only SetThreadToken needs this handle.
                    if (!DuplicateTokenEx(primary, TokenQuery | TokenImpersonate,
                            ref attributes, SecurityImpersonation, ImpersonationToken,
                            out SafeTokenHandle impersonation))
                        throw new Win32Exception(Marshal.GetLastPInvokeError(),
                            "The x86 SVG worker startup context could not be isolated.");

                    using (impersonation)
                    {
                        bool threadReference = false;
                        try
                        {
                            suspendedThread.DangerousAddRef(ref threadReference);
                            nint thread = suspendedThread.DangerousGetHandle();
                            if (!SetThreadToken(ref thread, impersonation))
                                throw new Win32Exception(Marshal.GetLastPInvokeError(),
                                    "The x86 SVG worker startup context could not be attached.");
                        }
                        finally
                        {
                            if (threadReference)
                                suspendedThread.DangerousRelease();
                        }
                    }
                }
                finally
                {
                    if (descriptorReference)
                        descriptor.DangerousRelease();
                }
            }
        }
        // Every broker bootstrap handle is closed before the caller resumes
        // the suspended initial thread. No handle is inherited by the worker.
    }

    private const uint TokenImpersonate = 0x0004;
    private const int SecurityImpersonation = 2;
    private const int ImpersonationToken = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupTokenSecurityAttributes
    {
        public uint Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateTokenEx(
        SafeTokenHandle existingToken,
        uint desiredAccess,
        ref StartupTokenSecurityAttributes attributes,
        int impersonationLevel,
        int tokenType,
        out SafeTokenHandle newToken);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadToken(ref nint thread, SafeTokenHandle token);
}
