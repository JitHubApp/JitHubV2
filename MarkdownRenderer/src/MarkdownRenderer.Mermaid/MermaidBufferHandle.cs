using Microsoft.Win32.SafeHandles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MarkdownRenderer.Mermaid;

internal sealed class MermaidBufferHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal MermaidBufferHandle(nint value)
        : base(ownsHandle: true) => SetHandle(value);

    protected override bool ReleaseHandle()
    {
        try
        {
            return NativeMethods.BufferRelease(handle) == NativeStatus.Success;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }
}
