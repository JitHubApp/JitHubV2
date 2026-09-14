using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Hosting;
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
    private readonly Dictionary<HighlightKey, InFlightHighlight> _inFlight = new();
    private readonly Dictionary<HighlightKey, LinkedListNode<CacheEntry>> _cache = new();
    private readonly LinkedList<CacheEntry> _cacheLru = new();
    private long _cachedBytes;
    private int _activeProviderOperations;
    private bool _disposed;
    private bool _providerDisposed;

    /// <summary>
    /// Creates a highlighter by discovering an installed grammar pack. The
    /// discovered provider is owned by this highlighter and is disposed when
    /// the highlighter is disposed. Explicit provider construction is required
    /// for trimming and NativeAOT.
    /// </summary>
    [Obsolete(
        "Construct a grammar provider explicitly, then pass it to TextMateCodeBlockSyntaxHighlighter. " +
        "This compatibility constructor owns the discovered provider; dispose the highlighter.")]
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(
        "Default TextMate grammar discovery loads optional provider types by name. " +
        "Trimmed and NativeAOT applications must construct and pass an explicit provider.")]
    public TextMateCodeBlockSyntaxHighlighter()
        : this(TextMateGrammarProviderFactory.CreateDefault(), options: null, ownsProvider: true)
    {
    }

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
    public ValueTask<CodeBlockHighlightResult?> HighlightAsync(CodeBlockHighlightRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return HighlightAsync(request, request.CancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<CodeBlockHighlightResult?> HighlightAsync(
        CodeBlockHighlightRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CancellationToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            return null;

        CancellationTokenSource? linkedCancellation = null;
        CancellationToken effectiveCancellation = cancellationToken;
        if (!effectiveCancellation.CanBeCanceled)
        {
            effectiveCancellation = request.CancellationToken;
        }
        else if (request.CancellationToken.CanBeCanceled &&
                 request.CancellationToken != cancellationToken)
        {
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                request.CancellationToken,
                cancellationToken);
            effectiveCancellation = linkedCancellation.Token;
        }

        try
        {
            if (request.ThemeVariant == CodeBlockThemeVariant.HighContrast)
                return CodeBlockHighlightResult.Empty;

            var languageId = NormalizeLanguageId(request.Language);
            if (languageId is null)
                return CodeBlockHighlightResult.Empty;
            if (!IsWithinInputBudget(request.Code))
                return CodeBlockHighlightResult.Empty;

            var providerRequest = new TextMateGrammarHighlightRequest(
                languageId,
                request.Code,
                request.ThemeVariant == CodeBlockThemeVariant.Light
                    ? TextMateGrammarThemeVariant.Light
                    : TextMateGrammarThemeVariant.Dark);

            var result = await GetOrCreateHighlightAsync(providerRequest, effectiveCancellation)
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
        finally
        {
            linkedCancellation?.Dispose();
        }
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
        var key = new HighlightKey(
            request.LanguageId,
            request.Code,
            request.ThemeVariant,
            providerRevision);
        InFlightHighlight operation;

        lock (_coordinationGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (TryGetCachedResult(key, out var cached))
                return cached;

            if (!_inFlight.TryGetValue(key, out operation!))
            {
                operation = new InFlightHighlight();
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
        HighlightKey key,
        TextMateGrammarHighlightRequest request,
        InFlightHighlight operation)
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
        HighlightKey key,
        InFlightHighlight operation,
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
        List<InFlightHighlight>? operations = null;
        bool disposeProvider;
        lock (_coordinationGate)
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_inFlight.Count > 0)
                operations = new List<InFlightHighlight>(_inFlight.Values);
            _inFlight.Clear();
            _cache.Clear();
            _cacheLru.Clear();
            _cachedBytes = 0;
            disposeProvider = _activeProviderOperations == 0 && MarkProviderDisposedUnderLock();
        }

        if (operations is not null)
        {
            foreach (InFlightHighlight operation in operations)
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

    private void ReleaseWaiter(HighlightKey key, InFlightHighlight operation)
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
        HighlightKey key,
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

    private void CacheResult(HighlightKey key, TextMateGrammarHighlightResult result)
    {
        if (_options.CacheBudgetBytes == 0)
            return;

        long weight = EstimateCacheWeight(key, result);
        if (weight > _options.CacheBudgetBytes)
            return;

        var entry = new CacheEntry(key, result, weight);
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
        HighlightKey key,
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

    private readonly struct HighlightKey : IEquatable<HighlightKey>
    {
        private readonly int _hashCode;

        public HighlightKey(
            string languageId,
            string code,
            TextMateGrammarThemeVariant themeVariant,
            int revision)
        {
            LanguageId = languageId;
            Code = code;
            ThemeVariant = themeVariant;
            Revision = revision;
            _hashCode = HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(languageId),
                StringComparer.Ordinal.GetHashCode(code),
                themeVariant,
                revision);
        }

        public string LanguageId { get; }

        public string Code { get; }

        public TextMateGrammarThemeVariant ThemeVariant { get; }

        public int Revision { get; }

        public bool Equals(HighlightKey other) =>
            _hashCode == other._hashCode &&
            Revision == other.Revision &&
            ThemeVariant == other.ThemeVariant &&
            string.Equals(LanguageId, other.LanguageId, StringComparison.Ordinal) &&
            string.Equals(Code, other.Code, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is HighlightKey other && Equals(other);

        public override int GetHashCode() => _hashCode;
    }

    private sealed class InFlightHighlight
    {
        private readonly object _cancellationGate = new();
        private Task? _cancellationTask;
        private bool _disposeCancellationRequested;
        private bool _cancellationDisposed;

        public CancellationTokenSource WorkCancellation { get; } = new();

        public Task<TextMateGrammarHighlightResult?> Task { get; set; } = null!;

        public int WaiterCount { get; set; }

        public void RequestCancellation()
        {
            lock (_cancellationGate)
            {
                if (_disposeCancellationRequested || _cancellationDisposed || _cancellationTask is not null)
                    return;

                _cancellationTask = WorkCancellation.CancelAsync();
                _ = _cancellationTask.ContinueWith(
                    static completed => _ = completed.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            }
        }

        public async ValueTask DisposeCancellationAsync()
        {
            Task? cancellationTask;
            lock (_cancellationGate)
            {
                if (_cancellationDisposed)
                    return;

                _disposeCancellationRequested = true;
                cancellationTask = _cancellationTask;
            }

            if (cancellationTask is not null)
            {
                try
                {
                    await cancellationTask.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Cancellation is best-effort optional work. The fault is
                    // observed here and must not replace the provider result.
                }
            }

            lock (_cancellationGate)
            {
                if (_cancellationDisposed)
                    return;

                WorkCancellation.Dispose();
                _cancellationDisposed = true;
            }
        }
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

    private sealed record CacheEntry(
        HighlightKey Key,
        TextMateGrammarHighlightResult Result,
        long Weight);
}
