using System;
using System.Net;

namespace JitHub.Services;

[Flags]
public enum IssueDeniedCapability
{
    None = 0,
    Edit = 1,
    Metadata = 2,
    State = 4,
    Comment = 8,
    Reaction = 16,
    Create = 32
}

public readonly record struct IssueCapabilityTarget(
    string RepositoryIdentity,
    int IssueNumber,
    long Generation);

public sealed class IssueCapabilityDenialState
{
    private const IssueDeniedCapability RepositoryCapabilities = IssueDeniedCapability.Create;
    private string _repositoryIdentity = string.Empty;
    private int _issueNumber;
    private IssueDeniedCapability _repositoryDenials;
    private IssueDeniedCapability _issueDenials;
    private long _generation;

    public string RepositoryIdentity => _repositoryIdentity;

    public int IssueNumber => _issueNumber;

    public IssueDeniedCapability DeniedCapabilities => _repositoryDenials | _issueDenials;

    public bool HasDenials => DeniedCapabilities != IssueDeniedCapability.None;

    public IssueCapabilityTarget CaptureTarget() => new(
        _repositoryIdentity,
        _issueNumber,
        _generation);

    public bool IsCurrent(IssueCapabilityTarget target) =>
        target.Generation == _generation &&
        target.IssueNumber == _issueNumber &&
        string.Equals(target.RepositoryIdentity, _repositoryIdentity, StringComparison.Ordinal);

    public void TrackTarget(string? repositoryIdentity, int issueNumber)
    {
        string normalizedRepositoryIdentity = NormalizeRepositoryIdentity(repositoryIdentity);
        if (!string.Equals(_repositoryIdentity, normalizedRepositoryIdentity, StringComparison.Ordinal))
        {
            _generation++;
            _repositoryIdentity = normalizedRepositoryIdentity;
            _repositoryDenials = IssueDeniedCapability.None;
            _issueDenials = IssueDeniedCapability.None;
            _issueNumber = issueNumber;
            return;
        }

        TrackIssue(issueNumber);
    }

    public void TrackIssue(int issueNumber)
    {
        if (_issueNumber == issueNumber)
        {
            return;
        }

        _generation++;
        _issueNumber = issueNumber;
        _issueDenials = IssueDeniedCapability.None;
    }

    public bool RecordFailure(int issueNumber, IssueDeniedCapability capability, HttpStatusCode statusCode)
    {
        TrackIssue(issueNumber);
        if (statusCode != HttpStatusCode.Forbidden)
        {
            return false;
        }

        IssueDeniedCapability repositoryCapability = capability & RepositoryCapabilities;
        IssueDeniedCapability issueCapability = capability & ~RepositoryCapabilities;
        _repositoryDenials |= repositoryCapability;
        _issueDenials |= issueCapability;
        return true;
    }

    public bool RecordRepositoryFailureForCurrent(
        string? repositoryIdentity,
        IssueDeniedCapability capability,
        HttpStatusCode statusCode)
    {
        if (!string.Equals(
                _repositoryIdentity,
                NormalizeRepositoryIdentity(repositoryIdentity),
                StringComparison.Ordinal) ||
            statusCode != HttpStatusCode.Forbidden)
        {
            return false;
        }

        IssueDeniedCapability repositoryCapability = capability & RepositoryCapabilities;
        if (repositoryCapability == IssueDeniedCapability.None)
        {
            return false;
        }

        _repositoryDenials |= repositoryCapability;
        return true;
    }

    public bool RecordRepositoryFailureForCurrent(
        IssueCapabilityTarget target,
        IssueDeniedCapability capability,
        HttpStatusCode statusCode)
    {
        if (!IsCurrent(target))
        {
            return false;
        }

        return RecordRepositoryFailureForCurrent(target.RepositoryIdentity, capability, statusCode);
    }

    public bool RecordFailureForCurrent(
        int issueNumber,
        IssueDeniedCapability capability,
        HttpStatusCode statusCode)
    {
        if (_issueNumber != issueNumber)
        {
            return false;
        }

        return RecordFailure(issueNumber, capability, statusCode);
    }

    public bool RecordFailureForCurrent(
        IssueCapabilityTarget target,
        IssueDeniedCapability capability,
        HttpStatusCode statusCode)
    {
        if (!IsCurrent(target))
        {
            return false;
        }

        return RecordFailure(target.IssueNumber, capability, statusCode);
    }

    public bool ConfirmAuthoritativeRefresh(string? repositoryIdentity, int issueNumber)
    {
        if (!string.Equals(
                _repositoryIdentity,
                NormalizeRepositoryIdentity(repositoryIdentity),
                StringComparison.Ordinal) ||
            _issueNumber != issueNumber ||
            !HasDenials)
        {
            return false;
        }

        _repositoryDenials = IssueDeniedCapability.None;
        _issueDenials = IssueDeniedCapability.None;
        return true;
    }

    public bool ConfirmAuthoritativeRefresh(IssueCapabilityTarget target, int refreshedIssueNumber)
    {
        if (!IsCurrent(target) || refreshedIssueNumber != target.IssueNumber)
        {
            return false;
        }

        return ConfirmAuthoritativeRefresh(target.RepositoryIdentity, refreshedIssueNumber);
    }

    public bool ConfirmAuthoritativeRepositoryRefresh(string? repositoryIdentity)
    {
        if (!string.Equals(
                _repositoryIdentity,
                NormalizeRepositoryIdentity(repositoryIdentity),
                StringComparison.Ordinal) ||
            _repositoryDenials == IssueDeniedCapability.None)
        {
            return false;
        }

        _repositoryDenials = IssueDeniedCapability.None;
        return true;
    }

    public bool ConfirmAuthoritativeRepositoryRefresh(IssueCapabilityTarget target)
    {
        if (!IsCurrent(target))
        {
            return false;
        }

        return ConfirmAuthoritativeRepositoryRefresh(target.RepositoryIdentity);
    }

    public bool IsDenied(IssueDeniedCapability capability) =>
        (DeniedCapabilities & capability) != 0;

    private static string NormalizeRepositoryIdentity(string? repositoryIdentity) =>
        repositoryIdentity?.Trim().ToUpperInvariant() ?? string.Empty;
}
