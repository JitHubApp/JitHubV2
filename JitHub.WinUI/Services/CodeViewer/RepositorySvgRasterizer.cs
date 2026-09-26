using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;

namespace JitHub.Services.CodeViewer;

internal interface IRepositorySvgRasterizer
{
    ValueTask<RepositorySvgDocument?> LoadAsync(
        byte[]? bytes,
        string? locale,
        MarkdownSvgColorScheme colorScheme,
        MarkdownSvgColor? semanticColor,
        CancellationToken cancellationToken);

    ValueTask<RepositorySvgTile> RasterizeTileAsync(
        RepositorySvgDocument document,
        RepositorySvgTileRequest request,
        MarkdownSvgPixelFormat pixelFormat,
        CancellationToken cancellationToken);
}

/// <summary>
/// Adapts JitHub's repository preview viewport to the shared, isolated SVG
/// provider. This type borrows the renderer and never owns or disposes it.
/// </summary>
internal sealed class RepositorySvgRasterizer(IMarkdownSvgRenderer renderer) : IRepositorySvgRasterizer
{
    private const double DefaultWidthDips = 300;
    private const double DefaultHeightDips = 150;

    private static readonly ConditionalWeakTable<IMarkdownSvgRenderer, RendererIdentity> RendererIdentities = new();
    private static long _nextRendererIdentity;

    private readonly IMarkdownSvgRenderer _renderer =
        renderer ?? throw new ArgumentNullException(nameof(renderer));
    private readonly long _rendererIdentity = RendererIdentities.GetValue(
        renderer,
        static _ => new RendererIdentity(Interlocked.Increment(ref _nextRendererIdentity))).Value;

    public async ValueTask<RepositorySvgDocument?> LoadAsync(
        byte[]? bytes,
        string? locale,
        MarkdownSvgColorScheme colorScheme,
        MarkdownSvgColor? semanticColor,
        CancellationToken cancellationToken)
    {
        (RepositorySvgValidationResult validation, string? contentHash) = await Task.Run(
            () =>
            {
                RepositorySvgValidationResult result =
                    RepositorySvgSecurityPolicy.Validate(bytes, cancellationToken);
                string? hash = result.Accepted && bytes is not null
                    ? Convert.ToHexString(SHA256.HashData(bytes))
                    : null;
                return (result, hash);
            },
            cancellationToken).ConfigureAwait(false);
        if (!validation.Accepted || bytes is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        long cacheGeneration = _renderer.CacheGeneration;
        IMarkdownSvgDocument? document = null;
        try
        {
            document = await _renderer.OpenAsync(
                new MarkdownSvgOpenRequest(bytes, locale, colorScheme, semanticColor),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            (double width, double height) = ResolveIntrinsicSize(document.Info);
            if (!RepositorySvgSecurityPolicy.ArePictureBoundsSafe(width, height))
            {
                document.Dispose();
                return null;
            }

            string cacheIdentity = CreateCacheIdentity(
                contentHash!,
                document.Info,
                _rendererIdentity,
                cacheGeneration,
                locale,
                colorScheme,
                semanticColor);
            RepositorySvgDocument result = new(
                document,
                width,
                height,
                cacheIdentity,
                cacheGeneration);
            document = null;
            return result;
        }
        finally
        {
            document?.Dispose();
        }
    }

    public ValueTask<RepositorySvgTile> RasterizeTileAsync(
        RepositorySvgDocument document,
        RepositorySvgTileRequest request,
        MarkdownSvgPixelFormat pixelFormat,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        return document.UseAsync(async svgDocument =>
        {
            MarkdownSvgRaster? raster = null;
            try
            {
                raster = await svgDocument.RenderAsync(
                    new MarkdownSvgRenderRequest(
                        request.TargetWidthPixels,
                        request.TargetHeightPixels,
                        new MarkdownSvgTileRegion(
                            request.PixelX,
                            request.PixelY,
                            request.PixelWidth,
                            request.PixelHeight),
                        pixelFormat),
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (raster.PixelFormat != pixelFormat ||
                    raster.WidthPixels != request.PixelWidth ||
                    raster.HeightPixels != request.PixelHeight)
                {
                    throw new InvalidOperationException("The SVG worker returned an unexpected tile layout.");
                }

                RepositorySvgTile result = new(
                    request.PixelX,
                    request.PixelY,
                    raster);
                raster = null;
                return result;
            }
            finally
            {
                raster?.Dispose();
            }
        });
    }

    private static (double Width, double Height) ResolveIntrinsicSize(MarkdownSvgDocumentInfo info)
    {
        double? width = PositiveFinite(info.IntrinsicWidthDips);
        double? height = PositiveFinite(info.IntrinsicHeightDips);
        double? aspectRatio = PositiveFinite(info.IntrinsicAspectRatio);

        if (width is not null && height is not null)
        {
            return (width.Value, height.Value);
        }

        if (width is not null)
        {
            return (width.Value, aspectRatio is null ? DefaultHeightDips : width.Value / aspectRatio.Value);
        }

        if (height is not null)
        {
            return (aspectRatio is null ? DefaultWidthDips : height.Value * aspectRatio.Value, height.Value);
        }

        if (aspectRatio is not null)
        {
            return (DefaultWidthDips, DefaultWidthDips / aspectRatio.Value);
        }

        return (DefaultWidthDips, DefaultHeightDips);
    }

    private static double? PositiveFinite(double? value) =>
        value is > 0 && double.IsFinite(value.Value) ? value : null;

    private static string CreateCacheIdentity(
        string contentHash,
        MarkdownSvgDocumentInfo info,
        long rendererIdentity,
        long cacheGeneration,
        string? locale,
        MarkdownSvgColorScheme colorScheme,
        MarkdownSvgColor? semanticColor)
    {
        string identity = string.Concat(
            contentHash,
            "|policy:1|renderer:",
            rendererIdentity.ToString(CultureInfo.InvariantCulture));
        if (info.HasText)
        {
            identity = string.Concat(
                identity,
                "|generation:",
                cacheGeneration.ToString(CultureInfo.InvariantCulture),
                "|locale:",
                locale ?? string.Empty);
        }

        if (info.UsesColorScheme)
        {
            identity = string.Concat(identity, "|scheme:", colorScheme.ToString());
        }

        if (info.UsesCurrentColor)
        {
            identity = semanticColor is MarkdownSvgColor color
                ? string.Concat(
                    identity,
                    "|color:",
                    color.Red.ToString("X2", CultureInfo.InvariantCulture),
                    color.Green.ToString("X2", CultureInfo.InvariantCulture),
                    color.Blue.ToString("X2", CultureInfo.InvariantCulture),
                    color.Alpha.ToString("X2", CultureInfo.InvariantCulture))
                : string.Concat(identity, "|color:none");
        }

        return identity;
    }

    private sealed class RendererIdentity(long value)
    {
        internal long Value { get; } = value;
    }
}

internal sealed partial class RepositorySvgDocument : IDisposable
{
    private readonly object _sync = new();
    private IMarkdownSvgDocument? _document;
    private int _activeOperations;
    private bool _disposeRequested;

    internal RepositorySvgDocument(
        IMarkdownSvgDocument document,
        double width,
        double height,
        string cacheIdentity,
        long cacheGeneration)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        ArgumentException.ThrowIfNullOrEmpty(cacheIdentity);
        Width = width;
        Height = height;
        CacheIdentity = cacheIdentity;
        CacheGeneration = cacheGeneration;
        Info = document.Info;
    }

    public double Width { get; }

    public double Height { get; }

    public string CacheIdentity { get; }

    public long CacheGeneration { get; }

    public MarkdownSvgDocumentInfo Info { get; }

    internal async ValueTask<T> UseAsync<T>(Func<IMarkdownSvgDocument, ValueTask<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        IMarkdownSvgDocument document;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested || _document is null, this);
            document = _document;
            _activeOperations++;
        }

        try
        {
            return await action(document).ConfigureAwait(false);
        }
        finally
        {
            IMarkdownSvgDocument? dispose = null;
            lock (_sync)
            {
                _activeOperations--;
                if (_disposeRequested && _activeOperations == 0)
                {
                    dispose = _document;
                    _document = null;
                }
            }

            dispose?.Dispose();
        }
    }

