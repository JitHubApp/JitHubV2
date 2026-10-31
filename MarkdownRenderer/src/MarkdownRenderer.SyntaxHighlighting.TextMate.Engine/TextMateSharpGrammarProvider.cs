using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.SyntaxHighlighting.TextMate;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;

namespace MarkdownRenderer.SyntaxHighlighting.TextMate.Engine;

/// <summary>
/// A grammar provider that tokenizes with TextMateSharp. The grammar packs
/// build on it; hosts install a pack, such as
/// <c>MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common</c>, rather
/// than using this type directly.
/// </summary>
/// <remarks>
/// This is the only renderer package whose public API names TextMateSharp
/// types, so it is versioned with TextMateSharp. The lean
/// <c>MarkdownRenderer.SyntaxHighlighting.TextMate</c> integration carries no
/// TextMateSharp or Onigwrap payload. Requests run one at a time on a worker,
/// within the input, span and per-line time limits of
/// <see cref="TextMateHighlighterOptions"/>, and stop at line-level
/// cancellation checkpoints.
/// </remarks>
public sealed class TextMateSharpGrammarProvider : ITextMateGrammarProvider, IDisposable
{
    private static readonly TimeSpan MaximumTokenizationSlice = TimeSpan.FromMilliseconds(16);

    private readonly SemaphoreSlim _highlightGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Dictionary<TextMateGrammarThemeVariant, ThemeState> _states = new();
    private readonly HashSet<string>? _allowedLanguageIds;
    private readonly TextMateHighlighterOptions _options;
    private readonly Func<TextMateGrammarThemeVariant, IRegistryOptions> _registryOptionsFactory;
    private readonly Func<IRegistryOptions, string, string?> _scopeResolver;
    private readonly Action<TextMateProviderCheckpoint, int>? _checkpointObserver;
    private volatile bool _disposed;

    /// <summary>Creates a provider over a TextMateSharp grammar registry.</summary>
    /// <param name="revision">
    /// The provider's <see cref="ITextMateGrammarProvider.Revision"/>. Change it whenever the
    /// grammars, themes or scope rules change, so cached highlighting is not reused.
    /// </param>
    /// <param name="registryOptionsFactory">
    /// Creates the registry options for a theme variant. Called at most once per variant.
    /// </param>
    /// <param name="scopeResolver">
    /// Maps a language identifier to a grammar scope name for the given registry options, or
    /// returns <see langword="null"/> when the language is not supported.
    /// </param>
    /// <param name="allowedLanguageIds">
    /// The languages the provider highlights, compared without case, or <see langword="null"/>
    /// for every language the registry resolves.
    /// </param>
    /// <param name="options">Resource limits, or <see langword="null"/> for the defaults.</param>
    public TextMateSharpGrammarProvider(
        int revision,
        Func<TextMateGrammarThemeVariant, IRegistryOptions> registryOptionsFactory,
        Func<IRegistryOptions, string, string?> scopeResolver,
        IEnumerable<string>? allowedLanguageIds = null,
        TextMateHighlighterOptions? options = null)
        : this(revision, registryOptionsFactory, scopeResolver, allowedLanguageIds, options, checkpointObserver: null)
    {
    }

    /// <summary>Creates a provider that reports each cancellation checkpoint, for tests.</summary>
    internal TextMateSharpGrammarProvider(
        int revision,
        Func<TextMateGrammarThemeVariant, IRegistryOptions> registryOptionsFactory,
        Func<IRegistryOptions, string, string?> scopeResolver,
        IEnumerable<string>? allowedLanguageIds,
        TextMateHighlighterOptions? options,
        Action<TextMateProviderCheckpoint, int>? checkpointObserver)
    {
        _allowedLanguageIds = allowedLanguageIds is null
            ? null
            : new HashSet<string>(allowedLanguageIds, StringComparer.OrdinalIgnoreCase);
        Revision = revision;
        _options = options ?? new TextMateHighlighterOptions();
        _registryOptionsFactory = registryOptionsFactory ??
            throw new ArgumentNullException(nameof(registryOptionsFactory));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _checkpointObserver = checkpointObserver;
    }

