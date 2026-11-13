using System;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class RepositoryBranchSelectionPolicyTests
{
    // flutter/flutter's first page of branches, by name: master is on a later page.
    private static readonly string[] FlutterFirstPage = ["3.29", "3.33.0-1.0.pre-1343", "add-ttff-metrics"];

    [Fact]
    public void AKnownDefaultBranchOnALaterPageIsSelectedByName()
    {
        string? selected = RepositoryBranchSelectionPolicy.Resolve(
            FlutterFirstPage, "master", StringComparer.Ordinal, null, "master");

        Assert.Equal("master", selected);
    }

    [Fact]
    public void ALoadedRequestedBranchWinsOverTheDefault()
    {
        string? selected = RepositoryBranchSelectionPolicy.Resolve(
            ["main", "feature"], "main", StringComparer.Ordinal, "feature", "main");

        Assert.Equal("feature", selected);
    }

    [Fact]
    public void ARequestedBranchNotLoadedYetFallsBackToTheKnownDefault()
    {
        string? selected = RepositoryBranchSelectionPolicy.Resolve(
            FlutterFirstPage, "master", StringComparer.Ordinal, "release-candidate", "master");

        Assert.Equal("master", selected);
    }

    [Fact]
    public void ALaterPreferredBranchIsUsedWhenAnEarlierOneIsNotLoaded()
    {
        string? selected = RepositoryBranchSelectionPolicy.Resolve(
            ["develop", "main"], "develop", StringComparer.Ordinal, "gone", "develop");

        Assert.Equal("develop", selected);
    }

    [Fact]
    public void WithNoKnownBranchAConventionalDefaultIsPreferred()
    {
        string? selected = RepositoryBranchSelectionPolicy.Resolve(
            ["alpha", "master", "zeta"], null, StringComparer.Ordinal, (string?)null);

        Assert.Equal("master", selected);
    }

    [Fact]
    public void WithNothingKnownTheFirstLoadedBranchIsUsed()
    {
        string? selected = RepositoryBranchSelectionPolicy.Resolve(
            ["alpha", "zeta"], " ", StringComparer.Ordinal, "", null);

        Assert.Equal("alpha", selected);
    }

    [Fact]
    public void WithNothingKnownOrLoadedNoBranchIsSelected()
    {
        Assert.Null(RepositoryBranchSelectionPolicy.Resolve([], null, StringComparer.Ordinal, (string?)null));
    }

    [Fact]
    public void TheComparerDecidesWhetherAPreferredBranchIsLoaded()
    {
        Assert.Equal(
            "Main",
            RepositoryBranchSelectionPolicy.Resolve(["main"], null, StringComparer.OrdinalIgnoreCase, "Main"));
        Assert.Equal(
            "main",
            RepositoryBranchSelectionPolicy.Resolve(["main"], null, StringComparer.Ordinal, "Main"));
    }
}
