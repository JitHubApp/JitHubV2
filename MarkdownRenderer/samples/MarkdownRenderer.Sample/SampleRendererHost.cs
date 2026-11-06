using System;
using System.Collections.Generic;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MarkdownRenderer.Sample;

/// <summary>
/// Owns the preview column's renderer views: the active view, which either
/// owns its viewport (<see cref="MarkdownScrollView"/>) or sits in a
/// page-owned ScrollViewer (<see cref="MarkdownDocumentView"/>) so both touch
/// viewport modes are exercised, and the optional second view that shares
/// the providers. Only the active view reports render results.
/// </summary>
internal sealed class SampleRendererHost : IDisposable
{
    private const int FirstDocumentBlockIndex = 1;

    private readonly SampleStrings _strings;
    private readonly SampleProviderSet _providers;
    private readonly SampleAutomationStatus _status;
    private readonly RendererDisposalEvidence _disposalEvidence;
    private readonly TouchContactObservation _touchObservation;
    private readonly TextBox _editor;
    private readonly Action _renderCompleted;
    private readonly Action<Exception> _renderFailed;
    private readonly EventHandler<MarkdownLinkClickEventArgs> _linkClick;
    private readonly Grid _viewHost = new();
    private readonly Border _secondViewHost;
    private readonly RowDefinition _secondViewRow = new() { Height = new GridLength(0) };
    private MarkdownScrollView? _secondRenderer;
    private bool _observeTouch;
    private bool _resetScrollOnRender;

    /// <param name="editor">The source editor; task commands edit its text.</param>
    /// <param name="renderCompleted">Called after the active view renders.</param>
    /// <param name="renderFailed">Called when the active view fails to render.</param>
    /// <param name="linkClick">Handles link activation in every view.</param>
    internal SampleRendererHost(
        SampleStrings strings,
        SampleProviderSet providers,
        SampleAutomationStatus status,
        RendererDisposalEvidence disposalEvidence,
        TouchContactObservation touchObservation,
        TextBox editor,
        Action renderCompleted,
        Action<Exception> renderFailed,
        EventHandler<MarkdownLinkClickEventArgs> linkClick)
    {
        _strings = strings;
        _providers = providers;
        _status = status;
        _disposalEvidence = disposalEvidence;
        _touchObservation = touchObservation;
        _editor = editor;
        _renderCompleted = renderCompleted;
        _renderFailed = renderFailed;
        _linkClick = linkClick;
        _secondViewHost = new Border
        {
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = (Thickness)Application.Current.Resources["SampleSecondViewBorderThickness"],
            Visibility = Visibility.Collapsed,
        };

        Renderer = CreateRenderer(ownsViewport: true);
        AttachToViewHost(Renderer, ownsViewport: true);
    }

    /// <summary>The active view.</summary>
    internal MarkdownRendererControl Renderer { get; private set; }

    internal bool HasSecondView => _secondRenderer is not null;

    /// <summary>Every live view, active first.</summary>
    internal IEnumerable<MarkdownRendererControl> Views
    {
        get
        {
            yield return Renderer;
            if (_secondRenderer is not null)
                yield return _secondRenderer;
        }
    }

    /// <summary>Creates the preview column: the active view above the second view.</summary>
    internal Grid CreatePreviewColumn()
    {
        var column = new Grid();
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        column.RowDefinitions.Add(_secondViewRow);
        Grid.SetRow(_viewHost, 0);
        Grid.SetRow(_secondViewHost, 1);
        column.Children.Add(_viewHost);
        column.Children.Add(_secondViewHost);
        return column;
    }

    /// <summary>Sets the source of every view.</summary>
    internal void SetMarkdown(string markdown)
    {
        Renderer.Markdown = markdown;
        if (_secondRenderer is not null)
            _secondRenderer.Markdown = markdown;
    }

    /// <summary>Scrolls the active view to the document start now and after its next render.</summary>
    internal void ResetScroll()
    {
        Renderer.ScrollToBlock(FirstDocumentBlockIndex);
        _resetScrollOnRender = true;
    }

    /// <summary>Observes touch contacts on the active view, now and after it is replaced.</summary>
    internal void ObserveTouch(bool enabled)
    {
        _observeTouch = enabled;
        if (enabled)
            _touchObservation.Attach(Renderer);
        else
            _touchObservation.Detach();
    }

