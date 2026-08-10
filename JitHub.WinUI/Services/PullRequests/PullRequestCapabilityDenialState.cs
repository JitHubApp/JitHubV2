using System;
using System.Net;

namespace JitHub.Services;

[Flags]
public enum PullRequestDeniedCapability
{
    None = 0,
    Edit = 1,
    Metadata = 2,
    State = 4,
    Comment = 8,
    Reaction = 16,
    Merge = 32,
    Review = 64
}

public sealed class PullRequestCapabilityDenialState
{
    private int _pullRequestNumber;
    private PullRequestDeniedCapability _deniedCapabilities;

    public int PullRequestNumber => _pullRequestNumber;

    public PullRequestDeniedCapability DeniedCapabilities => _deniedCapabilities;

    public void TrackPullRequest(int pullRequestNumber)
    {
        if (_pullRequestNumber == pullRequestNumber)
        {
            return;
        }

        _pullRequestNumber = pullRequestNumber;
        _deniedCapabilities = PullRequestDeniedCapability.None;
    }

    public bool RecordFailure(
        int pullRequestNumber,
        PullRequestDeniedCapability capability,
        HttpStatusCode statusCode)
    {
        TrackPullRequest(pullRequestNumber);
        if (statusCode != HttpStatusCode.Forbidden)
        {
            return false;
        }

        _deniedCapabilities |= capability;
        return true;
    }

    public bool RecordFailureForCurrent(
        int pullRequestNumber,
        PullRequestDeniedCapability capability,
        HttpStatusCode statusCode)
    {
        if (_pullRequestNumber != pullRequestNumber)
        {
            return false;
        }

        return RecordFailure(pullRequestNumber, capability, statusCode);
    }

    public bool ConfirmSuccessfulRefresh(int pullRequestNumber)
    {
        TrackPullRequest(pullRequestNumber);
        if (_deniedCapabilities == PullRequestDeniedCapability.None)
        {
            return false;
        }

        _deniedCapabilities = PullRequestDeniedCapability.None;
        return true;
    }

    public bool IsDenied(PullRequestDeniedCapability capability) =>
        (_deniedCapabilities & capability) != 0;
}
