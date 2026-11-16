using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JitHub.Core.Tests.ViewModels.Gists;

public sealed class GistsWorkspaceContractTests
{
    [Fact]
    public void Telemetry_UsesSanitizedDurationBucketsForRequiredGistPhases()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.Core",
            "ViewModels",
            "Gists",
            "GistsPageViewModel.cs"));

        Assert.Contains("\"cached_first\"", source, StringComparison.Ordinal);
        Assert.Contains("\"reconciliation\"", source, StringComparison.Ordinal);
        Assert.Contains("TrackAction(\"detail_selection\"", source, StringComparison.Ordinal);
        Assert.Contains("RunOwnedMutationAsync", source, StringComparison.Ordinal);
        Assert.Contains("TelemetrySanitizer.CreateDurationBucket", source, StringComparison.Ordinal);
        Assert.Contains("TrackCopyFileSuccess() => TrackAction(\"copy_file\", \"success\")", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) || Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
