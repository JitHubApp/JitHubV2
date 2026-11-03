using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal sealed class WorkerStartupExitCodeException : Exception
{
    internal WorkerStartupExitCodeException(uint nativeExitCode, Exception? innerException)
        : base($"The resvg worker exited with native exit code 0x{nativeExitCode:X8}.", innerException)
    {
        NativeExitCode = nativeExitCode;
    }

    internal uint NativeExitCode { get; }
}
