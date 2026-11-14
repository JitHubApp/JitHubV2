using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Internal;
using MarkdownRenderer.Theming;
using Windows.UI;

namespace MarkdownRenderer.SyntaxHighlighting.TextMate;

/// <summary>
/// Adapts an independently packaged TextMate grammar provider to MarkdownRenderer
/// code-block highlighting.
/// </summary>
public sealed class TextMateCodeBlockSyntaxHighlighter : ICodeHighlighter, IDisposable
{
    private readonly ITextMateGrammarProvider _provider;
    private readonly bool _ownsProvider;
    private readonly TextMateHighlighterOptions _options;
    private readonly object _coordinationGate = new();
    private readonly Dictionary<TextMateHighlightKey, TextMateInFlightHighlight> _inFlight = new();
    private readonly Dictionary<TextMateHighlightKey, LinkedListNode<TextMateHighlightCacheEntry>> _cache = new();
    private readonly LinkedList<TextMateHighlightCacheEntry> _cacheLru = new();
    private long _cachedBytes;
    private int _activeProviderOperations;
    private bool _disposed;
    private bool _providerDisposed;

    /// <summary>Creates a highlighter backed by <paramref name="provider"/>.</summary>
    public TextMateCodeBlockSyntaxHighlighter(ITextMateGrammarProvider provider)
        : this(provider, options: null, ownsProvider: false)
    {
    }

    /// <summary>
    /// Creates a highlighter backed by <paramref name="provider"/> with explicit
    /// resource limits.
    /// </summary>
    public TextMateCodeBlockSyntaxHighlighter(
        ITextMateGrammarProvider provider,
        TextMateHighlighterOptions? options)
        : this(provider, options, ownsProvider: false)
    {
    }

    /// <summary>
    /// Creates a highlighter with explicit provider ownership. When
    /// <paramref name="ownsProvider"/> is true, <see cref="Dispose"/> disposes
    /// a disposable provider after all admitted highlights have exited.
    /// </summary>
    public TextMateCodeBlockSyntaxHighlighter(
        ITextMateGrammarProvider provider,
        TextMateHighlighterOptions? options,
        bool ownsProvider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _options = options ?? new TextMateHighlighterOptions();
        _ownsProvider = ownsProvider;
    }

    /// <summary>Gets whether this highlighter owns the configured provider.</summary>
    public bool OwnsProvider => _ownsProvider;

    /// <inheritdoc />
    public int Revision
    {
        get => ReadProviderRevision();
    }

    /// <inheritdoc />
    public async ValueTask<CodeBlockHighlightResult?> HighlightAsync(
        CodeBlockHighlightRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cancellationToken.IsCancellationRequested)
            return null;

        if (request.ThemeVariant == MarkdownThemeVariant.HighContrast)
            return CodeBlockHighlightResult.Empty;

        var languageId = NormalizeLanguageId(request.Language);
        if (languageId is null)
            return CodeBlockHighlightResult.Empty;
        if (!IsWithinInputBudget(request.Code))
            return CodeBlockHighlightResult.Empty;

        var providerRequest = new TextMateGrammarHighlightRequest(
            languageId,
            request.Code,
            request.ThemeVariant == MarkdownThemeVariant.Light
                ? TextMateGrammarThemeVariant.Light
                : TextMateGrammarThemeVariant.Dark);

        var result = await GetOrCreateHighlightAsync(providerRequest, cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
            return null;
        if (result.Spans.Count == 0)
            return CodeBlockHighlightResult.Empty;

        var spans = new List<CodeBlockHighlightSpan>(result.Spans.Count);
        foreach (var span in result.Spans)
        {
            if (span.Start < 0 || span.Length <= 0 || span.Start > request.Code.Length - span.Length)
                continue;

            var argb = span.ForegroundArgb;
            spans.Add(new CodeBlockHighlightSpan(
                span.Start,
                span.Length,
                Color.FromArgb(
                    (byte)(argb >> 24),
                    (byte)(argb >> 16),
                    (byte)(argb >> 8),
                    (byte)argb)));
        }

        return spans.Count == 0
            ? CodeBlockHighlightResult.Empty
            : new CodeBlockHighlightResult(spans);
    }

