using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using RebuildReason = MarkdownRenderer.Controls.MarkdownRebuildCoordinatorReason;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Controls.Composition;
using MarkdownRenderer.Controls.Embeds;
using MarkdownRenderer.Controls.Focus;
using MarkdownRenderer.Controls.Highlighting;
using MarkdownRenderer.Controls.Images;
using MarkdownRenderer.Controls.Rendering;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Controls.Navigation;
using MarkdownRenderer.Controls.Pipeline;
using MarkdownRenderer.Controls.Readiness;
using MarkdownRenderer.Controls.Selection;
using MarkdownRenderer.Selection.Internal;

namespace MarkdownRenderer.Controls.Selection;

/// <summary>
/// Reacts to selection changes and to selection being enabled or disabled:
/// the overlay, other renderers’ selections, automation and cursor state.
/// </summary>
internal sealed class SelectionChangeHandling
{
    private readonly SelectionDismissalHook _selectionPeer;
    private readonly PointerCursorPresenter _cursor;
    private readonly RebuildScheduler _rebuild;
    private readonly InteractiveTextAdorner _textAdorner;
    private readonly RendererAutomation _automation;
    private readonly SelectionController _selection;
    private readonly TouchSelectionRestore _touchRestore;
    private readonly TouchTapSuppressionPolicy _touchTapSuppression;
    private readonly InteractionReset _interactionReset;

    internal SelectionChangeHandling(
        SelectionDismissalHook selectionPeer,
        PointerCursorPresenter cursor,
        RebuildScheduler rebuild,
        InteractiveTextAdorner textAdorner,
        RendererAutomation automation,
        SelectionController selection,
        TouchSelectionRestore touchRestore,
        TouchTapSuppressionPolicy touchTapSuppression,
        InteractionReset interactionReset)
    {
        _selectionPeer = selectionPeer;
        _cursor = cursor;
        _rebuild = rebuild;
        _textAdorner = textAdorner;
        _automation = automation;
        _selection = selection;
        _touchRestore = touchRestore;
        _touchTapSuppression = touchTapSuppression;
        _interactionReset = interactionReset;
    }

    internal void OnSelectionChanged()
    {
        _touchRestore.OnSelectionChanged();

        if (_selection.IsActive)
            _selectionPeer.ClearOtherSelections();

        _textAdorner.UpdateSelectionOverlay();
        _automation.QueueSelectionChanged();
    }

    internal void OnSelectionEnabledChanged(bool isEnabled)
    {
        if (!isEnabled)
        {
            _touchTapSuppression.Reset();
            _interactionReset.ClearSelectionForExternalInteraction();
        }

        // TextPattern remains available for document reading, but its
        // SupportedTextSelection value changes between Single and None.
        // Recreate the peer-facing semantic snapshot and refresh cursor state.
        _rebuild.Request(RebuildReason.Full);
        _automation.InvalidateLayout();
        _cursor.SetShape(null);
    }
}
