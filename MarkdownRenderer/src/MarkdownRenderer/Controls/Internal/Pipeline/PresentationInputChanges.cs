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
using RebuildReason = MarkdownRenderer.Controls.Internal.Pipeline.Rebuild.MarkdownRebuildCoordinatorReason;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Controls.Internal.Embeds;
using MarkdownRenderer.Controls.Internal.Highlighting;
using MarkdownRenderer.Controls.Internal.Pipeline.Rebuild;
using MarkdownRenderer.Controls.Internal.Selection.TouchSelection;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Document;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Controls.Internal.Pipeline;

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
