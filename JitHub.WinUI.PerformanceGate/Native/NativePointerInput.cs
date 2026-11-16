using System.Drawing;
using System.Runtime.InteropServices;

namespace JitHub.WinUI.PerformanceGate.Native;

/// <summary>
/// Sends native pointer moves, clicks and wheel input through SendInput.
/// </summary>
internal static class NativePointerInput
{
    internal static void SendNativePointerMove(Point target)
    {
        int virtualLeft = GetSystemMetrics(76);
        int virtualTop = GetSystemMetrics(77);
        int virtualWidth = Math.Max(2, GetSystemMetrics(78));
        int virtualHeight = Math.Max(2, GetSystemMetrics(79));
        int normalizedX = (int)Math.Round((target.X - virtualLeft) * 65_535d / (virtualWidth - 1));
        int normalizedY = (int)Math.Round((target.Y - virtualTop) * 65_535d / (virtualHeight - 1));
        NativeInput[] inputs =
        [
            new()
            {
                Type = 0,
                Mouse = new NativeMouseInput
                {
                    X = normalizedX,
                    Y = normalizedY,
                    Flags = 0x0001 | 0x4000 | 0x8000
                }
            }
        ];

        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) != inputs.Length)
        {
            throw new InvalidOperationException("Could not position the native cached-traversal pointer.");
        }
    }

    internal static void SendNativeClick()
    {
        NativeInput[] inputs =
        [
            new() { Type = 0, Mouse = new NativeMouseInput { Flags = 0x0002 } },
            new() { Type = 0, Mouse = new NativeMouseInput { Flags = 0x0004 } }
        ];

        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) != inputs.Length)
        {
            throw new InvalidOperationException("Could not deliver native cached-traversal click.");
        }
    }

    internal static void SendNativeWheel(bool scrollUp)
    {
        NativeInput[] inputs =
        [
            new()
            {
                Type = 0,
                Mouse = new NativeMouseInput
                {
                    MouseData = unchecked((uint)(scrollUp ? 120 : -120)),
                    Flags = 0x0800
                }
            }
        ];

        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) != inputs.Length)
        {
            throw new InvalidOperationException("Could not deliver native scroll-wheel input.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public NativeMouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
