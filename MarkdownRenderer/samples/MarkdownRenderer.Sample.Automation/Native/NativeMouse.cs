using FlaUI.Core.Input;

namespace MarkdownRenderer.Sample.Automation.Native;

/// <summary>
/// Sends absolute mouse input through SendInput so drags and presses reach the input desktop exactly.
/// </summary>
internal static class NativeMouse
{
    private const uint InputMouse = 0;
    private const int SystemMetricVirtualScreenX = 76;
    private const int SystemMetricVirtualScreenY = 77;
    private const int SystemMetricVirtualScreenWidth = 78;
    private const int SystemMetricVirtualScreenHeight = 79;

    internal static void DragMouseThrough(System.Drawing.Point start, System.Drawing.Point end)
    {
        const int steps = 24;
        SendMouseMove(start);
        Thread.Sleep(100);
        SendMouseButton(MouseEventFlags.LeftDown);
        try
        {
            Thread.Sleep(80);
            for (int i = 1; i <= steps; i++)
            {
                double t = i / (double)steps;
                var point = new System.Drawing.Point(
                    (int)Math.Round(start.X + (end.X - start.X) * t),
                    (int)Math.Round(start.Y + (end.Y - start.Y) * t));
                SendMouseMove(point);
                Thread.Sleep(25);
            }
        }
        finally
        {
            SendMouseButton(MouseEventFlags.LeftUp);
        }
    }

    internal static void SendMouseMove(System.Drawing.Point point)
    {
        int vx = GetSystemMetrics(SystemMetricVirtualScreenX);
        int vy = GetSystemMetrics(SystemMetricVirtualScreenY);
        int vw = Math.Max(1, GetSystemMetrics(SystemMetricVirtualScreenWidth));
        int vh = Math.Max(1, GetSystemMetrics(SystemMetricVirtualScreenHeight));
        int absoluteX = (int)Math.Round((point.X - vx) * 65535.0 / (vw - 1));
        int absoluteY = (int)Math.Round((point.Y - vy) * 65535.0 / (vh - 1));
        SendMouseInput(MouseEventFlags.Move | MouseEventFlags.Absolute | MouseEventFlags.VirtualDesk, absoluteX, absoluteY);
    }

    internal static void SendLeftButtonDown()
        => SendMouseButton(MouseEventFlags.LeftDown);

    internal static void SendLeftButtonUp()
        => SendMouseButton(MouseEventFlags.LeftUp);

    private static void SendMouseButton(MouseEventFlags flags)
        => SendMouseInput(flags, 0, 0);

    private static void SendMouseInput(MouseEventFlags flags, int dx, int dy)
    {
        var input = new Input
        {
            Type = InputMouse,
            MouseInput = new MouseInput
            {
                Dx = dx,
                Dy = dy,
                MouseData = 0,
                Flags = flags,
                Time = 0,
                ExtraInfo = UIntPtr.Zero,
            },
        };
        uint sent = SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<Input>());
        if (sent != 1)
        {
            throw new System.ComponentModel.Win32Exception(
                System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                $"SendInput failed for mouse flags {flags}");
        }
    }

    internal static void PositionPointerOnWritableInputDesktop(System.Drawing.Point target)
    {
        System.Drawing.Point original = Mouse.Position;
        var probe = new System.Drawing.Point(
            target.X == int.MaxValue ? target.X - 1 : target.X + 1,
            target.Y);
        Mouse.Position = probe;
        Thread.Sleep(50);
        System.Drawing.Point actual = Mouse.Position;
        if (actual != probe)
            throw new InvalidOperationException(
                "Pointer-input infrastructure precondition failed: the automation process cannot move " +
                $"the desktop cursor from {original} to {probe} (actual={actual}). " +
                "Run this probe on an interactive, unlocked, writable input desktop.");

        Mouse.Position = target;
        Thread.Sleep(50);
        actual = Mouse.Position;
        if (actual != target)
            throw new InvalidOperationException(
                "Pointer-input infrastructure precondition failed: the automation process moved a probe cursor " +
                $"but could not position it on the Mermaid link at {target} (actual={actual}).");
    }

    [Flags]
    private enum MouseEventFlags : uint
    {
        Move = 0x0001,
        LeftDown = 0x0002,
        LeftUp = 0x0004,
        Absolute = 0x8000,
        VirtualDesk = 0x4000,
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput MouseInput;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public MouseEventFlags Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPArray), System.Runtime.InteropServices.In] Input[] inputs,
        int inputSize);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
