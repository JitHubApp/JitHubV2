using System;

namespace JitHub.Markdown.Images.Repository;

public readonly record struct GitHubMarkdownImageReference(
    string Owner,
    string Repository,
    string Ref,
    string Path,
    Uri SourceUri);
