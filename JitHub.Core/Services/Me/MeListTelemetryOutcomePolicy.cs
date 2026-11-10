using System;

namespace JitHub.Services;

public static class MeListTelemetryOutcomePolicy
{
    public static string ForCompletedLoad(
        bool identityRefreshFailed,
        PagedDataCompleteness completeness) =>
        identityRefreshFailed
        || completeness is PagedDataCompleteness.Partial or PagedDataCompleteness.ApiLimited
            ? TelemetryTaxonomy.Results.Partial
            : TelemetryTaxonomy.Results.Success;

    public static string ForException(Exception exception) => exception switch
    {
        OperationCanceledException => TelemetryTaxonomy.Results.Cancelled,
        GitHubAuthenticationException => TelemetryTaxonomy.Results.AuthError,
        _ => TelemetryTaxonomy.Results.Error
    };
}
