using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using static JitHub.WinUI.Automation.Probes.Repositories.RepositoryLibraryProbe;

namespace JitHub.WinUI.Automation.Probes.Repositories;

/// <summary>
/// Moves the native pointer over repository library rows and opens their context menus.
/// </summary>
internal static class RepositoryLibraryPointer
{
    internal static void MovePointer(AutomationElement element)
    {
        Rectangle bounds = element.BoundingRectangle;
        Point target = new(bounds.X + Math.Min(96, bounds.Width / 2), bounds.Y + Math.Min(24, bounds.Height / 2));
        SendMouseMove(target);
    }

    internal static void MovePointerAwayFromRows(Window window)
    {
        Rectangle bounds = window.BoundingRectangle;
        Mouse.MoveTo(new Point(bounds.X + Math.Min(300, Math.Max(180, bounds.Width / 4)), bounds.Y + 18));
    }

    internal static void RightClickVisible(Window window, AutomationElement element)
    {
        window.SetForeground();
        if (element.Patterns.ScrollItem.IsSupported)
        {
            element.Patterns.ScrollItem.Pattern.ScrollIntoView();
            Thread.Sleep(100);
        }

        element.FocusNative();
        Rectangle bounds = element.BoundingRectangle;
        Assert(bounds.Width > 0 && bounds.Height > 0, "Repository row has no visible pointer target.");
        Mouse.RightClick(new Point(bounds.X + bounds.Width / 2, bounds.Y + Math.Min(24, bounds.Height / 2)));
    }

    private static void SendMouseMove(Point target)
    {
        int virtualLeft = GetSystemMetrics(76);
        int virtualTop = GetSystemMetrics(77);
        int virtualWidth = Math.Max(2, GetSystemMetrics(78));
        int virtualHeight = Math.Max(2, GetSystemMetrics(79));
        int normalizedX = (int)Math.Round((target.X - virtualLeft) * 65535d / (virtualWidth - 1));
        int normalizedY = (int)Math.Round((target.Y - virtualTop) * 65535d / (virtualHeight - 1));
        NativeInput[] inputs =
        [
            new NativeInput
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
            throw new InvalidOperationException("Could not deliver native mouse input for repository hover verification.");
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
