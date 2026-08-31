using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class DialogMutationOutcomePolicyTests
{
    [Theory]
    [InlineData("Pull request metadata updated, but reviewer changes failed: denied")]
    [InlineData("Pull request metadata updated, but JitHub could not update reviewers.")]
    [InlineData("GitHub did not merge this pull request.")]
    [InlineData("JitHub could not reach GitHub to create this pull request.")]
    public void ExplicitFailure_WinsEvenWhenStatusContainsSuccessVerb(string status)
    {
        DialogMutationOutcome outcome = DialogMutationOutcomePolicy.Resolve(
            "Updating...",
            status,
            observableSuccess: false,
            successText: "updated",
            fallbackError: "fallback");

        Assert.False(outcome.Succeeded);
        Assert.Equal(status, outcome.ErrorMessage);
    }

    [Fact]
    public void RefreshFailureAfterCompletedMutation_IsStillSuccess()
    {
        DialogMutationOutcome outcome = DialogMutationOutcomePolicy.Resolve(
            "Updating...",
            "Pull request updated, but JitHub could not refresh pull request details.",
            observableSuccess: false,
            successText: "updated",
            fallbackError: "fallback");

        Assert.True(outcome.Succeeded);
    }

    [Fact]
    public void ObservablePostcondition_IsSuccessWithoutStatusDependency()
    {
        DialogMutationOutcome outcome = DialogMutationOutcomePolicy.Resolve(
            "Creating...",
            "",
            observableSuccess: true,
            successText: "created",
            fallbackError: "fallback");

        Assert.True(outcome.Succeeded);
    }

    [Fact]
    public void UnknownChangedStatus_IsShownInline()
    {
        DialogMutationOutcome outcome = DialogMutationOutcomePolicy.Resolve(
            "Updating...",
            "Protected branch policy rejected this operation.",
            observableSuccess: false,
            successText: "updated",
            fallbackError: "fallback");

        Assert.False(outcome.Succeeded);
        Assert.Equal("Protected branch policy rejected this operation.", outcome.ErrorMessage);
    }
}
