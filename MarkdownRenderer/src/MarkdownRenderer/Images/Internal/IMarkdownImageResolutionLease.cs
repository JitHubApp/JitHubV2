using System;

namespace MarkdownRenderer.Images.Internal;

/// <summary>
/// Engine-neutral, consumer-owned source lifetime carried across the optional
/// preparation boundary. The base renderer never owns the shared resolver.
/// </summary>
internal interface IMarkdownImageResolutionLease : IDisposable
{
    MarkdownImageResolution Resolution { get; }
    IMarkdownImageResolutionLease Retain();
}
