using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace MarkdownRenderer.Layout.Boxes;

// Generated COM declarations for the small in-process WIC surface used by the
// synchronous, owned raster decoder. Keep these declarations local to this
// assembly: the decoder never uses reflection or projected WinRT operations.
internal static partial class RasterNativeWicInterop
{
    internal const uint ClsctxInprocServer = 0x1;
    internal const int DecodeMetadataCacheOnDemand = 0;
    internal const int BitmapInterpolationModeFant = 3;
    internal const int BitmapDitherTypeNone = 0;
    internal const int BitmapPaletteTypeCustom = 0;
    internal const int ColorContextTypeProfile = 1;
    internal const int ColorContextTypeExifColorSpace = 2;
    internal const int WicUnsupportedOperation = unchecked((int)0x88982F81);

    internal static readonly Guid ClsidWicImagingFactory2 = new("317d06e8-5f24-433d-bdf7-79ce68d8abc2");
    internal static readonly Guid IidWicImagingFactory = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
    internal static readonly Guid IidIStream = new("0000000c-0000-0000-c000-000000000046");
    internal static readonly Guid IidWicBitmapSource = new("00000120-a8f2-4877-ba0a-fd2b6645fb94");
    internal static readonly Guid IidWicColorContext = new("3c613a02-34b2-44ea-9a7c-45aea9c6fd6d");
    internal static readonly Guid WicPixelFormat32bppPbgra = new("6fddc324-4e03-4bfe-b185-3d77768dc910");
    // Windows SDK GUID_ContainerFormat* constants. The worker-selected codec
    // must agree with the same-source host admission, not just its dimensions.
    internal static readonly Guid ContainerFormatPng = new("1b7cfaf4-713f-473c-bbcd-6137425faeaf");
    internal static readonly Guid ContainerFormatBmp = new("0af1d87e-fcfe-4188-bdeb-a7906471cbe3");
    internal static readonly Guid ContainerFormatJpeg = new("19e4a5aa-5662-4fc5-a0c0-1758028e1057");
    internal static readonly Guid ContainerFormatWebp = new("e094b0e2-67f2-45b3-b0ea-115337ca7cf3");
    internal static readonly Guid ContainerFormatGif = new("1f8a5601-7d4d-4cbd-9c82-1bc8d4eeb9a5");

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
    [Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe partial interface IWICImagingFactory
    {
        [PreserveSig] int CreateDecoderFromFilename(char* filename, Guid* vendor, uint access, int options, nint* decoder);
        [PreserveSig] int CreateDecoderFromStream(nint stream, nint vendor, int options, nint* decoder);
        [PreserveSig] int CreateDecoderFromFileHandle(nuint fileHandle, nint vendor, int options, nint* decoder);
        [PreserveSig] int CreateComponentInfo(Guid* clsid, nint* componentInfo);
        [PreserveSig] int CreateDecoder(Guid* containerFormat, nint vendor, nint* decoder);
        [PreserveSig] int CreateEncoder(Guid* containerFormat, nint vendor, nint* encoder);
        [PreserveSig] int CreatePalette(nint* palette);
        [PreserveSig] int CreateFormatConverter(nint* converter);
        [PreserveSig] int CreateBitmapScaler(nint* scaler);
        [PreserveSig] int CreateBitmapClipper(nint* clipper);
        [PreserveSig] int CreateBitmapFlipRotator(nint* flipRotator);
        [PreserveSig] int CreateStream(nint* stream);
        [PreserveSig] int CreateColorContext(nint* colorContext);
        [PreserveSig] int CreateColorTransformer(nint* colorTransform);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
    [Guid("0000000c-0000-0000-c000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe partial interface IStreamAbi
    {
        [PreserveSig] int Read(void* buffer, uint count, uint* read);
        [PreserveSig] int Write(void* buffer, uint count, uint* written);
        [PreserveSig] int Seek(long move, uint origin, ulong* newPosition);
        [PreserveSig] int SetSize(ulong size);
        [PreserveSig] int CopyTo(nint target, ulong count, ulong* read, ulong* written);
        [PreserveSig] int Commit(uint flags);
        [PreserveSig] int Revert();
        [PreserveSig] int LockRegion(ulong offset, ulong count, uint lockType);
        [PreserveSig] int UnlockRegion(ulong offset, ulong count, uint lockType);
        [PreserveSig] int Stat(nint stat, uint flags);
        [PreserveSig] int Clone(nint* stream);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
    [Guid("135ff860-22b7-4ddf-b0f6-218f4f299a43")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe partial interface IWICStream : IStreamAbi
    {
        [PreserveSig] int InitializeFromIStream(nint stream);
        [PreserveSig] int InitializeFromFilename(char* filename, uint access);
        [PreserveSig] int InitializeFromMemory(byte* buffer, uint bufferSize);
        [PreserveSig] int InitializeFromIStreamRegion(nint stream, ulong offset, ulong maximumSize);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
    [Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe partial interface IWICBitmapDecoder
    {
        [PreserveSig] int QueryCapability(nint stream, uint* capability);
        [PreserveSig] int Initialize(nint stream, int options);
        [PreserveSig] int GetContainerFormat(Guid* format);
        [PreserveSig] int GetDecoderInfo(nint* decoderInfo);
        [PreserveSig] int CopyPalette(nint palette);
        [PreserveSig] int GetMetadataQueryReader(nint* reader);
        [PreserveSig] int GetPreview(nint* preview);
        [PreserveSig] int GetColorContexts(uint count, nint* contexts, uint* actualCount);
        [PreserveSig] int GetThumbnail(nint* thumbnail);
        [PreserveSig] int GetFrameCount(uint* count);
        [PreserveSig] int GetFrame(uint index, nint* frame);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
    [Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe partial interface IWICBitmapSource
    {
        [PreserveSig] int GetSize(uint* width, uint* height);
        [PreserveSig] int GetPixelFormat(Guid* pixelFormat);
        [PreserveSig] int GetResolution(double* dpiX, double* dpiY);
        [PreserveSig] int CopyPalette(nint palette);
        [PreserveSig] int CopyPixels(nint rect, uint stride, uint bufferSize, byte* buffer);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
    [Guid("3b16811b-6a43-4ec9-a813-3d930c13b940")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe partial interface IWICBitmapFrameDecode : IWICBitmapSource
    {
        [PreserveSig] int GetMetadataQueryReader(nint* reader);
        [PreserveSig] int GetColorContexts(uint count, nint* contexts, uint* actualCount);
        [PreserveSig] int GetThumbnail(nint* thumbnail);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
    [Guid("00000302-a8f2-4877-ba0a-fd2b6645fb94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe partial interface IWICBitmapScaler : IWICBitmapSource
    {
        [PreserveSig] int Initialize(nint source, uint width, uint height, int interpolationMode);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
    [Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe partial interface IWICFormatConverter : IWICBitmapSource
    {
        [PreserveSig]
        int Initialize(
            nint source,
            Guid* destinationFormat,
            int dither,
            nint palette,
            double alphaThresholdPercent,
            int paletteTranslate);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
    [Guid("3c613a02-34b2-44ea-9a7c-45aea9c6fd6d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe partial interface IWICColorContext
    {
        [PreserveSig] int InitializeFromFilename(char* filename);
        [PreserveSig] int InitializeFromMemory(byte* buffer, uint bufferSize);
        [PreserveSig] int InitializeFromExifColorSpace(uint value);
        [PreserveSig] int GetType(uint* type);
        [PreserveSig] int GetProfileBytes(uint bufferSize, byte* buffer, uint* actualSize);
        [PreserveSig] int GetExifColorSpace(uint* value);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
    [Guid("b66f034f-d0e2-40ab-b436-6de39e321a94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe partial interface IWICColorTransform : IWICBitmapSource
    {
        [PreserveSig]
        int Initialize(
            nint source,
            nint sourceColorContext,
            nint destinationColorContext,
            Guid* destinationFormat);
    }
}
