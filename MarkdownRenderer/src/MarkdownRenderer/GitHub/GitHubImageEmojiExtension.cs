using System;
using Markdig;
using Markdig.Extensions.Emoji;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Parsing;

namespace MarkdownRenderer.GitHub;

/// <summary>
/// Adds GitHub's image-backed emoji aliases without replacing Markdig's
/// Unicode emoji mapping. The aliases are static GitHub syntax, so resolving
/// them never requires a startup network request or generated reflection data.
/// </summary>
internal sealed class GitHubImageEmojiExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.InlineParsers.Contains<GitHubImageEmojiParser>())
        {
            pipeline.InlineParsers.InsertBefore<EmojiParser>(new GitHubImageEmojiParser());
        }
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
    }
}

internal sealed class GitHubImageEmojiParser : InlineParser
{
    private const int EmojiSize = 20;
    private const string AssetPrefix = "https://github.githubassets.com/images/icons/emoji/";

    // GitHub's image-backed aliases from its public emoji catalog. Unicode
    // aliases remain owned by Markdig and render through the platform color font.
    private static readonly string[] ImageAliases =
    [
        "accessibility",
        "atom",
        "basecamp",
        "basecampy",
        "bowtie",
        "copilot",
        "dependabot",
        "electron",
        "feelsgood",
        "finnadie",
        "fishsticks",
        "goberserk",
        "godmode",
        "hurtrealbad",
        "neckbeard",
        "octocat",
        "rage1",
        "rage2",
        "rage3",
        "rage4",
        "shipit",
        "suspect",
        "trollface",
    ];

    internal GitHubImageEmojiParser()
    {
        OpeningCharacters = [':'];
    }

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        string text = slice.Text;
        int tokenStart = slice.Start;
        if (tokenStart < 0 || tokenStart >= text.Length || text[tokenStart] != ':' ||
            !IsBoundaryBefore(text, tokenStart))
        {
            return false;
        }

        int closingColon = text.IndexOf(':', tokenStart + 1);
        if (closingColon < 0 || closingColon - tokenStart > 32 ||
            !IsBoundaryAfter(text, closingColon + 1))
        {
            return false;
        }

        ReadOnlySpan<char> candidate = text.AsSpan(tokenStart + 1, closingColon - tokenStart - 1);
        string? alias = FindAlias(candidate);
        if (alias is null)
            return false;

        int tokenLength = closingColon - tokenStart + 1;
        int sourceStart = processor.GetSourcePosition(tokenStart, out int line, out int column);
        var sourceSpan = new Markdig.Syntax.SourceSpan(sourceStart, sourceStart + tokenLength - 1);
        string token = text.Substring(tokenStart, tokenLength);
        var image = new SizedImageLinkInline
        {
            Url = $"{AssetPrefix}{alias}.png",
            IsImage = true,
            IsClosed = true,
            Label = token,
            Span = sourceSpan,
            Line = line,
            Column = column,
            RequestedWidth = new SafeHtmlLength(EmojiSize, IsPercent: false),
            RequestedHeight = new SafeHtmlLength(EmojiSize, IsPercent: false),
        };
        image.AppendChild(new LiteralInline(token)
        {
            IsClosed = true,
            Span = sourceSpan,
            Line = line,
            Column = column,
        });

        processor.Inline = image;
        slice.Start += tokenLength;
        return true;
    }

    private static string? FindAlias(ReadOnlySpan<char> candidate)
    {
        foreach (string alias in ImageAliases)
        {
            if (candidate.Equals(alias, StringComparison.Ordinal))
                return alias;
        }

        return null;
    }

    private static bool IsBoundaryBefore(string text, int index)
        => index == 0 || !IsIdentifierCharacter(text[index - 1]);

    private static bool IsBoundaryAfter(string text, int index)
        => index >= text.Length || !IsIdentifierCharacter(text[index]);

    private static bool IsIdentifierCharacter(char value)
        => char.IsLetterOrDigit(value) || value == '_';
}
