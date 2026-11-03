using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>A worker lease whose worker has published the requested font generation.</summary>
internal sealed record TextWorkerAdmission(WorkerLease Lease, WindowsWorkerProcess Worker);
