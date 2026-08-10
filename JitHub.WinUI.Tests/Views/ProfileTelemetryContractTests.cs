using System;
using System.IO;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class ProfileTelemetryContractTests
{
    [Fact]
    public void ProfileEmitsRouteSectionActionAndFailureTelemetryWithoutIdentityProperties()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "ProfilePageViewModel.cs"));

        foreach (string eventName in new[]
        {
            "profile.opened",
            "profile.loaded",
            "profile.section.opened",
            "profile.action.executed",
            "profile.error"
        })
        {
            Assert.Contains($"\"{eventName}\"", source, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("[\"login\"]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("[\"repository\"]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("[\"url\"]", source, StringComparison.Ordinal);
        Assert.Contains("TelemetrySanitizer.CreateDurationBucket", source, StringComparison.Ordinal);
        Assert.Contains("NormalizeTelemetrySource", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
