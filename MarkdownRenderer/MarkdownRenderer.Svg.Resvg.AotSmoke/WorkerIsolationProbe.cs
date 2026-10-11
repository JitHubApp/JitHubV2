using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static partial class WorkerIsolationProbe
{
    private const uint SnapshotProcesses = 0x00000002;
    private const uint SnapshotThreads = 0x00000004;
    private const uint ProcessQueryInformation = 0x00000400;
    private const uint ProcessTerminate = 0x00000001;
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const uint ProcessSynchronize = 0x00100000;
    private const uint ThreadQueryInformation = 0x00000040;
    private const uint ThreadSynchronize = 0x00100000;
    private const uint TokenQuery = 0x00000008;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x00000102;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorNoToken = 1008;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorInsufficientBuffer = 122;
    private const int ProcessEntry32X86Size = 556;
    private const int ProcessEntry32X64Size = 568;
    private const int ProcessEntry32X86ParentIdOffset = 24;
    private const int ProcessEntry32X64ParentIdOffset = 32;
    private const int ProcessEntry32X86ImageOffset = 36;
    private const int ProcessEntry32X64ImageOffset = 44;
    private const int ThreadEntry32Size = 28;
    private const int MaximumTokenInformationBytes = 1024 * 1024;
    private const int MaximumAceCount = 128;
    private const uint TokenPrimary = 1;
    private const uint SeGroupIntegrity = 0x00000020;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const uint GenericAll = 0x10000000;
    private const byte AccessAllowedAceType = 0;

    private const string RestrictedCodeSid = "S-1-5-12";
    private const string LowIntegritySid = "S-1-16-4096";
    private const string WorldSid = "S-1-1-0";
    private const string AuthenticatedUsersSid = "S-1-5-11";
    private const uint SeChangeNotifyPrivilegeLowPart = 23;

    private enum TokenInformationClass
    {
        User = 1,
        Privileges = 3,
        DefaultDacl = 6,
        Type = 8,
        RestrictedSids = 11,
        HasRestrictions = 21,
        IntegrityLevel = 25,
    }

    private enum ProcessMitigationPolicy
    {
        Dep = 0,
        Aslr = 1,
        DynamicCode = 2,
        StrictHandleCheck = 3,
        SystemCallDisable = 4,
        ExtensionPointDisable = 6,
        ImageLoad = 10,
        SehOp = 18,
    }

    private enum AclInformationClass
    {
        SizeInformation = 2,
    }

    internal static uint VerifyAfterWarmUp(int parentProcessId, string expectedWorkerPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The worker isolation probe requires Windows.");
        if (parentProcessId <= 0)
            throw new ArgumentOutOfRangeException(nameof(parentProcessId));
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedWorkerPath);

        string fullExpectedPath = NormalizePath(expectedWorkerPath);
        using SafeKernelHandle worker = FindExactWorker(parentProcessId, fullExpectedPath) ??
            throw new InvalidOperationException(
                $"No live SVG worker child of process {parentProcessId} was found at '{fullExpectedPath}'.");
        VerifyAlive(worker);
        VerifyJobMembership(worker);
        VerifyMitigations(worker);

        using SafeTokenHandle workerToken = OpenProcessTokenForQuery(worker);
        using SafeKernelHandle parent = OpenProcessForQuery(checked((uint)parentProcessId));
        using SafeTokenHandle parentToken = OpenProcessTokenForQuery(parent);
        string userSid = GetTokenUserSid(parentToken);

        VerifyPrimaryRestrictedToken(workerToken, userSid);
        uint workerPid = GetWorkerProcessId(worker);
        VerifyNoWorkerThreadImpersonation(workerPid);
        VerifyDefaultDacl(workerToken, userSid);

        Console.WriteLine(
            $"Verified live SVG worker isolation: pid={workerPid}, exact image path, primary RC-only restricted SID, low integrity, disabled dangerous privileges, no thread impersonation, job membership, required queryable process mitigations, and user+RC default DACL. Heap-termination-on-corruption was requested at process creation but is not exposed by the documented process-mitigation query API and is not claimed as verified.");
        return workerPid;
    }

    internal static ResourceSnapshot ReadResourceSnapshot(uint workerProcessId)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The worker resource probe requires Windows.");
        if (workerProcessId == 0)
            throw new ArgumentOutOfRangeException(nameof(workerProcessId));

        uint workerHandles;
        ulong workerPrivateBytes;
        using (SafeKernelHandle worker = OpenProcessForQuery(workerProcessId))
        {
            VerifyAlive(worker);
            workerHandles = ReadProcessHandleCount(worker.DangerousGetHandle(), "SVG worker");
            workerPrivateBytes = ReadPrivateBytes(worker.DangerousGetHandle(), "SVG worker");
        }

        nint currentProcess = GetCurrentProcess();
        uint currentProcessHandles = ReadProcessHandleCount(currentProcess, "AOT smoke host");
        ulong currentProcessPrivateBytes = ReadPrivateBytes(currentProcess, "AOT smoke host");
        return new ResourceSnapshot(
            currentProcessHandles,
            currentProcessPrivateBytes,
            workerHandles,
            workerPrivateBytes);
    }

    internal static async ValueTask DisposeAndVerifyExitAsync(uint workerProcessId, Func<ValueTask> disposeAsync)
    {
        // Retain the process identity across disposal; a recycled numeric PID
        // must not be accepted as proof that the original worker was reaped.
        using SafeKernelHandle worker = OpenProcessForQuery(workerProcessId);
        VerifyAlive(worker);
        await disposeAsync().ConfigureAwait(false);
        uint result = WaitForSingleObject(worker, 0);
        if (result == WaitTimeout)
            throw new InvalidOperationException("The SVG worker remained alive after renderer DisposeAsync completed.");
        if (result != WaitObject0)
            throw LastError("The SVG worker shutdown could not be verified.");
        Console.WriteLine("Verified the exact SVG worker exited before DisposeAsync returned.");
    }

    internal static void VerifyNoWorkerChildren(int parentProcessId, string expectedWorkerPath)
    {
        using SafeKernelHandle? worker = FindExactWorker(parentProcessId, NormalizePath(expectedWorkerPath));
        if (worker is not null)
            throw new InvalidOperationException("An SVG worker child remained after the startup/shutdown race completed.");
    }

    internal static async Task<uint> TerminateAndVerifyReplacementAsync(
        int parentProcessId,
        string expectedWorkerPath,
        Func<Task> renderAfterTermination)
    {
        ArgumentNullException.ThrowIfNull(renderAfterTermination);
        string expectedPath = NormalizePath(expectedWorkerPath);
        // Only terminate the exact worker owned by this smoke process. Retain
        // its kernel identity through recovery; a recycled PID is not evidence
        // of either a successful termination or a genuinely different worker.
        using SafeKernelHandle original = FindExactWorker(parentProcessId, expectedPath) ??
            throw new InvalidOperationException("The owned SVG worker was absent before the restart probe.");
        VerifyAlive(original);
        using SafeKernelHandle termination = OpenProcess(
            ProcessTerminate | ProcessSynchronize, false, GetWorkerProcessId(original));
        if (termination.IsInvalid)
            throw LastError("The owned SVG worker could not be opened for fault injection.");
        if (!CompareObjectHandles(original, termination))
            throw new InvalidOperationException("Worker identity changed before fault injection; no process was terminated.");
        if (!TerminateProcess(termination, 0xE0000001))
            throw LastError("The owned SVG worker could not be terminated for the restart probe.");
        uint wait = WaitForSingleObject(original, 5_000);
        if (wait == WaitTimeout)
            throw new TimeoutException("The exact injected SVG worker did not exit within five seconds.");
        if (wait != WaitObject0)
            throw LastError("The injected SVG worker exit could not be verified.");

        await renderAfterTermination().ConfigureAwait(false);
        uint replacementProcessId = VerifyAfterWarmUp(parentProcessId, expectedPath);
        using SafeKernelHandle replacement = OpenProcessForQuery(replacementProcessId);
        VerifyAlive(replacement);
        if (CompareObjectHandles(original, replacement))
            throw new InvalidOperationException("The restart probe did not produce a different SVG worker.");
        return replacementProcessId;
    }

    private static uint ReadProcessHandleCount(nint process, string description)
    {
        if (!GetProcessHandleCount(process, out uint handleCount))
            throw LastError($"The {description} handle count could not be read.");
        return handleCount;
    }

    private static ulong ReadPrivateBytes(nint process, string description)
    {
        var counters = new ProcessMemoryCountersEx
        {
            ByteLength = checked((uint)Marshal.SizeOf<ProcessMemoryCountersEx>()),
        };
        if (!GetProcessMemoryInfo(process, ref counters, counters.ByteLength))
            throw LastError($"The {description} private-byte count could not be read.");
        if (counters.ByteLength != Marshal.SizeOf<ProcessMemoryCountersEx>())
            throw new InvalidOperationException($"The {description} returned an unexpected process-memory-counter size.");
        return checked((ulong)counters.PrivateUsage);
    }

    private static SafeKernelHandle? FindExactWorker(int parentProcessId, string expectedPath)
    {
        using SafeSnapshotHandle snapshot = CreateSnapshot(SnapshotProcesses);
        using var entry = new SafeHGlobalBuffer(IntPtr.Size == 8
            ? ProcessEntry32X64Size
            : ProcessEntry32X86Size);
        int entrySize = checked((int)entry.ByteLength);
        Marshal.WriteInt32(entry.DangerousGetHandle(), entrySize);

        if (!Process32FirstW(snapshot, entry.DangerousGetHandle()))
            throw LastError("The worker process snapshot could not be read.");

        SafeKernelHandle? match = null;
        try
        {
            do
            {
                nint entryPointer = entry.DangerousGetHandle();
                uint processId = unchecked((uint)Marshal.ReadInt32(entryPointer, 8));
                uint parentId = unchecked((uint)Marshal.ReadInt32(
                    entryPointer,
                    IntPtr.Size == 8
                        ? ProcessEntry32X64ParentIdOffset
                        : ProcessEntry32X86ParentIdOffset));
                string imageName = ReadFixedUnicodeString(
                    entryPointer + (IntPtr.Size == 8
                        ? ProcessEntry32X64ImageOffset
                        : ProcessEntry32X86ImageOffset),
                    260);

                if (parentId != checked((uint)parentProcessId) ||
                    !string.Equals(
                        imageName,
                        Path.GetFileName(expectedPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using SafeKernelHandle candidate = OpenProcessForQuery(processId);
                string actualPath = NormalizePath(QueryImagePath(candidate));
                if (!string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"A child with the SVG worker image name was found at an unexpected path: '{actualPath}'. Expected '{expectedPath}'.");
                }

                if (match is not null)
                    throw new InvalidOperationException("More than one matching SVG worker child is running.");
                match = OpenProcessForQuery(processId);
            }
            while (Process32NextW(snapshot, entry.DangerousGetHandle()));

            int error = Marshal.GetLastPInvokeError();
            if (error != ErrorNoMoreFiles)
                throw new Win32Exception(error, "The worker process snapshot ended unexpectedly.");

            return match;
        }
        catch
        {
            match?.Dispose();
            throw;
        }
    }

    private static void VerifyAlive(SafeKernelHandle process)
    {
        uint waitResult = WaitForSingleObject(process, 0);
        if (waitResult == WaitTimeout)
            return;
        if (waitResult == WaitObject0)
            throw new InvalidOperationException("The SVG worker exited before its isolation state could be inspected.");
        throw LastError("The SVG worker liveness could not be verified.");
    }

    private static void VerifyJobMembership(SafeKernelHandle process)
    {
        if (!IsProcessInJob(process, 0, out bool inJob))
            throw LastError("The SVG worker job membership could not be inspected.");
        if (!inJob)
            throw new InvalidOperationException("The SVG worker is not assigned to a Windows job object.");
    }

    private static void VerifyMitigations(SafeKernelHandle process)
    {
        uint dep = ReadMitigationFlags(process, ProcessMitigationPolicy.Dep, sizeof(ulong));
        RequireFlags(dep, 0x00000001, "DEP");

        uint aslr = ReadMitigationFlags(process, ProcessMitigationPolicy.Aslr, sizeof(uint));
        RequireFlags(aslr, 0x00000001, "bottom-up ASLR");
        if (IntPtr.Size == 8)
            RequireFlags(aslr, 0x00000004, "high-entropy ASLR");

        uint dynamicCode = ReadMitigationFlags(process, ProcessMitigationPolicy.DynamicCode, sizeof(uint));
        RequireFlags(dynamicCode, 0x00000001, "dynamic-code prohibition");
        RequireFlagsClear(dynamicCode, 0x00000006, "dynamic-code opt-out/downgrade");

        uint strictHandles = ReadMitigationFlags(process, ProcessMitigationPolicy.StrictHandleCheck, sizeof(uint));
        RequireFlags(strictHandles, 0x00000003, "strict handle checks");

        uint win32k = ReadMitigationFlags(process, ProcessMitigationPolicy.SystemCallDisable, sizeof(uint));
        RequireFlags(win32k, 0x00000001, "Win32k system-call disable");

        uint extensionPoints = ReadMitigationFlags(process, ProcessMitigationPolicy.ExtensionPointDisable, sizeof(uint));
        RequireFlags(extensionPoints, 0x00000001, "extension-point disable");

        uint imageLoad = ReadMitigationFlags(process, ProcessMitigationPolicy.ImageLoad, sizeof(uint));
        RequireFlags(imageLoad, 0x00000007, "remote/low-integrity image-load restrictions and System32 preference");

        // GetProcessMitigationPolicy exposes SEHOP starting with Windows 11 22H2.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            uint sehOp = ReadMitigationFlags(process, ProcessMitigationPolicy.SehOp, sizeof(uint));
            RequireFlags(sehOp, 0x00000001, "SEHOP");
        }
        else
        {
            Console.WriteLine("SEHOP was requested at process creation but this Windows version does not expose its state through GetProcessMitigationPolicy; it is not claimed as verified.");
        }
    }

    private static uint ReadMitigationFlags(
        SafeKernelHandle process,
        ProcessMitigationPolicy policy,
        int bufferBytes)
    {
        using var buffer = new SafeHGlobalBuffer(bufferBytes);
        Marshal.WriteInt32(buffer.DangerousGetHandle(), 0);
        if (!GetProcessMitigationPolicy(
                process,
                policy,
                buffer.DangerousGetHandle(),
                checked((nuint)bufferBytes)))
        {
            throw LastError($"The SVG worker's {policy} mitigation state could not be queried.");
        }
        return unchecked((uint)Marshal.ReadInt32(buffer.DangerousGetHandle()));
    }

    private static void RequireFlags(uint actual, uint expected, string description)
    {
        if ((actual & expected) != expected)
            throw new InvalidOperationException($"The SVG worker is missing required {description} mitigation flags (actual 0x{actual:X8}, expected mask 0x{expected:X8}).");
    }

    private static void RequireFlagsClear(uint actual, uint forbidden, string description)
    {
        if ((actual & forbidden) != 0)
            throw new InvalidOperationException($"The SVG worker unexpectedly permits {description} (actual 0x{actual:X8}, forbidden mask 0x{forbidden:X8}).");
    }

    private static void VerifyPrimaryRestrictedToken(SafeTokenHandle token, string expectedUserSid)
    {
        using (SafeHGlobalBuffer typeInfo = GetTokenInformation(token, TokenInformationClass.Type))
        {
            if (unchecked((uint)Marshal.ReadInt32(typeInfo.DangerousGetHandle())) != TokenPrimary)
                throw new InvalidOperationException("The SVG worker process token is not a primary token.");
        }

        using (SafeHGlobalBuffer restrictedInfo = GetTokenInformation(token, TokenInformationClass.HasRestrictions))
        {
            if (Marshal.ReadInt32(restrictedInfo.DangerousGetHandle()) == 0)
                throw new InvalidOperationException("The SVG worker token does not report any restrictions.");
        }

        using (SafeHGlobalBuffer restrictedSids = GetTokenInformation(token, TokenInformationClass.RestrictedSids))
        {
            nint buffer = restrictedSids.DangerousGetHandle();
            uint count = unchecked((uint)Marshal.ReadInt32(buffer));
            if (count != 1)
                throw new InvalidOperationException($"The SVG worker must have exactly one restricting SID; found {count}.");

            nint groups = buffer + (IntPtr.Size == 8 ? 8 : 4);
            nint sid = Marshal.ReadIntPtr(groups);
            string actualSid = SidToString(sid);
            if (!string.Equals(actualSid, RestrictedCodeSid, StringComparison.Ordinal))
                throw new InvalidOperationException($"The SVG worker restricting SID is '{actualSid}', not Restricted Code.");
        }

        using (SafeHGlobalBuffer integrity = GetTokenInformation(token, TokenInformationClass.IntegrityLevel))
        {
            nint label = Marshal.ReadIntPtr(integrity.DangerousGetHandle());
            uint attributes = unchecked((uint)Marshal.ReadInt32(integrity.DangerousGetHandle(), IntPtr.Size));
            string actualSid = SidToString(label);
            if (!string.Equals(actualSid, LowIntegritySid, StringComparison.Ordinal) ||
                (attributes & SeGroupIntegrity) == 0)
            {
                throw new InvalidOperationException($"The SVG worker integrity label is '{actualSid}' with attributes 0x{attributes:X8}; low integrity is required.");
            }
        }

        string actualUserSid = GetTokenUserSid(token);
        if (!string.Equals(actualUserSid, expectedUserSid, StringComparison.Ordinal))
            throw new InvalidOperationException("The SVG worker token user differs from the launching process user.");

        VerifyDangerousPrivilegesDisabled(token);
    }

    private static void VerifyDangerousPrivilegesDisabled(SafeTokenHandle token)
    {
        using SafeHGlobalBuffer privileges = GetTokenInformation(token, TokenInformationClass.Privileges);
        nint buffer = privileges.DangerousGetHandle();
        uint count = unchecked((uint)Marshal.ReadInt32(buffer));
        if (count > 256)
            throw new InvalidOperationException($"The SVG worker token reports an implausible privilege count ({count}).");

        nint entry = buffer + sizeof(uint);
        for (uint index = 0; index < count; index++)
        {
            int offset = checked((int)index * 12);
            uint lowPart = unchecked((uint)Marshal.ReadInt32(entry, offset));
            uint highPart = unchecked((uint)Marshal.ReadInt32(entry, offset + sizeof(uint)));
            uint attributes = unchecked((uint)Marshal.ReadInt32(entry, offset + 8));
            if ((attributes & SePrivilegeEnabled) != 0 &&
                (lowPart != SeChangeNotifyPrivilegeLowPart || highPart != 0))
            {
                throw new InvalidOperationException(
                    $"The SVG worker token has a dangerous enabled privilege (LUID {highPart:X8}:{lowPart:X8}, attributes 0x{attributes:X8}).");
            }
        }
    }

    private static void VerifyDefaultDacl(SafeTokenHandle token, string userSid)
    {
        using SafeHGlobalBuffer tokenDaclInfo = GetTokenInformation(token, TokenInformationClass.DefaultDacl);
        nint acl = Marshal.ReadIntPtr(tokenDaclInfo.DangerousGetHandle());
        if (acl == 0)
            throw new InvalidOperationException("The SVG worker token has a NULL default DACL.");

        if (!GetAclInformation(
                acl,
                out AclSizeInformation information,
                checked((uint)Marshal.SizeOf<AclSizeInformation>()),
                AclInformationClass.SizeInformation))
        {
            throw LastError("The SVG worker default DACL could not be inspected.");
        }
        if (information.AceCount != 2 || information.AceCount > MaximumAceCount)
            throw new InvalidOperationException($"The SVG worker default DACL must contain exactly the user and Restricted Code ACEs; found {information.AceCount} ACEs.");

        var allowedSids = new HashSet<string>(StringComparer.Ordinal);
        for (uint index = 0; index < information.AceCount; index++)
        {
            if (!GetAce(acl, index, out nint ace))
                throw LastError($"Default DACL ACE {index} could not be read.");

            byte aceType = Marshal.ReadByte(ace);
            byte aceFlags = Marshal.ReadByte(ace, 1);
            uint mask = unchecked((uint)Marshal.ReadInt32(ace, 4));
            if (aceType != AccessAllowedAceType || aceFlags != 0 || mask != GenericAll)
                throw new InvalidOperationException($"Default DACL ACE {index} is not a non-inheritable allow-GENERIC_ALL ACE.");

            string sid = SidToString(ace + 8);
            if (sid is WorldSid or AuthenticatedUsersSid)
                throw new InvalidOperationException($"The worker default DACL must not grant access to broad SID {sid}.");
            if (sid != RestrictedCodeSid && sid != userSid)
                throw new InvalidOperationException($"The worker default DACL contains unexpected SID {sid}.");
            if (!allowedSids.Add(sid))
                throw new InvalidOperationException($"The worker default DACL contains a duplicate ACE for SID {sid}.");
        }

        if (!allowedSids.Contains(RestrictedCodeSid) || !allowedSids.Contains(userSid))
            throw new InvalidOperationException("The worker default DACL does not grant both the launching user and Restricted Code.");
    }

    private static void VerifyNoWorkerThreadImpersonation(uint processId)
    {
        using SafeSnapshotHandle snapshot = CreateSnapshot(SnapshotThreads);
        using var entry = new SafeHGlobalBuffer(ThreadEntry32Size);
        Marshal.WriteInt32(entry.DangerousGetHandle(), ThreadEntry32Size);
        if (!Thread32First(snapshot, entry.DangerousGetHandle()))
            throw LastError("The SVG worker thread snapshot could not be read.");

        do
        {
            nint entryPointer = entry.DangerousGetHandle();
            uint threadId = unchecked((uint)Marshal.ReadInt32(entryPointer, 8));
            uint ownerProcessId = unchecked((uint)Marshal.ReadInt32(entryPointer, 12));
            if (ownerProcessId != processId)
                continue;

            SafeKernelHandle thread = OpenThread(ThreadQueryInformation | ThreadSynchronize, false, threadId);
            if (thread.IsInvalid)
            {
                int openError = Marshal.GetLastPInvokeError();
                thread.Dispose();
                if (openError == ErrorInvalidParameter)
                    continue; // The thread exited after the system snapshot was taken.
                throw new Win32Exception(openError, $"SVG worker thread {threadId} could not be opened for impersonation inspection.");
            }

            using (thread)
            {
                if (OpenThreadToken(thread, TokenQuery, openAsSelf: true, out SafeTokenHandle threadToken))
                {
                    threadToken.Dispose();
                    throw new InvalidOperationException($"SVG worker thread {threadId} has an impersonation token; the primary restricted-token launch must not fall back to thread impersonation.");
                }

                int error = Marshal.GetLastPInvokeError();
                threadToken.Dispose();
                if (error == ErrorNoToken)
                    continue;
                if (WaitForSingleObject(thread, 0) == WaitObject0)
                    continue;
                throw new Win32Exception(error, $"SVG worker thread {threadId} impersonation state could not be verified.");
            }
        }
        while (Thread32Next(snapshot, entry.DangerousGetHandle()));

        int finalError = Marshal.GetLastPInvokeError();
        if (finalError != ErrorNoMoreFiles)
            throw new Win32Exception(finalError, "The worker thread snapshot ended unexpectedly.");
    }

    private static string GetTokenUserSid(SafeTokenHandle token)
    {
        using SafeHGlobalBuffer user = GetTokenInformation(token, TokenInformationClass.User);
        nint sid = Marshal.ReadIntPtr(user.DangerousGetHandle());
        return SidToString(sid);
    }

    private static SafeTokenHandle OpenProcessTokenForQuery(SafeKernelHandle process)
    {
        if (OpenProcessToken(process, TokenQuery, out SafeTokenHandle token))
            return token;
        int error = Marshal.GetLastPInvokeError();
        token.Dispose();
        throw new Win32Exception(error, "The process token could not be opened for read-only isolation inspection.");
    }

    private static SafeKernelHandle OpenProcessForQuery(uint processId)
    {
        SafeKernelHandle process = OpenProcess(
            ProcessQueryInformation | ProcessQueryLimitedInformation | ProcessSynchronize,
            inheritHandle: false,
            processId: processId);
        if (!process.IsInvalid)
            return process;
        process.Dispose();
        throw LastError($"Process {processId} could not be opened for read-only isolation inspection.");
    }

    private static uint GetWorkerProcessId(SafeKernelHandle process)
    {
        uint processId = GetProcessId(process);
        return processId != 0
            ? processId
            : throw LastError("The worker process ID could not be verified.");
    }

    private static string QueryImagePath(SafeKernelHandle process)
    {
        using var buffer = new SafeHGlobalBuffer(32_768 * sizeof(char));
        uint characterCount = 32_768;
        if (!QueryFullProcessImageNameW(process, 0, buffer.DangerousGetHandle(), ref characterCount))
            throw LastError("The SVG worker image path could not be queried.");
        if (characterCount == 0 || characterCount > 32_768)
            throw new InvalidOperationException("The SVG worker image path length is invalid.");
        return Marshal.PtrToStringUni(buffer.DangerousGetHandle(), checked((int)characterCount))
            ?? throw new InvalidOperationException("The SVG worker image path was empty.");
    }

    private static string NormalizePath(string path)
    {
        string normalized = Path.GetFullPath(path);
        string root = Path.GetPathRoot(normalized) ?? string.Empty;
        while (normalized.Length > root.Length &&
               (normalized[^1] == Path.DirectorySeparatorChar || normalized[^1] == Path.AltDirectorySeparatorChar))
        {
            normalized = normalized[..^1];
        }
        return normalized;
    }

    private static string ReadFixedUnicodeString(nint buffer, int maximumCharacters)
    {
        int length = 0;
        while (length < maximumCharacters && Marshal.ReadInt16(buffer, length * sizeof(char)) != 0)
            length++;
        return Marshal.PtrToStringUni(buffer, length) ?? string.Empty;
    }

    private static string SidToString(nint sid)
    {
        if (sid == 0)
            throw LastError("A process-token SID could not be converted for inspection.");
        if (!ConvertSidToStringSidW(sid, out SafeLocalFreeHandle value))
        {
            int error = Marshal.GetLastPInvokeError();
            value.Dispose();
            throw new Win32Exception(error, "A process-token SID could not be converted for inspection.");
        }
        using (value)
        {
            return Marshal.PtrToStringUni(value.DangerousGetHandle())
                ?? throw new InvalidOperationException("A process-token SID string was empty.");
        }
    }

    private static SafeHGlobalBuffer GetTokenInformation(
        SafeTokenHandle token,
        TokenInformationClass informationClass)
    {
        _ = GetTokenInformation(token, informationClass, 0, 0, out uint byteCount);
        int error = Marshal.GetLastPInvokeError();
        if (byteCount == 0 || byteCount > MaximumTokenInformationBytes || error != ErrorInsufficientBuffer)
            throw new Win32Exception(error, $"Token information {informationClass} returned an invalid size ({byteCount}).");

        var buffer = new SafeHGlobalBuffer(checked((int)byteCount));
        if (GetTokenInformation(token, informationClass, buffer.DangerousGetHandle(), byteCount, out uint returnedBytes))
        {
            if (returnedBytes == 0 || returnedBytes > byteCount)
            {
                buffer.Dispose();
                throw new InvalidOperationException($"Token information {informationClass} returned an invalid byte count ({returnedBytes}).");
            }
            return buffer;
        }

        error = Marshal.GetLastPInvokeError();
        buffer.Dispose();
        throw new Win32Exception(error, $"Token information {informationClass} could not be read.");
    }

    private static SafeSnapshotHandle CreateSnapshot(uint flags)
    {
        SafeSnapshotHandle snapshot = CreateToolhelp32Snapshot(flags, 0);
        if (!snapshot.IsInvalid)
            return snapshot;
        snapshot.Dispose();
        throw LastError("A read-only Toolhelp process/thread snapshot could not be created.");
    }

    private static Win32Exception LastError(string message) =>
        new(Marshal.GetLastPInvokeError(), message);

    [StructLayout(LayoutKind.Sequential)]
    private struct AclSizeInformation
    {
        internal uint AceCount;
        internal uint AclBytesInUse;
        internal uint AclBytesFree;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx
    {
        internal uint ByteLength;
        internal uint PageFaultCount;
        internal nuint PeakWorkingSetSize;
        internal nuint WorkingSetSize;
        internal nuint QuotaPeakPagedPoolUsage;
        internal nuint QuotaPagedPoolUsage;
        internal nuint QuotaPeakNonPagedPoolUsage;
        internal nuint QuotaNonPagedPoolUsage;
        internal nuint PagefileUsage;
        internal nuint PeakPagefileUsage;
        internal nuint PrivateUsage;
    }

    private sealed class SafeHGlobalBuffer : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeHGlobalBuffer(int byteLength)
            : base(ownsHandle: true)
        {
            if (byteLength <= 0)
                throw new ArgumentOutOfRangeException(nameof(byteLength));
            ByteLength = checked((uint)byteLength);
            SetHandle(Marshal.AllocHGlobal(byteLength));
            if (IsInvalid)
                throw new OutOfMemoryException("A native buffer required for Windows isolation inspection could not be allocated.");
            Marshal.Copy(new byte[byteLength], 0, handle, byteLength);
        }

        internal uint ByteLength { get; }

        protected override bool ReleaseHandle()
        {
            Marshal.FreeHGlobal(handle);
            return true;
        }
    }

    private sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeKernelHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    private sealed class SafeSnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeSnapshotHandle()
            : base(ownsHandle: true)
        {
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

    private sealed class SafeLocalFreeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeLocalFreeHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => LocalFree(handle) == 0;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeSnapshotHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(SafeSnapshotHandle snapshot, nint processEntry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(SafeSnapshotHandle snapshot, nint processEntry);

    [LibraryImport("kernel32.dll", EntryPoint = "Thread32First", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Thread32First(SafeSnapshotHandle snapshot, nint threadEntry);

    [LibraryImport("kernel32.dll", EntryPoint = "Thread32Next", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Thread32Next(SafeSnapshotHandle snapshot, nint threadEntry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeKernelHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(SafeKernelHandle process, uint flags, nint imageName, ref uint characterCount);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetProcessId(SafeKernelHandle process);

    [LibraryImport("kernelbase.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CompareObjectHandles(SafeKernelHandle first, SafeKernelHandle second);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeKernelHandle process, uint exitCode);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessHandleCount(nint process, out uint handleCount);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeKernelHandle OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(SafeKernelHandle process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMitigationPolicy(SafeKernelHandle process, ProcessMitigationPolicy policy, nint buffer, nuint bufferLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeKernelHandle process, uint desiredAccess, out SafeTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenThreadToken(SafeKernelHandle thread, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool openAsSelf, out SafeTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(SafeTokenHandle token, TokenInformationClass informationClass, nint information, uint informationLength, out uint returnLength);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertSidToStringSidW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertSidToStringSidW(nint sid, out SafeLocalFreeHandle stringSid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetAclInformation(nint acl, out AclSizeInformation information, uint informationLength, AclInformationClass informationClass);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetAce(nint acl, uint aceIndex, out nint ace);

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(nint process, ref ProcessMemoryCountersEx counters, uint size);
}

internal readonly record struct ResourceSnapshot(
    uint CurrentProcessHandleCount,
    ulong CurrentProcessPrivateBytes,
    uint WorkerHandleCount,
    ulong WorkerPrivateBytes);
