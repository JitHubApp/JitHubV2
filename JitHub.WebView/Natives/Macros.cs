using Microsoft.Web.WebView2.Core;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Input;
using Windows.UI.Xaml.Input;
using Windows.Win32.Foundation;
using Windows.Win32.System.SystemServices;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WebView2Ex.Natives;

class Macros
{
    public static uint MakeWParam(ushort low, ushort high)
    => ((uint)high << 16) | (uint)low;

    public static int MakeLParam(int LoWord, int HiWord)
        => ((HiWord << 16) | (LoWord & 0xffff));

    public static long HiWord(LPARAM Number)
        => (Number >> 16) & 0xffff;

    public static long LoWord(LPARAM Number)
        => Number & 0xffff;
    public static ulong HiWord(WPARAM Number)
        => (Number >> 16) & 0xffff;
    public static ulong LoWord(WPARAM Number)
        => Number & 0xffff;

    public static short GetWheelDataWParam(WPARAM wParam)
        => (short)HiWord(wParam);
    public static short GetXButtonWParam(WPARAM wParam)
        => (short)HiWord(wParam);
    public static short GetKeystateWParam(WPARAM wParam)
        => (short)LoWord(wParam);

    public static LPARAM PackIntoWin32StylePointerArgs_lparam(
    CoreWebView2MouseEventKind _,
    PointerRoutedEventArgs _1, Point point)
    {
        // These are the same for WM_POINTER and WM_MOUSE based events
        // Pointer: https://msdn.microsoft.com/en-us/ie/hh454929(v=vs.80)
        // Mouse: https://docs.microsoft.com/en-us/windows/desktop/inputdev/wm-mousemove
        LPARAM lParam = MakeLParam((int)point.X, (int)point.Y);
        return lParam;
    }

    public static WPARAM PackIntoWin32StyleMouseArgs_wparam(
    CoreWebView2MouseEventKind message,
    PointerRoutedEventArgs args, PointerPoint pointerPoint)
    {
        ushort lowWord = 0x0;       // unsigned modifier flags
        ushort highWord = 0x0;     // signed wheel delta

        VirtualKeyModifiers modifiers = args.KeyModifiers;

        // can support cases like Ctrl|Alt + Scroll where Alt will be ignored and it will be treated as Ctrl + Scroll
        if (((int)modifiers & (int)VirtualKeyModifiers.Control) != 0)
        {
            lowWord |= (ushort)MODIFIERKEYS_FLAGS.MK_CONTROL;
        }
        if (((int)modifiers & (int)VirtualKeyModifiers.Shift) != 0)
        {
            lowWord |= (ushort)MODIFIERKEYS_FLAGS.MK_SHIFT;
        }

        PointerPointProperties properties = pointerPoint.Properties;

        if (properties.IsLeftButtonPressed)
        {
            lowWord |= (ushort)MODIFIERKEYS_FLAGS.MK_LBUTTON;
        }
        if (properties.IsRightButtonPressed)
        {
            lowWord |= (ushort)MODIFIERKEYS_FLAGS.MK_RBUTTON;
        }
        if (properties.IsMiddleButtonPressed)
        {
            lowWord |= (ushort)MODIFIERKEYS_FLAGS.MK_MBUTTON;
        }
        if (properties.IsXButton1Pressed)
        {
            lowWord |= (ushort)MODIFIERKEYS_FLAGS.MK_XBUTTON1;
        }
        if (properties.IsXButton2Pressed)
        {
            lowWord |= (ushort)MODIFIERKEYS_FLAGS.MK_XBUTTON2;
        }

        // Mouse wheel : https://docs.microsoft.com/en-us/windows/desktop/inputdev/wm-mousewheel
        if (message is CoreWebView2MouseEventKind.Wheel or CoreWebView2MouseEventKind.HorizontalWheel)
        {
            // TODO_WebView2 : See if this needs to be multiplied with scale for different dpi scenarios
            highWord = (ushort)properties.MouseWheelDelta;
        }
        else if (message is CoreWebView2MouseEventKind.XButtonDown or CoreWebView2MouseEventKind.XButtonUp)
        {
            // highWord Specifies which of the two XButtons is referenced by the message
            PointerUpdateKind pointerUpdateKind = properties.PointerUpdateKind;
            if (pointerUpdateKind == PointerUpdateKind.XButton1Pressed ||
                pointerUpdateKind == PointerUpdateKind.XButton1Released)
            {
                highWord |= (ushort)MOUSEHOOKSTRUCTEX_MOUSE_DATA.XBUTTON1;
            }
            else if (pointerUpdateKind == PointerUpdateKind.XButton2Pressed ||
                     pointerUpdateKind == PointerUpdateKind.XButton2Released)
            {
                highWord |= (ushort)MOUSEHOOKSTRUCTEX_MOUSE_DATA.XBUTTON2;
            }
        }

        WPARAM wParam = MakeWParam(lowWord, highWord);
        return wParam;
    }
}
