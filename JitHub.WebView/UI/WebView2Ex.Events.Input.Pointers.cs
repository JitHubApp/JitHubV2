// Original: https://github.com/microsoft/microsoft-ui-xaml/blob/main/dev/WebView2/WebView2.cpp
#nullable enable
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Windows.Foundation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Windows.UI.Input;
using Windows.Devices.Input;
using Windows.Win32.UI.Input.Pointer;
using Point = Windows.Foundation.Point;
using MouseEvKind = Microsoft.Web.WebView2.Core.CoreWebView2MouseEventKind;
using PointerEvKind = Microsoft.Web.WebView2.Core.CoreWebView2PointerEventKind;
using static WebView2Ex.Natives.Macros;

namespace WebView2Ex.UI;

partial class WebView2Ex
{
    CoreCursor? oldCursor;
    bool
        hasMouseCapture,
        isLeftMouseButtonPressed,
        isMiddleMouseButtonPressed,
        isRightMouseButtonPressed,
        isXButton1Pressed,
        isXButton2Pressed,
        hasPenCapture,
        isPointerOver;
    readonly Dictionary<uint, bool> hasTouchCapture = new();
    void RegisterXamlPointerEventHandlers()
    {
        PointerPressed += HandlePointerPressed;
        PointerReleased += HandlePointerReleased;
        PointerMoved += HandlePointerMoved;
        PointerWheelChanged += HandlePointerWheelChanged;
        PointerExited += HandlePointerExited;
        PointerEntered += HandlePointerEntered;
        PointerCanceled += HandlePointerCanceled;
        PointerCaptureLost += HandlePointerCaptureLost;
    }
    void UnregisterXamlPointerEventHandlers()
    {
        PointerPressed -= HandlePointerPressed;
        PointerReleased -= HandlePointerReleased;
        PointerMoved -= HandlePointerMoved;
        PointerWheelChanged -= HandlePointerWheelChanged;
        PointerExited -= HandlePointerExited;
        PointerEntered -= HandlePointerEntered;
        PointerCanceled -= HandlePointerCanceled;
        PointerCaptureLost -= HandlePointerCaptureLost;
    }

