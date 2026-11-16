using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Mermaid.NativeRuntime.Internal;

internal sealed class MermaidEngineHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal MermaidEngineHandle(nint value)
        : base(ownsHandle: true) => SetHandle(value);

    protected override bool ReleaseHandle()
    {
        try
        {
            return NativeMethods.EngineRelease(handle) == NativeStatus.Success;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }
}
