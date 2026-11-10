using System;
using System.Text;

namespace JitHub.Models.CodeViewer;

public static class RepoCodeAutomation
{
    public static string CreateId(string prefix, string? value)
    {
        string source = value ?? string.Empty;
        var token = new StringBuilder(Math.Min(source.Length, 48));
        uint hash = 2166136261;

        foreach (char character in source)
        {
            hash ^= character;
            hash = unchecked(hash * 16777619);

            if (token.Length >= 48)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                token.Append(character);
            }
            else if (token.Length == 0 || token[^1] != '_')
            {
                token.Append('_');
            }
        }

        string readableToken = token.ToString().Trim('_');
        if (readableToken.Length == 0)
        {
            readableToken = "root";
        }

        return $"{prefix}_{readableToken}_{hash:x8}";
    }
}
