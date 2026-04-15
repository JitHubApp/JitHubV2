using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using WinRT;
using WinRT.Interop;

namespace JitHub.WinUI.Helpers;

internal static class DesktopDataTransferManagerHelper
{
    private const string DataTransferManagerRuntimeClassName = "Windows.ApplicationModel.DataTransfer.DataTransferManager";

    private static readonly Guid DataTransferManagerGuid = new("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");
    private static readonly Guid DataTransferManagerInteropGuid = new("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8");

    [StructLayout(LayoutKind.Sequential)]
    private readonly unsafe struct IDataTransferManagerInteropVftbl
    {
        public readonly delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int> QueryInterface;
        public readonly delegate* unmanaged[Stdcall]<IntPtr, uint> AddRef;
        public readonly delegate* unmanaged[Stdcall]<IntPtr, uint> Release;
        public readonly delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int> GetForWindow;
        public readonly delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int> ShowShareUIForWindow;
    }

    public static unsafe DataTransferManager GetForWindow(Window window)
    {
        IntPtr hwnd = WindowNative.GetWindowHandle(window);
        IntPtr interopPtr = GetInteropPointer();
        try
        {
            IDataTransferManagerInteropVftbl* vftbl = *(IDataTransferManagerInteropVftbl**)interopPtr;
            IntPtr managerPtr = IntPtr.Zero;
            Guid managerGuid = DataTransferManagerGuid;
            int hr = vftbl->GetForWindow(interopPtr, hwnd, &managerGuid, &managerPtr);
            Marshal.ThrowExceptionForHR(hr);
            return MarshalInterface<DataTransferManager>.FromAbi(managerPtr);
        }
        finally
        {
            Marshal.Release(interopPtr);
        }
    }

    public static unsafe void ShowShareUIForWindow(Window window)
    {
        IntPtr hwnd = WindowNative.GetWindowHandle(window);
        IntPtr interopPtr = GetInteropPointer();
        try
        {
            IDataTransferManagerInteropVftbl* vftbl = *(IDataTransferManagerInteropVftbl**)interopPtr;
            int hr = vftbl->ShowShareUIForWindow(interopPtr, hwnd);
            Marshal.ThrowExceptionForHR(hr);
        }
        finally
        {
            Marshal.Release(interopPtr);
        }
    }

    private static IntPtr GetInteropPointer()
    {
        IObjectReference activationFactory = ActivationFactory.Get(DataTransferManagerRuntimeClassName);
        IntPtr interopPtr;
        Guid interopGuid = DataTransferManagerInteropGuid;
        int hr = Marshal.QueryInterface(activationFactory.ThisPtr, in interopGuid, out interopPtr);
        Marshal.ThrowExceptionForHR(hr);
        return interopPtr;
    }
}
