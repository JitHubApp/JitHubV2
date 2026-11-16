using System.Runtime.InteropServices;
using System.Security.Principal;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests.Internal.Workers.Processes;

public sealed partial class WindowsWorkerTokenIsolationContractTests
{
    private const int TokenRestrictedSids = 11;
    private const int TokenIntegrityLevel = 25;

    [Fact]
    public void RestrictedCodeSidIsPassedAsARestrictingSid()
    {
        using SafeTokenHandle sourceToken = WorkerProcessSandbox.OpenCurrentPrimaryTokenForWorker();
        using WorkerObjectSecurity.WorkerObjectSecurityContext objectSecurity =
            WorkerObjectSecurity.CreateForPrimaryToken(sourceToken);
        using SafeTokenHandle workerToken =
            WorkerProcessSandbox.CreateRestrictedWorkerToken(sourceToken, objectSecurity);

        SecurityIdentifier restrictingSid = Assert.Single(ReadRestrictingSids(workerToken));
        Assert.Equal(new SecurityIdentifier("S-1-5-12"), restrictingSid);
        Assert.Equal(new SecurityIdentifier("S-1-16-4096"), ReadIntegrityLevel(workerToken));
    }

    private static SecurityIdentifier[] ReadRestrictingSids(SafeTokenHandle token)
    {
        byte[] buffer = ReadTokenInformation(token, TokenRestrictedSids);
        GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            nint groups = pin.AddrOfPinnedObject();
            int count = Marshal.ReadInt32(groups);
            // TOKEN_GROUPS: a DWORD count, then pointer-aligned SID_AND_ATTRIBUTES.
            int entrySize = Marshal.SizeOf<SidAndAttributes>();
            nint first = groups + IntPtr.Size;
            var sids = new SecurityIdentifier[count];
            for (int index = 0; index < count; index++)
                sids[index] = new SecurityIdentifier(Marshal.ReadIntPtr(first + (index * entrySize)));
            return sids;
        }
        finally
        {
            pin.Free();
        }
    }

    private static SecurityIdentifier ReadIntegrityLevel(SafeTokenHandle token)
    {
        byte[] buffer = ReadTokenInformation(token, TokenIntegrityLevel);
        GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            // TOKEN_MANDATORY_LABEL starts with the label's SID pointer.
            return new SecurityIdentifier(Marshal.ReadIntPtr(pin.AddrOfPinnedObject()));
        }
        finally
        {
            pin.Free();
        }
    }

    private static byte[] ReadTokenInformation(SafeTokenHandle token, int informationClass)
    {
        _ = GetTokenInformation(token, informationClass, null, 0, out int length);
        Assert.True(length > 0, $"Token information class {informationClass} reported no length.");
        byte[] buffer = new byte[length];
        Assert.True(GetTokenInformation(token, informationClass, buffer, length, out _),
            $"Token information class {informationClass} could not be read: {Marshal.GetLastPInvokeError()}.");
        return buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        SafeTokenHandle token,
        int informationClass,
        byte[]? information,
        int informationLength,
        out int returnLength);
}
