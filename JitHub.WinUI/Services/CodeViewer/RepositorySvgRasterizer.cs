using System;
using System.IO;
using System.Threading;
using SkiaSharp;
using SvgSkia = Svg.Skia;

namespace JitHub.Services.CodeViewer;

internal interface IRepositorySvgRasterizer
{
    RepositorySvgDocument? Load(byte[]? bytes, CancellationToken cancellationToken);

    RepositorySvgTile RasterizeTile(
        RepositorySvgDocument document,
        RepositorySvgTileRequest request,
        CancellationToken cancellationToken);
}

internal sealed class RepositorySvgRasterizer : IRepositorySvgRasterizer
{
    public RepositorySvgDocument? Load(byte[]? bytes, CancellationToken cancellationToken)
    {
        RepositorySvgValidationResult validation = RepositorySvgSecurityPolicy.Validate(bytes, cancellationToken);
        if (!validation.Accepted || bytes is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        SvgSkia.SKSvg? svg = null;
        try
        {
            svg = new SvgSkia.SKSvg();
            using MemoryStream stream = new(bytes, writable: false);
            svg.Load(stream);
            cancellationToken.ThrowIfCancellationRequested();

            SKPicture? picture = svg.Picture;
            if (picture is null ||
                !RepositorySvgSecurityPolicy.ArePictureBoundsSafe(
                    picture.CullRect.Width,
                    picture.CullRect.Height))
            {
                DisposeSvg(svg);
                return null;
            }

            return new RepositorySvgDocument(svg, picture.CullRect);
        }
        catch (OperationCanceledException)
        {
            DisposeSvg(svg);
            throw;
        }
        catch
        {
            DisposeSvg(svg);
            return null;
        }
    }

    public RepositorySvgTile RasterizeTile(
        RepositorySvgDocument document,
        RepositorySvgTileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        return document.UsePicture(picture =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            SKImageInfo imageInfo = new(
                request.PixelWidth,
                request.PixelHeight,
                SKColorType.Bgra8888,
                SKAlphaType.Premul);
            using SKBitmap bitmap = new(imageInfo);
            using SKCanvas canvas = new(bitmap);
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(-request.PixelX, -request.PixelY);
            canvas.Scale(request.PixelsPerSourceUnit, request.PixelsPerSourceUnit);
            canvas.Translate(-document.Bounds.Left, -document.Bounds.Top);
            canvas.DrawPicture(picture);
            canvas.Flush();
            cancellationToken.ThrowIfCancellationRequested();

            byte[] pixels = bitmap.Bytes;
            int expectedLength = checked(request.PixelWidth * request.PixelHeight * 4);
            if (pixels.Length != expectedLength)
            {
                throw new InvalidDataException("Skia returned an unexpected SVG tile stride.");
            }

            return new RepositorySvgTile(
                request.PixelX,
                request.PixelY,
                request.PixelWidth,
                request.PixelHeight,
                pixels);
        });
    }

    private static void DisposeSvg(SvgSkia.SKSvg? svg)
    {
        if (svg is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

internal sealed partial class RepositorySvgDocument : IDisposable
{
    private readonly object _sync = new();
    private SvgSkia.SKSvg? _svg;

    internal RepositorySvgDocument(SvgSkia.SKSvg svg, SKRect bounds)
    {
        _svg = svg;
        Bounds = bounds;
    }

    public float Width => Bounds.Width;

    public float Height => Bounds.Height;

    internal SKRect Bounds { get; }

    internal T UsePicture<T>(Func<SKPicture, T> action)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_svg is null, this);
            SKPicture? picture = _svg.Picture;
            if (picture is null)
            {
                throw new ObjectDisposedException(nameof(RepositorySvgDocument));
            }

            return action(picture);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            SvgSkia.SKSvg? svg = _svg;
            _svg = null;
            if (svg is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}

internal readonly record struct RepositorySvgTileRequest(
    int PixelX,
    int PixelY,
    int PixelWidth,
    int PixelHeight,
    float PixelsPerSourceUnit)
{
    public const int MaximumTileEdge = 1024;

    internal void Validate()
    {
        if (PixelX < 0 ||
            PixelY < 0 ||
            PixelWidth is <= 0 or > MaximumTileEdge ||
            PixelHeight is <= 0 or > MaximumTileEdge ||
            !float.IsFinite(PixelsPerSourceUnit) ||
            PixelsPerSourceUnit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RepositorySvgTileRequest));
        }
    }
}

internal sealed class RepositorySvgTile
{
    public RepositorySvgTile(
        int pixelX,
        int pixelY,
        int pixelWidth,
        int pixelHeight,
        byte[] bgraPixels)
    {
        PixelX = pixelX;
        PixelY = pixelY;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        BgraPixels = bgraPixels;
    }

    public int PixelX { get; }

    public int PixelY { get; }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public byte[] BgraPixels { get; }

    public int ByteCount => BgraPixels.Length;
}
