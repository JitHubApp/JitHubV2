using JitHub.Markdown;

namespace JitHub.Services.CodeViewer;

public static class GitReferencePolicy
{
    /// <summary>Returns whether the value is a full Git object id; see <see cref="GitObjectIdPolicy"/>.</summary>
    public static bool IsImmutableObjectId(string? value) => GitObjectIdPolicy.IsImmutableObjectId(value);

    public static string CacheResourceFor(string? value) =>
        IsImmutableObjectId(value)
            ? GitHubCachePolicy.ImmutableShaResource
            : GitHubCachePolicy.MutableResource;
}
