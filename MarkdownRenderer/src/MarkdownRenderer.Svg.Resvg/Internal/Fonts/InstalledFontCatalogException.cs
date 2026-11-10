using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal.Fonts;

internal sealed class InstalledFontCatalogException : Exception
{
    internal InstalledFontCatalogException(
        InstalledFontCatalogFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    internal InstalledFontCatalogFailure Failure { get; }
}
