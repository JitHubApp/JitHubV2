using System.Threading;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;

namespace MarkdownRenderer.Controls;

/// <summary>
/// The layout snapshot a renderer currently shows, with the generation that
/// identifies its document commit and the device its text was laid out on.
/// </summary>
/// <remarks>
/// <para>
/// Only the rebuild commit and teardown change these, on the UI thread.
/// Work that started against one snapshot captures <see cref="Snapshot"/>
/// and <see cref="Generation"/> and checks them again before publishing, so
/// a replaced document never receives results computed for the old one.
/// </para>
/// <para>
/// Image and provider callbacks compare <see cref="Snapshot"/> and
/// <see cref="Generation"/> from other threads, so both are read and written
/// volatile.
/// </para>
/// </remarks>
internal sealed class PublishedLayout
{
    private LayoutSnapshot? _snapshot;
    private long _generation;

    /// <summary>The snapshot being shown, or null before the first commit and after teardown.</summary>
    public LayoutSnapshot? Snapshot
    {
        get => Volatile.Read(ref _snapshot);
        set => Volatile.Write(ref _snapshot, value);
    }

    /// <summary>Increases with every document commit.</summary>
    public long Generation
    {
        get => Volatile.Read(ref _generation);
        set => Volatile.Write(ref _generation, value);
    }

    /// <summary>
    /// True while <paramref name="snapshot"/>, <paramref name="generation"/>
    /// and <paramref name="layoutRevision"/> are still the published ones.
    /// </summary>
    public bool IsCurrent(LayoutSnapshot snapshot, long generation, long layoutRevision) =>
        ReferenceEquals(snapshot, Snapshot) &&
        generation == Generation &&
        snapshot.LayoutRevision == layoutRevision;

    /// <summary>
    /// The Win2D device that measured <see cref="Snapshot"/>. Text layouts
    /// belong to it, so a device change needs a rebuild.
    /// </summary>
    public CanvasDevice? LayoutDevice { get; set; }

    /// <summary>
    /// The caches and native custody the boxes of <see cref="Snapshot"/> use:
    /// those of the engine whose document it shows. Device-loss recovery
    /// releases the lost device from them.
    /// </summary>
    public MarkdownRenderResources? RenderResources { get; set; }

    /// <summary>
    /// The theme <see cref="Snapshot"/> was laid out with. UI work after the
    /// commit (focus ring, selection, automation text attributes) reads its
    /// colours here, so they always match the painted snapshot.
    /// </summary>
    public ThemeSnapshot? Theme { get; set; }

    /// <summary>
    /// The variant <see cref="Theme"/> renders, or null while no snapshot is
    /// shown: high contrast first, then dark or light.
    /// </summary>
    public MarkdownThemeVariant? ThemeVariant =>
        Snapshot is null || Theme is not { } theme
            ? null
            : theme.IsHighContrast
                ? MarkdownThemeVariant.HighContrast
                : theme.IsDark ? MarkdownThemeVariant.Dark : MarkdownThemeVariant.Light;
}
