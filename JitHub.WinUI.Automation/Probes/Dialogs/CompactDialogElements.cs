using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using static JitHub.WinUI.Automation.Probes.Dialogs.CompactDialogAssertions;
using static JitHub.WinUI.Automation.Probes.Dialogs.CompactDialogMatrixProbe;

namespace JitHub.WinUI.Automation.Probes.Dialogs;

/// <summary>
/// Finds, waits for, resizes, invokes and captures the elements the compact dialog matrix drives.
/// </summary>
internal static class CompactDialogElements
{
    internal static AutomationElement? Find(Window window, UIA3Automation automation, string automationId)
    {
        AutomationElement? element = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        if (element is not null)
        {
            return element;
        }

        int processId = window.Properties.ProcessId.ValueOrDefault;
        return automation.GetDesktop()
            .FindAllDescendants(cf => cf.ByAutomationId(automationId))
            .FirstOrDefault(candidate => candidate.Properties.ProcessId.ValueOrDefault == processId);
    }

    internal static AutomationElement? FindVisible(Window window, UIA3Automation automation, string automationId)
    {
        AutomationElement? candidate = Find(window, automation, automationId);
        return IsVisible(candidate) ? candidate : null;
    }

    private static AutomationElement? FindByName(Window window, UIA3Automation automation, string name)
    {
        AutomationElement? element = window.FindAllDescendants()
            .FirstOrDefault(candidate => IsVisible(candidate) && string.Equals(candidate.Name, name, StringComparison.Ordinal));
        if (element is not null)
        {
            return element;
        }

        int processId = window.Properties.ProcessId.ValueOrDefault;
        return automation.GetDesktop().FindAllDescendants()
            .FirstOrDefault(candidate =>
                candidate.Properties.ProcessId.ValueOrDefault == processId &&
                IsVisible(candidate) &&
                string.Equals(candidate.Name, name, StringComparison.Ordinal));
    }

    internal static AutomationElement WaitFor(string description, Func<AutomationElement?> find)
    {
        AutomationElement? result = Retry.WhileNull(
            find,
            TimeSpan.FromSeconds(15),
            TimeSpan.FromMilliseconds(100),
            ignoreException: true).Result;
        return result ?? throw new InvalidOperationException($"Timed out waiting for {description}.");
    }

    internal static AutomationElement WaitForVisible(string description, Func<AutomationElement?> find) =>
        WaitFor(description, () =>
        {
            AutomationElement? candidate = find();
            return IsVisible(candidate) ? candidate : null;
        });

    internal static void WaitUntil(string description, Func<bool> predicate)
    {
        bool result = Retry.WhileFalse(
            predicate,
            TimeSpan.FromSeconds(8),
            TimeSpan.FromMilliseconds(100),
            ignoreException: true).Result;
        Assert(result, $"Timed out waiting for {description}.");
    }

    internal static void Resize(Window window, CompactViewport viewport)
    {
        Assert(window.Patterns.Transform.IsSupported, "JitHub main window does not support UIA resize.");
        window.Patterns.Transform.Pattern.Resize(viewport.Width, viewport.Height);
        window.Move(24, 24);
        window.SetForeground();
        Thread.Sleep(300);
    }

    internal static void Invoke(AutomationElement element)
    {
        if (element.Patterns.Invoke.IsSupported)
        {
            element.Patterns.Invoke.Pattern.Invoke();
        }
        else
        {
            element.Click();
        }
    }

    internal static void TryInvoke(AutomationElement element)
    {
        try
        {
            Invoke(element);
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or FlaUI.Core.Exceptions.ElementNotEnabledException)
        {
            // A production modal can make its opener non-interactive before UIA dispatches.
            // The single-surface assertion still verifies that no duplicate was presented.
        }
    }

    internal static void Toggle(AutomationElement element)
    {
        if (element.Patterns.Toggle.IsSupported)
        {
            element.Patterns.Toggle.Pattern.Toggle();
        }
        else
        {
            Invoke(element);
        }
    }

    internal static void Focus(AutomationElement element)
    {
        element.FocusNative();
        WaitUntil("automation focus", () => element.Properties.HasKeyboardFocus.ValueOrDefault);
    }

    internal static string Capture(Window window, string outputDirectory, string fileName)
    {
        window.SetForeground();
        Thread.Sleep(100);
        Rectangle bounds = window.BoundingRectangle;
        Assert(bounds.Width > 0 && bounds.Height > 0, "Cannot capture an empty JitHub window.");
        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory, fileName);
        using Bitmap bitmap = new(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        }
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    internal static void EnsureVisible(AutomationElement element)
    {
        if (!IsVisible(element) && element.Patterns.ScrollItem.IsSupported)
        {
            element.Patterns.ScrollItem.Pattern.ScrollIntoView();
        }

        WaitUntil($"{element.Name} is visible", () => IsVisible(element));
    }

    internal static bool IsVisible(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            Rectangle bounds = element.BoundingRectangle;
            return !element.Properties.IsOffscreen.ValueOrDefault && bounds.Width > 0 && bounds.Height > 0;
        }
        catch (COMException)
        {
            return false;
        }
    }
}
