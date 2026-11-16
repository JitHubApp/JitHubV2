using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using static JitHub.WinUI.Automation.Probes.HighContrast.HighContrastNativeMethods;

namespace JitHub.WinUI.Automation.Probes.HighContrast;

/// <summary>
/// Captures the app window and checks that its pixels use the system high contrast colors and show focus changes.
/// </summary>
internal static class HighContrastPixelAssertions
{
    internal static void AssertSelected(AutomationElement element, string description)
    {
        Assert(
            element.Patterns.SelectionItem.IsSupported,
            $"{description} does not expose native SelectionItem state.");
        Assert(
            element.Patterns.SelectionItem.Pattern.IsSelected.Value,
            $"{description} is not selected.");
    }

    internal static void AssertFocusPixelsChanged(
        CapturedFrame before,
        CapturedFrame after,
        Rectangle screenRegion,
        string description)
    {
        Rectangle region = after.ToLocal(screenRegion);
        Assert(region.Width > 0 && region.Height > 0, $"{description} has no visible screenshot region.");
        Assert(before.Bounds == after.Bounds, $"{description} window bounds shifted during focus capture.");

        int changedPixels = 0;
        for (int y = region.Top; y < region.Bottom; y++)
        {
            for (int x = region.Left; x < region.Right; x++)
            {
                if (before.Bitmap.GetPixel(x, y) != after.Bitmap.GetPixel(x, y))
                {
                    changedPixels++;
                }
            }
        }

        Assert(
            changedPixels >= 8,
            $"{description} focus produced only {changedPixels} changed pixels in its bounds.");
    }

    internal static void AssertSystemColorTreatment(
        CapturedFrame frame,
        Rectangle focusRegion,
        SystemColorPalette palette,
        string description) =>
        AssertSystemColorTreatment(frame, frame.Bounds, focusRegion, palette, description);

    internal static void AssertSystemColorTreatment(
        CapturedFrame frame,
        Rectangle contentRegion,
        Rectangle focusRegion,
        SystemColorPalette palette,
        string description)
    {
        Rectangle localContent = frame.ToLocal(contentRegion);
        AssertColorCount(frame.Bitmap, localContent, palette.Window, 64, $"{description} SystemColorWindow");
        AssertColorCount(frame.Bitmap, localContent, palette.WindowText, 8, $"{description} SystemColorWindowText");
        AssertColorCount(frame.Bitmap, localContent, palette.Highlight, 4, $"{description} SystemColorHighlight");

        Rectangle localFocus = frame.ToLocal(focusRegion);
        int focusSystemPixels = CountColor(frame.Bitmap, localFocus, palette.Highlight) +
            CountColor(frame.Bitmap, localFocus, palette.HighlightText);
        Assert(
            focusSystemPixels >= 2,
            $"{description} focus/selection region did not contain a system highlight color.");
    }

    private static void AssertColorCount(
        Bitmap bitmap,
        Rectangle region,
        Color color,
        int minimum,
        string description)
    {
        int count = CountColor(bitmap, region, color);
        Assert(count >= minimum, $"{description} appeared in only {count} screenshot pixels; expected {minimum}.");
    }

    private static int CountColor(Bitmap bitmap, Rectangle region, Color color)
    {
        region.Intersect(new Rectangle(Point.Empty, bitmap.Size));
        int count = 0;
        for (int y = region.Top; y < region.Bottom; y++)
        {
            for (int x = region.Left; x < region.Right; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                if (pixel.R == color.R && pixel.G == color.G && pixel.B == color.B)
                {
                    count++;
                }
            }
        }

        return count;
    }

