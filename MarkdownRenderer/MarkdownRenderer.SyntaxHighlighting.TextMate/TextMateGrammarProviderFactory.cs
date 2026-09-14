using System;
using System.Diagnostics.CodeAnalysis;

namespace MarkdownRenderer.SyntaxHighlighting.TextMate;

/// <summary>Locates an installed TextMate grammar pack for compatibility scenarios.</summary>
public static class TextMateGrammarProviderFactory
{
    private static readonly (string AssemblyName, string TypeName)[] KnownProviders =
    [
        (
            "MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common",
            "MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common.CommonTextMateGrammarProvider"),
        (
            "MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.All",
            "MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.All.AllTextMateGrammarProvider"),
    ];

    /// <summary>
    /// Tries to create the provider from an installed Common or All grammar pack.
    /// Explicitly constructing a pack provider is recommended for trimming and NativeAOT.
    /// </summary>
    [RequiresUnreferencedCode(
        "Default TextMate grammar discovery loads optional provider types by name. " +
        "Trimmed and NativeAOT applications must construct and pass an explicit provider.")]
    public static ITextMateGrammarProvider? TryCreateDefault()
    {
        foreach (var (assemblyName, typeName) in KnownProviders)
        {
            try
            {
                var type = Type.GetType($"{typeName}, {assemblyName}", throwOnError: false);
                if (type is not null &&
                    typeof(ITextMateGrammarProvider).IsAssignableFrom(type) &&
                    Activator.CreateInstance(type) is ITextMateGrammarProvider provider)
                {
                    return provider;
                }
            }
            catch (Exception exception) when (
                exception is TypeLoadException or
                System.IO.FileNotFoundException or
                System.IO.FileLoadException or
                MissingMethodException)
            {
                // An optional grammar pack is not present or cannot be loaded.
            }
        }

        return null;
    }

    /// <summary>Creates an installed provider or throws a package guidance error.</summary>
    [RequiresUnreferencedCode(
        "Default TextMate grammar discovery loads optional provider types by name. " +
        "Trimmed and NativeAOT applications must construct and pass an explicit provider.")]
    public static ITextMateGrammarProvider CreateDefault()
    {
        return TryCreateDefault() ?? throw new InvalidOperationException(
            "No TextMate grammar provider is installed. Reference " +
            "MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common or " +
            "MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.All, then pass its provider " +
            "to TextMateCodeBlockSyntaxHighlighter. Explicit construction is required by " +
            "trimmed and NativeAOT applications.");
    }
}
