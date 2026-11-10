using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using RebuildReason = MarkdownRenderer.Controls.MarkdownRebuildCoordinatorReason;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Controls.Composition;
using MarkdownRenderer.Controls.Embeds;
using MarkdownRenderer.Controls.Highlighting;
using MarkdownRenderer.Controls.Rendering;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Controls.LazyLayout;
using MarkdownRenderer.Controls.Navigation;
using MarkdownRenderer.Controls.Pipeline;
using MarkdownRenderer.Controls.Providers;
using MarkdownRenderer.Controls.Readiness;
using MarkdownRenderer.Controls.Selection;

namespace MarkdownRenderer.Controls.Pipeline;

/// <summary>
/// Reacts to presentation inputs whose change needs more than a rebuild:
/// localized strings, the hosted element factory and the code highlighter.
/// </summary>
internal sealed class PresentationInputChanges
{
    private readonly MarkdownViewLifetime _lifetime;
    private readonly RebuildScheduler _rebuild;
    private readonly EmbedRealization _embeds;
    private readonly CodeBlockHighlighting _highlighting;
    private readonly SelectionHandlesPresenter _handles;

    internal PresentationInputChanges(
        MarkdownViewLifetime lifetime,
        RebuildScheduler rebuild,
        EmbedRealization embeds,
        CodeBlockHighlighting highlighting,
        SelectionHandlesPresenter handles)
    {
        _lifetime = lifetime;
        _rebuild = rebuild;
        _embeds = embeds;
        _highlighting = highlighting;
        _handles = handles;
    }

    internal void OnStringProviderChanged()
    {
        _handles.UpdateLocalization();
        _rebuild.Request(RebuildReason.Restyle);
    }

    internal void OnHostedElementFactoryChanged()
    {
        _embeds.ClearHostedElementFallbacks();
        _rebuild.Request(RebuildReason.Full);
    }

    internal void OnCodeHighlighterChanged(DependencyPropertyChangedEventArgs e)
        => OnEffectiveCodeHighlighterChanged(
            e.OldValue as ICodeHighlighter,
            e.NewValue as ICodeHighlighter);

    private void OnEffectiveCodeHighlighterChanged(
        ICodeHighlighter? previous,
        ICodeHighlighter? current)
    {
        if (ReferenceEquals(previous, current))
            return;

        _highlighting.Retire(restart: !_lifetime.IsUnloaded && !_lifetime.IsDisposed);
        _highlighting.ClearCache();
        _rebuild.Request(RebuildReason.Full);
    }
}
