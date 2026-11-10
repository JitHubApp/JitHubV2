using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Authentication;

/// <summary>
/// Drives the login-auth-ui probe and the login screenshot checks the capture mode also uses.
/// </summary>
internal static class LoginAuthUiProbe
{
    internal static void RunLoginAuthUiProbe(CaptureOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.AttachProcess))
        {
            throw new InvalidOperationException("The login-auth-ui probe requires fresh light and dark app processes.");
        }

        var screenshotPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string theme in new[] { "light", "dark" })
        {
            using var app = LaunchApplication(options.AppPath, "--page=login", $"--theme={theme}");
            using var automation = new UIA3Automation();
            try
            {
                Window window = GetReadyWindow(app, automation, $"login-auth-ui {theme} theme");
                ResizeWindow(window, 1180, 760);

                AutomationElement root = WaitForElement(
                    "LoginRoot",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("LoginRoot")),
                    TimeSpan.FromSeconds(8));
                AutomationElement card = WaitForElement(
                    "LoginCard",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("LoginCard")),
                    TimeSpan.FromSeconds(8));
                AutomationElement signInButton = AssertNamedAutomationElement(
                    window,
                    "LoginSignInButton",
                    ControlType.Button);
                AutomationElement status = AssertNamedAutomationElement(
                    window,
                    "LoginStatusText",
                    ControlType.Text);

                AssertProbe(string.Equals(signInButton.Name, "Continue with GitHub", StringComparison.Ordinal),
                    $"The {theme} login button exposed the unexpected accessible name '{signInButton.Name}'.");
                AssertProbe(signInButton.IsEnabled, $"The {theme} login button was not enabled.");
                AssertProbe(status.Name.Contains("code", StringComparison.OrdinalIgnoreCase),
                    $"The {theme} login status did not describe device sign-in.");
                AssertProbe(IsInsideWindowBounds(root, window), $"The {theme} login root escaped the app window.");
                AssertProbe(IsInsideElementBounds(signInButton, card, 1.5),
                    $"The {theme} login button escaped the sign-in card.");
                AssertProbe(signInButton.BoundingRectangle.Height >= 32,
                    $"The {theme} login button was too short for a reliable touch target.");
                AssertProbe(!IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("LoginErrorInfoBar"))),
                    $"The {theme} login page started with an error visible.");

                string screenshotPath = Path.Combine(options.OutputDirectory, $"{theme}-login.png");
                CaptureWindow(window, screenshotPath);
                AssertLoginThemeScreenshot(screenshotPath, theme);
                screenshotPaths[theme] = screenshotPath;
            }
            finally
            {
                TryClose(app);
                KillExistingApplicationInstances(options.AppPath);
            }
        }

        AssertLoginThemeContrast(screenshotPaths["light"], screenshotPaths["dark"]);

        Console.WriteLine($"login-auth-ui probe: light={screenshotPaths["light"]}, dark={screenshotPaths["dark"]}");
    }

    internal static void AssertLoginLiveUi(Window window)
    {
        AutomationElement root = WaitForElement(
            "LoginRoot",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("LoginRoot")),
            TimeSpan.FromSeconds(8));
        AutomationElement signInButton = AssertNamedAutomationElement(
            window,
            "LoginSignInButton",
            ControlType.Button);
        AutomationElement status = AssertNamedAutomationElement(
            window,
            "LoginStatusText",
            ControlType.Text);

        AssertProbe(IsVisible(root), "The login root was not visible in the live UIA tree.");
        AssertProbe(string.Equals(signInButton.Name, "Continue with GitHub", StringComparison.Ordinal),
            $"The login button exposed the unexpected accessible name '{signInButton.Name}'.");
        AssertProbe(signInButton.IsEnabled, "The login button was not enabled.");
        AssertProbe(status.Name.Contains("opens GitHub", StringComparison.OrdinalIgnoreCase),
            "The login status did not describe the browser sign-in flow.");
    }

    internal static void AssertLoginThemeScreenshot(string filePath, string theme)
    {
        using var bitmap = new Bitmap(filePath);
        AssertProbe(bitmap.Width >= 900 && bitmap.Height >= 600,
            $"The {theme} login artifact was unexpectedly small ({bitmap.Width}x{bitmap.Height}).");

        double backgroundLuminance = SampleLoginBackgroundLuminance(bitmap);
        if (string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase))
        {
            AssertProbe(backgroundLuminance >= 170,
                $"The light login artifact did not render a light app canvas (luminance={backgroundLuminance:0.0}).");
        }
        else if (string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase))
        {
            AssertProbe(backgroundLuminance <= 90,
                $"The dark login artifact did not render a dark app canvas (luminance={backgroundLuminance:0.0}).");
        }
        else
        {
            throw new InvalidOperationException($"Unsupported login screenshot theme '{theme}'.");
        }
    }

    private static void AssertLoginThemeContrast(string lightPath, string darkPath)
    {
        using var light = new Bitmap(lightPath);
        using var dark = new Bitmap(darkPath);
        double lightLuminance = SampleLoginBackgroundLuminance(light);
        double darkLuminance = SampleLoginBackgroundLuminance(dark);
        AssertProbe(lightLuminance - darkLuminance >= 90,
            $"Light and dark login artifacts were not visually distinct enough (light={lightLuminance:0.0}, dark={darkLuminance:0.0}).");
    }

    private static double SampleLoginBackgroundLuminance(Bitmap bitmap)
    {
        (double X, double Y)[] points =
        [
            (0.12, 0.18),
            (0.24, 0.30),
            (0.82, 0.22),
            (0.88, 0.52),
            (0.18, 0.78),
            (0.76, 0.82)
        ];

        return points.Average(point =>
        {
            int x = Math.Clamp((int)Math.Round((bitmap.Width - 1) * point.X), 0, bitmap.Width - 1);
            int y = Math.Clamp((int)Math.Round((bitmap.Height - 1) * point.Y), 0, bitmap.Height - 1);
            Color color = bitmap.GetPixel(x, y);
            return (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B);
        });
    }
}
