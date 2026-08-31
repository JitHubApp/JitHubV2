using System;
using System.IO;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class HighContrastLiveProbeContractTests
{
    [Fact]
    public void ProbeUsesSupportedWin32ApisAndRespectsHighContrastSchemeBufferOwnership()
    {
        string source = ReadProbeSource();

        Assert.Contains("SpiGetHighContrast = 0x0042", source, StringComparison.Ordinal);
        Assert.Contains("SpiSetHighContrast = 0x0043", source, StringComparison.Ordinal);
        Assert.Contains("SystemParametersInfoW", source, StringComparison.Ordinal);
        Assert.Contains("SpifUpdateIniFile | SpifSendChange", source, StringComparison.Ordinal);
        Assert.Contains("Marshal.PtrToStringUni(native.DefaultScheme)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalFree", source, StringComparison.Ordinal);
        Assert.Contains("Marshal.StringToHGlobalUni(target.DefaultScheme)", source, StringComparison.Ordinal);
        Assert.Contains("Marshal.FreeHGlobal(scheme)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.Win32.Registry", source, StringComparison.Ordinal);
        Assert.DoesNotContain("--high-contrast", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PRINT SCREEN", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CopyFromScreen", source, StringComparison.Ordinal);
        Assert.Contains("PrintWindow(windowHandle, deviceContext, PwRenderFullContent)", source, StringComparison.Ordinal);
        Assert.Contains("DwmGetWindowAttribute", source, StringComparison.Ordinal);
        Assert.Contains("fullWindow.Clone(crop", source, StringComparison.Ordinal);
        Assert.Contains("uint dpi = GetDpiForWindow(windowHandle);", source, StringComparison.Ordinal);
        Assert.Contains("viewport.Width * scale", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Min(viewport.Width, availableWidth)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeEnablesOnlyWhenNeededAndRestoresTheExactCapturedStateInFinally()
    {
        string source = ReadProbeSource();

        Assert.Contains("NativeHighContrastSnapshot prior = ReadHighContrast();", source, StringComparison.Ordinal);
        Assert.Contains("if (!prior.IsEnabled)", source, StringComparison.Ordinal);
        Assert.Contains("changedHighContrast = true;", source, StringComparison.Ordinal);
        Assert.Contains("finally", source, StringComparison.Ordinal);
        Assert.Contains("RestoreHighContrast(prior);", source, StringComparison.Ordinal);
        Assert.Contains("WaitForHighContrast(state => state == prior", source, StringComparison.Ordinal);
        Assert.Contains("current == prior", source, StringComparison.Ordinal);
        Assert.Contains("CloseOwnedApplication(app);", source, StringComparison.Ordinal);
        Assert.Contains("process.Kill(entireProcessTree: true);", source, StringComparison.Ordinal);
        Assert.Contains("EnsureNoExistingApplicationProcess", source, StringComparison.Ordinal);
        Assert.Contains("JITHUB_AUTOMATION_DATA_ROOT", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeExercisesRepresentativePagesAtWideAndCompactSizesWithVisualEvidence()
    {
        string source = ReadProbeSource();

        Assert.Contains("new(1366, 900)", source, StringComparison.Ordinal);
        Assert.Contains("new(760, 650)", source, StringComparison.Ordinal);
        Assert.Contains("\"settings\"", source, StringComparison.Ordinal);
        Assert.Contains("\"profile\"", source, StringComparison.Ordinal);
        Assert.Contains("\"repo-code\"", source, StringComparison.Ordinal);
        Assert.Contains("SettingsSection_appearance", source, StringComparison.Ordinal);
        Assert.Contains("SettingsCompactSectionPicker", source, StringComparison.Ordinal);
        Assert.Contains("ProfileModeOverviewItem", source, StringComparison.Ordinal);
        Assert.Contains("ProfileContributionGraph", source, StringComparison.Ordinal);
        Assert.Contains("MarkdownHost_RepositoryReadme_RepoCodeReadme", source, StringComparison.Ordinal);
        Assert.Contains("JITHUB_MARKDOWN_LIFECYCLE_FIXTURE", source, StringComparison.Ordinal);
        Assert.Contains("Lifecycle long document final marker", source, StringComparison.Ordinal);
        Assert.Contains("keyboard text selection", source, StringComparison.Ordinal);
        Assert.Contains("AssertSelected", source, StringComparison.Ordinal);
        Assert.Contains("AssertFocusPixelsChanged", source, StringComparison.Ordinal);
        Assert.Contains("AssertSystemColorTreatment", source, StringComparison.Ordinal);
        Assert.Contains("VirtualKeyShort.HOME", source, StringComparison.Ordinal);
        Assert.Contains("VirtualKeyShort.END", source, StringComparison.Ordinal);
        Assert.Contains("GetForegroundWindow() == windowHandle", source, StringComparison.Ordinal);
        Assert.Contains("GetSysColor", source, StringComparison.Ordinal);
        Assert.Contains("high-contrast-live-settings-1366x900.png", source, StringComparison.Ordinal);
        Assert.Contains("high-contrast-live-settings-760x650.png", source, StringComparison.Ordinal);
        Assert.Contains("high-contrast-live-profile-1366x900.png", source, StringComparison.Ordinal);
        Assert.Contains("high-contrast-live-profile-760x650.png", source, StringComparison.Ordinal);
        Assert.Contains("high-contrast-live-profile-graph-focus-1366x900.png", source, StringComparison.Ordinal);
        Assert.Contains("high-contrast-live-markdown-1366x900.png", source, StringComparison.Ordinal);
        Assert.Contains("high-contrast-live-markdown-760x650.png", source, StringComparison.Ordinal);
        Assert.Contains("high-contrast-live-destructive-dialog-{viewportName}.png", source, StringComparison.Ordinal);
        Assert.Contains("high-contrast-live-editor-dialog-{viewportName}.png", source, StringComparison.Ordinal);
        Assert.Contains("high-contrast-live-custom-shell-dialog-{viewportName}.png", source, StringComparison.Ordinal);
        Assert.Contains("ValidateDestructiveDialog(window, options.OutputDirectory, palette, Wide, \"1366x900\")", source, StringComparison.Ordinal);
        Assert.Contains("ValidateDestructiveDialog(window, options.OutputDirectory, palette, Compact, \"760x650\")", source, StringComparison.Ordinal);
        Assert.Contains("ValidateGistEditorDialog(window, options.OutputDirectory, palette, Compact, \"760x650\")", source, StringComparison.Ordinal);
        Assert.Contains("ValidateDashboardCustomShellDialog(window, options.OutputDirectory, palette, Compact, \"760x650\")", source, StringComparison.Ordinal);
        Assert.Contains("windowBounds.Contains(dialogBounds)", source, StringComparison.Ordinal);
        Assert.Contains("dialog.FindFirstDescendant(cf => cf.ByAutomationId(\"CloseButton\"))", source, StringComparison.Ordinal);
        Assert.Contains("FindVisible(window, \"GistEditorDialog\") is null", source, StringComparison.Ordinal);
        Assert.Contains("FindVisible(window, \"DashboardCustomizeDialog\") is null", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeExposesAStandaloneDispatchSurfaceWithoutDependingOnProgramHelpers()
    {
        string source = ReadProbeSource();
        string program = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

        Assert.Contains("internal static class HighContrastLiveProbe", source, StringComparison.Ordinal);
        Assert.Contains("public static void Run(CaptureOptions options)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RunSettingsHighContrastProbe", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeMethods.IsHighContrastEnabled", source, StringComparison.Ordinal);
        Assert.Contains(
            "string.Equals(options.Probe, \"high-contrast-live\", StringComparison.OrdinalIgnoreCase)",
            program,
            StringComparison.Ordinal);
        Assert.Contains("HighContrastLiveProbe.Run(options);", program, StringComparison.Ordinal);
    }

    private static string ReadProbeSource() => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "JitHub.WinUI.Automation",
        "HighContrastLiveProbe.cs"));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) &&
                Directory.Exists(Path.Combine(directory.FullName, "JitHub.WinUI.Automation")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
