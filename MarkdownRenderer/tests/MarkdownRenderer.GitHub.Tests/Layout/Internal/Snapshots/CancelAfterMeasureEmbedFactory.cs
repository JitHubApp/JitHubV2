using MarkdownRenderer.Extensions;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Hosting.Internal;
using Markdig.Syntax;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Snapshots;

internal sealed class CancelAfterMeasureEmbedFactory : IMarkdownEmbedFactory
{
    internal Action? AfterMeasure { get; set; }

    public bool CanCreate(Block block) => true;

    public float MeasureHeight(Block block, float availableWidth)
    {
        AfterMeasure?.Invoke();
        return 20;
    }

    public FrameworkElement CreateBlock(Block block) => null!;
}
