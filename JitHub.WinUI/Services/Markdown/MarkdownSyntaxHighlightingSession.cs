using System;

namespace JitHub.Services.Markdown;

internal readonly record struct MarkdownSyntaxHighlightingState<TProvider>(
    TProvider? Provider,
    bool IsEnabled)
    where TProvider : class, IDisposable;

/// <summary>
/// Owns a lazily-created markdown syntax highlighter across enable/disable
/// toggles and releases it when the renderer host is torn down.
/// </summary>
internal sealed class MarkdownSyntaxHighlightingSession<TProvider>
    where TProvider : class, IDisposable
{
    private readonly Func<TProvider> _factory;
    private TProvider? _provider;

    internal MarkdownSyntaxHighlightingSession(Func<TProvider> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    internal MarkdownSyntaxHighlightingState<TProvider> Apply(bool isEnabled)
    {
        if (isEnabled)
            _provider ??= _factory();

        return new MarkdownSyntaxHighlightingState<TProvider>(_provider, isEnabled);
    }

    internal void Reset()
    {
        TProvider? provider = _provider;
        _provider = null;
        provider?.Dispose();
    }
}
