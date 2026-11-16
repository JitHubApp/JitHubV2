using System;

namespace MarkdownRenderer.Images;

/// <summary>
/// Host resolution result. A handled result with no asset deliberately prevents the renderer
/// from bypassing host authentication, caching, or content policy through its default loader.
/// </summary>
public readonly record struct MarkdownImageResolution(
    bool IsHandled,
    MarkdownImageAsset? Asset,
    MarkdownImageUnavailableReason UnavailableReason = MarkdownImageUnavailableReason.None)
{
    /// <summary>Allows the renderer to use its built-in source loader.</summary>
    public static MarkdownImageResolution NotHandled => new(false, null);

    /// <summary>Prevents fallback because the host owns the source but could not provide it.</summary>
    public static MarkdownImageResolution Unavailable =>
        new(true, null, MarkdownImageUnavailableReason.Unavailable);

    /// <summary>Prevents fallback and records why the host blocked the source.</summary>
    public static MarkdownImageResolution Blocked(MarkdownImageUnavailableReason reason) =>
        new(true, null, reason == MarkdownImageUnavailableReason.None
            ? MarkdownImageUnavailableReason.Unavailable
            : reason);

    /// <summary>Returns bytes supplied by the host resolver.</summary>
    public static MarkdownImageResolution Resolved(MarkdownImageAsset asset) =>
        new(true, asset ?? throw new ArgumentNullException(nameof(asset)));
}
