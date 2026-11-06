namespace MarkdownRenderer.Images;

/// <summary>
/// Explains why an image resolver deliberately declined to expose image bytes.
/// Hosts can use this value to offer an explicit, privacy-preserving recovery action.
/// </summary>
public enum MarkdownImageUnavailableReason
{
    /// <summary>No unavailable state was reported.</summary>
    None,
    /// <summary>The source could not be resolved or decoded.</summary>
    Unavailable,
    /// <summary>The host's remote-content policy blocked the source.</summary>
    RemoteContentBlocked,
    /// <summary>The source used insecure HTTP.</summary>
    InsecureRemoteContent,
    /// <summary>The host is offline.</summary>
    Offline,
    /// <summary>Automatic loading was suppressed on a metered connection.</summary>
    MeteredConnection,
}
