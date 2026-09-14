using System;
using MarkdownRenderer;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Math;
using MarkdownRenderer.Mermaid;
using MarkdownRenderer.SyntaxHighlighting.TextMate;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common;

namespace JitHub.Services.Markdown;

/// <summary>
/// Owns the immutable Markdown services shared by every JitHub document surface.
/// The expensive Mermaid and TextMate caches are process-wide and bounded by
/// their renderer defaults instead of being duplicated for every comment or card.
/// </summary>
internal static class JitHubMarkdownRuntime
{
    private static readonly object Gate = new();
    private static MarkdownEngine? _engine;
    private static TextMateCodeBlockSyntaxHighlighter? _codeHighlighter;
    private static bool _isShuttingDown;

    internal static MarkdownEngine Engine
    {
        get
        {
            lock (Gate)
            {
                ObjectDisposedException.ThrowIf(_isShuttingDown, typeof(JitHubMarkdownRuntime));
                return _engine ??= new MarkdownEngineBuilder()
                    .UseGitHubReadme()
                    .UseMathematics()
                    .UseMermaid()
                    .Build();
            }
        }
    }

    internal static TextMateCodeBlockSyntaxHighlighter CodeHighlighter
    {
        get
        {
            lock (Gate)
            {
                ObjectDisposedException.ThrowIf(_isShuttingDown, typeof(JitHubMarkdownRuntime));
                return _codeHighlighter ??= new TextMateCodeBlockSyntaxHighlighter(
                    new CommonTextMateGrammarProvider(),
                    options: null,
                    ownsProvider: true);
            }
        }
    }

    /// <summary>Stops new work and releases shared native and grammar resources once.</summary>
    internal static void Shutdown()
    {
        MarkdownEngine? engine;
        TextMateCodeBlockSyntaxHighlighter? codeHighlighter;
        lock (Gate)
        {
            if (_isShuttingDown)
            {
                return;
            }

            _isShuttingDown = true;
            engine = _engine;
            _engine = null;
            codeHighlighter = _codeHighlighter;
            _codeHighlighter = null;
        }

        // Both owners defer their final provider/native release until admitted
        // callbacks retire, so shutdown does not race active parse/highlight work.
        codeHighlighter?.Dispose();
        engine?.Dispose();
    }
}
