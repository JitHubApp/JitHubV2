using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using MarkdownRenderer.CodeBlocks;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using Windows.UI;

namespace MarkdownRenderer.SyntaxHighlighting.TextMate;

/// <summary>
/// Syntax highlighter backed by the grammars and VS Code-style themes bundled
/// by <c>TextMateSharp.Grammars</c>.
/// </summary>
public sealed class TextMateCodeBlockSyntaxHighlighter : ICodeBlockSyntaxHighlighter
{
    private readonly object _gate = new();
    private readonly Dictionary<CodeBlockThemeVariant, ThemeState> _states = new();

    /// <inheritdoc />
    public int Revision { get; init; }

    /// <inheritdoc />
    public ValueTask<CodeBlockHighlightResult?> HighlightAsync(CodeBlockHighlightRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        request.CancellationToken.ThrowIfCancellationRequested();

        if (request.ThemeVariant == CodeBlockThemeVariant.HighContrast)
            return ValueTask.FromResult<CodeBlockHighlightResult?>(CodeBlockHighlightResult.Empty);

        var languageId = NormalizeLanguageId(request.Language);
        if (languageId is null)
            return ValueTask.FromResult<CodeBlockHighlightResult?>(CodeBlockHighlightResult.Empty);

        lock (_gate)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            var state = GetState(request.ThemeVariant);
            var scope = ResolveScope(state.Options, languageId);
            if (string.IsNullOrWhiteSpace(scope))
                return ValueTask.FromResult<CodeBlockHighlightResult?>(CodeBlockHighlightResult.Empty);

            var grammar = state.Registry.LoadGrammar(scope);
            if (grammar is null)
                return ValueTask.FromResult<CodeBlockHighlightResult?>(CodeBlockHighlightResult.Empty);

            var spans = Tokenize(request.Code, grammar, state.Registry.GetTheme(), request.CancellationToken);
            return ValueTask.FromResult<CodeBlockHighlightResult?>(new CodeBlockHighlightResult(spans));
        }
    }

    private ThemeState GetState(CodeBlockThemeVariant variant)
    {
        if (_states.TryGetValue(variant, out var state))
            return state;

        var themeName = variant == CodeBlockThemeVariant.Light ? ThemeName.LightPlus : ThemeName.DarkPlus;
        var options = new RegistryOptions(themeName);
        var registry = new Registry(options);
        state = new ThemeState(options, registry);
        _states[variant] = state;
        return state;
    }

    private static IReadOnlyList<CodeBlockHighlightSpan> Tokenize(
        string code,
        IGrammar grammar,
        TextMateSharp.Themes.Theme theme,
        System.Threading.CancellationToken cancellationToken)
    {
        var spans = new List<CodeBlockHighlightSpan>();
        IStateStack? state = null;

        foreach (var line in EnumerateLines(code))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = state is null
                ? grammar.TokenizeLine(line.Text)
                : grammar.TokenizeLine(line.Text, state, TimeSpan.FromMilliseconds(200));
            state = result.RuleStack;

            foreach (var token in result.Tokens)
            {
                int length = token.Length;
                if (length <= 0)
                    continue;

                var color = ResolveColor(theme, token.Scopes);
                if (color is null)
                    continue;

                spans.Add(new CodeBlockHighlightSpan(line.Offset + token.StartIndex, length, color.Value));
            }
        }

        return spans;
    }

    private static Color? ResolveColor(TextMateSharp.Themes.Theme theme, IList<string> scopes)
    {
        var rule = theme.Match(scopes).LastOrDefault(r => r.foreground != 0);
        if (rule is null)
            return null;

        var hex = theme.GetColor(rule.foreground);
        return TryParseHexColor(hex, out var color) ? color : null;
    }

    private static bool TryParseHexColor(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex))
            return false;

        var value = hex.Trim();
        if (value.StartsWith("#", StringComparison.Ordinal))
            value = value.Substring(1);

        if (value.Length == 6 &&
            byte.TryParse(value.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
            byte.TryParse(value.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
            byte.TryParse(value.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            color = Color.FromArgb(0xFF, r, g, b);
            return true;
        }

        if (value.Length == 8 &&
            byte.TryParse(value.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a) &&
            byte.TryParse(value.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r8) &&
            byte.TryParse(value.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g8) &&
            byte.TryParse(value.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b8))
        {
            color = Color.FromArgb(a, r8, g8, b8);
            return true;
        }

        return false;
    }

    private static string? ResolveScope(RegistryOptions options, string languageId)
    {
        try
        {
            var scope = options.GetScopeByLanguageId(languageId);
            if (!string.IsNullOrWhiteSpace(scope))
                return scope;
        }
        catch
        {
        }

        try
        {
            var scope = options.GetScopeByExtension("." + languageId);
            return string.IsNullOrWhiteSpace(scope) ? null : scope;
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeLanguageId(string? language)
    {
        var value = language?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value switch
        {
            "cs" or "csharp" => "csharp",
            "js" or "javascript" => "javascript",
            "jsx" => "javascriptreact",
            "ts" or "typescript" => "typescript",
            "tsx" => "typescriptreact",
            "py" or "python" => "python",
            "ps" or "ps1" or "pwsh" or "powershell" => "powershell",
            "bash" or "sh" or "shell" or "shellscript" => "shellscript",
            "md" or "markdown" => "markdown",
            "yml" or "yaml" => "yaml",
            _ => value,
        };
    }

    private static IEnumerable<(string Text, int Offset)> EnumerateLines(string code)
    {
        if (code.Length == 0)
        {
            yield return (string.Empty, 0);
            yield break;
        }

        int offset = 0;
        while (offset < code.Length)
        {
            int newline = code.IndexOfAny(['\r', '\n'], offset);
            if (newline < 0)
            {
                yield return (code.Substring(offset), offset);
                yield break;
            }

            int end = newline;
            yield return (code.Substring(offset, end - offset), offset);
            offset = newline + 1;
            if (code[newline] == '\r' && offset < code.Length && code[offset] == '\n')
                offset++;
        }
    }

    private sealed record ThemeState(RegistryOptions Options, Registry Registry);
}
