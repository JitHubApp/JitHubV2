using System;

namespace JitHub.Services.CodeViewer;

public static class GitReferencePolicy
{
    public static bool IsImmutableObjectId(string? value)
    {
        if (value is null || value.Length is not (40 or 64))
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    public static string CacheResourceFor(string? value) =>
        IsImmutableObjectId(value)
            ? GitHubCachePolicy.ImmutableShaResource
            : GitHubCachePolicy.MutableResource;
}
