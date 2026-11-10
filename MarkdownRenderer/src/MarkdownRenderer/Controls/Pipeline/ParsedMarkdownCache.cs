using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Parsing.Internal;

namespace MarkdownRenderer.Controls.Pipeline;

/// <summary>
/// The control's most recent legacy (registry-based) parse, reused while the
/// source text and registry revision are unchanged. Reads and writes are
/// guarded because the performance session may parse off the UI thread.
/// </summary>
internal sealed class ParsedMarkdownCache
{
    private readonly object _gate = new();
    private ParsedMarkdown? _parsed;
    private string? _source;
    private MarkdownExtensionRegistry? _registry;
    private int _registryRevision = -1;

    /// <summary>
    /// Returns the cached parse when <paramref name="source"/> and the
    /// registry revision match it; otherwise parses on the pool and caches the
    /// result. Returns null when cancelled.
    /// </summary>
    internal async Task<ParsedMarkdown?> GetAsync(
        string source,
        MarkdownExtensionRegistry registry,
        CancellationToken ct)
    {
        var normalizedSource = ForgivingDataUriFixer.Fix(source ?? string.Empty);
        int registryRevision = registry.Revision;
        lock (_gate)
        {
            if (_parsed is not null &&
                ReferenceEquals(_registry, registry) &&
                _registryRevision == registryRevision &&
                string.Equals(_source, normalizedSource, StringComparison.Ordinal))
            {
                return _parsed;
            }
        }

        if (ct.IsCancellationRequested)
            return null;

        var pipeline = registry.BuildPipeline();
        var parser = new MarkdigParser(pipeline);
        var parsed = await parser.ParseAsync(normalizedSource, ct).ConfigureAwait(true);
        if (parsed is null || ct.IsCancellationRequested)
            return null;

        lock (_gate)
        {
            _parsed = parsed;
            _source = parsed.SourceText;
            _registry = registry;
            _registryRevision = registryRevision;
        }

        return parsed;
    }
}
