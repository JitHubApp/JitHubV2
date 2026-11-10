using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal.Sources;

/// <summary>Private native memory that backs a large <see cref="SvgSourceStorage"/>.</summary>
internal sealed unsafe class SvgSourceNativeMemory : SafeHandleZeroOrMinusOneIsInvalid
{
    public SvgSourceNativeMemory(int length)
        : base(ownsHandle: true)
    {
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        void* allocation = NativeMemory.Alloc((nuint)length);
        if (allocation is null)
        {
            throw new OutOfMemoryException();
        }

        ByteLength = length;
        SetHandle((nint)allocation);
    }

    public int ByteLength { get; }

    public Span<byte> GetSpan() => new((void*)DangerousGetHandle(), ByteLength);

    public Span<byte> GetSpan(int startIndex, int length)
    {
        ValidateRange(startIndex, length);
        return new Span<byte>((byte*)DangerousGetHandle() + startIndex, length);
    }

    public ReadOnlySpan<byte> GetReadOnlySpan(int startIndex, int length)
    {
        ValidateRange(startIndex, length);
        return new ReadOnlySpan<byte>((byte*)DangerousGetHandle() + startIndex, length);
    }

    protected override bool ReleaseHandle()
    {
        NativeMemory.Free((void*)handle);
        return true;
    }

    private void ValidateRange(int startIndex, int length)
    {
        if (startIndex < 0 || length < 0 || startIndex > ByteLength - length)
        {
            throw new ArgumentOutOfRangeException(nameof(startIndex));
        }
    }
}
