using System;
using System.Reflection;
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

            Version assemblyVersion = Assembly.GetEntryAssembly()?.GetName().Version
                ?? Assembly.GetExecutingAssembly().GetName().Version
                ?? new Version(0, 0, 0, 0);

            return new PackageVersion
            {
                Major = (ushort)Math.Max(0, assemblyVersion.Major),
                Minor = (ushort)Math.Max(0, assemblyVersion.Minor),
                Build = (ushort)Math.Max(0, assemblyVersion.Build),
                Revision = (ushort)Math.Max(0, assemblyVersion.Revision)
            };
        }
    }

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
