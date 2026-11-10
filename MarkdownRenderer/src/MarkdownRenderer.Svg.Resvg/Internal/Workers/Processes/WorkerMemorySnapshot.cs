using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using MarkdownRenderer.Images;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;

internal readonly record struct WorkerMemorySnapshot(
    int WorkingSetKiB,
    int PrivateCommitKiB,
    int PageFaults)
{
    internal static WorkerMemorySnapshot Unavailable => new(-1, -1, -1);
}
