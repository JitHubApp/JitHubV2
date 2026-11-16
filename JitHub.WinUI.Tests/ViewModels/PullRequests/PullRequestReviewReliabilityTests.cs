using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace JitHub.WinUI.Tests.ViewModels.PullRequests;

public sealed class PullRequestReviewReliabilityTests
{
    [Fact]
    public void MutationHandlersUseInitiatingPullRequestNumberForCapabilityDenials()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "ViewModels",
            "PullRequests",
            "RepoPullRequestPageViewModel.cs"));

        Assert.Contains("_capabilityDenials.RecordFailureForCurrent(", source, StringComparison.Ordinal);
        Assert.Contains("_capabilityDenials.TrackPullRequest(value.Number);", source, StringComparison.Ordinal);
        foreach (string capability in new[] { "edit", "metadata", "state", "comment", "reaction", "review", "merge" })
        {
            Assert.DoesNotContain(
                $"DisableRejectedCapability(ex, \"{capability}\");",
                source,
                StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