    private async ValueTask<TextMateGrammarHighlightResult?> GetOrCreateHighlightAsync(
        TextMateGrammarHighlightRequest request,
        CancellationToken cancellationToken)
    {
        // Provider code is outside our coordination lock. Besides avoiding a
        // global bottleneck for unrelated highlights, this permits providers to
        // invalidate or dispose their owner reentrantly without deadlocking.
        // ReadProviderRevision admits the call into the same lifetime counter as
        // HighlightAsync, so an owned provider cannot be disposed mid-callback.
        int providerRevision = ReadProviderRevision();
        var key = new TextMateHighlightKey(
            request.LanguageId,
            request.Code,
            request.ThemeVariant,
            providerRevision);
        TextMateInFlightHighlight operation;

        lock (_coordinationGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (TryGetCachedResult(key, out var cached))
                return cached;

            if (!_inFlight.TryGetValue(key, out operation!))
            {
                operation = new TextMateInFlightHighlight();
                _inFlight.Add(key, operation);
                _activeProviderOperations++;
                operation.Task = Task.Run(
                    () => RunProviderAsync(key, request, operation),
                    CancellationToken.None);
            }

            operation.WaiterCount++;
        }

        try
        {
            return await operation.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            ReleaseWaiter(key, operation);
        }
    }

    private async Task<TextMateGrammarHighlightResult?> RunProviderAsync(
        TextMateHighlightKey key,
        TextMateGrammarHighlightRequest request,
        TextMateInFlightHighlight operation)
    {
        TextMateGrammarHighlightResult? result;
        try
        {
            result = await _provider
                .HighlightAsync(request, operation.WorkCancellation.Token)
                .ConfigureAwait(false);
            result = SanitizeProviderResult(request.Code.Length, result);
        }
        catch (OperationCanceledException) when (operation.WorkCancellation.IsCancellationRequested)
        {
            result = null;
        }
        catch (Exception)
        {
            // Highlighting is optional. A provider failure must atomically fall
            // back to the original unhighlighted code block.
            result = TextMateGrammarHighlightResult.Empty;
        }

        await CompleteOperationAsync(key, operation, result).ConfigureAwait(false);
        return result;
    }

    private TextMateGrammarHighlightResult? SanitizeProviderResult(
        int codeLength,
        TextMateGrammarHighlightResult? result)
    {
        if (result is null || result.Spans.Count == 0)
            return result;
        if (result.Spans.Count > _options.MaximumSpanCount)
            return TextMateGrammarHighlightResult.Empty;

        var spans = new List<TextMateGrammarHighlightSpan>(result.Spans.Count);
        foreach (var span in result.Spans)
        {
            if (span.Length <= 0 || span.Start < 0 || span.Start > codeLength - span.Length)
                return TextMateGrammarHighlightResult.Empty;

            spans.Add(span);
        }

        if (spans.Count == 0)
            return TextMateGrammarHighlightResult.Empty;

        spans.Sort(static (left, right) =>
        {
            int start = left.Start.CompareTo(right.Start);
            return start != 0 ? start : left.Length.CompareTo(right.Length);
        });

        int previousEnd = 0;
        foreach (var span in spans)
        {
            if (span.Start < previousEnd)
                return TextMateGrammarHighlightResult.Empty;

            previousEnd = span.Start + span.Length;
        }

        return new TextMateGrammarHighlightResult(spans);
    }

    private async Task CompleteOperationAsync(
        TextMateHighlightKey key,
        TextMateInFlightHighlight operation,
        TextMateGrammarHighlightResult? result)
    {
        bool disposeProvider = false;
        lock (_coordinationGate)
        {
            if (_inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, operation))
            {
                _inFlight.Remove(key);
                if (!_disposed && result is not null)
                    CacheResult(key, result);
            }

            _activeProviderOperations--;
            if (_disposed && _activeProviderOperations == 0)
                disposeProvider = MarkProviderDisposedUnderLock();
        }

