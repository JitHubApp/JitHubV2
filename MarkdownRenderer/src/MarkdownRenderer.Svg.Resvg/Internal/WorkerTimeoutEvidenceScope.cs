using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using MarkdownRenderer.Images;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal readonly record struct WorkerTimeoutEvidenceScope(
    TimeSpan TotalDeadline,
    long WorkerCpuAtStart,
    long WallStartedAt = 0)
{
    internal static WorkerTimeoutEvidenceScope Resolve(
        TimeSpan requestDeadline,
        long requestCpuAtStart,
        WorkerTimeoutEvidenceScope? outerWindow) =>
        outerWindow ?? new WorkerTimeoutEvidenceScope(
            requestDeadline,
            requestCpuAtStart,
            Stopwatch.GetTimestamp());
}