    /// <summary>
    /// Replaces the active view with a new one of the requested viewport
    /// ownership, carrying over its flow direction, theme and task editing,
    /// and renders <paramref name="source"/> in it from the document start.
    /// </summary>
    internal void Replace(bool ownsViewport, string source)
    {
        MarkdownRendererControl previous = Renderer;
        MarkdownRendererControl replacement = CreateRenderer(ownsViewport);
        replacement.FlowDirection = previous.FlowDirection;
        replacement.Theme = previous.Theme ?? new MarkdownTheme();
        replacement.IsTaskListEditingEnabled = previous.IsTaskListEditingEnabled;
        Renderer = replacement;
        if (_observeTouch)
            _touchObservation.Attach(replacement);
        else
            _touchObservation.Detach();
        AttachToViewHost(replacement, ownsViewport);
        previous.Dispose();
        _resetScrollOnRender = true;
        replacement.Markdown = source;
        _status.SetFlowDirection(replacement.FlowDirection);
        _status.MarkThemePending();
    }

    internal void SetSecondView(bool visible, string source)
    {
        if (visible && _secondRenderer is null)
        {
            var second = (MarkdownScrollView)CreateRenderer(ownsViewport: true);
            AutomationProperties.SetAutomationId(second, "SecondMarkdownRenderer");
            AutomationProperties.SetName(
                second,
                _strings.Get("SecondRendererAutomationName", "Second rendered markdown preview"));
            second.Theme = Renderer.Theme ?? new MarkdownTheme();
            second.FlowDirection = Renderer.FlowDirection;
            _secondRenderer = second;
            _secondViewHost.Child = second;
            _secondViewHost.Visibility = Visibility.Visible;
            _secondViewRow.Height = new GridLength(1, GridUnitType.Star);
            second.Markdown = source;
        }
        else if (!visible && _secondRenderer is { } existing)
        {
            _secondRenderer = null;
            _secondViewHost.Child = null;
            _secondViewHost.Visibility = Visibility.Collapsed;
            _secondViewRow.Height = new GridLength(0);
            existing.Dispose();
        }
    }

    public void Dispose()
    {
        _touchObservation.Detach();
        Renderer.Dispose();
        _secondRenderer?.Dispose();
        _secondRenderer = null;
    }

    private MarkdownRendererControl CreateRenderer(bool ownsViewport)
    {
        MarkdownRendererControl renderer = ownsViewport
            ? new MarkdownScrollView()
            : new MarkdownDocumentView();
        renderer.CommandProvider = new SampleTaskCommandProvider(
            () => _editor.Text ?? string.Empty,
            value => _editor.Text = value);
        renderer.Markdown = string.Empty;
        renderer.Theme = new MarkdownTheme();
        renderer.Margin = new Thickness(0);
        renderer.Padding = (Thickness)Application.Current.Resources["SampleRendererPadding"];
        _providers.Configure(renderer);
        _disposalEvidence.Attach(renderer);
        AutomationProperties.SetAutomationId(renderer, "MarkdownRenderer");
        AutomationProperties.SetName(
            renderer,
            _strings.Get("RendererAutomationName", "Rendered markdown preview"));
        renderer.LinkClick += _linkClick;
        renderer.EmbedsRealizationChanged += (_, _) =>
        {
            if (ReferenceEquals(renderer, Renderer))
                _status.SetRealizedCount(renderer.RealizedEmbedCount);
        };
        renderer.RenderCompleted += (_, _) =>
        {
            if (!ReferenceEquals(renderer, Renderer))
                return;

            if (_resetScrollOnRender)
            {
                _resetScrollOnRender = false;
                renderer.ScrollToBlock(FirstDocumentBlockIndex);
            }

            _renderCompleted();
        };
        renderer.RenderFailed += (_, e) =>
        {
            if (ReferenceEquals(renderer, Renderer))
                _renderFailed(e.Exception);
        };
        return renderer;
    }

    private void AttachToViewHost(MarkdownRendererControl renderer, bool ownsViewport)
    {
        _viewHost.Children.Clear();
        if (ownsViewport)
        {
            _viewHost.Children.Add(renderer);
            return;
        }

        var pageViewport = new ScrollViewer
        {
            Content = renderer,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            ZoomMode = ZoomMode.Disabled,
        };
        AutomationProperties.SetAutomationId(pageViewport, "TouchSelectionAncestorViewport");
        AutomationProperties.SetName(
            pageViewport,
            _strings.Get("AncestorViewportAutomationName", "Page-owned markdown viewport"));
        _viewHost.Children.Add(pageViewport);
    }
}