    // This method stays whole because it is one Win32 capture routine that must switch the thread to per-monitor DPI
    // awareness, read logical, physical and DWM frame bounds, render with PrintWindow, crop and map back to logical
    // coordinates, then restore the prior DPI context in the same finally block.
    internal static CapturedFrame CaptureWindow(Window window)
    {
        const int DwmwaExtendedFrameBounds = 9;
        const uint PwRenderFullContent = 0x00000002;
        IntPtr windowHandle = new(window.Properties.NativeWindowHandle.ValueOrDefault);
        Assert(windowHandle != IntPtr.Zero, "JitHub did not expose a native window handle for capture.");
        Assert(GetWindowRect(windowHandle, out NativeRect logicalBounds), "Could not read JitHub logical window bounds.");
        _ = SetForegroundWindow(windowHandle);
        Thread.Sleep(150);

        IntPtr priorDpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            Assert(GetWindowRect(windowHandle, out NativeRect physicalBounds), "Could not read JitHub physical window bounds.");
            Rectangle logicalRectangle = Rectangle.FromLTRB(
                logicalBounds.Left,
                logicalBounds.Top,
                logicalBounds.Right,
                logicalBounds.Bottom);
            Rectangle physicalRectangle = Rectangle.FromLTRB(
                physicalBounds.Left,
                physicalBounds.Top,
                physicalBounds.Right,
                physicalBounds.Bottom);
            Assert(logicalRectangle.Width > 0 && logicalRectangle.Height > 0, "JitHub logical window bounds were empty.");
            Assert(physicalRectangle.Width > 0 && physicalRectangle.Height > 0, "JitHub physical window bounds were empty.");

            NativeRect visibleBounds = physicalBounds;
            if (DwmGetWindowAttribute(
                    windowHandle,
                    DwmwaExtendedFrameBounds,
                    out NativeRect extendedFrameBounds,
                    Marshal.SizeOf<NativeRect>()) == 0)
            {
                visibleBounds = extendedFrameBounds;
            }
            Rectangle visibleRectangle = Rectangle.FromLTRB(
                visibleBounds.Left,
                visibleBounds.Top,
                visibleBounds.Right,
                visibleBounds.Bottom);
            Rectangle crop = Rectangle.Intersect(
                new Rectangle(
                    visibleRectangle.Left - physicalRectangle.Left,
                    visibleRectangle.Top - physicalRectangle.Top,
                    visibleRectangle.Width,
                    visibleRectangle.Height),
                new Rectangle(Point.Empty, physicalRectangle.Size));
            Assert(crop.Width > 0 && crop.Height > 0, "JitHub DWM bounds did not intersect its rendered window surface.");

            using var fullWindow = new Bitmap(
                physicalRectangle.Width,
                physicalRectangle.Height,
                PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(fullWindow))
            {
                IntPtr deviceContext = graphics.GetHdc();
                try
                {
                    Assert(
                        PrintWindow(windowHandle, deviceContext, PwRenderFullContent),
                        "Windows could not render the JitHub surface for High Contrast capture.");
                }
                finally
                {
                    graphics.ReleaseHdc(deviceContext);
                }
            }

            Bitmap bitmap = fullWindow.Clone(crop, PixelFormat.Format32bppArgb);
            double logicalScaleX = logicalRectangle.Width / (double)physicalRectangle.Width;
            double logicalScaleY = logicalRectangle.Height / (double)physicalRectangle.Height;
            Rectangle logicalVisibleRectangle = Rectangle.FromLTRB(
                logicalRectangle.Left + (int)Math.Round(crop.Left * logicalScaleX),
                logicalRectangle.Top + (int)Math.Round(crop.Top * logicalScaleY),
                logicalRectangle.Left + (int)Math.Round(crop.Right * logicalScaleX),
                logicalRectangle.Top + (int)Math.Round(crop.Bottom * logicalScaleY));
            return new CapturedFrame(bitmap, logicalVisibleRectangle);
        }
        finally
        {
            if (priorDpiContext != IntPtr.Zero)
            {
                _ = SetThreadDpiAwarenessContext(priorDpiContext);
            }
        }
    }

    internal static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    internal readonly record struct SystemColorPalette(
        Color Window,
        Color WindowText,
        Color Highlight,
        Color HighlightText)
    {
        private const int ColorWindow = 5;
        private const int ColorWindowText = 8;
        private const int ColorHighlight = 13;
        private const int ColorHighlightText = 14;

        public static SystemColorPalette Capture() => new(
            FromColorRef(GetSysColor(ColorWindow)),
            FromColorRef(GetSysColor(ColorWindowText)),
            FromColorRef(GetSysColor(ColorHighlight)),
            FromColorRef(GetSysColor(ColorHighlightText)));

        private static Color FromColorRef(uint colorRef) => Color.FromArgb(
            255,
            (int)(colorRef & 0xFF),
            (int)((colorRef >> 8) & 0xFF),
            (int)((colorRef >> 16) & 0xFF));
    }

    internal sealed class CapturedFrame(Bitmap bitmap, Rectangle bounds) : IDisposable
    {
        public Bitmap Bitmap { get; } = bitmap;

        public Rectangle Bounds { get; } = bounds;

        public Rectangle ToLocal(Rectangle screenRegion)
        {
            double scaleX = Bitmap.Width / (double)Bounds.Width;
            double scaleY = Bitmap.Height / (double)Bounds.Height;
            Rectangle local = Rectangle.FromLTRB(
                (int)Math.Floor((screenRegion.Left - Bounds.Left) * scaleX),
                (int)Math.Floor((screenRegion.Top - Bounds.Top) * scaleY),
                (int)Math.Ceiling((screenRegion.Right - Bounds.Left) * scaleX),
                (int)Math.Ceiling((screenRegion.Bottom - Bounds.Top) * scaleY));
            local.Intersect(new Rectangle(Point.Empty, Bitmap.Size));
            return local;
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
            Bitmap.Save(path, ImageFormat.Png);
        }

        public void Dispose() => Bitmap.Dispose();
    }
}
