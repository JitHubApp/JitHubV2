using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal static partial class WorkerObjectSecurity
{
    private const uint LabelSecurityInformation = 0x00000010;
    private const uint SddlRevision1 = 1;
    private const uint TokenUserInformationClass = 1;
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>
    /// Captures the object ACL once for one worker generation. The SID is read
    /// from the exact process primary token that will be restricted for that
    /// worker; this is deliberately not a process-wide or thread-identity cache.
    /// </summary>
    internal static WorkerObjectSecurityContext CreateForPrimaryToken(SafeHandle primaryToken)
    {
        ArgumentNullException.ThrowIfNull(primaryToken);
        ObjectDisposedException.ThrowIf(primaryToken.IsClosed || primaryToken.IsInvalid, primaryToken);

        string userSid = GetPrimaryTokenUserSid(primaryToken);
        string dacl = $"D:P(A;;GA;;;{userSid})(A;;GA;;;RC)";
        SecurityDescriptorHandle descriptor = CreateDescriptor($"{dacl}S:(ML;;NW;;;LW)");
        try
        {
            return new WorkerObjectSecurityContext(dacl, descriptor);
        }
        catch
        {
            descriptor.Dispose();
            throw;
        }
    }

    public static string CreateRestrictedDaclSddl()
    {
        // Kept only for isolated legacy tests. Production worker startup uses
        // CreateForPrimaryToken so the pipe, token default DACL, and mappings
        // share the exact primary-token identity captured for that generation.
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string user = identity.User?.Value ??
            throw new InvalidOperationException("The current Windows identity has no user SID.");
        return $"D:P(A;;GA;;;{user})(A;;GA;;;RC)";
    }

    public static SecurityDescriptorHandle CreateRestrictedLowIntegrityDescriptor()
    {
        string sddl = $"{CreateRestrictedDaclSddl()}S:(ML;;NW;;;LW)";
        return CreateDescriptor(sddl);
    }

    public static void SetLowIntegrityLabel(SafeHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        using SecurityDescriptorHandle descriptor = CreateDescriptor("S:(ML;;NW;;;LW)");
        if (!SetKernelObjectSecurity(
                handle,
                LabelSecurityInformation,
                descriptor.DangerousGetHandle()))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "The SVG worker object could not be assigned a low-integrity label.");
        }
    }

    private static SecurityDescriptorHandle CreateDescriptor(string sddl)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl,
                SddlRevision1,
                out nint descriptor,
                out _))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "The SVG worker object security descriptor could not be created.");
        }
        try
        {
            return new SecurityDescriptorHandle(descriptor);
        }
        catch
        {
            _ = LocalFree(descriptor);
            throw;
        }
    }

    private static string GetPrimaryTokenUserSid(SafeHandle primaryToken)
    {
        bool addedRef = false;
        try
        {
            primaryToken.DangerousAddRef(ref addedRef);
            if (GetTokenInformation(
                    primaryToken,
                    TokenUserInformationClass,
                    0,
                    0,
                    out uint requiredBytes))
            {
                throw new InvalidOperationException("The primary token unexpectedly returned an empty user record.");
            }

            int error = Marshal.GetLastPInvokeError();
            if (error != ErrorInsufficientBuffer ||
                requiredBytes < Marshal.SizeOf<TokenUserInformation>() ||
                requiredBytes > 1024 * 1024)
            {
                throw new Win32Exception(error, "The worker primary-token user SID could not be queried.");
            }

            nint tokenUserBuffer = Marshal.AllocHGlobal(checked((nint)requiredBytes));
            try
            {
                if (!GetTokenInformation(
                        primaryToken,
                        TokenUserInformationClass,
                        tokenUserBuffer,
                        requiredBytes,
                        out uint writtenBytes) ||
                    writtenBytes < Marshal.SizeOf<TokenUserInformation>())
                {
                    throw new Win32Exception(
                        Marshal.GetLastPInvokeError(),
                        "The worker primary-token user SID could not be read.");
                }

                TokenUserInformation tokenUser = Marshal.PtrToStructure<TokenUserInformation>(tokenUserBuffer);
                if (tokenUser.User.Sid == 0)
                    throw new InvalidOperationException("The worker primary token has no user SID.");

                return new SecurityIdentifier(tokenUser.User.Sid).Value ??
                    throw new InvalidOperationException("The worker primary-token user SID is empty.");
            }
            finally
            {
                Marshal.FreeHGlobal(tokenUserBuffer);
            }
        }
        finally
        {
            if (addedRef)
                primaryToken.DangerousRelease();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenUserInformation
    {
        public SidAndAttributes User;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        SafeHandle token,
        uint tokenInformationClass,
        nint tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor,
        uint stringSdRevision,
        out nint securityDescriptor,
        out uint securityDescriptorSize);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetKernelObjectSecurity(
        SafeHandle handle,
        uint securityInformation,
        nint securityDescriptor);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    internal sealed class WorkerObjectSecurityContext : IDisposable
    {
        private SecurityDescriptorHandle? _descriptor;

        internal WorkerObjectSecurityContext(string restrictedDaclSddl, SecurityDescriptorHandle descriptor)
        {
            RestrictedDaclSddl = restrictedDaclSddl;
            _descriptor = descriptor;
        }

        internal string RestrictedDaclSddl { get; }

        // Consumers must DangerousAddRef around any native call that reads the
        // pointer. SafeHandle then arbitrates disposal races without a custom
        // reference-counting scheme.
        internal SecurityDescriptorHandle Descriptor =>
            Volatile.Read(ref _descriptor) ?? throw new ObjectDisposedException(nameof(WorkerObjectSecurityContext));

        public void Dispose() => Interlocked.Exchange(ref _descriptor, null)?.Dispose();
    }

    internal sealed class SecurityDescriptorHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SecurityDescriptorHandle(nint descriptor)
            : base(ownsHandle: true)
        {
            SetHandle(descriptor);
        }

        protected override bool ReleaseHandle() => LocalFree(handle) == 0;
    }
}
