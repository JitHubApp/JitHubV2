using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>Owns one read-only installed-font section handle.</summary>
internal sealed class SafeFileMappingHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeFileMappingHandle(nint value) : base(ownsHandle: true) => SetHandle(value);

    protected override bool ReleaseHandle() => InstalledFontNativeMethods.CloseHandle(handle);
}
