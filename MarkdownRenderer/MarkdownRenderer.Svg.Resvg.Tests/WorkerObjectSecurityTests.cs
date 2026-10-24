using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Security.AccessControl;
using System.Security.Principal;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

[CollectionDefinition("Process resource measurements", DisableParallelization = true)]
public sealed class ProcessResourceMeasurementsCollection;

[Collection("Process resource measurements")]
public sealed partial class WorkerObjectSecurityTests
{
    [Fact]
    public void RepeatedDaclConstructionClosesIdentityTokensWithoutWaitingForGc()
    {
        for (int i = 0; i < 10; i++)
            _ = WorkerObjectSecurity.CreateRestrictedDaclSddl();
        uint before = ReadHandleCount();
        for (int i = 0; i < 1000; i++)
        {
            string sddl = WorkerObjectSecurity.CreateRestrictedDaclSddl();
            Assert.StartsWith("D:P(A;;GA;;;S-1-", sddl);
            Assert.EndsWith(")(A;;GA;;;RC)", sddl);
        }
        uint after = ReadHandleCount();
        Assert.True(after <= before + 4, $"DACL creation retained process handles: {before}->{after}.");
    }

    [Fact]
    public void WorkerGenerationContextReusesPrimaryTokenDescriptorAndSafeHandleProtectsNativeLifetime()
    {
        using WindowsWorkerProcess.SafeTokenHandle primaryToken =
            WindowsWorkerProcess.OpenCurrentPrimaryTokenForWorker();
        var context = WorkerObjectSecurity.CreateForPrimaryToken(primaryToken);
        WorkerObjectSecurity.SecurityDescriptorHandle descriptor = context.Descriptor;
        Assert.Same(descriptor, context.Descriptor);
        string primaryUserSid = ReadPrimaryTokenUserSid(primaryToken);
        Assert.Equal($"D:P(A;;GA;;;{primaryUserSid})(A;;GA;;;RC)", context.RestrictedDaclSddl);
        AssertSecurityDescriptorPolicy(descriptor, primaryUserSid);

        nint descriptorPointer = descriptor.DangerousGetHandle();
        bool addedRef = false;
        descriptor.DangerousAddRef(ref addedRef);
        Assert.True(addedRef);
        try
        {
            context.Dispose();
            Assert.Throws<ObjectDisposedException>(() => _ = context.Descriptor);
            Assert.True(GetSecurityDescriptorDacl(descriptorPointer, out int present, out nint dacl, out _));
            Assert.Equal(1, present);
            Assert.NotEqual(0, dacl);
        }
        finally
        {
            if (addedRef)
                descriptor.DangerousRelease();
        }

        Assert.True(descriptor.IsClosed);
    }

    [Fact]
    public void SeparateWorkerGenerationsOwnSeparateSecurityDescriptors()
    {
        using WindowsWorkerProcess.SafeTokenHandle primaryToken =
            WindowsWorkerProcess.OpenCurrentPrimaryTokenForWorker();
        using WorkerObjectSecurity.WorkerObjectSecurityContext first =
            WorkerObjectSecurity.CreateForPrimaryToken(primaryToken);
        using WorkerObjectSecurity.WorkerObjectSecurityContext second =
            WorkerObjectSecurity.CreateForPrimaryToken(primaryToken);

        Assert.NotSame(first, second);
        Assert.NotSame(first.Descriptor, second.Descriptor);
        Assert.Equal(first.RestrictedDaclSddl, second.RestrictedDaclSddl);
    }

    [Fact]
    public void SharedMappingsReuseContextDescriptorAndRemainValidAfterContextRetirement()
    {
        using WindowsWorkerProcess.SafeTokenHandle primaryToken =
            WindowsWorkerProcess.OpenCurrentPrimaryTokenForWorker();
        var context = WorkerObjectSecurity.CreateForPrimaryToken(primaryToken);
        WorkerObjectSecurity.SecurityDescriptorHandle descriptor = context.Descriptor;

        using var first = new SharedMemoryLease(
            "worker-security-first",
            outputLength: 16,
            securityContext: context);
        Assert.Same(descriptor, context.Descriptor);
        using var second = new SharedMemoryLease(
            "worker-security-second",
            outputLength: 16,
            securityContext: context);
        Assert.Same(descriptor, context.Descriptor);
        first.GetSpan().Fill(0x3A);
        second.GetSpan().Fill(0xC5);

        context.Dispose();
        Assert.All(first.GetSpan().ToArray(), value => Assert.Equal((byte)0x3A, value));
        Assert.All(second.GetSpan().ToArray(), value => Assert.Equal((byte)0xC5, value));
    }

