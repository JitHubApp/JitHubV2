using System;
using System.IO;
using System.Text;

namespace JitHub.Services.Markdown.Automation;

/// <summary>
/// Composes the Markdown a lifecycle-automation host renders: a harness corpus
/// file when one is configured, otherwise the lifecycle fixture prepended to the
/// host's own content.
/// </summary>
internal sealed class MarkdownLifecycleFixtureDocument
{
    private const string Marker = "Markdown host lifecycle fixture";
    private const int MaxCorpusBytes = 4 * 1024 * 1024;

    private readonly string? _targetHost;
    private readonly string? _corpusPath;
    private readonly bool _includeSecurityFixture;
    private readonly Lazy<string> _securityFixture =
        new(MarkdownLifecycleFixtureText.BuildSecurityLifecycleFixtureMarkdown);

    /// <param name="targetHost">The automation id prefix of the only host to change, or <see langword="null"/> for every host.</param>
    /// <param name="corpusPath">A Markdown corpus file that replaces the host's content, if any.</param>
    /// <param name="includeSecurityFixture">Whether to append the security resource-budget fixture.</param>
    public MarkdownLifecycleFixtureDocument(string? targetHost, string? corpusPath, bool includeSecurityFixture)
    {
        _targetHost = targetHost;
        _corpusPath = corpusPath;
        _includeSecurityFixture = includeSecurityFixture;
    }

    /// <summary>Composes the Markdown a host renders.</summary>
    /// <param name="markdown">The host's own Markdown.</param>
    /// <param name="automationId">The host's automation id.</param>
    /// <returns>The Markdown to render.</returns>
    public string Compose(string markdown, string automationId)
    {
        if (!string.IsNullOrWhiteSpace(_targetHost) &&
            !automationId.StartsWith(_targetHost, StringComparison.Ordinal))
        {
            return markdown;
        }

        if (TryReadCorpus(out string corpus))
        {
            return corpus;
        }

        if (markdown.Contains(Marker, StringComparison.Ordinal))
        {
            return markdown;
        }

        string fixture = MarkdownLifecycleFixtureText.BuildLifecycleFixtureMarkdown();
        if (_includeSecurityFixture)
        {
            fixture += _securityFixture.Value;
        }

        return fixture + markdown;
    }

    private bool TryReadCorpus(out string corpus)
    {
        corpus = string.Empty;
        string? path = _corpusPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            string extension = Path.GetExtension(fullPath);
            if (!extension.Equals(".md", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            FileInfo file = new(fullPath);
            if (!file.Exists || file.Length is <= 0 or > MaxCorpusBytes)
            {
                return false;
            }

            corpus = File.ReadAllText(fullPath, Encoding.UTF8);
            return corpus.Length > 0;
        }
        catch (Exception)
        {
            corpus = string.Empty;
            return false;
        }
    }
}
