using MarkdownRenderer.Sample.Automation;
using MarkdownRenderer.Sample.Automation.Applications;
using Xunit;

namespace MarkdownRenderer.Sample.Automation.Tests.Applications;

public sealed class DisposalEvidenceTests
{
    [Fact]
    public void CompletedDisposalFromTheSameProcessCounts()
        => Assert.True(DisposalEvidence.ShowsCompletedDisposal(
            """{"processId":42,"disposalCompleted":true,"disposedViews":1,"attachedViews":1}""",
            42));

    [Fact]
    public void EvidenceFromAnotherProcessDoesNotCount()
        => Assert.False(DisposalEvidence.ShowsCompletedDisposal(
            """{"processId":41,"disposalCompleted":true}""",
            42));

    [Fact]
    public void IncompleteDisposalDoesNotCount()
        => Assert.False(DisposalEvidence.ShowsCompletedDisposal(
            """{"processId":42,"disposalCompleted":false,"disposedViews":1,"attachedViews":2}""",
            42));
}
