using System;
using MarkdownRenderer.Performance;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MarkdownRenderer.Sample;

/// <summary>
/// Keeps the source editor and the preview in step: it loads sample pages
/// into both, renders the user's edits, marks the page title as modified, and
/// replaces the active view without losing the source.
/// </summary>
internal sealed class SampleEditorSession : IDisposable
{
    private readonly SampleStrings _strings;
    private readonly TextBox _editor;
    private readonly TextBlock _pageTitle;
    private readonly SampleRendererHost _host;
    private readonly SampleAutomationStatus _status;
    private MarkdownPerformanceSession? _performanceSession;
    private string _currentPageTitle = string.Empty;
    private string _currentSampleSource = string.Empty;
    private string _lastRenderedEditorText = string.Empty;
    private bool _isLoadingSample;

    internal SampleEditorSession(
        SampleStrings strings,
        TextBox editor,
        TextBlock pageTitle,
        SampleRendererHost host,
        SampleAutomationStatus status)
    {
        _strings = strings;
        _editor = editor;
        _pageTitle = pageTitle;
        _host = host;
        _status = status;
        _editor.TextChanged += (_, _) => OnEditorTextChanged();
    }

    internal string Source => _editor.Text ?? string.Empty;

    /// <summary>
    /// The progressive-scenes session when the active view renders through
    /// it, so its deferred-scene diagnostics belong to the shown document.
    /// </summary>
    internal MarkdownPerformanceSession? ActiveDeferredSceneSession =>
        _performanceSession is not null &&
        ReferenceEquals(_host.Renderer.PerformanceSession, _performanceSession)
            ? _performanceSession
            : null;

    /// <summary>Loads a sample page into the editor and the active view.</summary>
    /// <param name="useProgressiveScenes">Whether the page opts into deferred scenes.</param>
    internal void Load(SampleDefinition page, bool useProgressiveScenes)
    {
        _currentPageTitle = _strings.Get(page.TitleResourceKey, page.FallbackTitle);
        UpdatePageTitle(modified: false);
        _status.SetCurrentPage(page.Key);

        string source = page.SourceFactory();
        _isLoadingSample = true;
        try
        {
            _editor.Text = source;
            // TextBox normalizes line endings. Use its stored value as the clean
            // baseline so a deferred TextChanged event does not mark navigation
            // loads as user edits.
            _currentSampleSource = _editor.Text ?? string.Empty;
            _lastRenderedEditorText = _currentSampleSource;
            _editor.SelectionStart = 0;
            _editor.SelectionLength = 0;
            // Sample destinations are independent documents. Reset the old
            // viewport before replacing the source so document-level scroll
            // anchoring cannot carry a position from one page into another.
            _host.ResetScroll();
            // Keep the established Math/Mermaid regression pages eager. Only
            // this page opts into deferred scenes and their session diagnostics.
            _host.Renderer.PerformanceSession = useProgressiveScenes
                ? _performanceSession ??= new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive)
                : null;
            // The TextBox normalizes CRLF line endings to CR. Commands use its
            // text as their source, so the renderer must parse that same text;
            // otherwise source spans after the first newline no longer address
            // the task marker that a native CheckBox is trying to toggle.
            _host.Renderer.Markdown = _currentSampleSource;
        }
        finally
        {
            _isLoadingSample = false;
        }
    }

    /// <summary>Replaces the active view, keeping the editor's source; ignored while a page loads.</summary>
    internal void ReplaceRenderer(bool ownsViewport)
    {
        if (_isLoadingSample)
            return;

        string source = Source;
        _lastRenderedEditorText = source;
        _host.Replace(ownsViewport, source);
    }

    public void Dispose() => _performanceSession?.Dispose();

    private void OnEditorTextChanged()
    {
        string editorText = _editor.Text ?? string.Empty;
        if (!_isLoadingSample &&
            !string.Equals(editorText, _lastRenderedEditorText, StringComparison.Ordinal))
        {
            _lastRenderedEditorText = editorText;
            _host.SetMarkdown(editorText);
        }

        if (!_isLoadingSample && _currentPageTitle.Length > 0)
            UpdatePageTitle(!string.Equals(
                editorText,
                _currentSampleSource,
                StringComparison.Ordinal));
    }

    private void UpdatePageTitle(bool modified)
    {
        string title = modified
            ? _currentPageTitle + _strings.Get("ModifiedSuffix", " — modified")
            : _currentPageTitle;
        _pageTitle.Text = title;
        AutomationProperties.SetName(_pageTitle, title);
    }
}
