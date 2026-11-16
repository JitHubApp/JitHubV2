using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Scheduling;

namespace MarkdownRenderer.Svg.Resvg.Internal.Workers.FontReadiness;

/// <summary>A worker lease whose worker has published the requested font generation.</summary>
internal sealed record TextWorkerAdmission(WorkerLease Lease, WindowsWorkerProcess Worker);