    // Chromium handles WM_MOUSEXXX for mouse, WM_POINTERXXX for touch
    void HandlePointerPressed(object sender, PointerRoutedEventArgs args)
    {
        PointerPoint pointerPoint = args.GetCurrentPoint(this);

        switch (args.Pointer.PointerDeviceType)
        {
            case PointerDeviceType.Mouse:
                MouseEvKind message = 0x0;
                // WebView takes mouse capture to avoid missing pointer released events that occur outside of the element that
                // end pointer pressed state inside the webview. Example, scrollbar is being used and mouse is moved out
                // of webview bounds before being released, the webview will miss the released event and upon reentry into
                // the webview, the mouse will still cause the scrollbar to move as if selected.
                hasMouseCapture = CapturePointer(args.Pointer);

                PointerPointProperties properties = pointerPoint.Properties;
                if (properties.IsLeftButtonPressed)
                {
                    // Double Click is working as well with this code, presumably by being recognized on browser side from WM_LBUTTONDOWN/ WM_LBUTTONUP
                    message = MouseEvKind.LeftButtonDown;
                    isLeftMouseButtonPressed = true;
                }
                else if (properties.IsMiddleButtonPressed)
                {
                    message = MouseEvKind.MiddleButtonDown;
                    isMiddleMouseButtonPressed = true;
                }
                else if (properties.IsRightButtonPressed)
                {
                    message = MouseEvKind.RightButtonDown;
                    isRightMouseButtonPressed = true;
                }
                else if (properties.IsXButton1Pressed)
                {
                    message = MouseEvKind.XButtonDown;
                    isXButton1Pressed = true;
                }
                else if (properties.IsXButton2Pressed)
                {
                    message = MouseEvKind.XButtonDown;
                    isXButton2Pressed = true;
                }
                else
                {
                    Debugger.Break();
                    throw new InvalidOperationException("Should not reach here");
                }
                SetFocus();
                OnXamlMouseMessage(message, args);
                break;
            case PointerDeviceType.Touch:
                hasTouchCapture.Add(pointerPoint.PointerId, CapturePointer(args.Pointer));
                SetFocus();
                OnXamlPointerMessage(PointerEvKind.Down, args);
                break;
            case PointerDeviceType.Pen:
                hasPenCapture = CapturePointer(args.Pointer);
                SetFocus();
                OnXamlPointerMessage(PointerEvKind.Down, args);
                break;
        }
    }
    void HandlePointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (args.Pointer.PointerDeviceType is PointerDeviceType.Mouse)
        {
            MouseEvKind message;
            if (isLeftMouseButtonPressed)
            {
                message = MouseEvKind.LeftButtonUp;
                isLeftMouseButtonPressed = false;
            }
            else if (isMiddleMouseButtonPressed)
            {
                message = MouseEvKind.MiddleButtonUp;
                isMiddleMouseButtonPressed = false;
            }
            else if (isRightMouseButtonPressed)
            {
                message = MouseEvKind.RightButtonUp;
                isRightMouseButtonPressed = false;
            }
            else if (isXButton1Pressed)
            {
                message = MouseEvKind.XButtonUp;
                isXButton1Pressed = false;
            }
            else if (isXButton2Pressed)
            {
                message = MouseEvKind.XButtonUp;
                isXButton2Pressed = false;
            }
            else
            {
                // It is not guaranteed that we will get a PointerPressed before PointerReleased.
                // For example, the mouse can be pressed in the space next to a webview, dragged
                // into the webview, and then released. This is a valid case and should not crash.
                // Because we can't always know what button was pressed before a release, we can't
                // forward this message on to CoreWebView2.
                return;
            }

            if (hasMouseCapture)
            {
                ReleasePointerCapture(args.Pointer);
                hasMouseCapture = false;
            }
            OnXamlMouseMessage(message, args);
        }
        else
        {
            // Get pointer id for handling multi-touch capture
            uint id = args.GetCurrentPoint(this).PointerId;
            if (hasTouchCapture.ContainsKey(id))
            {
                ReleasePointerCapture(args.Pointer);
                hasTouchCapture.Remove(id);
            }

            if (hasPenCapture)
            {
                ReleasePointerCapture(args.Pointer);
                hasPenCapture = false;
            }

            OnXamlPointerMessage(PointerEvKind.Up, args);
        }
    }
    void HandlePointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (args.Pointer.PointerDeviceType is PointerDeviceType.Mouse)
            OnXamlMouseMessage(MouseEvKind.Move, args);
        else
            OnXamlPointerMessage(PointerEvKind.Update, args);
    }
    void HandlePointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        if (args.Pointer.PointerDeviceType is PointerDeviceType.Mouse)
        {
            OnXamlMouseMessage(
                args.GetCurrentPoint(this).Properties.IsHorizontalMouseWheel ?
                MouseEvKind.HorizontalWheel : MouseEvKind.Wheel
            , args);
        }
        else OnXamlPointerMessage((PointerEvKind)PInvoke.WM_POINTERWHEEL, args);

    }
    void HandlePointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (isPointerOver)
        {
            isPointerOver = false;
            CoreWindow.GetForCurrentThread().PointerCursor = oldCursor;
            oldCursor = null;
        }

        if (args.Pointer.PointerDeviceType is PointerDeviceType.Mouse)
        {
            if (!hasMouseCapture) ResetMouseInputState();
            OnXamlMouseMessage(MouseEvKind.Leave, args);
        }
        else OnXamlPointerMessage(PointerEvKind.Leave, args);
    }
    void HandlePointerEntered(object sender, PointerRoutedEventArgs args)
    {
        PointerDeviceType deviceType = args.Pointer.PointerDeviceType;

        isPointerOver = true;
        oldCursor = CoreWindow.GetForCurrentThread().PointerCursor;

        UpdateCoreWindowCursor();

        if (deviceType is not PointerDeviceType.Mouse) //mouse does not have an equivalent pointer_entered event, so only handling pen/touch
            OnXamlPointerMessage(PointerEvKind.Enter, args);
    }
    void HandlePointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        ResetPointerHelper(args);
    }
    void HandlePointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        ResetPointerHelper(args);
    }
    
    void OnXamlPointerMessage(PointerEvKind message, PointerRoutedEventArgs args)
    {
        // Set Handled to prevent ancestor actions such as ScrollViewer taking focus on PointerPressed/PointerReleased.
        args.Handled = true;
        var runtime = WebView2Runtime;
        if (runtime is null) return;
        var CoreWebView2 = runtime.CoreWebView2;
        var Controller = runtime.CompositionController;
        var Environment = runtime.Environment;
        if (CoreWebView2 is null || Controller is null)
        {
            // returning only because one can click within webview2 element even before it gets loaded
            // in such scenarios, the input gets ignored
            return;
        }

        PointerPoint inputPt = args.GetCurrentPoint(this);
        CoreWebView2PointerInfo outputPt = Environment!.CreateCoreWebView2PointerInfo();

        PointerPoint logicalPointerPoint = args.GetCurrentPoint(this);
        Point logicalPoint = logicalPointerPoint.Position;
        Point physicalPoint = new(logicalPoint.X * rasterizationScale, logicalPoint.Y * rasterizationScale);
        PointerDeviceType deviceType = args.Pointer.PointerDeviceType;

        //PEN INPUT
        if (deviceType == PointerDeviceType.Pen)
        {
            FillPointerPenInfo(inputPt, outputPt);
        }

        //TOUCH INPUT
        if (deviceType == PointerDeviceType.Touch)
        {
            FillPointerTouchInfo(inputPt, outputPt);
        }

        //GENERAL POINTER INPUT
        FillPointerInfo(inputPt, outputPt, args);

        Controller.SendPointerInput(message, outputPt);
    }
    void OnXamlMouseMessage(MouseEvKind message, PointerRoutedEventArgs args)
    {
        // Set Handled to prevent ancestor actions such as ScrollViewer taking focus on PointerPressed/PointerReleased.
        args.Handled = true;

        var runtime = WebView2Runtime;
        if (runtime is null) return;
        var CoreWebView2 = runtime.CoreWebView2;
        var Controller = runtime.CompositionController;
        var Environment = runtime.Environment;
        if (CoreWebView2 is null || Controller is null)
        {
            // returning only because one can click within webview2 element even before it gets loaded
            // in such scenarios, the input gets ignored
            return;
        }

        PointerPoint logicalPointerPoint = args.GetCurrentPoint(this);
        Point logicalPoint = logicalPointerPoint.Position;
        Point physicalPoint = new(logicalPoint.X * rasterizationScale, logicalPoint.Y * rasterizationScale);

        if (message is MouseEvKind.Leave)
        {
            Controller.SendMouseInput(
                (MouseEvKind)message,
                CoreWebView2MouseEventVirtualKeys.None,
            0,
            new Point(0, 0));
        }
        else
        {
            LPARAM l_param = PackIntoWin32StylePointerArgs_lparam(message, args, physicalPoint);
            WPARAM w_param = PackIntoWin32StyleMouseArgs_wparam(message, args, logicalPointerPoint);

            Point coords_win32 = new((short)LoWord(l_param), (short)HiWord(l_param));

            Point coords = coords_win32;

            // mouse data is nonzero for mouse wheel scrolling and XBUTTON events
            uint mouse_data = 0;
            switch (message)
            {
                case MouseEvKind.Wheel or MouseEvKind.HorizontalWheel:
                    mouse_data = (uint)GetWheelDataWParam(w_param);
                    break;
                case MouseEvKind.XButtonDown
                    or MouseEvKind.XButtonUp
                    or MouseEvKind.XButtonDoubleClick:
                    mouse_data = (uint)GetXButtonWParam(w_param);
                    break;
            }
            Controller.SendMouseInput(
                message,
            (CoreWebView2MouseEventVirtualKeys)GetKeystateWParam(w_param),
            mouse_data,
            coords);
        }
    }
    void FillPointerPenInfo(PointerPoint inputPt, CoreWebView2PointerInfo outputPt)
    {
        PointerPointProperties inputProperties = inputPt.Properties;
        uint outputPt_penFlags = PInvoke.PEN_FLAG_NONE;

        if (inputProperties.IsBarrelButtonPressed)
        {
            outputPt_penFlags |= PInvoke.PEN_FLAG_BARREL;
        }

        if (inputProperties.IsInverted)
        {
            outputPt_penFlags |= PInvoke.PEN_FLAG_INVERTED;
        }

        if (inputProperties.IsEraser)
        {
            outputPt_penFlags |= PInvoke.PEN_FLAG_ERASER;
        }

        outputPt.PenFlags = outputPt_penFlags;

        uint outputPt_penMask = PInvoke.PEN_MASK_PRESSURE | PInvoke.PEN_MASK_ROTATION | PInvoke.PEN_MASK_TILT_X | PInvoke.PEN_MASK_TILT_Y;
        outputPt.PenMask = outputPt_penMask;

        uint outputPt_penPressure = (uint)inputProperties.Pressure * 1024;
        outputPt.PenPressure = outputPt_penPressure;

        uint outputPt_penRotation = (uint)inputProperties.Twist;
        outputPt.PenRotation = outputPt_penRotation;

        int outputPt_penTiltX = (int)inputProperties.XTilt;
        outputPt.PenTiltX = outputPt_penTiltX;

        int outputPt_penTiltY = (int)inputProperties.YTilt;
        outputPt.PenTiltY = outputPt_penTiltY;
    }
    void FillPointerTouchInfo(PointerPoint inputPt, CoreWebView2PointerInfo outputPt)
    {
        PointerPointProperties inputProperties = inputPt.Properties;

        outputPt.TouchFlags = PInvoke.TOUCH_FLAG_NONE;

        uint outputPt_touchMask = PInvoke.TOUCH_MASK_CONTACTAREA | PInvoke.TOUCH_MASK_ORIENTATION | PInvoke.TOUCH_MASK_ORIENTATION;
        outputPt.TouchMask = outputPt_touchMask;

        //TOUCH CONTACT
        double width = inputProperties.ContactRect.Width * rasterizationScale;
        double height = inputProperties.ContactRect.Height * rasterizationScale;
        double leftVal = inputProperties.ContactRect.X * rasterizationScale;
        double topVal = inputProperties.ContactRect.Y * rasterizationScale;

        Rect outputPt_touchContact = new(leftVal, topVal, width, height);
        outputPt.TouchContact = outputPt_touchContact;

        //TOUCH CONTACT RAW
        double widthRaw = inputProperties.ContactRectRaw.Width * rasterizationScale;
        double heightRaw = inputProperties.ContactRectRaw.Height * rasterizationScale;
        double leftValRaw = inputProperties.ContactRectRaw.X * rasterizationScale;
        double topValRaw = inputProperties.ContactRectRaw.Y * rasterizationScale;

        Rect outputPt_touchContactRaw = new(leftValRaw, topValRaw, widthRaw, heightRaw);
        outputPt.TouchContactRaw = outputPt_touchContactRaw;

        uint outputPt_touchOrientation = (uint)inputProperties.Orientation;
        outputPt.TouchOrientation = outputPt_touchOrientation;

        uint outputPt_touchPressure = (uint)(inputProperties.Pressure * 1024);
        outputPt.TouchPressure = outputPt_touchPressure;
    }
    unsafe void FillPointerInfo(PointerPoint inputPt, CoreWebView2PointerInfo outputPt, PointerRoutedEventArgs args)
    {
        PointerPointProperties inputProperties = inputPt.Properties;

        //DEVICE TYPE
        PointerDeviceType deviceType = inputPt.PointerDevice.PointerDeviceType;

        if (deviceType == PointerDeviceType.Pen)
        {
            outputPt.PointerKind = (uint)POINTER_INPUT_TYPE.PT_PEN;
        }
        else if (deviceType == PointerDeviceType.Touch)
        {
            outputPt.PointerKind = (uint)POINTER_INPUT_TYPE.PT_TOUCH;
        }

        outputPt.PointerId = args.Pointer.PointerId;

        outputPt.FrameId = inputPt.FrameId;

        //POINTER FLAGS
        POINTER_FLAGS outputPt_pointerFlags = POINTER_FLAGS.POINTER_FLAG_NONE;

        if (inputProperties.IsInRange)
        {
            outputPt_pointerFlags |= POINTER_FLAGS.POINTER_FLAG_INRANGE;
        }

        if (deviceType == PointerDeviceType.Touch)
        {
            if (inputPt.IsInContact)
            {
                outputPt_pointerFlags |= POINTER_FLAGS.POINTER_FLAG_INCONTACT;
                outputPt_pointerFlags |= POINTER_FLAGS.POINTER_FLAG_FIRSTBUTTON;
            }

            if (inputProperties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
            {
                outputPt_pointerFlags |= POINTER_FLAGS.POINTER_FLAG_NEW;
            }
        }

        if (deviceType == PointerDeviceType.Pen)
        {
            if (inputPt.IsInContact)
            {
                outputPt_pointerFlags |= POINTER_FLAGS.POINTER_FLAG_INCONTACT;

                if (!inputProperties.IsBarrelButtonPressed)
                {
                    outputPt_pointerFlags |= POINTER_FLAGS.POINTER_FLAG_FIRSTBUTTON;
                }

                else
                {
                    outputPt_pointerFlags |= POINTER_FLAGS.POINTER_FLAG_SECONDBUTTON;
                }
            } // POINTER_FLAG_NEW is currently omitted for pen input
        }

        if (inputProperties.IsPrimary)
        {
            outputPt_pointerFlags |= POINTER_FLAGS.POINTER_FLAG_PRIMARY;
        }

        if (inputProperties.TouchConfidence)
        {
            outputPt_pointerFlags |= POINTER_FLAGS.POINTER_FLAG_CONFIDENCE;
        }

        if (inputProperties.IsCanceled)
        {
            outputPt_pointerFlags |= POINTER_FLAGS.POINTER_FLAG_CANCELED;
        }

        if (inputProperties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
        {
            outputPt_pointerFlags |= POINTER_FLAGS.POINTER_FLAG_DOWN;
        }

        if (inputProperties.PointerUpdateKind == PointerUpdateKind.Other)
        {
            outputPt_pointerFlags |= POINTER_FLAGS.POINTER_FLAG_UPDATE;
        }

        if (inputProperties.PointerUpdateKind == PointerUpdateKind.LeftButtonReleased)
        {
            outputPt_pointerFlags |= POINTER_FLAGS.POINTER_FLAG_UP;
        }

        outputPt.PointerFlags = (uint)outputPt_pointerFlags;

        Point outputPt_pointerPixelLocation = new((rasterizationScale * inputPt.Position.X), (rasterizationScale * inputPt.Position.Y));
        outputPt.PixelLocation = outputPt_pointerPixelLocation;

        //HIMETRIC LOCATION (task 30544057 exists to finish this)
        //var himetricScale = 26.4583; //1 hiMetric = 0.037795280352161 PX
        //Point outputPt_pointerHimetricLocation(static_cast<float>(inputPt.Position().X), static_cast<float>(inputPt.Position().Y));
        //outputPt->HimetricLocation(outputPt_pointerHimetricLocation);

        Point outputPt_pointerRawPixelLocation = new(rasterizationScale * inputPt.RawPosition.X, rasterizationScale * inputPt.RawPosition.Y);
        outputPt.PixelLocationRaw = outputPt_pointerRawPixelLocation;

        //RAW HIMETRIC LOCATION
        //Point outputPt_pointerRawHimetricLocation = { static_cast<float>(inputPt.RawPosition().X), static_cast<float>(inputPt.RawPosition().Y) };
        //outputPt.HimetricLocationRaw(outputPt_pointerRawHimetricLocation);

        uint outputPoint_pointerTime = (uint)(inputPt.Timestamp / 1000); //microsecond to millisecond conversion(for tick count)
        outputPt.Time = outputPoint_pointerTime;

        var outputPoint_pointerHistoryCount = (uint)(args.GetIntermediatePoints(this).Count);
        outputPt.HistoryCount = outputPoint_pointerHistoryCount;

        //PERFORMANCE COUNT
        long lpFrequency = default;
        bool res = PInvoke.QueryPerformanceFrequency(&lpFrequency);
        if (res)
        {
            var scale = 1000000;
            var frequency = (ulong)lpFrequency;
            var outputPoint_pointerPerformanceCount = (inputPt.Timestamp * frequency) / (ulong)scale;
            outputPt.PerformanceCount = outputPoint_pointerPerformanceCount;
        }

        var outputPoint_pointerButtonChangeKind = (int)inputProperties.PointerUpdateKind;
        outputPt.ButtonChangeKind = outputPoint_pointerButtonChangeKind;
    }

    private void CoreWebView2CursorChanged(CoreWebView2CompositionController sender, object args)
    {
        var Controller = this.Controller;
        if (isPointerOver && Controller is not null)
            CoreWindow.GetForCurrentThread().PointerCursor = Controller.Cursor;
    }

    void ResetPointerHelper(PointerRoutedEventArgs args)
    {
        PointerDeviceType deviceType = args.Pointer.PointerDeviceType;

        if (deviceType == PointerDeviceType.Mouse)
        {
            hasMouseCapture = false;
            ResetMouseInputState();
        }
        else if (deviceType == PointerDeviceType.Touch)
        {
            // Get pointer id for handling multi-touch capture
            PointerPoint logicalPointerPoint = args.GetCurrentPoint(this);
            uint id = logicalPointerPoint.PointerId;
            if (hasTouchCapture.ContainsKey(id))
            {
                hasTouchCapture.Remove(id);
            }
        }
        else if (deviceType == PointerDeviceType.Pen)
        {
            hasPenCapture = false;
        }
    }
    void SetFocus()
    {
        // Since OnXamlPointerMessage() will mark the args handled, Xaml FocusManager will ignore
        // this Pressed event when it bubbles up to the XamlRoot, not setting focus as expected.
        // Thus, we need to manually set Xaml Focus (Pointer) on WebView2 here.
        // TODO_WebView2: Update this to UIElement.Focus [sync version], when it becomes available.
        if (IsTabStop)
        {
            _ = FocusManager.TryFocusAsync(this, FocusState.Pointer);
        }
    }
    void ResetMouseInputState()
    {
        isLeftMouseButtonPressed = false;
        isMiddleMouseButtonPressed = false;
        isRightMouseButtonPressed = false;
        isXButton1Pressed = false;
        isXButton2Pressed = false;
    }
    void UpdateCoreWindowCursor()
    {
        var Controller = this.Controller;
        if (Controller is not null && isPointerOver)
        {
            CoreWindow.GetForCurrentThread().PointerCursor = Controller.Cursor;
        }
    }
}
