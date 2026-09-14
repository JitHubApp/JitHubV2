using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace MarkdownRenderer.Svg.ThorVG.Tests;

public sealed class ThorVgPackTests
{
    private static byte[] Svg(string value) => Encoding.UTF8.GetBytes(value);

    [Fact]
    public void CompiledPack_RasterizesPremultipliedBgra()
    {
        ThorVgRaster? raster = ThorVgFeature.Rasterize(
            Svg("<svg xmlns='http://www.w3.org/2000/svg' width='32' height='32'><rect width='32' height='32' fill='#ff0000'/></svg>"),
            32,
            32);

        Assert.NotNull(raster);
        Assert.Equal(32, raster.WidthPixels);
        Assert.Equal(32, raster.HeightPixels);
        Assert.Equal(32 * 32 * 4, raster.BgraPremultipliedPixels.Length);

        ReadOnlySpan<byte> pixels = raster.BgraPremultipliedPixels.Span;
        int center = ((16 * 32) + 16) * 4;
        Assert.InRange(pixels[center + 0], (byte)0, (byte)40);
        Assert.InRange(pixels[center + 1], (byte)0, (byte)40);
        Assert.InRange(pixels[center + 2], (byte)200, byte.MaxValue);
        Assert.InRange(pixels[center + 3], (byte)200, byte.MaxValue);
    }

    [Fact]
    public void CompiledPack_ReportsPinnedNativeVersion()
    {
        Version? version = ThorVgFeature.TryGetLoadedNativeVersion();

        Assert.NotNull(version);
        Assert.Equal(ThorVgFeature.NativeVersion, version.ToString(3));
    }

    [Fact]
    public void CompiledPack_RasterizesPatternFills()
    {
        ThorVgRaster? raster = ThorVgFeature.Rasterize(
            Svg("<svg xmlns='http://www.w3.org/2000/svg' width='24' height='24'><defs><pattern id='p' width='8' height='8' patternUnits='userSpaceOnUse'><rect width='4' height='8' fill='#0078d4'/><rect x='4' width='4' height='8' fill='#fff'/></pattern></defs><rect width='24' height='24' fill='url(#p)'/></svg>"),
            24,
            24);

        Assert.NotNull(raster);
        ReadOnlySpan<byte> pixels = raster.BgraPremultipliedPixels.Span;
        int bluePixel = ((12 * 24) + 2) * 4;
        int whitePixel = ((12 * 24) + 6) * 4;
        Assert.True(pixels[bluePixel] != pixels[whitePixel] ||
                    pixels[bluePixel + 1] != pixels[whitePixel + 1] ||
                    pixels[bluePixel + 2] != pixels[whitePixel + 2]);
    }

    [Theory]
    [InlineData("<feColorMatrix type='saturate' values='0'/>")]
    [InlineData("<feDropShadow dx='2' dy='2'/>")]
    public void PublicRasterizer_RendersBaseArtworkForBoundedStandardFilters(string primitive)
    {
        ThorVgRaster? raster = ThorVgFeature.Rasterize(
            Svg($"<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16'><defs><filter id='f'>{primitive}</filter></defs><rect width='16' height='16' fill='#0078d4' filter='url(#f)'/></svg>"),
            16,
            16);

        Assert.NotNull(raster);
        Assert.Contains(raster!.BgraPremultipliedPixels.ToArray(), static channel => channel != 0);
    }

    [Theory]
    [InlineData("<feImage href='https://example.test/image.png'/>")]
    [InlineData("<feDisplacementMap scale='20'/>")]
    public void PublicRasterizer_UsesAtomicFallbackForUnsupportedFilterPrimitives(string primitive)
    {
        ThorVgRaster? raster = ThorVgFeature.Rasterize(
            Svg($"<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16'><defs><filter id='f'>{primitive}</filter></defs><rect width='16' height='16' filter='url(#f)'/></svg>"),
            16,
            16);

        Assert.Null(raster);
    }

    [Fact]
    public void PublicRasterizer_RejectsInvalidAndBudgetExhaustingInput()
    {
        Assert.Null(ThorVgFeature.Rasterize(Svg("not svg"), 16, 16));
        Assert.Null(ThorVgFeature.Rasterize(new byte[(2 * 1024 * 1024) + 1], 16, 16));
        Assert.Null(ThorVgFeature.Rasterize(Svg("<svg/>"), 0, 16));
        Assert.Null(ThorVgFeature.Rasterize(Svg("<svg/>"), 16, 0));
    }

    [Fact]
    public void PublicRasterizer_ObservesPreCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ThorVgFeature.Rasterize(
                Svg("<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><rect width='1' height='1'/></svg>"),
                1,
                1,
                cancellation.Token));
    }

    [Fact]
    public async Task NativeOwnership_IsStableUnderConcurrentRepeatedRendering()
    {
        byte[] source = Svg(
            "<svg xmlns='http://www.w3.org/2000/svg' width='24' height='24'><defs><linearGradient id='g'><stop stop-color='#f00'/><stop offset='1' stop-color='#00f'/></linearGradient></defs><circle cx='12' cy='12' r='10' fill='url(#g)'/></svg>");

        Task[] workers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int iteration = 0; iteration < 32; iteration++)
            {
                ThorVgRaster? raster = ThorVgFeature.Rasterize(source, 24, 24);
                Assert.NotNull(raster);
                Assert.Equal(24 * 24 * 4, raster.BgraPremultipliedPixels.Length);
            }
        })).ToArray();

        await Task.WhenAll(workers);
    }

    [Fact]
    public void CompiledInterop_UsesLibraryImportCdeclAndSafeHandles()
    {
        Assembly winui = typeof(global::MarkdownRenderer.Controls.MarkdownScrollView).Assembly;
        Type native = Assert.Single(
            winui.GetTypes(),
            type => type.FullName == "MarkdownRenderer.Layout.Boxes.ThorVgNative");
        MethodInfo[] imports = native.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(method => method.Name.StartsWith("tvg_", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(imports);
        foreach (MethodInfo import in imports)
        {
            Assert.NotNull(import.GetCustomAttribute<LibraryImportAttribute>());
            UnmanagedCallConvAttribute? callingConvention = import.GetCustomAttribute<UnmanagedCallConvAttribute>();
            Assert.NotNull(callingConvention);
            Assert.Contains(typeof(CallConvCdecl), callingConvention.CallConvs!);
        }

        Assert.Contains(winui.GetTypes(), type =>
            type.FullName == "MarkdownRenderer.Layout.Boxes.ThorVgNative+SafeTvgCanvasHandle" &&
            typeof(SafeHandle).IsAssignableFrom(type));
        Assert.Contains(winui.GetTypes(), type =>
            type.FullName == "MarkdownRenderer.Layout.Boxes.ThorVgNative+SafeTvgPaintHandle" &&
            typeof(SafeHandle).IsAssignableFrom(type));
    }

    [Fact]
    public void StablePackSurface_DoesNotExposeRendererOrThirdPartyTypes()
    {
        Assembly pack = typeof(ThorVgFeature).Assembly;
        Type[] publicTypes = pack.GetExportedTypes();

        Assert.All(publicTypes.SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)), method =>
        {
            IEnumerable<Type> signature = method.GetParameters().Select(parameter => parameter.ParameterType)
                .Append(method.ReturnType);
            Assert.DoesNotContain(signature, type =>
                (type.Namespace ?? string.Empty).StartsWith("Microsoft.Graphics.Canvas", StringComparison.Ordinal) ||
                (type.Namespace ?? string.Empty).StartsWith("MarkdownRenderer.Layout", StringComparison.Ordinal));
        });
    }
}
