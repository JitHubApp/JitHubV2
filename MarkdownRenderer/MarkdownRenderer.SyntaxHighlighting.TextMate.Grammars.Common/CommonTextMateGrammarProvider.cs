using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Internal;

namespace MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common;

/// <summary>
/// TextMate provider restricted to the curated languages commonly encountered in
/// README files, code reviews, issues, and release notes.
/// </summary>
public sealed class CommonTextMateGrammarProvider : ITextMateGrammarProvider, IDisposable
{
    private static readonly string[] Languages =
    [
        "c",
        "cpp",
        "csharp",
        "css",
        "cuda-cpp",
        "diff",
        "dockerfile",
        "fsharp",
        "go",
        "hlsl",
        "html",
        "java",
        "javascript",
        "javascriptreact",
        "json",
        "jsonc",
        "lua",
        "markdown",
        "objectivec",
        "objectivecpp",
        "php",
        "powershell",
        "python",
        "ruby",
        "rust",
        "shaderlab",
        "shellscript",
        "sql",
        "swift",
        "typescript",
        "typescriptreact",
        "vb",
        "xml",
        "xsl",
        "yaml",
    ];

    private readonly BundledTextMateGrammarProvider _provider;

    /// <summary>Creates a provider using the default resource limits.</summary>
    public CommonTextMateGrammarProvider()
        : this(options: null)
    {
    }

    /// <summary>Creates a provider using explicit resource limits.</summary>
    public CommonTextMateGrammarProvider(TextMateHighlighterOptions? options)
    {
        _provider = new BundledTextMateGrammarProvider(
            Languages,
            revision: 20_003_001,
            options ?? new TextMateHighlighterOptions(),
            registryOptionsFactory: variant => new CuratedRegistryOptions(variant),
            scopeResolver: static (registryOptions, languageId) =>
                registryOptions is CuratedRegistryOptions curated
                    ? curated.ResolveScope(languageId)
                    : null);
    }

    /// <summary>Language identifiers enabled by the common profile.</summary>
    public static IReadOnlyList<string> SupportedLanguageIds { get; } = Array.AsReadOnly(Languages);

    /// <inheritdoc />
    public int Revision => _provider.Revision;

    /// <inheritdoc />
    public ValueTask<TextMateGrammarHighlightResult?> HighlightAsync(
        TextMateGrammarHighlightRequest request,
        CancellationToken cancellationToken) =>
        _provider.HighlightAsync(request, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _provider.Dispose();
}
