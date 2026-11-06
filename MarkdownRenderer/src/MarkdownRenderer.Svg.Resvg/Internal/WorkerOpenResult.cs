using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal readonly record struct WorkerOpenResult(
    WorkerResponse Response,
    WorkerDocumentHandle Document);