        await operation.DisposeCancellationAsync().ConfigureAwait(false);
        if (disposeProvider)
            DisposeProvider();
    }

    /// <summary>
    /// Cancels outstanding highlights, clears retained source/result data, and
    /// disposes an owned provider after active provider calls have returned.
    /// </summary>
    public void Dispose()
    {
        List<TextMateInFlightHighlight>? operations = null;
        bool disposeProvider;
        lock (_coordinationGate)
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_inFlight.Count > 0)
                operations = new List<TextMateInFlightHighlight>(_inFlight.Values);
            _inFlight.Clear();
            _cache.Clear();
            _cacheLru.Clear();
            _cachedBytes = 0;
            disposeProvider = _activeProviderOperations == 0 && MarkProviderDisposedUnderLock();
        }

        if (operations is not null)
        {
            foreach (TextMateInFlightHighlight operation in operations)
                operation.RequestCancellation();
        }

        if (disposeProvider)
            DisposeProvider();
    }

    private bool MarkProviderDisposedUnderLock()
    {
        if (!_ownsProvider || _providerDisposed || _provider is not IDisposable)
            return false;

        _providerDisposed = true;
        return true;
    }

    private void DisposeProvider() => ((IDisposable)_provider).Dispose();

    private void ReleaseWaiter(TextMateHighlightKey key, TextMateInFlightHighlight operation)
    {
        bool cancel = false;
        lock (_coordinationGate)
        {
            if (operation.WaiterCount > 0)
                operation.WaiterCount--;

            if (operation.WaiterCount == 0 &&
                !operation.Task.IsCompleted &&
                _inFlight.TryGetValue(key, out var current) &&
                ReferenceEquals(current, operation))
            {
                _inFlight.Remove(key);
                cancel = true;
            }
        }

        if (cancel)
        {
            // A third-party provider may register expensive cancellation
            // callbacks. Never execute those callbacks on the caller (which can
            // be the UI thread).
            operation.RequestCancellation();
        }
    }

    private bool TryGetCachedResult(
        TextMateHighlightKey key,
        out TextMateGrammarHighlightResult result)
    {
        if (!_cache.TryGetValue(key, out var node))
        {
            result = null!;
            return false;
        }

        _cacheLru.Remove(node);
        _cacheLru.AddFirst(node);
        result = node.Value.Result;
        return true;
    }

    private void CacheResult(TextMateHighlightKey key, TextMateGrammarHighlightResult result)
    {
        if (_options.CacheBudgetBytes == 0)
            return;

        long weight = EstimateCacheWeight(key, result);
        if (weight > _options.CacheBudgetBytes)
            return;

        var entry = new TextMateHighlightCacheEntry(key, result, weight);
        var node = _cacheLru.AddFirst(entry);
        _cache.Add(key, node);
        _cachedBytes += weight;

        while (_cachedBytes > _options.CacheBudgetBytes && _cacheLru.Last is { } oldest)
        {
            _cacheLru.RemoveLast();
            _cache.Remove(oldest.Value.Key);
            _cachedBytes -= oldest.Value.Weight;
        }
    }

    private static long EstimateCacheWeight(
        TextMateHighlightKey key,
        TextMateGrammarHighlightResult result) =>
        256L +
        (key.LanguageId.Length * sizeof(char)) +
        (key.Code.Length * sizeof(char)) +
        (result.Spans.Count * 24L);

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

    private static string? NormalizeLanguageId(string? language)
    {
        var value = language?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value switch
        {
            "cs" or "csharp" => "csharp",
            "cc" or "cxx" or "cplusplus" or "c++" => "cpp",
            "cu" or "cuda" => "cuda-cpp",
            "docker" or "dockerfile" => "dockerfile",
            "fs" or "f#" => "fsharp",
            "golang" => "go",
            "js" or "javascript" => "javascript",
            "jsx" => "javascriptreact",
            "ts" or "typescript" => "typescript",
            "tsx" => "typescriptreact",
            "py" or "python" => "python",
            "ps" or "ps1" or "pwsh" or "powershell" => "powershell",
            "rb" => "ruby",
            "rs" => "rust",
            "bash" or "sh" or "shell" or "shellscript" => "shellscript",
            "objc" or "objective-c" => "objectivec",
            "objcpp" or "objective-c++" => "objectivecpp",
            "vbnet" or "visualbasic" => "vb",
            "md" or "markdown" => "markdown",
            "yml" or "yaml" => "yaml",
            _ => value,
        };
    }

    private int ReadProviderRevision()
    {
        lock (_coordinationGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeProviderOperations++;
        }

        try
        {
            return _provider.Revision;
        }
        finally
        {
            bool disposeProvider = false;
            lock (_coordinationGate)
            {
                _activeProviderOperations--;
                if (_disposed && _activeProviderOperations == 0)
                    disposeProvider = MarkProviderDisposedUnderLock();
            }

            if (disposeProvider)
                DisposeProvider();
        }
    }
}
