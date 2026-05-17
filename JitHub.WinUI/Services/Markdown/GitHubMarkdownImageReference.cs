using System;

namespace JitHub.Services.Markdown;

public readonly record struct GitHubMarkdownImageReference(
    string Owner,
    string Repository,
    string Ref,
    string Path,
    Uri SourceUri);
