namespace JitHub.Services.Markdown;

internal enum GitHubRawImageFetchOutcomeKind
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
