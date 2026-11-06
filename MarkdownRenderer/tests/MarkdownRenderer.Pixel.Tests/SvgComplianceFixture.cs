using MarkdownRenderer.Svg.Resvg;
using Xunit;

namespace MarkdownRenderer.Pixel.Tests;

/// <summary>
/// Shares one resvg provider and one persistent headless Edge session across
/// every <see cref="SvgComplianceTests"/> fixture case.
/// </summary>
public sealed class SvgComplianceFixture : IAsyncLifetime
{
    internal ResvgMarkdownSvgRenderer Renderer { get; } = new();
    internal PersistentEdgeSvgRasterizer Browser { get; } = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        try { await Renderer.DisposeAsync(); }
        finally { await Browser.DisposeAsync(); }
    }
}
