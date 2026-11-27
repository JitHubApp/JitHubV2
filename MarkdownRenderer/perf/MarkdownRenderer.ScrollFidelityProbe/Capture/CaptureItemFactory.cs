using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace MarkdownRenderer.ScrollFidelityProbe.Capture;

/// <summary>Creates a capture item for a window through <c>IGraphicsCaptureItemInterop</c>.</summary>
internal static class CaptureItemFactory
{
    private static readonly Guid InteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid ItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    internal static unsafe GraphicsCaptureItem CreateForWindow(nint window)
    {
        using WinRT.IObjectReference factory =
            WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        Guid interopIid = InteropIid;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, in interopIid, out nint interop));
        try
        {
            // IGraphicsCaptureItemInterop::CreateForWindow is the first method after IUnknown.
            var createForWindow = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)
                (*(void***)interop)[3];
            Guid itemIid = ItemIid;
            nint item;
            Marshal.ThrowExceptionForHR(createForWindow(interop, window, &itemIid, &item));
            try
            {
                return GraphicsCaptureItem.FromAbi(item);
            }
            finally
            {
                Marshal.Release(item);
            }
        }
        finally
        {
            Marshal.Release(interop);
        }
    }
}