    public void Dispose()
    {
        IMarkdownSvgDocument? dispose = null;
        lock (_sync)
        {
            if (_disposeRequested)
            {
                return;
            }

            _disposeRequested = true;
            if (_activeOperations == 0)
            {
                dispose = _document;
                _document = null;
            }
        }

        dispose?.Dispose();
    }
}

internal readonly record struct RepositorySvgTileRequest(
    int TargetWidthPixels,
    int TargetHeightPixels,
    int PixelX,
    int PixelY,
    int PixelWidth,
    int PixelHeight)
{
    public const int MaximumTileEdge = 1024;

    internal void Validate()
    {
        if (TargetWidthPixels <= 0 ||
            TargetHeightPixels <= 0 ||
            PixelX < 0 ||
            PixelY < 0 ||
            PixelWidth is <= 0 or > MaximumTileEdge ||
            PixelHeight is <= 0 or > MaximumTileEdge ||
            PixelX > TargetWidthPixels - PixelWidth ||
            PixelY > TargetHeightPixels - PixelHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(RepositorySvgTileRequest));
        }
    }
}

internal sealed partial class RepositorySvgTile : IDisposable
{
    private MarkdownSvgRaster? _raster;

    internal RepositorySvgTile(int pixelX, int pixelY, MarkdownSvgRaster raster)
    {
        _raster = raster ?? throw new ArgumentNullException(nameof(raster));
        PixelX = pixelX;
        PixelY = pixelY;
        PixelWidth = raster.WidthPixels;
        PixelHeight = raster.HeightPixels;
        StrideBytes = raster.StrideBytes;
        PixelFormat = raster.PixelFormat;
    }

    public int PixelX { get; }

    public int PixelY { get; }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public int StrideBytes { get; }

    public MarkdownSvgPixelFormat PixelFormat { get; }

    public ReadOnlyMemory<byte> BgraPixels =>
        (_raster ?? throw new ObjectDisposedException(nameof(RepositorySvgTile))).Pixels;

    public int ByteCount => checked(PixelWidth * PixelHeight * 4);

    public void Dispose() => Interlocked.Exchange(ref _raster, null)?.Dispose();
}
