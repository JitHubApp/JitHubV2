using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;

namespace JitHub.WinUI.Automation.Interaction;

/// <summary>
/// Sends native mouse input: moves, drags and the back and forward buttons.
/// </summary>
internal static class MouseInput
{
    internal static void DragBetween(AutomationElement source, AutomationElement target, Window window)
    {
        System.Drawing.Point start = CenterPoint(source, window);
        System.Drawing.Point end = CenterPoint(target, window);
        SendMouseInput(start, MouseEventFlags.MOUSEEVENTF_MOVE);
        SendMouseInput(start, MouseEventFlags.MOUSEEVENTF_LEFTDOWN);
        Thread.Sleep(350);
        for (int step = 1; step <= 24; step++)
        {
            double ratio = step / 24d;
            SendMouseInput(new System.Drawing.Point(
                (int)Math.Round(start.X + ((end.X - start.X) * ratio)),
                (int)Math.Round(start.Y + ((end.Y - start.Y) * ratio))),
                MouseEventFlags.MOUSEEVENTF_MOVE | MouseEventFlags.MOUSEEVENTF_MOVE_NOCOALESCE);
            Thread.Sleep(step == 3 ? 800 : 65);
        }
        Thread.Sleep(250);
        SendMouseInput(end, MouseEventFlags.MOUSEEVENTF_LEFTUP);
        Thread.Sleep(450);
    }

    internal static void SendMouseXButton(Window window, bool forward)
    {
        System.Drawing.Point point = CenterPoint(window, window);
        Mouse.Click(point, forward ? MouseButton.XButton2 : MouseButton.XButton1);
    }

    internal static void SendMouseInput(System.Drawing.Point point, MouseEventFlags flags)
    {
        int virtualLeft = User32.GetSystemMetrics(SystemMetric.SM_XVIRTUALSCREEN);
        int virtualTop = User32.GetSystemMetrics(SystemMetric.SM_YVIRTUALSCREEN);
        int virtualWidth = Math.Max(2, User32.GetSystemMetrics(SystemMetric.SM_CXVIRTUALSCREEN));
        int virtualHeight = Math.Max(2, User32.GetSystemMetrics(SystemMetric.SM_CYVIRTUALSCREEN));
        int normalizedX = (int)Math.Round((point.X - virtualLeft) * 65535d / (virtualWidth - 1));
        int normalizedY = (int)Math.Round((point.Y - virtualTop) * 65535d / (virtualHeight - 1));
        var input = new INPUT
        {
            type = InputType.INPUT_MOUSE,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = normalizedX,
                    dy = normalizedY,
                    dwFlags = flags
                        | MouseEventFlags.MOUSEEVENTF_ABSOLUTE
                        | MouseEventFlags.MOUSEEVENTF_VIRTUALDESK
                }
            }
        };
        uint sent = User32.SendInput(1, [input], Marshal.SizeOf<INPUT>());
        if (sent != 1)
        {
            throw new InvalidOperationException($"SendInput failed for mouse flags {flags}.");
        }
    }
}
