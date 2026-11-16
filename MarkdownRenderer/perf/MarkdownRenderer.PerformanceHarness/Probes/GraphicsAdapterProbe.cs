using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Graphics.Canvas;
using WinRT;

namespace MarkdownRenderer.PerformanceHarness.Probes;

/// <summary>
/// Identifies the GPU adapter that renders the benchmark (through the Win2D
/// device) and the adapter that drives its display (through D3DKMT), with
/// their driver versions.
/// </summary>
internal static class GraphicsAdapterProbe
{
    private static readonly Guid Direct3DDxgiInterfaceAccessId =
        new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid DxgiDeviceId =
        new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    internal static GraphicsAdapterMetadata CaptureRenderingAdapter(CanvasDevice graphicsDevice)
    {
        if (!TryGetCanvasDeviceAdapterLuid(graphicsDevice, out LocallyUniqueIdentifier adapterLuid))
            return new GraphicsAdapterMetadata(string.Empty, string.Empty, string.Empty);

        var open = new D3dKmtOpenAdapterFromLuidData
        {
            AdapterLuid = adapterLuid,
        };
        if (D3dKmtOpenAdapterFromLuid(ref open) != 0)
            return new GraphicsAdapterMetadata(string.Empty, string.Empty, string.Empty);

        try
        {
            return CaptureOpenAdapter(open.Adapter, adapterLuid);
        }
        finally
        {
            CloseAdapter(open.Adapter);
        }
    }

    private static bool TryGetCanvasDeviceAdapterLuid(
        CanvasDevice graphicsDevice,
        out LocallyUniqueIdentifier adapterLuid)
    {
        adapterLuid = default;
        IntPtr canvasAbi = IntPtr.Zero;
        IntPtr interfaceAccess = IntPtr.Zero;
        IntPtr dxgiDevice = IntPtr.Zero;
        IntPtr adapter = IntPtr.Zero;
        try
        {
            canvasAbi = MarshalInspectable<CanvasDevice>.FromManaged(graphicsDevice);
            Guid interfaceAccessId = Direct3DDxgiInterfaceAccessId;
            if (Marshal.QueryInterface(canvasAbi, in interfaceAccessId, out interfaceAccess) < 0)
                return false;

            GetDxgiInterface getInterface = GetVtableMethod<GetDxgiInterface>(interfaceAccess, 3);
            Guid dxgiDeviceId = DxgiDeviceId;
            if (getInterface(interfaceAccess, ref dxgiDeviceId, out dxgiDevice) < 0)
                return false;

            GetDxgiAdapter getAdapter = GetVtableMethod<GetDxgiAdapter>(dxgiDevice, 7);
            if (getAdapter(dxgiDevice, out adapter) < 0)
                return false;

            GetDxgiAdapterDescription getDescription =
                GetVtableMethod<GetDxgiAdapterDescription>(adapter, 8);
            if (getDescription(adapter, out DxgiAdapterDescription description) < 0)
                return false;

            adapterLuid = description.AdapterLuid;
            return true;
        }
        finally
        {
            ReleaseComPointer(adapter);
            ReleaseComPointer(dxgiDevice);
            ReleaseComPointer(interfaceAccess);
            if (canvasAbi != IntPtr.Zero)
                MarshalInspectable<CanvasDevice>.DisposeAbi(canvasAbi);
        }
    }

