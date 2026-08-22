using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;

namespace CommunityToolkit.WinUI.Helpers;

public sealed class SystemInformation
{
    private SystemInformation()
    {
    }

    public static SystemInformation Instance { get; } = new();

    public PackageVersion ApplicationVersion
    {
        get
        {
            if (TryGetPackagedVersion(out PackageVersion version))
            {
                return version;
            }

            FileVersionInfo? fileVersion = TryGetProcessVersion();

            return new PackageVersion
            {
                Major = ToPackageVersionPart(fileVersion?.FileMajorPart ?? 0),
                Minor = ToPackageVersionPart(fileVersion?.FileMinorPart ?? 0),
                Build = ToPackageVersionPart(fileVersion?.FileBuildPart ?? 0),
                Revision = ToPackageVersionPart(fileVersion?.FilePrivatePart ?? 0)
            };
        }
    }

    private static FileVersionInfo? TryGetProcessVersion()
    {
        try
        {
            string? processPath = Environment.ProcessPath;
            return string.IsNullOrWhiteSpace(processPath) ? null : FileVersionInfo.GetVersionInfo(processPath);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ushort ToPackageVersionPart(int value) =>
        (ushort)Math.Clamp(value, 0, ushort.MaxValue);

    private static bool TryGetPackagedVersion(out PackageVersion version)
    {
        try
        {
            version = Package.Current.Id.Version;
            return true;
        }
        catch (COMException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        version = default;
        return false;
    }
}
