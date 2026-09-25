using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using MarkdownRenderer.Images;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal sealed partial class WindowsWorkerProcess : IAsyncDisposable, IDisposable
{
    private static long s_nextInstanceId;
    private readonly SafeProcessHandle _process;
    private readonly NamedPipeServerStream _pipe;
    private readonly SemaphoreSlim _transactions = new(1, 1);
    private readonly int _queueCapacity;
    private int _queued;
    private int _fontCatalogReady;
    private int _disposed;

    private WindowsWorkerProcess(
        SafeProcessHandle process,
        NamedPipeServerStream pipe,
        ulong nonceLow,
        ulong nonceHigh,
        int queueCapacity)
    {
        _process = process;
        _pipe = pipe;
        NonceLow = nonceLow;
        NonceHigh = nonceHigh;
        _queueCapacity = queueCapacity;
        InstanceId = Interlocked.Increment(ref s_nextInstanceId);
    }

    public ulong NonceLow { get; }
    public ulong NonceHigh { get; }
    public long InstanceId { get; }
    public bool IsFontCatalogReady => Volatile.Read(ref _fontCatalogReady) != 0;
    public bool HasExited
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
                return true;
            try
            {
                return WaitForSingleObject(_process, 0) != WaitTimeout;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }
    }

    public static async Task<WindowsWorkerProcess> StartAsync(
        string executablePath,
        ResvgMarkdownSvgRendererOptions options,
        WorkerJob job,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The resvg worker is packaged for Windows only.");
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("The architecture-specific resvg worker is missing.", executablePath);

        byte[] nonceBytes = RandomNumberGenerator.GetBytes(16);
        ulong nonceLow = BitConverter.ToUInt64(nonceBytes, 0);
        ulong nonceHigh = BitConverter.ToUInt64(nonceBytes, 8);
        string nonce = $"{nonceHigh:x16}{nonceLow:x16}";
        string pipeName = $"MarkdownRenderer.Resvg.{Environment.ProcessId}.{nonce}";
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.SetSecurityDescriptorSddlForm(
            WorkerObjectSecurity.CreateRestrictedDaclSddl(),
            AccessControlSections.Access);
        var pipe = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            WorkerProtocol.ResponseSize,
            WorkerProtocol.RequestSize,
            pipeSecurity,
            HandleInheritability.None,
            (PipeAccessRights)0);
        WorkerObjectSecurity.SetLowIntegrityLabel(pipe.SafePipeHandle);

        SafeProcessHandle? process = null;
        try
        {
            process = StartRestrictedSuspended(
                executablePath,
                pipeName,
                nonce,
                Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
                job);

            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(WorkerSchedulingPolicy.InitializationDeadline);
            try
            {
                await pipe.WaitForConnectionAsync(startup.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                WorkerTimeoutEvents.Log.Timeout(
                    stage: 0,
                    deadlineMilliseconds: (int)WorkerSchedulingPolicy.InitializationDeadline.TotalMilliseconds,
                    workerProcessCpuMilliseconds: -1);
                throw new WorkerInitializationDeadlineException(
                    "The resvg worker did not complete startup before its initialization deadline.",
                    exception);
            }

            var worker = new WindowsWorkerProcess(
                process,
                pipe,
                nonceLow,
                nonceHigh,
                options.QueueCapacity);
            process = null;
            pipe = null!;
            return worker;
        }
        finally
        {
            if (process is not null)
            {
                _ = TerminateProcess(process, WorkerTerminationExitCode);
                _ = WaitForSingleObject(process, WorkerTerminationWaitMilliseconds);
                process.Dispose();
            }
            pipe?.Dispose();
        }
    }

    public void MarkFontCatalogReady() => Volatile.Write(ref _fontCatalogReady, 1);

    public async Task<WorkerResponse> ExchangeAsync(
        WorkerRequest request,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        int queued = Interlocked.Increment(ref _queued);
        if (queued > _queueCapacity)
        {
            Interlocked.Decrement(ref _queued);
            throw new MarkdownSvgException(
                MarkdownRenderer.Images.MarkdownSvgFailureReason.ResourceLimitExceeded,
                "The SVG worker queue is full.");
        }

        bool entered = false;
        try
        {
            await _transactions.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (HasExited)
                throw new WorkerProtocolException("The resvg worker exited before the request.");

            // Audit-only evidence must not add a process query to every normal
            // SVG request. The listener is installed before audit rendering.
            long workerCpuAtStart = WorkerTimeoutEvents.Log.IsEnabled()
                ? GetProcessCpuTicks()
                : -1;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            operation.CancelAfter(deadline);
            byte[] requestFrame = WorkerProtocol.Encode(request);
            byte[] responseFrame = new byte[WorkerProtocol.ResponseSize];
            try
            {
                await _pipe.WriteAsync(requestFrame, operation.Token).ConfigureAwait(false);
                await _pipe.FlushAsync(operation.Token).ConfigureAwait(false);
                await _pipe.ReadExactlyAsync(responseFrame, operation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                long workerCpuAtTimeout = workerCpuAtStart >= 0
                    ? GetProcessCpuTicks()
                    : -1;
                int workerProcessCpuMilliseconds = workerCpuAtStart >= 0 &&
                    workerCpuAtTimeout >= workerCpuAtStart
                        ? (int)Math.Min((workerCpuAtTimeout - workerCpuAtStart) / 10_000, int.MaxValue)
                        : -1;
                WorkerTimeoutEvents.Log.Timeout(
                    stage: (int)request.Operation,
                    deadlineMilliseconds: (int)deadline.TotalMilliseconds,
                    workerProcessCpuMilliseconds);
                DisposeForRestart();
                throw new WorkerDeadlineException("The resvg worker exceeded its request deadline.", exception);
            }
            catch (OperationCanceledException)
            {
                DisposeForRestart();
                throw;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                DisposeForRestart();
                throw new WorkerProtocolException("The resvg worker transport failed.", exception);
            }

            return WorkerProtocol.DecodeResponse(
                responseFrame,
                request.RequestId,
                NonceLow,
                NonceHigh);
        }
        finally
        {
            if (entered)
                _transactions.Release();
            Interlocked.Decrement(ref _queued);
        }
    }

    public void Dispose() => DisposeCore(waitForExit: false);

    internal long GetProcessCpuTicks()
    {
        try
        {
            if (!GetProcessTimes(_process, out _, out _, out FileTime kernel, out FileTime user))
                return -1;

            ulong kernelTicks = ((ulong)kernel.High << 32) | kernel.Low;
            ulong userTicks = ((ulong)user.High << 32) | user.Low;
            return userTicks <= long.MaxValue &&
                   kernelTicks <= (ulong)long.MaxValue - userTicks
                ? (long)(kernelTicks + userTicks)
                : -1;
        }
        catch (ObjectDisposedException)
        {
            return -1;
        }
    }

    internal void DisposeForRestart() => DisposeCore(waitForExit: true);

    private void DisposeCore(bool waitForExit)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _pipe.Dispose();
        _ = TerminateProcess(_process, WorkerTerminationExitCode);
        if (waitForExit)
        {
            // TerminateProcess is asynchronous. The job counts a dying process
            // until it is signaled; immediately launching its replacement can
            // exceed the one/two-process job limit and fail unrelated queued
            // images in a cascade after one content timeout.
            _ = WaitForSingleObject(_process, WorkerTerminationWaitMilliseconds);
        }
        _process.Dispose();
        // An active ExchangeAsync releases this gate in its finally block.
        // SemaphoreSlim owns no native handle, so retaining it avoids masking
        // the typed cancellation/transport failure during concurrent disposal.
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal static WorkerJob CreateConstrainedJob(long commitBytes, int activeProcessLimit)
    {
        nint rawJob = CreateJobObjectW(0, null);
        if (rawJob == 0 || rawJob == -1)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "A resvg worker job object could not be created.");
        var job = new SafeJobHandle(rawJob);
        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitFlags.ActiveProcess |
                             JobObjectLimitFlags.DieOnUnhandledException |
                             JobObjectLimitFlags.JobMemory |
                             JobObjectLimitFlags.KillOnJobClose,
                ActiveProcessLimit = checked((uint)activeProcessLimit),
            },
            JobMemoryLimit = checked((nuint)commitBytes),
        };
        if (!SetInformationJobObject(
                job,
                JobObjectInformationClass.ExtendedLimitInformation,
                ref information,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            int error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error, "The resvg worker job limits could not be applied.");
        }
        return new WorkerJob(job);
    }

    private static unsafe SafeProcessHandle StartRestrictedSuspended(
        string executablePath,
        string pipeName,
        string nonce,
        string workingDirectory,
        WorkerJob job)
    {
        if (!OpenProcessToken(
                GetCurrentProcess(),
                TokenAssignPrimary | TokenDuplicate | TokenQuery | TokenAdjustDefault,
                out SafeTokenHandle sourceToken))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The host process token could not be opened for SVG worker isolation.");
        }

        using (sourceToken)
        {
            byte[] restrictedCodeSid = GetSidBytes("S-1-5-12");
            fixed (byte* restrictedCodeSidPointer = restrictedCodeSid)
            {
                var restrictingSid = new SidAndAttributes
                {
                    Sid = (nint)restrictedCodeSidPointer,
                    Attributes = 0,
                };
                if (!CreateRestrictedToken(
                        sourceToken,
                        DisableMaxPrivilege,
                        1,
                        (nint)(&restrictingSid),
                        0,
                        0,
                        0,
                        0,
                        out SafeTokenHandle restrictedToken))
                {
                    int error = Marshal.GetLastPInvokeError();
                    throw new Win32Exception(error, $"A restricted SVG worker token could not be created (Win32 error {error}).");
                }

                using (restrictedToken)
                {
                    SetLowIntegrity(restrictedToken);
                    string commandLine = $"\"{executablePath}\" --pipe {pipeName} --nonce {nonce}";
                    char[] mutableCommandLine = [.. commandLine, '\0'];
                    char[] sanitizedEnvironment = BuildSanitizedEnvironment();
                    nuint attributeBytes = 0;
                    _ = InitializeProcThreadAttributeList(0, 1, 0, ref attributeBytes);
                    if (attributeBytes == 0)
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "The SVG worker mitigation list size could not be determined.");
                    nint attributeList = Marshal.AllocHGlobal(checked((nint)attributeBytes));
                    try
                    {
                        if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeBytes))
                        {
                            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The SVG worker mitigation list could not be initialized.");
                        }

                        try
                        {
                            ulong mitigationPolicy = CreateMitigationPolicy();
                            if (!UpdateProcThreadAttribute(
                                    attributeList,
                                    0,
                                    ProcThreadAttributeMitigationPolicy,
                                    (nint)(&mitigationPolicy),
                                    checked((nuint)sizeof(ulong)),
                                    0,
                                    0))
                            {
                                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The SVG worker process mitigations could not be applied.");
                            }

                            var startup = new StartupInfoEx
                            {
                                StartupInfo = new StartupInfo
                                {
                                    Size = checked((uint)Marshal.SizeOf<StartupInfoEx>()),
                                },
                                AttributeList = attributeList,
                            };
                            fixed (char* commandLinePointer = mutableCommandLine)
                            fixed (char* environmentPointer = sanitizedEnvironment)
                            {
                                if (!CreateProcessAsUserW(
                                        restrictedToken,
                                        executablePath,
                                        commandLinePointer,
                                        0,
                                        0,
                                        inheritHandles: false,
                                        CreateNoWindow | CreateSuspended | CreateUnicodeEnvironment | ExtendedStartupInfoPresent,
                                        (nint)environmentPointer,
                                        workingDirectory,
                                        ref startup,
                                        out ProcessInformation information))
                                {
                                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "The restricted SVG worker could not start.");
                                }

                                var process = new SafeProcessHandle(information.Process, ownsHandle: true);
                                using var thread = new SafeWaitHandle(information.Thread, ownsHandle: true);
                                try
                                {
                                    job.Assign(process);
                                    if (ResumeThread(thread) == uint.MaxValue)
                                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "The resvg worker could not leave its suspended launch state.");
                                    return process;
                                }
                                catch
                                {
                                    _ = TerminateProcess(process, WorkerTerminationExitCode);
                                    _ = WaitForSingleObject(process, WorkerTerminationWaitMilliseconds);
                                    process.Dispose();
                                    throw;
                                }
                            }
                        }
                        finally
                        {
                            DeleteProcThreadAttributeList(attributeList);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(attributeList);
                    }
                }
            }
        }
    }

    private static ulong CreateMitigationPolicy()
    {
        ulong policy = MitigationDepEnable |
            MitigationSehopEnable |
            MitigationHeapTerminateAlwaysOn |
            MitigationBottomUpAslrAlwaysOn |
            MitigationStrictHandleChecksAlwaysOn |
            MitigationWin32kDisableAlwaysOn |
            MitigationExtensionPointDisableAlwaysOn |
            MitigationProhibitDynamicCodeAlwaysOn |
            MitigationImageLoadNoRemoteAlwaysOn |
            MitigationImageLoadNoLowLabelAlwaysOn |
            MitigationImageLoadPreferSystem32AlwaysOn;
        if (Environment.Is64BitProcess)
            policy |= MitigationHighEntropyAslrAlwaysOn;
        return policy;
    }

    private static unsafe void SetLowIntegrity(SafeTokenHandle token)
    {
        byte[] lowIntegritySid = GetSidBytes("S-1-16-4096");
        fixed (byte* sidPointer = lowIntegritySid)
        {
            var label = new TokenMandatoryLabel
            {
                Label = new SidAndAttributes
                {
                    Sid = (nint)sidPointer,
                    Attributes = SeGroupIntegrity,
                },
            };
            uint length = checked((uint)(Marshal.SizeOf<TokenMandatoryLabel>() + lowIntegritySid.Length));
            if (!SetTokenInformation(
                    token,
                    TokenInformationClass.TokenIntegrityLevel,
                    (nint)(&label),
                    length))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The SVG worker token could not be lowered to low integrity.");
            }
        }
    }

    private static byte[] GetSidBytes(string value)
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

    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint DisableMaxPrivilege = 0x00000001;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint SeGroupIntegrity = 0x00000020;
    private const uint WaitTimeout = 0x00000102;
    private const uint WorkerTerminationExitCode = 0xE0000001;
    private const uint WorkerTerminationWaitMilliseconds = 3_000;
    private const nuint ProcThreadAttributeMitigationPolicy = 0x00020007;
    private const ulong MitigationDepEnable = 0x00000001;
    private const ulong MitigationSehopEnable = 0x00000004;
    private const ulong MitigationHeapTerminateAlwaysOn = 0x00001000;
    private const ulong MitigationBottomUpAslrAlwaysOn = 0x00010000;
    private const ulong MitigationHighEntropyAslrAlwaysOn = 0x00100000;
    private const ulong MitigationStrictHandleChecksAlwaysOn = 0x01000000;
    private const ulong MitigationWin32kDisableAlwaysOn = 0x10000000;
    private const ulong MitigationExtensionPointDisableAlwaysOn = 0x0000000100000000;
    private const ulong MitigationProhibitDynamicCodeAlwaysOn = 0x0000001000000000;
    private const ulong MitigationImageLoadNoRemoteAlwaysOn = 0x0010000000000000;
    private const ulong MitigationImageLoadNoLowLabelAlwaysOn = 0x0100000000000000;
    private const ulong MitigationImageLoadPreferSystem32AlwaysOn = 0x1000000000000000;

    [Flags]
    private enum JobObjectLimitFlags : uint
    {
        ActiveProcess = 0x00000008,
        JobMemory = 0x00000200,
        DieOnUnhandledException = 0x00000400,
        KillOnJobClose = 0x00002000,
    }

    private enum JobObjectInformationClass
    {
        ExtendedLimitInformation = 9,
    }

    private enum TokenInformationClass
    {
        TokenIntegrityLevel = 25,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
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
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
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
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    internal sealed class WorkerJob : IDisposable
    {
        private SafeJobHandle? _handle;

        internal WorkerJob(SafeJobHandle handle)
        {
            _handle = handle;
        }

        internal void Assign(SafeProcessHandle process)
        {
            SafeJobHandle handle = _handle ?? throw new ObjectDisposedException(nameof(WorkerJob));
            if (!AssignProcessToJobObject(handle, process))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The resvg worker could not enter its aggregate kill-on-close job.");
        }

        public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();
    }

    internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeJobHandle(nint handle)
            : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    private sealed class SafeTokenHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeTokenHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(
        SafeProcessHandle process,
        out FileTime creation,
        out FileTime exit,
        out FileTime kernel,
        out FileTime user);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(
        nint process,
        uint desiredAccess,
        out SafeTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateRestrictedToken(
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
    private static partial bool SetTokenInformation(
        SafeTokenHandle token,
        TokenInformationClass tokenInformationClass,
        nint tokenInformation,
        uint tokenInformationLength);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessAsUserW(
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
    private static partial bool InitializeProcThreadAttributeList(
        nint attributeList,
        uint attributeCount,
        uint flags,
        ref nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(
        nint attributeList,
        uint flags,
        nuint attribute,
        nint value,
        nuint size,
        nint previousValue,
        nint returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint attributeList);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint jobAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        SafeJobHandle job,
        JobObjectInformationClass informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(SafeWaitHandle thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}

internal class WorkerDeadlineException : Exception
{
    public WorkerDeadlineException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

internal sealed class WorkerInitializationDeadlineException : WorkerDeadlineException
{
    public WorkerInitializationDeadlineException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

internal sealed class WorkerInitializationException : Exception
{
    public WorkerInitializationException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
