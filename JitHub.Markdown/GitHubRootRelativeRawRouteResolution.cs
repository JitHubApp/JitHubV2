namespace JitHub.Markdown;

/// <summary>The outcome of reading an image source as GitHub's root-relative raw route.</summary>
internal enum GitHubRootRelativeRawRouteResolution
{
    /// <summary>The source does not have the raw-route shape and keeps its ordinary meaning.</summary>
    NotRoute,

    /// <summary>The source is a raw route for the current document's repository and ref.</summary>
    Resolved,

    /// <summary>The source has the raw-route shape but is unsafe or names another repository or ref.</summary>
    Rejected,
}