    private static uint ReadHandleCount()
    {
        if (!GetProcessHandleCount(new nint(-1), out uint count))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        return count;
    }

    private static string ReadPrimaryTokenUserSid(WindowsWorkerProcess.SafeTokenHandle token)
    {
        const uint TokenUser = 1;
        if (GetTokenInformation(token, TokenUser, 0, 0, out uint requiredBytes))
            throw new InvalidOperationException("A token-user query unexpectedly succeeded without a buffer.");
        int error = Marshal.GetLastPInvokeError();
        Assert.Equal(122, error);
        Assert.InRange(requiredBytes, (uint)Marshal.SizeOf<TokenUserRecord>(), 1024u * 1024u);

        nint buffer = Marshal.AllocHGlobal(checked((nint)requiredBytes));
        try
        {
            Assert.True(GetTokenInformation(token, TokenUser, buffer, requiredBytes, out uint written));
            Assert.InRange(written, (uint)Marshal.SizeOf<TokenUserRecord>(), requiredBytes);
            TokenUserRecord record = Marshal.PtrToStructure<TokenUserRecord>(buffer);
            Assert.NotEqual(0, record.User.Sid);
            return new SecurityIdentifier(record.User.Sid).Value!;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void AssertSecurityDescriptorPolicy(
        WorkerObjectSecurity.SecurityDescriptorHandle descriptor,
        string expectedUserSid)
    {
        nint pointer = descriptor.DangerousGetHandle();
        uint length = GetSecurityDescriptorLength(pointer);
        Assert.InRange(length, 1u, 64u * 1024u);
        byte[] binary = new byte[checked((int)length)];
        Marshal.Copy(pointer, binary, 0, checked((int)length));
        var raw = new RawSecurityDescriptor(binary, 0);

        Assert.NotNull(raw.DiscretionaryAcl);
        Assert.True((raw.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0);
        Assert.Equal(2, raw.DiscretionaryAcl!.Count);
        var daclSids = new HashSet<string>(StringComparer.Ordinal);
        foreach (GenericAce genericAce in raw.DiscretionaryAcl)
        {
            var ace = Assert.IsType<CommonAce>(genericAce);
            Assert.Equal(AceQualifier.AccessAllowed, ace.AceQualifier);
            Assert.Equal(AceFlags.None, ace.AceFlags);
            Assert.Equal(unchecked((int)0x10000000), ace.AccessMask); // GENERIC_ALL
            daclSids.Add(ace.SecurityIdentifier.Value);
        }
        Assert.Equal(
            new[] { expectedUserSid, "S-1-5-12" }.OrderBy(static sid => sid, StringComparer.Ordinal),
            daclSids.OrderBy(static sid => sid, StringComparer.Ordinal));

        Assert.True((raw.ControlFlags & ControlFlags.SystemAclPresent) != 0);
        Assert.NotNull(raw.SystemAcl);
        Assert.Single(raw.SystemAcl!);
        GenericAce genericLabel = raw.SystemAcl![0];
        byte[] nativeLabel = new byte[genericLabel.BinaryLength];
        genericLabel.GetBinaryForm(nativeLabel, 0);
        Assert.Equal((byte)0x11, nativeLabel[0]); // SYSTEM_MANDATORY_LABEL_ACE_TYPE
        Assert.Equal((byte)0, nativeLabel[1]);
        Assert.Equal(checked((ushort)nativeLabel.Length),
            BinaryPrimitives.ReadUInt16LittleEndian(nativeLabel.AsSpan(2)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(nativeLabel.AsSpan(4))); // NO_WRITE_UP
        Assert.Equal("S-1-16-4096", new SecurityIdentifier(nativeLabel, 8).Value);
        Assert.Equal(checked(8 + new SecurityIdentifier(nativeLabel, 8).BinaryLength), nativeLabel.Length);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessHandleCount(nint process, out uint count);

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenUserRecord
    {
        public SidAndAttributes User;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        WindowsWorkerProcess.SafeTokenHandle token,
        uint informationClass,
        nint information,
        uint informationLength,
        out uint returnLength);

    [LibraryImport("advapi32.dll")]
    private static partial uint GetSecurityDescriptorLength(nint securityDescriptor);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSecurityDescriptorDacl(
        nint securityDescriptor,
        out int daclPresent,
        out nint dacl,
        out int daclDefaulted);
}
