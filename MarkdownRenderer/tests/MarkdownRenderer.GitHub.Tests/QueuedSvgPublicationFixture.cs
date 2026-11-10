using System.Runtime.CompilerServices;
using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A headless SVG image box whose publications and opened-render handoffs are
/// built from real box state, so retention tests can hold a queued callback or
/// handoff alive and prove which source bytes and documents it does not keep.
/// The source arrays and documents are allocated in
/// <see cref="MethodImplOptions.NoInlining"/> helpers so no test local roots them.
/// </summary>
internal sealed class QueuedSvgPublicationFixture : IAsyncDisposable
{
    internal const int TargetWidthPixels = 64;
    internal const int TargetHeightPixels = 32;

    private readonly MarkdownRenderResources _renderResources = new();

    internal QueuedSvgPublicationFixture()
    {
        Image = new ImageBox(
            CreateContext(),
            _renderResources.Images,
            "data:image/svg+xml,%3Csvg%20xmlns%3D%27http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%27%3E%3C%2Fsvg%3E",
            "Queued publication retention");
    }

    internal PublicationCommitSvgRenderer Renderer { get; } = new();

    internal ImageBox Image { get; }

    /// <summary>
    /// Creates publication metadata over a fresh source array that only the
    /// returned weak reference observes, with document metadata from
    /// <paramref name="info"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal (SvgPublicationMetadata Metadata, WeakReference Source) CreateMetadata(
        MarkdownSvgDocumentInfo info,
        string identity,
        int sourceLength = 4096)
    {
        byte[] source = new byte[sourceLength];
        source.AsSpan().Fill((byte)'s');
        var metadata = new SvgPublicationMetadata(
            new WeakReference<byte[]>(source),
            source.LongLength,
            Renderer,
            _renderResources.Images.ProviderIdentities.GetSvgRendererIdentity(Renderer),
            Renderer.CacheGeneration,
            Image.Svg.AdvanceRenderGeneration(),
            MetadataWasKnown: false,
            "resolved:" + identity,
            "failure:" + identity,
            new Size((float)(info.IntrinsicWidthDips ?? 1), (float)(info.IntrinsicHeightDips ?? 1)),
            "title",
            "description",
            new string('A', 64),
            new string('B', 64),
            info,
            TargetWidthPixels,
            TargetHeightPixels,
            identity,
            WorkId: 0,
            PublishRequestedAt: 0,
            TargetPixels: (long)TargetWidthPixels * TargetHeightPixels);
        return (metadata, new WeakReference(source));
    }

    /// <summary>Reserves a fresh document as this box's opened candidate.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal (WeakReference Document, MarkdownSvgDocumentInfo Info, SvgDocumentUseLease CandidateUse, Task Disposed)
        OpenCandidateDocument()
    {
        var document = new PublicationCommitSvgDocument();
        SvgDocumentUseLease candidateUse = Assert.IsType<SvgDocumentUseLease>(
            Image.SvgDocuments.TryAcquireCandidateUse(document, out bool ownershipConflict));
        Assert.False(ownershipConflict);
        return (new WeakReference(document), document.Info, candidateUse, document.DisposeCompleted.Task);
    }

    /// <summary>
    /// Retires an unadopted candidate document and releases its use, as an
    /// opened whole-bitmap render does once its bitmap is published or dropped.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void RetireCandidateDocument(WeakReference document, SvgDocumentUseLease candidateUse)
    {
        Image.SvgDocuments.RetireUncommitted(Assert.IsAssignableFrom<IMarkdownSvgDocument>(document.Target));
        candidateUse.Dispose();
    }

    /// <summary>
    /// Queues <paramref name="publication"/> through the box's UI publisher
    /// and returns the queued callback without running it.
    /// </summary>
    internal Action Queue((Action Publish, Action? OnDropped) publication)
    {
        Action? queued = null;
        Image.PublishOnUiThread(publication.Publish, publication.OnDropped, callback =>
        {
            queued = callback;
            return true;
        });
        return Assert.IsType<Action>(queued);
    }

    public async ValueTask DisposeAsync()
    {
        Image.Dispose();
        await Image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private MarkdownLayoutContext CreateContext()
    {
        var body = new ElementStyle { Foreground = Color.FromArgb(255, 12, 34, 56) };
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
            {
                [MarkdownElementKeys.Body] = body,
                [MarkdownElementKeys.ImageCaption] = body,
                [MarkdownElementKeys.ListMarker] = body,
            },
            new Dictionary<string, ElementStyleOverride>(),
            Color.FromArgb(0, 0, 0, 0),
            Color.FromArgb(0, 0, 0, 0),
            Color.FromArgb(0, 0, 0, 0),
            Color.FromArgb(0, 0, 0, 0),
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);

        return new MarkdownLayoutContext(
            resourceCreator: null!,
            theme,
            new MarkdownSourceMap(string.Empty),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            RenderResources = _renderResources,
            SvgRenderer = Renderer,
            ImageCancellationToken = CancellationToken.None,
            RasterizationScale = 1,
        };
    }
}
