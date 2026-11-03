using Microsoft.Win32.SafeHandles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MarkdownRenderer.Mermaid;

internal sealed class MermaidCancellationHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal MermaidCancellationHandle(nint value)
        : base(ownsHandle: true) => SetHandle(value);

    internal void Request()
    {
        if (IsInvalid || IsClosed)
        {
            return;
        }

        try
        {
            _ = NativeMethods.CancellationRequest(handle);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // Cancellation is best-effort during native teardown. No exception crosses the callback boundary.
        }
    }

    protected override bool ReleaseHandle()
    {
        try
        {
            return NativeMethods.CancellationRelease(handle) == NativeStatus.Success;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }
}
