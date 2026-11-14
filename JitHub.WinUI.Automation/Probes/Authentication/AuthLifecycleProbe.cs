using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.AutomationDataRoot;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Dialogs.DialogAssertions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Probes.Authentication.AuthDataRootReader;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Authentication;

/// <summary>
/// Drives the auth-lifecycle probe: each sign-in, sign-out and recovery scenario runs against its own isolated data root.
/// </summary>
internal static class AuthLifecycleProbe
{
    internal static void RunAuthLifecycleProbe(CaptureOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.AttachProcess))
        {
            throw new InvalidOperationException("The auth-lifecycle probe requires isolated fresh app processes.");
        }

        RunAuthCancelScenario(options, "light");
        RunAuthCancelScenario(options, "dark");
        RunAuthDeviceSuccessScenario(options);
        RunAuthCancelAfterApprovalScenario(options);
        RunAuthExpiredTokenScenario(options);
        RunAuthNotificationReconnectScenario(options);
        RunAuthOfflineLaunchScenario(options);
        RunAuthExpiredOfflineRecoveryScenario(options);
        RunAuthMultiAccountCleanupScenario(options);
        Console.WriteLine("auth-lifecycle probe completed all deterministic production-path scenarios.");
    }

    private static void RunAuthCancelScenario(CaptureOptions options, string theme)
    {
        RunIsolatedAuthScenario(options, "auth-cancel", theme, (window, automation, root) =>
        {
            AutomationElement signIn = WaitForVisibleAutomationElement(window, "LoginSignInButton");
            InvokeOrClick(signIn);
            AutomationElement dialog = WaitForVisibleAutomationElement(window, "GitHubDeviceAuthorizationDialog");
            AssertProbe(IsVisible(WaitForVisibleAutomationElement(window, "GitHubDeviceCode")),
                "Device authorization did not show a user code.");
            InvokeOrClick(FindDialogButton(dialog, automation, "Cancel"));
            WaitUntil(
                "cancelled sign-in status",
                () => AutomationElementText(window.FindFirstDescendant(cf => cf.ByAutomationId("LoginStatusText")))
                    .Contains("canceled", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(8));
            AssertProbe(signIn.IsEnabled, "Sign-in was not recoverable after cancellation.");
            AssertAuthMarker(root, "device.code.requested");
        });
    }

    private static void RunAuthDeviceSuccessScenario(CaptureOptions options)
    {
        RunIsolatedAuthScenario(options, "auth-device-success", "light", (window, _, root) =>
        {
            InvokeOrClick(WaitForVisibleAutomationElement(window, "LoginSignInButton"));
            WaitForVisibleAutomationElement(window, "GitHubDeviceAuthorizationDialog");
            WaitForVisibleAutomationElement(window, "ShellRoot", TimeSpan.FromSeconds(20));
            AssertAuthMarker(root, "device.approved");
            AssertProbe(ReadAuthCredentials(root).ContainsKey("101"),
                "Device sign-in did not persist the authenticated account session.");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "auth-device-success-light.png"));
        });
    }

    private static void RunAuthCancelAfterApprovalScenario(CaptureOptions options)
    {
        RunIsolatedAuthScenario(options, "auth-cancel-after-approval", "dark", (window, automation, root) =>
        {
            InvokeOrClick(WaitForVisibleAutomationElement(window, "LoginSignInButton"));
            AutomationElement dialog = WaitForVisibleAutomationElement(window, "GitHubDeviceAuthorizationDialog");
            WaitUntil(
                "GitHub device approval before dismissal",
                () => AutomationElementText(window.FindFirstDescendant(cf => cf.ByAutomationId("GitHubDeviceStatus")))
                    .Contains("Approved by GitHub", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(8));
            InvokeOrClick(FindDialogButton(dialog, automation, "Cancel"));
            WaitUntil(
                "cancelled approved sign-in status",
                () => AutomationElementText(window.FindFirstDescendant(cf => cf.ByAutomationId("LoginStatusText")))
                    .Contains("canceled", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(8));
            AssertAuthMarker(root, "device.approved");
            AssertProbe(!ReadAuthCredentials(root).ContainsKey("101"),
                "Dismissal after approval persisted the cancelled sign-in session.");
        });
    }

    private static void RunAuthExpiredTokenScenario(CaptureOptions options)
    {
        RunIsolatedAuthScenario(options, "auth-expired-token", "dark", (window, _, root) =>
        {
            AutomationElement error = WaitForVisibleAutomationElement(window, "LoginErrorInfoBar");
            AssertProbe(
                AutomationElementText(error).Contains("expired", StringComparison.OrdinalIgnoreCase),
                "Expired-token launch did not explain that the session expired.");
            AssertProbe(!ReadAuthCredentials(root).ContainsKey("101"), "Expired account token was not removed.");
            AssertProbe(ReadAuthSetting(root, "USER_ID") is null or "0", "Expired launch retained the current account id.");
            AssertAuthMarker(root, "http.unauthorized");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "auth-expired-token-dark.png"));
        });
    }

    private static void RunAuthOfflineLaunchScenario(CaptureOptions options)
    {
        RunIsolatedAuthScenario(options, "auth-offline-launch", "light", (window, _, root) =>
        {
            WaitForVisibleAutomationElement(window, "LoginStatusText");
            WaitUntil(
                "saved session retry status",
                () => AutomationElementText(window.FindFirstDescendant(cf => cf.ByAutomationId("LoginStatusText")))
                    .Contains("retry your saved session", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(12));
            AssertProbe(ReadAuthCredentials(root).ContainsKey("101"), "Offline launch removed the reusable account token.");
            AssertProbe(ReadAuthSetting(root, "USER_ID") == "101", "Offline launch lost the active account id.");
            AssertAuthMarker(root, "http.offline");

            File.WriteAllText(Path.Combine(root, "Local", "AuthLifecycle", "network-restored"), "ready");
            WaitForVisibleAutomationElement(window, "ShellRoot", TimeSpan.FromSeconds(20));
            AssertProbe(ReadAuthCredentials(root).ContainsKey("101"),
                "Automatic recovery did not retain the saved access token.");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "auth-offline-recovered-light.png"));
        });
    }

    private static void RunAuthExpiredOfflineRecoveryScenario(CaptureOptions options)
    {
        RunIsolatedAuthScenario(options, "auth-expired-offline-recovery", "light", (window, _, root) =>
        {
            WaitUntil(
                "saved session retry status",
                () => AutomationElementText(window.FindFirstDescendant(cf => cf.ByAutomationId("LoginStatusText")))
                    .Contains("retry your saved session", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(12));
            AssertProbe(ReadAuthCredentials(root).ContainsKey("101"),
                "Offline startup removed the rotating token pair.");
            AssertAuthMarker(root, "refresh.offline");

            File.WriteAllText(Path.Combine(root, "Local", "AuthLifecycle", "network-restored"), "ready");
            WaitForVisibleAutomationElement(window, "ShellRoot", TimeSpan.FromSeconds(20));
            AssertAuthMarker(root, "refresh.completed");
            AssertProbe(ReadAuthCredentials(root).ContainsKey("101"),
                "Automatic recovery did not retain the rotated token pair.");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "auth-expired-offline-recovered-light.png"));
        });
    }

    private static void RunAuthNotificationReconnectScenario(CaptureOptions options)
    {
        RunIsolatedAuthScenario(options, "auth-notification-reconnect", "dark", (window, automation, root) =>
        {
            AutomationElement reconnect = WaitForVisibleAutomationElement(window, "DashboardReconnectButton", TimeSpan.FromSeconds(20));
            InvokeOrClick(reconnect);
            AutomationElement dialog = WaitForVisibleAutomationElement(window, "GitHubDeviceAuthorizationDialog");
            string requestedScopes = WaitForAuthMarkerValue(root, "device.code.requested", TimeSpan.FromSeconds(8));
            AssertProbe(requestedScopes.Contains("notifications", StringComparison.OrdinalIgnoreCase) &&
                requestedScopes.Contains("repo", StringComparison.OrdinalIgnoreCase),
                "Reconnect device request omitted new or previously granted scopes.");
            InvokeOrClick(FindDialogButton(dialog, automation, "Cancel"));
            AssertProbe(ReadAuthCredentials(root).ContainsKey("101"), "Notification reconnect removed the current session.");
            AssertAuthMarker(root, "notifications.scope.required");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "auth-notification-reconnect-dark.png"));
        });
    }

    private static void RunAuthMultiAccountCleanupScenario(CaptureOptions options)
    {
        RunIsolatedAuthScenario(options, "auth-multi-account-cleanup", "dark", (window, automation, root) =>
        {
            WaitForVisibleAutomationElement(window, "ShellRoot", TimeSpan.FromSeconds(20));
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "auth-multi-account-route-dark.png"));
            WaitForVisibleAutomationElement(window, "SettingsPageTitle", TimeSpan.FromSeconds(10));
            AutomationElement sectionList = WaitForVisibleAutomationElement(window, "SettingsSectionList");
            AutomationElement general = WaitForElement(
                "General settings section",
                () => sectionList.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                    .FirstOrDefault(item => string.Equals(item.Name, "General", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(8));
            general.Click();
            InvokeOrClick(WaitForVisibleAutomationElement(window, "SettingsSignOutButton"));

            AutomationElement dialog = WaitForVisibleAutomationElement(window, "SignOutConfirmationDialog");
            AutomationElement removeData = WaitForVisibleAutomationElement(window, "SignOutRemoveAccountDataCheckBox");
            if (!removeData.Patterns.Toggle.IsSupported || removeData.Patterns.Toggle.Pattern.ToggleState.Value != ToggleState.On)
            {
                InvokeOrClick(removeData);
            }
            AssertProbe(removeData.Patterns.Toggle.IsSupported && removeData.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.On,
                "The remove-account-data checkbox could not be selected.");
            CaptureWindowWithPopups(window, Path.Combine(options.OutputDirectory, "auth-multi-account-confirmation-dark.png"));
            InvokeOrClick(FindDialogButton(dialog, automation, "Sign out"));

            WaitForVisibleAutomationElement(window, "LoginRoot", TimeSpan.FromSeconds(20));
            IReadOnlyDictionary<string, string> credentials = ReadAuthCredentials(root);
            AssertProbe(!credentials.ContainsKey("101"), "Signing out with cleanup retained the active account token.");
            AssertProbe(credentials.TryGetValue("202", out string? secondary) && secondary == "automation-secondary-token",
                "Signing out one account removed the other account's token.");
            AssertProbe(ReadAuthSetting(root, "USER_ID") is null or "0", "Sign-out cleanup retained the active account id.");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "auth-multi-account-cleanup-dark.png"));
        }, "--page=settings");
    }

    private static void RunIsolatedAuthScenario(
        CaptureOptions options,
        string scenario,
        string theme,
        Action<Window, UIA3Automation, string> assertion,
        params string[] additionalArguments)
    {
        string root = PrepareAuthScenarioRoot(scenario);
        string[] arguments = [$"--scenario={scenario}", $"--theme={theme}", .. additionalArguments];
        using var app = LaunchApplicationWithDataRoot(options.AppPath, root, killExisting: true, arguments);
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, scenario);
            ResizeWindow(window, 1180, 760);
            assertion(window, automation, root);
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }

    private static string PrepareAuthScenarioRoot(string scenario)
    {
        string root = Path.Combine(GetAutomationDataRoot(), "auth-lifecycle", scenario);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        Directory.CreateDirectory(root);
        return root;
    }
}
