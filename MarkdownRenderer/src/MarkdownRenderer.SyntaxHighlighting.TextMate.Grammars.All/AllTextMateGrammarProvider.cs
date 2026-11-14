using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Engine;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;

namespace MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.All;

/// <summary>TextMate provider exposing every grammar in TextMateSharp.Grammars.</summary>
public sealed class AllTextMateGrammarProvider : ITextMateGrammarProvider, IDisposable
{
    private readonly TextMateSharpGrammarProvider _provider;

    /// <summary>Creates a provider using the default resource limits.</summary>
    public AllTextMateGrammarProvider()
        : this(options: null)
    {
    }

    /// <summary>Creates a provider using explicit resource limits.</summary>
    public AllTextMateGrammarProvider(TextMateHighlighterOptions? options)
    {
        _provider = new TextMateSharpGrammarProvider(
            revision: 20_003_002,
            registryOptionsFactory: CreateRegistryOptions,
            scopeResolver: ResolveScope,
            options: options);
    }

    /// <inheritdoc />
    public int Revision => _provider.Revision;

    /// <inheritdoc />
    public ValueTask<TextMateGrammarHighlightResult?> HighlightAsync(
        TextMateGrammarHighlightRequest request,
        CancellationToken cancellationToken) =>
        _provider.HighlightAsync(request, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _provider.Dispose();

    private static IRegistryOptions CreateRegistryOptions(TextMateGrammarThemeVariant variant) =>
        new RegistryOptions(
            variant == TextMateGrammarThemeVariant.Light
                ? ThemeName.LightPlus
                : ThemeName.DarkPlus);

    private static string? ResolveScope(IRegistryOptions options, string languageId)
    {
        if (options is not RegistryOptions bundledOptions)
            return null;

        try
        {
            var scope = bundledOptions.GetScopeByLanguageId(languageId);
            if (!string.IsNullOrWhiteSpace(scope))
                return scope;
        }
        catch
        {
        }

        try
        {
            var scope = bundledOptions.GetScopeByExtension("." + languageId);
            return string.IsNullOrWhiteSpace(scope) ? null : scope;
        }
        catch
        {
            return null;
        }
    }
}
