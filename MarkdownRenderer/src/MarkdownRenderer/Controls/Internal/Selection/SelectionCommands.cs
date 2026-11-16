using System;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Document;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;

namespace MarkdownRenderer.Controls.Internal.Selection;

/// <summary>
/// Runs the copy commands for the active selection. A host command for the
/// same kind replaces the built-in clipboard write.
/// </summary>
internal sealed class SelectionCommands
{
    private readonly ISelectionCommandsHost _host;
    private readonly PublishedLayout _published;
    private readonly SelectionController _selection;

    internal SelectionCommands(
        ISelectionCommandsHost host,
        PublishedLayout published,
        SelectionController selection)
    {
        _host = host;
        _published = published;
        _selection = selection;
    }

    /// <summary>
    /// Copies the active selection to the clipboard and raises copy completion.
    /// </summary>
    /// <returns>True when a selection was copied successfully.</returns>
    internal bool CopyToClipboard(MarkdownCopyOptions? options = null)
    {
        var snapshot = _published.Snapshot;
        if (snapshot is null || !_selection.IsActive)
            return false;

        bool succeeded;
        try
        {
            var effectiveOptions = options ?? MarkdownCopyOptions.Default;
            string? renderedText = null;
            string? renderedHtml = null;
            if (effectiveOptions.PlainTextMode == MarkdownPlainTextCopyMode.RenderedText ||
                effectiveOptions.IncludeHtml)
            {
                var rendered = GetRenderedSelectionPayload(snapshot, _selection.Range);
                if (effectiveOptions.PlainTextMode == MarkdownPlainTextCopyMode.RenderedText)
                    renderedText = rendered.Text;
                if (effectiveOptions.IncludeHtml)
                    renderedHtml = rendered.Html;
            }

            succeeded = MarkdownClipboardWriter.Copy(
                snapshot.SourceMap,
                _selection.Range,
                effectiveOptions,
                renderedText,
                renderedHtml);
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] Selection copy failed: {ex.Message}");
            succeeded = false;
        }

        _host.RaiseCopyCompleted(MarkdownCopyKind.Selection, succeeded);
        return succeeded;
    }

    /// <summary>Copies the active selection as exact markdown source plus CF_HTML.</summary>
    internal bool CopyAsMarkdown()
        => CopyToClipboard(new MarkdownCopyOptions
        {
            PlainTextMode = MarkdownPlainTextCopyMode.SourceMarkdown,
            IncludeHtml = true,
        });

    /// <summary>
    /// Runs the host command for <paramref name="kind"/>, or the built-in
    /// copy when the host provides none.
    /// </summary>
    internal bool Execute(MarkdownCommandKind kind)
    {
        try
        {
            if (TryResolve(kind, out var command, out var context))
            {
                if (!command.CanExecute(context))
                    return false;

                command.Execute(context);
                return true;
            }

            return kind == MarkdownCommandKind.CopyMarkdown
                ? CopyAsMarkdown()
                : CopyToClipboard();
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Host command '{kind}' failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// True when the host command for <paramref name="kind"/> can run, or
    /// when the host provides none and the built-in copy applies.
    /// </summary>
    internal bool CanExecuteOrDefault(MarkdownCommandKind kind)
    {
        try
        {
            return !TryResolve(kind, out var command, out var context) ||
                   command.CanExecute(context);
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Host command '{kind}' query failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The rendered text and HTML fragment for <paramref name="range"/>, read
    /// from the snapshot's semantic document.
    /// </summary>
    internal static RenderedSelectionPayload GetRenderedSelectionPayload(
        LayoutSnapshot snapshot,
        DocumentRange range)
    {
        var semantic = snapshot.SemanticDocument;
        var normalized = range.Normalized();
        int start = semantic.TextOffsetFromDocumentPosition(normalized.Start);
        int end = semantic.TextOffsetFromDocumentPosition(normalized.End);
        if (end < start)
            (start, end) = (end, start);

        start = Math.Clamp(start, 0, semantic.Text.Length);
        end = Math.Clamp(end, start, semantic.Text.Length);
        return new RenderedSelectionPayload(
            semantic.Text.Substring(start, end - start),
            MarkdownRenderedHtmlWriter.BuildFragment(semantic, start, end));
    }

    private bool TryResolve(
        MarkdownCommandKind kind,
        out System.Windows.Input.ICommand command,
        out MarkdownCommandContext context)
    {
        SourceSpan sourceRange = SourceSpan.Empty;
        if (_published.Snapshot is { } snapshot && _selection.IsActive)
            snapshot.SourceMap.TryMapRange(_selection.Range, out sourceRange);

        context = new MarkdownCommandContext(kind, sourceRange);
        command = _host.CommandProvider?.GetCommand(context)!;
        return command is not null;
    }
}
