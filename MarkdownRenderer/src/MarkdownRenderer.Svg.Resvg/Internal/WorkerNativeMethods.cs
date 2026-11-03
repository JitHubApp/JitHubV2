using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using MarkdownRenderer.Images;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Win32 process, token and job-object interop used to launch and observe the resvg worker.
/// </summary>
internal static partial class WorkerNativeMethods
{
    internal const uint TokenAssignPrimary = 0x0001;

    internal const uint TokenDuplicate = 0x0002;

    internal const uint TokenQuery = 0x0008;

    internal const uint TokenAdjustDefault = 0x0080;

    internal const uint DisableMaxPrivilege = 0x00000001;

    internal const uint CreateSuspended = 0x00000004;

    internal const uint CreateNoWindow = 0x08000000;

    internal const uint CreateUnicodeEnvironment = 0x00000400;

    internal const uint ExtendedStartupInfoPresent = 0x00080000;

    internal const uint SeGroupIntegrity = 0x00000020;

    internal const uint WaitTimeout = 0x00000102;

    internal const nuint ProcThreadAttributeMitigationPolicy = 0x00020007;

    internal const ulong MitigationDepEnable = 0x00000001;

    internal const ulong MitigationSehopEnable = 0x00000004;

    internal const ulong MitigationHeapTerminateAlwaysOn = 0x00001000;

    internal const ulong MitigationBottomUpAslrAlwaysOn = 0x00010000;

    internal const ulong MitigationHighEntropyAslrAlwaysOn = 0x00100000;

    internal const ulong MitigationStrictHandleChecksAlwaysOn = 0x01000000;

    internal const ulong MitigationWin32kDisableAlwaysOn = 0x10000000;

    internal const ulong MitigationExtensionPointDisableAlwaysOn = 0x0000000100000000;

    internal const ulong MitigationProhibitDynamicCodeAlwaysOn = 0x0000001000000000;

    internal const ulong MitigationImageLoadNoRemoteAlwaysOn = 0x0010000000000000;

    internal const ulong MitigationImageLoadNoLowLabelAlwaysOn = 0x0100000000000000;

    internal const ulong MitigationImageLoadPreferSystem32AlwaysOn = 0x1000000000000000;

    [Flags]
    internal enum JobObjectLimitFlags : uint
    {
        ActiveProcess = 0x00000008,
        JobMemory = 0x00000200,
        DieOnUnhandledException = 0x00000400,
        KillOnJobClose = 0x00002000,
    }

    internal enum JobObjectInformationClass
    {
        ExtendedLimitInformation = 9,
    }

    internal enum TokenInformationClass
    {
        TokenDefaultDacl = 6,
        TokenIntegrityLevel = 25,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenDefaultDacl
    {
        public nint DefaultDacl;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public JobObjectLimitFlags LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfo
    {
        public uint Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Length;
        public nint Reserved2;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessMemoryCountersEx
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessTimes(
        SafeProcessHandle process,
        out FileTime creation,
        out FileTime exit,
        out FileTime kernel,
        out FileTime user);

    [LibraryImport("psapi.dll", EntryPoint = "GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessMemoryInfo(
        SafeProcessHandle process,
        ref ProcessMemoryCountersEx counters,
        uint size);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(
        nint process,
        uint desiredAccess,
        out SafeTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateRestrictedToken(
        SafeTokenHandle existingToken,
        uint flags,
        uint disableSidCount,
        nint sidsToDisable,
        uint deletePrivilegeCount,
        nint privilegesToDelete,
        uint restrictedSidCount,
        nint sidsToRestrict,
        out SafeTokenHandle restrictedToken);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSecurityDescriptorDacl(
        nint securityDescriptor, out int daclPresent, out nint dacl, out int daclDefaulted);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetTokenInformation(
        SafeTokenHandle token,
        TokenInformationClass tokenInformationClass,
        nint tokenInformation,
        uint tokenInformationLength);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool CreateProcessAsUserW(
        SafeTokenHandle token,
        string applicationName,
        char* commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InitializeProcThreadAttributeList(
        nint attributeList,
        uint attributeCount,
        uint flags,
        ref nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateProcThreadAttribute(
        nint attributeList,
        uint flags,
        nuint attribute,
        nint value,
        nuint size,
        nint previousValue,
        nint returnSize);

    [LibraryImport("kernel32.dll")]
    internal static partial void DeleteProcThreadAttributeList(nint attributeList);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateJobObjectW(nint jobAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetInformationJobObject(
        SafeJobHandle job,
        JobObjectInformationClass informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint ResumeThread(SafeWaitHandle thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    internal const uint TokenImpersonate = 0x0004;

    internal const int SecurityImpersonation = 2;

    internal const int ImpersonationToken = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupTokenSecurityAttributes
    {
        public uint Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DuplicateTokenEx(
        SafeTokenHandle existingToken,
        uint desiredAccess,
        ref StartupTokenSecurityAttributes attributes,
        int impersonationLevel,
        int tokenType,
        out SafeTokenHandle newToken);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetThreadToken(ref nint thread, SafeTokenHandle token);
}
