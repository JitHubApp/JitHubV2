using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Pixel.Tests;

/// <summary>
/// Starts the headless browser suspended inside a kill-on-close Windows job
/// object, so disposing the job ends the launcher and every helper process it
/// spawned. Used by <see cref="HeadlessBrowserRasterizer"/>.
/// </summary>
internal sealed class BrowserProcessJob : IDisposable
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectBasicAccountingInformationClass = 1;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private SafeFileHandle? _jobHandle;

    private BrowserProcessJob(SafeFileHandle jobHandle)
    {
        _jobHandle = jobHandle;
    }

    public static BrowserProcessJob StartSuspended(
        ProcessStartInfo startInfo,
        out Process process)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        SafeFileHandle jobHandle = CreateJobObject(IntPtr.Zero, null);
        if (jobHandle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create browser process job.");
        }

        BrowserProcessJob job = new(jobHandle);
        ProcessInformation processInformation = default;
        try
        {
            ConfigureKillOnClose(jobHandle);
            StartupInfo startupInfo = new() { Size = Marshal.SizeOf<StartupInfo>() };
            StringBuilder commandLine = new($"\"{startInfo.FileName}\" {startInfo.Arguments}");
            if (!CreateProcess(
                    startInfo.FileName,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    CreateSuspended | CreateNoWindow,
                    IntPtr.Zero,
                    startInfo.WorkingDirectory,
                    ref startupInfo,
                    out processInformation))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start the browser process.");
            }

            if (!AssignProcessToJobObject(jobHandle, processInformation.ProcessHandle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not assign the browser to its process job.");
            }

            process = Process.GetProcessById(unchecked((int)processInformation.ProcessId));
            if (ResumeThread(processInformation.ThreadHandle) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resume the browser process.");
            }

            return job;
        }
        catch
        {
            if (processInformation.ProcessHandle != IntPtr.Zero)
            {
                _ = TerminateProcess(processInformation.ProcessHandle, 1);
            }

            job.Dispose();
            throw;
        }
        finally
        {
            if (processInformation.ThreadHandle != IntPtr.Zero)
            {
                _ = CloseHandle(processInformation.ThreadHandle);
            }

            if (processInformation.ProcessHandle != IntPtr.Zero)
            {
                _ = CloseHandle(processInformation.ProcessHandle);
            }
        }
    }

    public void Dispose()
    {
        SafeFileHandle? handle = Interlocked.Exchange(ref _jobHandle, null);
        if (handle is null)
        {
            return;
        }

        if (!handle.IsInvalid && !handle.IsClosed)
        {
            _ = TerminateJobObject(handle, 1);
            WaitForOwnedProcessesToExit(handle);
        }

        handle.Dispose();
    }

    private static void WaitForOwnedProcessesToExit(SafeFileHandle jobHandle)
    {
        int size = Marshal.SizeOf<JobObjectBasicAccountingInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            long deadline = Environment.TickCount64 + 5_000;
            while (Environment.TickCount64 < deadline)
            {
                if (!QueryInformationJobObject(
                        jobHandle,
                        JobObjectBasicAccountingInformationClass,
                        buffer,
                        unchecked((uint)size),
                        out _))
                {
                    return;
                }

                JobObjectBasicAccountingInformation information =
                    Marshal.PtrToStructure<JobObjectBasicAccountingInformation>(buffer);
                if (information.ActiveProcesses == 0)
                {
                    return;
                }

                Thread.Sleep(10);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ConfigureKillOnClose(SafeFileHandle jobHandle)
    {
        JobObjectExtendedLimitInformation information = new();
        information.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, buffer, false);
            if (!SetInformationJobObject(
                    jobHandle,
                    JobObjectExtendedLimitInformationClass,
                    buffer,
                    unchecked((uint)size)))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not configure the browser process job.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        IntPtr information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        IntPtr information,
        uint informationLength,
        out uint returnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string workingDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
