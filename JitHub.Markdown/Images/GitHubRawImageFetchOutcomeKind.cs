namespace JitHub.Markdown.Images;

/// <summary>The kind of a <see cref="GitHubRawImageFetchOutcome"/>.</summary>
public enum GitHubRawImageFetchOutcomeKind
{
    Success,
    NullResult,
    HttpStatus,
    InvalidPayload,
    CallerCanceled,
    SourceDeferred,
    TransportFailure,
    OtherFailure,
}
