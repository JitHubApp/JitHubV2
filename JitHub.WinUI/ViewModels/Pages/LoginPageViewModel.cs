using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Services;

namespace JitHub.WinUI.ViewModels.Pages;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class LoginPageViewModel : ObservableObject
{
    private readonly IAuthService _authService;
    private readonly ITelemetryService _telemetryService;
    private readonly Func<string, string, string> _getString;

    public LoginPageViewModel(
        IAuthService authService,
        LocalizationService strings,
        ITelemetryService telemetryService)
        : this(authService, strings.GetStringOrDefault, telemetryService)
    {
    }

    internal LoginPageViewModel(
        IAuthService authService,
        Func<string, string, string> getString,
        ITelemetryService telemetryService)
    {
        _authService = authService;
        _getString = getString;
        _telemetryService = SafeTelemetryService.Wrap(telemetryService);
        LoginErrorTitle = GetText("Login.LaunchErrorTitle", "GitHub sign-in needs attention");
        StatusText = SignInDescription;
    }

    public string AppTitle => "JitHub";

    public string HeroTitle => GetText("Login.HeroTitle", "A native GitHub experience for Windows.");

    public string HeroDescription => GetText(
        "Login.HeroDescription",
        "Browse repositories, issues, pull requests, and code in a desktop client that stays fast and keeps the browser-based GitHub sign-in flow.");

    public string CallbackDescription => GetText(
        "Login.CallbackDescription",
        "This WinUI 3 host already reuses the running app instance for jithub:// protocol callbacks, so GitHub sign-in returns to the existing window instead of opening a second one.");

    public string SignInTitle => GetText("Login.SignInTitle", "Sign in with GitHub");

    public string SignInDescription => GetText(
        "Login.SignInDescription",
        "JitHub opens GitHub in your browser.");

    public string ContinueWithGitHubButtonText => GetText("Login.ContinueWithGitHubButton", "Continue with GitHub");

    [ObservableProperty]
    public partial string LoginErrorTitle { get; set; } = string.Empty;

    public bool IsAuthenticated => _authService.Authenticated;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoginEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool HasLoginError { get; set; }

    [ObservableProperty]
    public partial string LoginErrorMessage { get; set; } = string.Empty;

    public void PrepareForDisplay()
    {
        TrackEvent("auth.opened", new Dictionary<string, string?>
        {
            ["page"] = "auth",
            ["source"] = TelemetryTaxonomy.Sources.Route
        });
        LoginErrorTitle = GetText("Login.LaunchErrorTitle", "GitHub sign-in needs attention");
        StatusText = SignInDescription;
        IsLoginEnabled = true;
        HasLoginError = false;
        LoginErrorMessage = string.Empty;
        switch (_authService.RecoveryState)
        {
            case AuthSessionRecoveryState.InvalidCallback:
                ShowLoginError(GetText(
                    "Login.InvalidCallbackError",
                    "GitHub returned a sign-in response that JitHub could not verify. No token was accepted. Try signing in again."));
                break;
            case AuthSessionRecoveryState.Expired:
                ShowLoginError(GetText(
                    "Login.ExpiredSessionError",
                    "Your GitHub session expired. The expired token was removed; sign in again to continue."));
                break;
            case AuthSessionRecoveryState.Cancelled:
                StatusText = GetText(
                    "Login.CancelledStatus",
                    "Sign-in was canceled. You can try again.");
                break;
        }
    }

    public async Task StartLoginAsync()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        TrackEvent("auth.action.executed", new Dictionary<string, string?>
        {
            ["page"] = "auth",
            ["action"] = TelemetryTaxonomy.Actions.SignIn,
            ["source"] = TelemetryTaxonomy.Sources.Login,
            ["result"] = TelemetryTaxonomy.Results.Started
        });
        IsLoginEnabled = false;
        HasLoginError = false;
        LoginErrorMessage = string.Empty;
        StatusText = GetText("Login.OpeningBrowserStatus", "Opening GitHub sign-in in your browser...");

        try
        {
            await _authService.Authenticate();
            StatusText = GetText(
                "Login.CompleteInBrowserStatus",
                "Finish sign-in in the browser. JitHub will return to this window automatically.");
            stopwatch.Stop();
            TrackLoginOutcome(TelemetryTaxonomy.Results.Launched, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            StatusText = GetText(
                "Login.CancelledStatus",
                "Sign-in was canceled. You can try again.");
            TrackLoginOutcome(
                TelemetryTaxonomy.Results.Cancelled,
                stopwatch.Elapsed,
                TelemetryTaxonomy.ErrorKinds.Cancelled);
        }
        catch (InvalidOperationException ex)
        {
            stopwatch.Stop();
            ShowLoginError(JitHub.WinUI.Helpers.UserFacingError.For(
                ex,
                JitHub.WinUI.Helpers.UserFacingErrorKind.SignIn,
                "sign-in"));
            TrackLoginOutcome(
                TelemetryTaxonomy.Results.Error,
                stopwatch.Elapsed,
                TelemetryTaxonomy.ErrorKinds.Launch);
        }
        catch (Exception)
        {
            stopwatch.Stop();
            ShowLoginError(GetGenericLaunchErrorMessage());
            TrackLoginOutcome(
                TelemetryTaxonomy.Results.Error,
                stopwatch.Elapsed,
                TelemetryTaxonomy.ErrorKinds.Unexpected);
        }
        finally
        {
            IsLoginEnabled = true;
        }
    }

    private void TrackEvent(string name, IReadOnlyDictionary<string, string?> properties)
    {
        try
        {
            _telemetryService.TrackEvent(name, properties);
        }
        catch
        {
            // Sign-in UI remains functional if diagnostics are unavailable.
        }
    }

    private void TrackLoginOutcome(string result, TimeSpan duration, string? errorKind = null) =>
        TrackEvent("auth.action.executed", new Dictionary<string, string?>
        {
            ["page"] = "auth",
            ["action"] = TelemetryTaxonomy.Actions.SignIn,
            ["source"] = TelemetryTaxonomy.Sources.Login,
            ["result"] = result,
            ["error_kind"] = errorKind,
            ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(duration)
        });

    private void ShowLoginError(string message)
    {
        StatusText = SignInDescription;
        LoginErrorMessage = message;
        HasLoginError = true;
    }

    private string GetGenericLaunchErrorMessage() => GetText(
        "Login.UnexpectedLaunchError",
        "JitHub could not open GitHub sign-in. Check your default browser and try again.");

    private string GetText(string resourceKey, string fallback) => _getString(resourceKey, fallback);
}
