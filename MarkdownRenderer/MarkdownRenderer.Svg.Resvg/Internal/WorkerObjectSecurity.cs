using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal static partial class WorkerObjectSecurity
{
    private const uint LabelSecurityInformation = 0x00000010;
    private const uint SddlRevision1 = 1;

    public static string CreateRestrictedDaclSddl()
    {
        string user = WindowsIdentity.GetCurrent().User?.Value ??
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
        return new SecurityDescriptorHandle(descriptor);
    }

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
