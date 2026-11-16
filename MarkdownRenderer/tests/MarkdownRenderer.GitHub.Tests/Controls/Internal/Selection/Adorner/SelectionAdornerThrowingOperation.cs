using MarkdownRenderer.Controls;
using MarkdownRenderer.Controls.Internal.Selection.Adorner;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.GitHub.Tests.Controls.Internal.Selection.Adorner;

/// <summary>
/// A selection-adornment refresh that throws the injected failure.
/// </summary>
internal struct SelectionAdornerThrowingOperation(Exception failure) : ISelectionAdornerRefreshOperation
{
    private readonly Exception _failure = failure;

    internal int CallCount { get; private set; }

    public SelectionAdornerRefreshOperationResult RefreshUnderPaintLease(LayoutSnapshot snapshot)
    {
        CallCount++;
        throw _failure;
    }
}
