using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Creates the resvg worker's sandbox: the kill-on-close job, the restricted
/// low-integrity token with its default DACL, the process mitigation policy,
/// the sanitized environment, and the suspended launch that is resumed only
/// after job assignment.
/// </summary>
internal static class WorkerProcessSandbox
{
    internal static WorkerJob CreateConstrainedJob(long commitBytes, int activeProcessLimit)
    {
        nint rawJob = WorkerNativeMethods.CreateJobObjectW(0, null);
        if (rawJob == 0 || rawJob == -1)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "A resvg worker job object could not be created.");
        var job = new SafeJobHandle(rawJob);
        var information = new WorkerNativeMethods.JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new WorkerNativeMethods.JobObjectBasicLimitInformation
            {
                LimitFlags = WorkerNativeMethods.JobObjectLimitFlags.ActiveProcess |
                             WorkerNativeMethods.JobObjectLimitFlags.DieOnUnhandledException |
                             WorkerNativeMethods.JobObjectLimitFlags.JobMemory |
                             WorkerNativeMethods.JobObjectLimitFlags.KillOnJobClose,
                ActiveProcessLimit = checked((uint)activeProcessLimit),
            },
            JobMemoryLimit = checked((nuint)commitBytes),
        };
        if (!WorkerNativeMethods.SetInformationJobObject(
                job,
                WorkerNativeMethods.JobObjectInformationClass.ExtendedLimitInformation,
                ref information,
                (uint)Marshal.SizeOf<WorkerNativeMethods.JobObjectExtendedLimitInformation>()))
        {
            int error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error, "The resvg worker job limits could not be applied.");
        }
        return new WorkerJob(job);
    }

    internal static unsafe SafeProcessHandle StartRestrictedSuspended(
        string executablePath,
        string pipeName,
        string nonce,
        string workingDirectory,
        WorkerJob job,
        SafeTokenHandle sourceToken,
        WorkerObjectSecurity.WorkerObjectSecurityContext objectSecurity)
    {
        byte[] restrictedCodeSid = GetSidBytes("S-1-5-12");
        fixed (byte* restrictedCodeSidPointer = restrictedCodeSid)
        {
            var restrictingSid = new WorkerNativeMethods.SidAndAttributes
            {
                Sid = (nint)restrictedCodeSidPointer,
                Attributes = 0,
            };
            if (!WorkerNativeMethods.CreateRestrictedToken(
                    sourceToken,
                    WorkerNativeMethods.DisableMaxPrivilege,
                    0,
                    0,
                    0,
                    0,
                    1,
                    (nint)(&restrictingSid),
                    out SafeTokenHandle restrictedToken))
            {
                int error = Marshal.GetLastPInvokeError();
                throw new Win32Exception(error, $"A restricted SVG worker token could not be created (Win32 error {error}).");
            }

            using (restrictedToken)
            {
                SetLowIntegrity(restrictedToken);
                SetRestrictedDefaultDacl(restrictedToken, objectSecurity);
                string commandLine = $"\"{executablePath}\" --pipe {pipeName} --nonce {nonce}";
                char[] mutableCommandLine = [.. commandLine, '\0'];
                char[] sanitizedEnvironment = BuildSanitizedEnvironment();
                nuint attributeBytes = 0;
                _ = WorkerNativeMethods.InitializeProcThreadAttributeList(0, 1, 0, ref attributeBytes);
                if (attributeBytes == 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "The SVG worker mitigation list size could not be determined.");
                nint attributeList = Marshal.AllocHGlobal(checked((nint)attributeBytes));
                try
                {
                    if (!WorkerNativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeBytes))
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "The SVG worker mitigation list could not be initialized.");
                    }

                    try
                    {
                        ulong mitigationPolicy = CreateMitigationPolicy();
                        if (!WorkerNativeMethods.UpdateProcThreadAttribute(
                                attributeList,
                                0,
                                WorkerNativeMethods.ProcThreadAttributeMitigationPolicy,
                                (nint)(&mitigationPolicy),
                                checked((nuint)sizeof(ulong)),
                                0,
                                0))
                        {
                            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The SVG worker process mitigations could not be applied.");
                        }

                        var startup = new WorkerNativeMethods.StartupInfoEx
                        {
                            StartupInfo = new WorkerNativeMethods.StartupInfo
                            {
                                Size = checked((uint)Marshal.SizeOf<WorkerNativeMethods.StartupInfoEx>()),
                            },
                            AttributeList = attributeList,
                        };
                        fixed (char* commandLinePointer = mutableCommandLine)
                        fixed (char* environmentPointer = sanitizedEnvironment)
                        {
                            if (!WorkerNativeMethods.CreateProcessAsUserW(
                                    restrictedToken,
                                    executablePath,
                                    commandLinePointer,
                                    0,
                                    0,
                                    inheritHandles: false,
                                    WorkerNativeMethods.CreateNoWindow | WorkerNativeMethods.CreateSuspended | WorkerNativeMethods.CreateUnicodeEnvironment | WorkerNativeMethods.ExtendedStartupInfoPresent,
                                    (nint)environmentPointer,
                                    workingDirectory,
                                    ref startup,
                                    out WorkerNativeMethods.ProcessInformation information))
                            {
                                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The restricted SVG worker could not start.");
                            }

                            var process = new SafeProcessHandle(information.Process, ownsHandle: true);
                            using var thread = new SafeWaitHandle(information.Thread, ownsHandle: true);
                            try
                            {
                                job.Assign(process);
                                if (RuntimeInformation.ProcessArchitecture == Architecture.X86)
                                    AttachX86StartupToken(thread, sourceToken, objectSecurity);
                                if (WorkerNativeMethods.ResumeThread(thread) == uint.MaxValue)
                                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "The resvg worker could not leave its suspended launch state.");
                                return process;
                            }
                            catch
                            {
                                _ = WorkerNativeMethods.TerminateProcess(process, WorkerTerminationExitCode);
                                _ = WorkerNativeMethods.WaitForSingleObject(process, WorkerTerminationWaitMilliseconds);
                                process.Dispose();
                                throw;
                            }
                        }
                    }
                    finally
                    {
                        WorkerNativeMethods.DeleteProcThreadAttributeList(attributeList);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(attributeList);
                }
            }
        }
    }

    internal static SafeTokenHandle OpenCurrentPrimaryTokenForWorker()
    {
        if (!WorkerNativeMethods.OpenProcessToken(
                WorkerNativeMethods.GetCurrentProcess(),
                WorkerNativeMethods.TokenAssignPrimary | WorkerNativeMethods.TokenDuplicate | WorkerNativeMethods.TokenQuery | WorkerNativeMethods.TokenAdjustDefault,
                out SafeTokenHandle sourceToken))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "The host process primary token could not be opened for SVG worker isolation.");
        }

        return sourceToken;
    }

    internal static ulong CreateMitigationPolicy()
    {
        ulong policy = WorkerNativeMethods.MitigationDepEnable |
            WorkerNativeMethods.MitigationSehopEnable |
            WorkerNativeMethods.MitigationHeapTerminateAlwaysOn |
            WorkerNativeMethods.MitigationBottomUpAslrAlwaysOn |
            WorkerNativeMethods.MitigationStrictHandleChecksAlwaysOn |
            WorkerNativeMethods.MitigationWin32kDisableAlwaysOn |
            WorkerNativeMethods.MitigationExtensionPointDisableAlwaysOn |
            WorkerNativeMethods.MitigationProhibitDynamicCodeAlwaysOn |
            WorkerNativeMethods.MitigationImageLoadNoRemoteAlwaysOn |
            WorkerNativeMethods.MitigationImageLoadNoLowLabelAlwaysOn |
            WorkerNativeMethods.MitigationImageLoadPreferSystem32AlwaysOn;
        if (Environment.Is64BitProcess)
            policy |= WorkerNativeMethods.MitigationHighEntropyAslrAlwaysOn;
        return policy;
    }

    internal static unsafe void SetLowIntegrity(SafeTokenHandle token)
    {
        byte[] lowIntegritySid = GetSidBytes("S-1-16-4096");
        fixed (byte* sidPointer = lowIntegritySid)
        {
            var label = new WorkerNativeMethods.TokenMandatoryLabel
            {
                Label = new WorkerNativeMethods.SidAndAttributes
                {
                    Sid = (nint)sidPointer,
                    Attributes = WorkerNativeMethods.SeGroupIntegrity,
                },
            };
            uint length = checked((uint)(Marshal.SizeOf<WorkerNativeMethods.TokenMandatoryLabel>() + lowIntegritySid.Length));
            if (!WorkerNativeMethods.SetTokenInformation(
                    token,
                    WorkerNativeMethods.TokenInformationClass.TokenIntegrityLevel,
                    (nint)(&label),
                    length))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The SVG worker token could not be lowered to low integrity.");
            }
        }
    }

    internal static unsafe void SetRestrictedDefaultDacl(
        SafeTokenHandle token,
        WorkerObjectSecurity.WorkerObjectSecurityContext objectSecurity)
    {
        // The inherited user-only DACL fails the token's second (restricting-SID)
        // access check when the loader or runtime opens its own kernel objects.
        // Change only defaults for objects created with this worker token; never
        // grant the worker access to host files, the desktop or existing objects.
        WorkerObjectSecurity.SecurityDescriptorHandle descriptor = objectSecurity.Descriptor;
        bool descriptorReferenceAdded = false;
        try
        {
            descriptor.DangerousAddRef(ref descriptorReferenceAdded);
            if (!WorkerNativeMethods.GetSecurityDescriptorDacl(
                    descriptor.DangerousGetHandle(), out int present, out nint dacl, out _) ||
                present == 0 || dacl == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(),
                    "The SVG worker default object permissions could not be created.");
            }

            var information = new WorkerNativeMethods.TokenDefaultDacl { DefaultDacl = dacl };
            if (!WorkerNativeMethods.SetTokenInformation(token, WorkerNativeMethods.TokenInformationClass.TokenDefaultDacl,
                    (nint)(&information), checked((uint)sizeof(WorkerNativeMethods.TokenDefaultDacl))))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(),
                    "The SVG worker default object permissions could not be applied.");
            }
        }
        finally
        {
            if (descriptorReferenceAdded)
                descriptor.DangerousRelease();
        }
    }

    internal static byte[] GetSidBytes(string value)
    {
        var sid = new SecurityIdentifier(value);
        byte[] bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        return bytes;
    }

    internal static char[] BuildSanitizedEnvironment()
    {
        string windowsDirectory = Path.GetDirectoryName(Environment.SystemDirectory) ?? @"C:\Windows";
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string temporaryDirectory = Path.GetFullPath(Path.GetTempPath());
        string[] entries =
        [
            $"LOCALAPPDATA={localApplicationData}",
            $"SystemRoot={windowsDirectory}",
            $"TEMP={temporaryDirectory}",
            $"TMP={temporaryDirectory}",
            $"USERPROFILE={userProfile}",
            $"WINDIR={windowsDirectory}",
        ];
        Array.Sort(entries, StringComparer.OrdinalIgnoreCase);
        return [.. string.Join('\0', entries), '\0', '\0'];
    }

    internal const uint WorkerTerminationExitCode = 0xE0000001;

    internal const uint WorkerTerminationWaitMilliseconds = 3_000;

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
    internal static unsafe void AttachX86StartupToken(
        SafeWaitHandle suspendedThread,
        SafeTokenHandle sourceToken,
        WorkerObjectSecurity.WorkerObjectSecurityContext objectSecurity)
    {
        byte[] restrictedCodeSid = GetSidBytes("S-1-5-12");
        byte[] usersSid = GetSidBytes("S-1-5-32-545");
        fixed (byte* restrictedCode = restrictedCodeSid)
        fixed (byte* users = usersSid)
        {
            WorkerNativeMethods.SidAndAttributes* restrictingSids = stackalloc WorkerNativeMethods.SidAndAttributes[2];
            restrictingSids[0] = new WorkerNativeMethods.SidAndAttributes { Sid = (nint)restrictedCode };
            restrictingSids[1] = new WorkerNativeMethods.SidAndAttributes { Sid = (nint)users };
            if (!WorkerNativeMethods.CreateRestrictedToken(sourceToken, WorkerNativeMethods.DisableMaxPrivilege,
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
                    var attributes = new WorkerNativeMethods.StartupTokenSecurityAttributes
                    {
                        Length = checked((uint)sizeof(WorkerNativeMethods.StartupTokenSecurityAttributes)),
                        SecurityDescriptor = descriptor.DangerousGetHandle(),
                        InheritHandle = 0,
                    };
                    // No TOKEN_DUPLICATE/WRITE_DAC/WRITE_OWNER is retained even
                    // by the broker. Only SetThreadToken needs this handle.
                    if (!WorkerNativeMethods.DuplicateTokenEx(primary, WorkerNativeMethods.TokenQuery | WorkerNativeMethods.TokenImpersonate,
                            ref attributes, WorkerNativeMethods.SecurityImpersonation, WorkerNativeMethods.ImpersonationToken,
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
                            if (!WorkerNativeMethods.SetThreadToken(ref thread, impersonation))
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
}
