namespace JitHub.Services.Markdown;

/// <summary>
/// Fixed, privacy-safe outcome for one raw GitHub image-service operation,
/// which may be fulfilled from cache or include internal transport retries. It intentionally
/// carries no URL, exception message, response body, or credential data.
/// </summary>
internal readonly record struct GitHubRawImageFetchOutcome(
    GitHubRawImageFetchOutcomeKind Kind,
    int? HttpStatusCode = null);
