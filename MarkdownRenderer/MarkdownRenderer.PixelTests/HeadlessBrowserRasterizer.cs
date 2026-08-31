using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.PixelTests;

/// <summary>
/// Wraps a headless Chromium-family browser (Edge or Chrome) as the
/// industry-standard SVG ground-truth renderer. The test process launches
/// the browser with <c>--headless --screenshot</c> against a tiny HTML
/// shim that hosts the SVG at exact pixel dimensions, captures the PNG,
/// and crops it to the requested rectangle.
///
/// If no Chromium binary is found, <see cref="TryFindBrowser"/> returns
/// null and the dependent xUnit theories self-skip via <c>Skip.IfNot</c>.
/// </summary>
public static class HeadlessBrowserRasterizer
{
    // Edge reserves roughly 102 logical pixels for browser-frame metrics when
    // a private profile is supplied, even in headless mode. The screenshot is
    // cropped to the requested SVG bounds by the caller, so a conservative
    // vertical allowance preserves the full content viewport without changing
    // the comparison dimensions.
    private const int BrowserFrameHeightAllowancePx = 128;

    private static readonly string[] Candidates =
    {
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    };

    /// <summary>Returns the path to a Chromium binary, or null if none found.</summary>
    public static string? TryFindBrowser()
    {
        foreach (var p in Candidates)
            if (File.Exists(p)) return p;
        return null;
    }

    // Keep browser launches serialized because Chromium still creates helper
    // processes for a headless screenshot. Every launch also receives its own
    // temporary profile below, so Edge cannot forward the command to the user's
    // running browser or contend for the default profile lock.
    private static readonly object BrowserLock = new();

    /// <summary>
    /// Renders <paramref name="svgBytes"/> with a Chromium headless browser
    /// at <paramref name="widthPx"/> × <paramref name="heightPx"/> pixels and
    /// returns the path to the captured PNG. Caller is responsible for
    /// deleting the returned file when finished.
    /// </summary>
    /// <param name="extraArgs">Browser CLI extras (e.g. <c>--force-color-profile=srgb</c>).</param>
    /// <returns>Path to the PNG file, or null if the browser invocation failed.</returns>
    public static string? Rasterize(byte[] svgBytes, int widthPx, int heightPx, string[]? extraArgs = null)
    {
        var browser = TryFindBrowser();
        if (browser is null) return null;

        var dir = Path.Combine(Path.GetTempPath(), "mdr-pixel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string svgPath = Path.Combine(dir, "fixture.svg");
        string pngPath = Path.Combine(dir, "out.png");
        string profilePath = Path.Combine(dir, "browser-profile");

        // Chromium will render the SVG directly when given a file:// URL to
        // a .svg file — no HTML wrapper, no <img>, no data URI. This avoids
        // both (a) data-URI security restrictions that strip url(#...)
        // references from <defs> and (b) HTML5 parsing differences that
        // miscompute the SVG's intrinsic size. The browser sizes the SVG to
        // the window using its native width/height attributes, so we set
        // --window-size to the requested pixel box.
        File.WriteAllBytes(svgPath, svgBytes);

        var args = new System.Collections.Generic.List<string>
        {
            "--headless=new",
            "--disable-gpu",
            "--no-sandbox",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-search-engine-choice-screen",
            "--disable-background-networking",
            "--disable-component-update",
            "--disable-extensions",
            "--disable-sync",
            "--metrics-recording-only",
            "--disable-features=msEdgeFirstRunExperience,EdgeFirstRunExperience",
            "--hide-scrollbars",
            "--default-background-color=00000000",
            "--virtual-time-budget=5000",
            $"--user-data-dir=\"{profilePath}\"",
            $"--screenshot=\"{pngPath}\"",
            $"--window-size={widthPx},{heightPx + BrowserFrameHeightAllowancePx}",
            "--force-device-scale-factor=1",
        };
        if (extraArgs is not null) args.AddRange(extraArgs);
        args.Add("\"file:///" + svgPath.Replace('\\', '/') + "\"");

        var psi = new ProcessStartInfo
        {
            FileName = browser,
            Arguments = string.Join(' ', args),
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = dir,
        };

        Process? proc = null;
        BrowserProcessJob? processJob = null;
        bool success = false;
        lock (BrowserLock)
        try
        {
            processJob = BrowserProcessJob.StartSuspended(psi, out proc);
            // Edge/Chrome stub launchers commonly exit immediately while the
            // real browser process continues to work in the background, so
            // WaitForExit on the launcher PID is unreliable. Wait for the PNG
            // file to materialize on disk instead, with a hard wall-clock cap.
            // Wait for the PNG to materialize *and* stabilize. Chromium writes
            // the screenshot file in stages — the first few bytes can appear
            // before the SVG's <defs>/url(#...) references resolve, so reading
            // too early yields a near-blank capture (radial gradients, filters,
            // patterns all hit this). We poll until the file size is the same
            // for several consecutive reads, which means the writer is done.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            long lastSize = -1;
            int stableCount = 0;
            const int requiredStable = 5; // ~500ms of no change
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(pngPath))
                {
                    long sz = new FileInfo(pngPath).Length;
                    if (sz > 0 && sz == lastSize) { stableCount++; if (stableCount >= requiredStable) break; }
                    else { stableCount = 0; lastSize = sz; }
                }
                System.Threading.Thread.Sleep(100);
            }
            if (!File.Exists(pngPath) || new FileInfo(pngPath).Length == 0) return null;
            // Extra grace so any final flush completes.
            System.Threading.Thread.Sleep(250);
            success = true;
            return pngPath;
        }
        finally
        {
            // Closing the job kills the browser launcher and every descendant,
            // including Edge's detached helper process.
            try { processJob?.Dispose(); } catch { }
            if (proc is not null)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                try { proc.Dispose(); } catch { }
            }
            if (!success)
            {
                // Best-effort cleanup of the temp profile + shim files when
                // we didn't produce a PNG (caller handles dir on success).
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }

}

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
