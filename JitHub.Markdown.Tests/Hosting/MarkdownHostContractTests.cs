using System;
using System.Collections.Generic;
using JitHub.Markdown.Hosting;
using Xunit;

namespace JitHub.Markdown.Tests.Hosting;

public sealed class MarkdownHostContractTests
{
    [Fact]
    public void HostKinds_ResolveStableThemeAndAutomationMetadata()
    {
        HashSet<string> automationIds = new(StringComparer.Ordinal);
        foreach (MarkdownHostKind kind in Enum.GetValues<MarkdownHostKind>())
        {
            string value = kind.ToString();
            Assert.False(string.IsNullOrWhiteSpace(MarkdownHostContract.GetSurfaceColorToken(value)));
            Assert.StartsWith("#", MarkdownHostContract.GetSurfaceFallback(value, dark: false));
            Assert.StartsWith("#", MarkdownHostContract.GetSurfaceFallback(value, dark: true));
            Assert.Contains("Markdown", MarkdownHostContract.GetAutomationName(value), StringComparison.Ordinal);
            Assert.StartsWith("MarkdownHost_", MarkdownHostContract.GetAutomationId(value), StringComparison.Ordinal);
            Assert.True(automationIds.Add(MarkdownHostContract.GetAutomationId(value)),
                $"Duplicate Markdown host automation id for {value}.");
        }
    }

    [Fact]
    public void AutomationIdentity_NormalizesAndSeparatesHostInstances()
    {
        Assert.Equal(
            "MarkdownHost_Comment_IssueComment_42",
            MarkdownHostContract.GetAutomationId(MarkdownHostContract.Comment, "IssueComment:42"));
        Assert.NotEqual(
            MarkdownHostContract.GetAutomationId(MarkdownHostContract.Comment, "IssueComment:42"),
            MarkdownHostContract.GetAutomationId(MarkdownHostContract.Comment, "IssueComment:43"));
    }
}
