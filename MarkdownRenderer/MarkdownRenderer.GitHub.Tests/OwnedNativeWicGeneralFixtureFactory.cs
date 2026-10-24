using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Creates small, deterministic raster fixtures for the test-only native WIC
/// lifetime qualification. The asymmetric pattern makes axis swaps and corner
/// mapping observable; PNG additionally exercises alpha at its edges.
///
/// No ICC profile is synthesized or borrowed from the machine: the WinRT
/// BitmapEncoder surface used here has no deterministic ICC-payload injection
/// contract, and fabricating a profile without validating its tag structure
/// would not make a trustworthy color-management fixture. The relevant format
/// contract is ICC.1-2022-05 (https://www.color.org/specifications/ICC.1-2022-05.pdf);
/// WIC profile ingestion is separately defined by
/// IWICColorContext.InitializeFromMemory
/// (https://learn.microsoft.com/windows/win32/api/wincodec/nf-wincodec-iwiccolorcontext-initializefrommemory).
/// </summary>
internal static class OwnedNativeWicGeneralFixtureFactory
{
    private const int MaximumDimension = 4096;
    private const long MaximumPixelCount = 16_777_216;

    internal static Task<byte[]> CreatePatternedPngAsync(
        int width = 37,
        int height = 23,
        bool withAlphaEdges = true) =>
        EncodeAsync(
            BitmapEncoder.PngEncoderId,
            width,
            height,
            orientation: null,
            withAlphaEdges: withAlphaEdges);

    internal static Task<byte[]> CreatePatternedBmpAsync(
        int width = 37,
        int height = 23) =>
        EncodeAsync(
            BitmapEncoder.BmpEncoderId,
            width,
            height,
            orientation: null,
            withAlphaEdges: false);

    internal static Task<byte[]> CreatePatternedJpegAsync(
        ushort orientation = 1,
        int width = 37,
        int height = 23) =>
        EncodeAsync(
            BitmapEncoder.JpegEncoderId,
            width,
            height,
            orientation,
            withAlphaEdges: false);

    private static async Task<byte[]> EncodeAsync(
        Guid encoderId,
        int width,
        int height,
        ushort? orientation,
        bool withAlphaEdges)
    {
        ValidateDimensions(width, height);
        if (orientation is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(
                nameof(orientation),
                "EXIF orientation must be in the range 1 through 8.");
        }

        byte[] pixels = CreatePattern(width, height, withAlphaEdges);
        using var stream = new InMemoryRandomAccessStream();

        BitmapEncoder encoder = await AwaitAndCloseAsync(
            BitmapEncoder.CreateAsync(encoderId, stream));

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            withAlphaEdges ? BitmapAlphaMode.Straight : BitmapAlphaMode.Ignore,
            checked((uint)width),
            checked((uint)height),
            96,
            96,
            pixels);

        if (orientation is { } orientationValue)
        {
            var properties = new BitmapPropertySet();
            properties.Add(
                "System.Photo.Orientation",
                new BitmapTypedValue(orientationValue, PropertyType.UInt16));

            await AwaitAndCloseAsync(encoder.BitmapProperties.SetPropertiesAsync(properties));
        }

        await AwaitAndCloseAsync(encoder.FlushAsync());
        stream.Seek(0);

        using var reader = new DataReader(stream.GetInputStreamAt(0));
        uint length = checked((uint)stream.Size);
        await AwaitAndCloseAsync(reader.LoadAsync(length));

        byte[] bytes = new byte[checked((int)length)];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static byte[] CreatePattern(int width, int height, bool withAlphaEdges)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = checked((y * width + x) * 4);
                byte blue = (byte)((x * 17 + y * 7 + 19) & 0xFF);
                byte green = (byte)((x * 3 + y * 23 + 41) & 0xFF);
                byte red = (byte)((x * 29 + y * 5 + 73) & 0xFF);

                // Distinct interior corner markers survive the transparent
                // PNG border and make rotations/reflections easy to diagnose.
                if (x == 2 && y == 2)
                {
                    (blue, green, red) = (12, 24, 240);
                }
                else if (x == width - 3 && y == 2)
                {
                    (blue, green, red) = (18, 224, 32);
                }
                else if (x == 2 && y == height - 3)
                {
                    (blue, green, red) = (232, 36, 20);
                }
                else if (x == width - 3 && y == height - 3)
                {
                    (blue, green, red) = (20, 196, 232);
                }

                pixels[offset] = blue;
                pixels[offset + 1] = green;
                pixels[offset + 2] = red;
                pixels[offset + 3] = withAlphaEdges ? GetEdgeAlpha(x, y, width, height) : byte.MaxValue;
            }
        }

        return pixels;
    }

    private static byte GetEdgeAlpha(int x, int y, int width, int height)
    {
        int edgeDistance = System.Math.Min(
            System.Math.Min(x, width - 1 - x),
            System.Math.Min(y, height - 1 - y));
        return edgeDistance switch
        {
            0 => 0,
            1 => 64,
            2 => 160,
            _ => byte.MaxValue,
        };
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width is <= 0 or > MaximumDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height is <= 0 or > MaximumDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if ((long)width * height > MaximumPixelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Fixture exceeds the pixel limit.");
        }
    }

    private static async Task<T> AwaitAndCloseAsync<T>(IAsyncOperation<T> operation)
    {
        try
        {
            return await operation;
        }
        finally
        {
            operation.Close();
        }
    }

    private static async Task AwaitAndCloseAsync(IAsyncAction operation)
    {
        try
        {
            await operation;
        }
        finally
        {
            operation.Close();
        }
    }
}