    /// <inheritdoc />
    public int Revision { get; }

    /// <inheritdoc />
    public async ValueTask<TextMateGrammarHighlightResult?> HighlightAsync(
        TextMateGrammarHighlightRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (cancellationToken.IsCancellationRequested)
            return null;
        if (_allowedLanguageIds is not null && !_allowedLanguageIds.Contains(request.LanguageId))
            return TextMateGrammarHighlightResult.Empty;
        if (!IsWithinInputBudget(request.Code))
            return TextMateGrammarHighlightResult.Empty;

        using var effectiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);

        try
        {
            await _highlightGate.WaitAsync(effectiveCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (effectiveCancellation.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            // TextMateSharp tokenization is synchronous. Keep the entire registry
            // operation on a worker and retain line-level cancellation checkpoints.
            return await Task.Run(
                    () => HighlightCore(request, effectiveCancellation.Token),
                    effectiveCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (effectiveCancellation.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            _highlightGate.Release();
        }
    }

    /// <summary>Cancels outstanding requests; later requests throw <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _lifetimeCancellation.Cancel();
    }

    private TextMateGrammarHighlightResult? HighlightCore(
        TextMateGrammarHighlightRequest request,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return null;

        var state = GetState(request.ThemeVariant);
        var scope = _scopeResolver(state.Options, request.LanguageId);
        if (string.IsNullOrWhiteSpace(scope))
            return TextMateGrammarHighlightResult.Empty;

        _checkpointObserver?.Invoke(TextMateProviderCheckpoint.BeforeGrammarLoad, -1);
        var grammar = state.Registry.LoadGrammar(scope);
        _checkpointObserver?.Invoke(TextMateProviderCheckpoint.AfterGrammarLoad, -1);
        if (cancellationToken.IsCancellationRequested)
            return null;
        if (grammar is null)
            return TextMateGrammarHighlightResult.Empty;

        var spans = Tokenize(
            request.Code,
            grammar,
            state.Registry.GetTheme(),
            cancellationToken,
            _options.MaximumSpanCount,
            _options.MaximumLineProcessingTime,
            _checkpointObserver);
        return spans is null
            ? null
            : spans.Count == 0
                ? TextMateGrammarHighlightResult.Empty
                : new TextMateGrammarHighlightResult(spans);
    }

    private ThemeState GetState(TextMateGrammarThemeVariant variant)
    {
        if (_states.TryGetValue(variant, out var state))
            return state;

        var options = _registryOptionsFactory(variant);
        var registry = new Registry(options);
        state = new ThemeState(options, registry);
        _states[variant] = state;
        return state;
    }

    private static List<TextMateGrammarHighlightSpan>? Tokenize(
        string code,
        IGrammar grammar,
        TextMateSharp.Themes.Theme theme,
        CancellationToken cancellationToken,
        int maximumSpanCount,
        TimeSpan maximumLineProcessingTime,
        Action<TextMateProviderCheckpoint, int>? checkpointObserver)
    {
        var spans = new List<TextMateGrammarHighlightSpan>();
        IStateStack? state = null;

        int lineIndex = 0;
        foreach (var line in EnumerateLines(code))
        {
            if (cancellationToken.IsCancellationRequested)
                return null;

            checkpointObserver?.Invoke(TextMateProviderCheckpoint.BeforeLineTokenization, lineIndex);
            if (cancellationToken.IsCancellationRequested)
                return null;

            ITokenizeLineResult result;
            var lineStopwatch = Stopwatch.StartNew();
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                    return null;

                TimeSpan remaining = maximumLineProcessingTime - lineStopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    return [];

                TimeSpan slice = remaining < MaximumTokenizationSlice
                    ? remaining
                    : MaximumTokenizationSlice;
                result = grammar.TokenizeLine(
                    line.Memory,
                    state ?? StateStack.NULL,
                    slice);
                checkpointObserver?.Invoke(
                    TextMateProviderCheckpoint.AfterLineTokenizationSlice,
                    lineIndex);
                if (cancellationToken.IsCancellationRequested)
                    return null;

                if (result is not TextMateSharp.Internal.Grammars.TokenizeLineResult
                    { StoppedEarly: true })
                {
                    break;
                }
            }

            state = result.RuleStack;

            foreach (var token in result.Tokens)
            {
                if (cancellationToken.IsCancellationRequested)
                    return null;

                // TextMate grammars can expose the synthetic end-of-line
                // sentinel as one extra UTF-16 unit. Never let that internal
                // sentinel escape the provider's exact source-coordinate API.
                int relativeStart = token.StartIndex;
                if (relativeStart < 0 || relativeStart > line.Memory.Length)
                    return [];

                int length = Math.Min(token.Length, line.Memory.Length - relativeStart);
                if (length <= 0)
                    continue;

                var color = ResolveColor(theme, token.Scopes);
                if (color is null)
                    continue;

                if (spans.Count >= maximumSpanCount)
                    return [];

                spans.Add(new TextMateGrammarHighlightSpan(
                    line.Offset + relativeStart,
                    length,
                    color.Value));
            }

            checkpointObserver?.Invoke(TextMateProviderCheckpoint.AfterLineTokenization, lineIndex);
            if (cancellationToken.IsCancellationRequested)
                return null;
            lineIndex++;
        }

        return spans;
    }

    private bool IsWithinInputBudget(string code)
    {
        if (code.Length > _options.MaximumCodeLength)
            return false;

        int lineCount = 1;
        int lineLength = 0;
        for (int index = 0; index < code.Length; index++)
        {
            char character = code[index];
            if (character is not ('\r' or '\n'))
            {
                lineLength++;
                if (lineLength > _options.MaximumLineLength)
                    return false;
                continue;
            }

            lineCount++;
            if (lineCount > _options.MaximumLineCount)
                return false;
            lineLength = 0;

            if (character == '\r' && index + 1 < code.Length && code[index + 1] == '\n')
                index++;
        }

        return true;
    }

    private static uint? ResolveColor(TextMateSharp.Themes.Theme theme, IList<string> scopes)
    {
        TextMateSharp.Themes.ThemeTrieElementRule? rule = null;
        foreach (var candidate in theme.Match(scopes))
        {
            if (candidate.foreground != 0)
                rule = candidate;
        }

        if (rule is null)
            return null;

        return TryParseHexColor(theme.GetColor(rule.foreground), out var color)
            ? color
            : null;
    }

    private static bool TryParseHexColor(string? hex, out uint color)
    {
        color = 0;
        if (string.IsNullOrWhiteSpace(hex))
            return false;

        var value = hex.Trim();
        if (value.StartsWith("#", StringComparison.Ordinal))
            value = value[1..];

        if (value.Length == 6 &&
            uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            color = 0xFF000000u | rgb;
            return true;
        }

        if (value.Length == 8 &&
            uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
        {
            color = argb;
            return true;
        }

        return false;
    }

    private static IEnumerable<(ReadOnlyMemory<char> Memory, int Offset)> EnumerateLines(string code)
    {
        if (code.Length == 0)
        {
            yield return (ReadOnlyMemory<char>.Empty, 0);
            yield break;
        }

        int offset = 0;
        while (offset < code.Length)
        {
            int newline = code.IndexOfAny(['\r', '\n'], offset);
            if (newline < 0)
            {
                yield return (code.AsMemory(offset), offset);
                yield break;
            }

            yield return (code.AsMemory(offset, newline - offset), offset);
            offset = newline + 1;
            if (code[newline] == '\r' && offset < code.Length && code[offset] == '\n')
                offset++;
        }
    }

    private sealed record ThemeState(IRegistryOptions Options, Registry Registry);
}

internal enum TextMateProviderCheckpoint
{
    BeforeGrammarLoad,
    AfterGrammarLoad,
    BeforeLineTokenization,
    AfterLineTokenizationSlice,
    AfterLineTokenization,
}
