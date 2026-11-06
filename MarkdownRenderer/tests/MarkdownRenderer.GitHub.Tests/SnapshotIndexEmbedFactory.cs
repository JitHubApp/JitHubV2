using MarkdownRenderer.Hosting;
using Markdig.Syntax;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// An embed factory that accepts every block and measures it to 20 DIPs without
/// creating a control.
/// </summary>
internal sealed class SnapshotIndexEmbedFactory : IMarkdownEmbedFactory
{
    internal static readonly SnapshotIndexEmbedFactory Instance = new();
    public bool CanCreate(Block block) => true;
    public float MeasureHeight(Block block, float availableWidth) => 20;
    public FrameworkElement CreateBlock(Block block) => null!;
}
