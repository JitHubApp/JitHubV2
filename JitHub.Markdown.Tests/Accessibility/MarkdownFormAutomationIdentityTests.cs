using System;
using JitHub.Markdown.Accessibility;
using JitHub.Markdown.Hosting;
using Xunit;

namespace JitHub.Markdown.Tests.Accessibility;

public sealed class MarkdownFormAutomationIdentityTests
{
    [Fact]
    public void AnonymousFormsGetDistinctFallbackScopes()
    {
        MarkdownFormAutomationIdentity first = new();
        MarkdownFormAutomationIdentity second = new();

        Assert.False(string.IsNullOrWhiteSpace(first.FallbackInstanceId));
        Assert.StartsWith("MarkdownForm_", first.FallbackInstanceId, StringComparison.Ordinal);
        Assert.NotEqual(first.FallbackInstanceId, second.FallbackInstanceId);
        Assert.NotEqual(
            MarkdownFormAutomationIdentity.GetPeerIds(first.ResolvePrefix(null, null)).Editor,
            MarkdownFormAutomationIdentity.GetPeerIds(second.ResolvePrefix(null, null)).Editor);
    }

    [Fact]
    public void FallbackScopeIsStableForOneForm()
    {
        Guid instance = Guid.NewGuid();

        Assert.Equal(
            new MarkdownFormAutomationIdentity(instance).FallbackInstanceId,
            new MarkdownFormAutomationIdentity(instance).FallbackInstanceId);
    }

    [Fact]
    public void ExplicitInstanceIdWinsOverAttachedIdAndFallback()
    {
        MarkdownFormAutomationIdentity identity = new();

        Assert.Equal("IssueComment_42", identity.ResolvePrefix("  IssueComment_42 ", "Attached"));
        Assert.Equal("Attached", identity.ResolvePrefix(" ", " Attached "));
        Assert.Equal(identity.FallbackInstanceId, identity.ResolvePrefix(null, " "));
    }

    [Fact]
    public void RescopingThePrefixRescopesEveryInteractivePeer()
    {
        MarkdownFormPeerAutomationIds first = MarkdownFormAutomationIdentity.GetPeerIds("Thread_1_ReplyForm");
        MarkdownFormPeerAutomationIds second = MarkdownFormAutomationIdentity.GetPeerIds("Thread_2_ReplyForm");

        Assert.Equal(
            new MarkdownFormPeerAutomationIds(
                "Thread_1_ReplyForm_Mode",
                "Thread_1_ReplyForm_Mode_Write",
                "Thread_1_ReplyForm_Mode_Preview",
                "Thread_1_ReplyForm_Editor",
                "Thread_1_ReplyForm_Preview"),
            first);
        Assert.NotEqual(first.Mode, second.Mode);
        Assert.NotEqual(first.WriteMode, second.WriteMode);
        Assert.NotEqual(first.PreviewMode, second.PreviewMode);
        Assert.NotEqual(first.Editor, second.Editor);
        Assert.NotEqual(first.Preview, second.Preview);
    }

    [Fact]
    public void PreviewHostIdentityFollowsThePreviewInstanceId()
    {
        Assert.Equal("IssueComment_42_Preview", MarkdownFormAutomationIdentity.GetPreviewInstanceId("IssueComment_42"));
        Assert.Equal(
            MarkdownHostContract.GetAutomationId(MarkdownHostContract.EditorPreview, "IssueComment_42_Preview"),
            MarkdownFormAutomationIdentity.GetPreviewHostId("IssueComment_42"));
    }
}
