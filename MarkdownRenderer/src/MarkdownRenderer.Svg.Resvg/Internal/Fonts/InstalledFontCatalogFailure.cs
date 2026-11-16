using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal.Fonts;

internal enum InstalledFontCatalogFailure
{
    CatalogUnavailable,
    EmptyCatalog,
    InvalidGeneration,
    MappingFailed,
    ResourceLimit,
    UnsafePath,
}
