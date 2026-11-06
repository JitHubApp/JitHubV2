using System;
using System.Runtime.InteropServices;
using WinRT;

namespace MarkdownRenderer.Layout.Boxes;

// COM plumbing shared by the native color-glyph renderer: borrows Direct2D and
// DirectWrite interfaces from projected Win2D objects through
// ICanvasResourceWrapperNative, creates the shared IDWriteFactory4, and
// releases raw COM references. Declared partial because LibraryImport
// generates the DWriteCreateFactory marshalling stub.
internal static unsafe partial class Win2DNativeComInterop
{
    private static readonly Guid IidCanvasResourceWrapperNative = new("5f10688d-ea55-4d55-a3b0-4ddb55c0c20a");
    private static readonly Guid IidDWriteFactory4 = new("4b0b5bd3-0797-4549-8ac5-fe915cc53856");
    private const int ENoInterface = unchecked((int)0x80004002);
    private const uint DWriteFactoryTypeShared = 0;

    internal static nint CreateDWriteFactory4()
    {
        nint factory = 0;
        Guid iid = IidDWriteFactory4;
        int hr = DWriteCreateFactory(DWriteFactoryTypeShared, &iid, &factory);
        if (hr < 0)
        {
            Release(ref factory);
            Marshal.ThrowExceptionForHR(hr);
        }
        if (factory == 0)
            throw new InvalidOperationException("DWriteCreateFactory succeeded without returning IDWriteFactory4.");
        return factory;
    }

    internal static nint GetNativeResource(object wrapper, Guid resourceIid)
    {
        if (!TryGetNativeResource(wrapper, resourceIid, out nint resource))
            throw new COMException($"Win2D did not expose the required native resource {resourceIid:B}.", ENoInterface);
        return resource;
    }

    internal static bool TryGetNativeResource(object wrapper, Guid resourceIid, out nint resource)
    {
        ArgumentNullException.ThrowIfNull(wrapper);
        if (wrapper is not IWinRTObject winrtObject)
            throw new InvalidOperationException("The Win2D object does not expose its projected WinRT reference.");

        nint inspectable = winrtObject.NativeObject.GetRef();
        nint resourceWrapper = 0;
        try
        {
            if (!TryQueryInterface(inspectable, IidCanvasResourceWrapperNative, out resourceWrapper))
                throw new InvalidOperationException("The Win2D object does not implement ICanvasResourceWrapperNative.");

            nint nativeResource = 0;
            Guid iid = resourceIid;
            var vtable = *(nint**)resourceWrapper;
            var getNativeResource = (delegate* unmanaged[Stdcall]<nint, nint, float, Guid*, nint*, int>)vtable[3];
            int result = getNativeResource(resourceWrapper, 0, 0f, &iid, &nativeResource);
            if (result == ENoInterface)
            {
                Release(ref nativeResource);
                resource = 0;
                return false;
            }
            if (result < 0)
            {
                Release(ref nativeResource);
                Marshal.ThrowExceptionForHR(result);
            }
            if (nativeResource == 0)
                throw new InvalidOperationException("ICanvasResourceWrapperNative::GetNativeResource returned null.");

            resource = nativeResource;
            return true;
        }
        finally
        {
            Release(ref resourceWrapper);
            Release(ref inspectable);
        }
    }

    private static bool TryQueryInterface(nint source, Guid iid, out nint result)
    {
        nint queried = 0;
        Guid requestedIid = iid;
        var vtable = *(nint**)source;
        var queryInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[0];
        int hr = queryInterface(source, &requestedIid, &queried);
        if (hr == ENoInterface)
        {
            Release(ref queried);
            result = 0;
            return false;
        }
        if (hr < 0)
        {
            Release(ref queried);
            result = 0;
            Marshal.ThrowExceptionForHR(hr);
        }
        if (queried == 0)
            throw new InvalidOperationException("QueryInterface succeeded but returned null.");
        result = queried;
        return true;
    }

    internal static void Release(ref nint pointer)
    {
        nint value = pointer;
        pointer = 0;
        if (value == 0)
            return;

        var vtable = *(nint**)value;
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
        _ = release(value);
    }

    [LibraryImport("dwrite.dll", EntryPoint = "DWriteCreateFactory")]
    private static partial int DWriteCreateFactory(uint factoryType, Guid* factoryIid, nint* factory);
}
