using System;

namespace JitHub.Markdown.Git;

/// <summary>Recognizes full Git object ids, which name immutable commits and trees.</summary>
public static class GitObjectIdPolicy
{
    /// <summary>
    /// Returns whether <paramref name="value"/> is a full SHA-1 (40) or SHA-256 (64)
    /// hexadecimal object id rather than a branch, tag or abbreviated id.
    /// </summary>
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
}