    private static TDelegate GetVtableMethod<TDelegate>(IntPtr instance, int slot)
        where TDelegate : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(instance);
        IntPtr method = Marshal.ReadIntPtr(vtable, checked(slot * IntPtr.Size));
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(method);
    }

    private static void ReleaseComPointer(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
            _ = Marshal.Release(pointer);
    }

    internal static GraphicsAdapterMetadata CaptureDisplayAdapter(string displayDeviceName)
    {
        if (string.IsNullOrWhiteSpace(displayDeviceName))
            return new GraphicsAdapterMetadata(string.Empty, string.Empty, string.Empty);

        IntPtr deviceContext = CreateDC(null, displayDeviceName, null, IntPtr.Zero);
        if (deviceContext == IntPtr.Zero)
            return new GraphicsAdapterMetadata(string.Empty, string.Empty, string.Empty);

        try
        {
            var open = new D3dKmtOpenAdapterFromHdcData
            {
                DeviceContext = deviceContext,
            };
            if (D3dKmtOpenAdapterFromHdc(ref open) != 0)
                return new GraphicsAdapterMetadata(string.Empty, string.Empty, string.Empty);

            try
            {
                return CaptureOpenAdapter(open.Adapter, open.AdapterLuid);
            }
            finally
            {
                CloseAdapter(open.Adapter);
            }
        }
        finally
        {
            _ = DeleteDC(deviceContext);
        }
    }

    private static GraphicsAdapterMetadata CaptureOpenAdapter(
        uint adapter,
        LocallyUniqueIdentifier adapterLuid)
    {
        string luid = $"{unchecked((uint)adapterLuid.HighPart):X8}:" +
            $"{adapterLuid.LowPart:X8}";
        return new GraphicsAdapterMetadata(
            GetGpuAdapterIdentitySha256(adapter),
            luid,
            GetUmdDriverVersion(adapter));
    }

    private static void CloseAdapter(uint adapter)
    {
        var close = new D3dKmtCloseAdapterData
        {
            Adapter = adapter,
        };
        _ = D3dKmtCloseAdapter(ref close);
    }

    private static string GetGpuAdapterIdentitySha256(uint adapter)
    {
        var physicalAdapterCount = new D3dKmtPhysicalAdapterCount();
        var adapterAddress = new D3dKmtAdapterAddress();
        if (!TryQueryAdapterInfo(
                adapter,
                KmtQueryAdapterInfoType.PhysicalAdapterCount,
                ref physicalAdapterCount) ||
            !TryQueryAdapterInfo(
                adapter,
                KmtQueryAdapterInfoType.AdapterAddress,
                ref adapterAddress) ||
            physicalAdapterCount.Count == 0 ||
            physicalAdapterCount.Count > 64)
            return string.Empty;

        var canonicalDescription = new StringBuilder();
        canonicalDescription.AppendLine("MarkdownRenderer.Performance.GpuAdapter/v2");
        canonicalDescription.Append("physicalAdapterCount=")
            .Append(physicalAdapterCount.Count)
            .Append('\n');
        canonicalDescription.Append("adapterAddress=")
            .Append(adapterAddress.BusNumber.ToString("X8"))
            .Append(':').Append(adapterAddress.DeviceNumber.ToString("X8"))
            .Append(':').Append(adapterAddress.FunctionNumber.ToString("X8"))
            .Append('\n');
        for (uint index = 0; index < physicalAdapterCount.Count; index++)
        {
            var query = new D3dKmtQueryDeviceIds
            {
                PhysicalAdapterIndex = index,
            };
            if (!TryQueryAdapterInfo(
                    adapter,
                    KmtQueryAdapterInfoType.PhysicalAdapterDeviceIds,
                    ref query))
                return string.Empty;

            canonicalDescription
                .Append("adapter[").Append(index).Append("]=")
                .Append(query.DeviceIds.VendorId.ToString("X8"))
                .Append(':').Append(query.DeviceIds.DeviceId.ToString("X8"))
                .Append(':').Append(query.DeviceIds.SubVendorId.ToString("X8"))
                .Append(':').Append(query.DeviceIds.SubSystemId.ToString("X8"))
                .Append(':').Append(query.DeviceIds.RevisionId.ToString("X8"))
                .Append(':').Append(query.DeviceIds.BusType.ToString("X8"))
                .Append('\n');
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalDescription.ToString())));
    }

    private static string GetUmdDriverVersion(uint adapter)
    {
        var version = new D3dKmtUmdDriverVersion();
        if (!TryQueryAdapterInfo(
                adapter,
                KmtQueryAdapterInfoType.UmdDriverVersion,
                ref version) ||
            version.DriverVersion == 0)
            return string.Empty;

        ulong bits = unchecked((ulong)version.DriverVersion);
        return string.Join(
            '.',
            (bits >> 48) & 0xFFFF,
            (bits >> 32) & 0xFFFF,
            (bits >> 16) & 0xFFFF,
            bits & 0xFFFF);
    }

    private static bool TryQueryAdapterInfo<T>(
        uint adapter,
        KmtQueryAdapterInfoType type,
        ref T data)
        where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(data, buffer, fDeleteOld: false);
            var query = new D3dKmtQueryAdapterInfoData
            {
                Adapter = adapter,
                Type = type,
                PrivateDriverData = buffer,
                PrivateDriverDataSize = checked((uint)size),
            };
            if (D3dKmtQueryAdapterInfo(ref query) != 0)
                return false;

            data = Marshal.PtrToStructure<T>(buffer);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDC(
        string? driver,
        string device,
        string? output,
        IntPtr initializationData);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", EntryPoint = "D3DKMTOpenAdapterFromHdc")]
    private static extern int D3dKmtOpenAdapterFromHdc(
        ref D3dKmtOpenAdapterFromHdcData data);

    [DllImport("gdi32.dll", EntryPoint = "D3DKMTOpenAdapterFromLuid")]
    private static extern int D3dKmtOpenAdapterFromLuid(
        ref D3dKmtOpenAdapterFromLuidData data);

    [DllImport("gdi32.dll", EntryPoint = "D3DKMTCloseAdapter")]
    private static extern int D3dKmtCloseAdapter(ref D3dKmtCloseAdapterData data);

    [DllImport("gdi32.dll", EntryPoint = "D3DKMTQueryAdapterInfo")]
    private static extern int D3dKmtQueryAdapterInfo(
        ref D3dKmtQueryAdapterInfoData data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDxgiInterface(
        IntPtr instance,
        ref Guid interfaceId,
        out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDxgiAdapter(IntPtr instance, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDxgiAdapterDescription(
        IntPtr instance,
        out DxgiAdapterDescription description);

    [StructLayout(LayoutKind.Sequential)]
    private struct LocallyUniqueIdentifier
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dKmtOpenAdapterFromHdcData
    {
        public IntPtr DeviceContext;
        public uint Adapter;
        public LocallyUniqueIdentifier AdapterLuid;
        public uint VideoPresentSourceId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dKmtOpenAdapterFromLuidData
    {
        public LocallyUniqueIdentifier AdapterLuid;
        public uint Adapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dKmtCloseAdapterData
    {
        public uint Adapter;
    }

    private enum KmtQueryAdapterInfoType
    {
        AdapterAddress = 6,
        UmdDriverVersion = 18,
        PhysicalAdapterCount = 30,
        PhysicalAdapterDeviceIds = 31,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dKmtQueryAdapterInfoData
    {
        public uint Adapter;
        public KmtQueryAdapterInfoType Type;
        public IntPtr PrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dKmtPhysicalAdapterCount
    {
        public uint Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dKmtAdapterAddress
    {
        public uint BusNumber;
        public uint DeviceNumber;
        public uint FunctionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dKmtUmdDriverVersion
    {
        public long DriverVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dKmtDeviceIds
    {
        public uint VendorId;
        public uint DeviceId;
        public uint SubVendorId;
        public uint SubSystemId;
        public uint RevisionId;
        public uint BusType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dKmtQueryDeviceIds
    {
        public uint PhysicalAdapterIndex;
        public D3dKmtDeviceIds DeviceIds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSystemId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public LocallyUniqueIdentifier AdapterLuid;
    }

}
