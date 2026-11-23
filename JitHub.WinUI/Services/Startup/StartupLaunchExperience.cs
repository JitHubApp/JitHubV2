using System.Threading;
using JitHub.Core.Models.Activation;
using JitHub.Core.Models.Startup;
using static JitHub.WinUI.Services.Startup.StartupSplashInterop;

namespace JitHub.WinUI.Services.Startup;

/// <summary>
/// Owns the launch handoff: the splash stays up while the main window is built
/// cloaked, then the window is revealed in place once it has real content.
/// </summary>
internal sealed unsafe class StartupLaunchExperience
{
    private readonly NativeStartupSplash _splash;
    private nint _cloakedWindow;
    private int _completed;

    public StartupLaunchExperience(LaunchOptions launchOptions)
    {
        _splash = new NativeStartupSplash(launchOptions, Complete);
    }

    public StartupWindowBounds? PreferredWindowBounds => _splash.WindowBounds;

    /// <summary>Hides the window from composition until <see cref="Complete"/> runs.</summary>
    public void CloakWindow(nint window)
    {
        if (Volatile.Read(ref _completed) != 0)
        {
            return;
        }

        int cloak = 1;
        if (DwmSetWindowAttribute(window, DwmwaCloak, &cloak, sizeof(int)) >= 0)
        {
            Volatile.Write(ref _cloakedWindow, window);
        }

        if (Volatile.Read(ref _completed) != 0)
        {
            Uncloak();
        }
    }

    /// <summary>Reveals the main window and removes the splash. Safe to call repeatedly.</summary>
    public void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        Uncloak();
        _splash.Dismiss();
        Program.LogStartupPhase("startup.handoff-complete");
    }

    private void Uncloak()
    {
        nint window = Interlocked.Exchange(ref _cloakedWindow, 0);
        if (window == 0)
        {
            return;
        }

        int cloak = 0;
        _ = DwmSetWindowAttribute(window, DwmwaCloak, &cloak, sizeof(int));
    }
}
