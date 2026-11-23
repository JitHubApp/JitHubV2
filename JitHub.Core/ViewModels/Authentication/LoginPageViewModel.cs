using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Core.Services.Authentication;
using JitHub.Core.Services.Localization;
using JitHub.Core.Services.Telemetry;

namespace JitHub.Core.ViewModels.Authentication;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class LoginPageViewModel : ObservableObject
{
    private readonly IAuthService _authService;
    private readonly ITelemetryService _telemetryService;
    private readonly Func<string, string, string> _getString;
    private SynchronizationContext? _activityContext;
    private bool _isRestoreSlow;

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
        CardTitle = SignInTitle;
    }

    /// <summary>
    /// How long a saved-session restore may run before the sign-in action returns,
    /// so a stalled network request can never leave the page without a way forward.
    /// </summary>
    public static TimeSpan SessionRestoreActionDelay { get; } = TimeSpan.FromSeconds(10);

    public string AppTitle => "JitHub";

    public string HeroTitle => GetText("Login.HeroTitle", "A native GitHub experience for Windows.");

    public string HeroDescription => GetText(
        "Login.HeroDescription",
        "Browse repositories, issues, pull requests, and code in a native desktop client.");

    public string CallbackDescription => GetText(
        "Login.CallbackDescription",
        "JitHub signs in directly with GitHub using a code shown in the app.");

    public string SignInTitle => GetText("Login.SignInTitle", "Sign in with GitHub");

    public string SignInDescription => GetText(
        "Login.SignInDescription",
        "JitHub will show a code to enter on GitHub. No JitHub server is needed.");

    public string ContinueWithGitHubButtonText => GetText("Login.ContinueWithGitHubButton", "Continue with GitHub");

    [ObservableProperty]
    public partial string LoginErrorTitle { get; set; } = string.Empty;

    public bool IsAuthenticated => _authService.Authenticated;

    public bool IsSavedSessionRecoveryPending =>
        _authService.RecoveryState is AuthSessionRecoveryState.Offline or
            AuthSessionRecoveryState.ServiceUnavailable;

    public Task WaitForStoredSessionAsync() => _authService.InitializeAsync();

    public void ShowSavedSessionRecoveryStatus()
    {
        if (IsSavedSessionRecoveryPending)
        {
            StatusText = GetText(
                "Login.SavedSessionRecoveryStatus",
                "GitHub is unavailable. JitHub will retry your saved session automatically.");
        }
        else if (_authService.RecoveryState == AuthSessionRecoveryState.Expired)
        {
            ShowLoginError(GetText(
                "Login.ExpiredSessionError",
                "Your GitHub session expired. The expired token was removed; sign in again to continue."));
        }
    }

    /// <summary>Starts reflecting auth work on this page. Pair with <see cref="EndObservingSessionActivity"/>.</summary>
    public void BeginObservingSessionActivity()
    {
        _activityContext = SynchronizationContext.Current;
        _authService.ActivityChanged -= AuthService_ActivityChanged;
        _authService.ActivityChanged += AuthService_ActivityChanged;
        RefreshSessionPresentation();
    }

    public void EndObservingSessionActivity()
    {
        _authService.ActivityChanged -= AuthService_ActivityChanged;
        _activityContext = null;
    }

    /// <summary>Returns the sign-in action while a slow restore keeps running behind it.</summary>
    public void ShowSlowSessionRestore()
    {
        if (_authService.Activity != AuthSessionActivity.RestoringSession)
        {
            return;
        }

        _isRestoreSlow = true;
        RefreshSessionPresentation();
        StatusText = GetText(
            "Login.SlowRestoreStatus",
            "GitHub is slow to respond. Keep waiting, or sign in again.");
    }

    private void AuthService_ActivityChanged(object? sender, EventArgs e)
    {
        SynchronizationContext? context = _activityContext;
        if (context is null || ReferenceEquals(context, SynchronizationContext.Current))
        {
            RefreshSessionPresentation();
            return;
        }

        context.Post(static state => ((LoginPageViewModel)state!).RefreshSessionPresentation(), this);
    }

    private void RefreshSessionPresentation()
    {
        AuthSessionActivity activity = _authService.Activity;
        bool slowRestoreEnded = _isRestoreSlow && activity != AuthSessionActivity.RestoringSession;
        if (slowRestoreEnded)
        {
            _isRestoreSlow = false;
        }

        // Authenticated on this page means navigation home is about to happen.
        bool isCompleting = activity == AuthSessionActivity.CompletingSignIn || _authService.Authenticated;
        bool isRestoring = activity == AuthSessionActivity.RestoringSession && !_isRestoreSlow;
        bool isBusy = isCompleting || isRestoring;
        bool wasBusy = IsSessionWorkInProgress;

        IsSessionWorkInProgress = isBusy;
        IsSignInActionVisible = !isBusy;
        if (isCompleting)
        {
            CardTitle = GetText("Login.FinishingTitle", "Finishing sign-in");
            StatusText = GetText(
                "Login.FinishingDetail",
                "GitHub approved this device. JitHub is loading your account.");
        }
        else if (isRestoring)
        {
            CardTitle = GetText("Login.RestoringTitle", "Signing you back in");
            StatusText = GetText(
                "Login.RestoringDetail",
                "JitHub is reconnecting to GitHub with your saved session.");
        }
        else
        {
            CardTitle = SignInTitle;
            if (wasBusy || slowRestoreEnded)
            {
                StatusText = SignInDescription;
            }
        }
    }

    public string NoActionNeededText => GetText("Login.NoActionNeeded", "You don't need to do anything.");

    [ObservableProperty]
    public partial string CardTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSessionWorkInProgress { get; set; }

    [ObservableProperty]
    public partial bool IsSignInActionVisible { get; set; } = true;

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
            case AuthSessionRecoveryState.Offline:
            case AuthSessionRecoveryState.ServiceUnavailable:
                ShowSavedSessionRecoveryStatus();
                break;
        }
    }

    public async Task<bool> RetrySavedSessionAsync()
    {
        if (!IsSavedSessionRecoveryPending)
        {
            return IsAuthenticated;
        }

        await _authService.RefreshAuthenticatedUserAsync();
        if (IsAuthenticated)
        {
            return true;
        }

        if (_authService.RecoveryState == AuthSessionRecoveryState.Expired)
        {
            ShowLoginError(GetText(
                "Login.ExpiredSessionError",
                "Your GitHub session expired. The expired token was removed; sign in again to continue."));
        }

        return false;
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
        _isRestoreSlow = false;
        IsLoginEnabled = false;
        HasLoginError = false;
        LoginErrorMessage = string.Empty;
        StatusText = GetText("Login.OpeningBrowserStatus", "Requesting a sign-in code from GitHub...");

        try
        {
            await _authService.Authenticate();
            RefreshSessionPresentation();
            if (!IsSessionWorkInProgress)
            {
                StatusText = GetText(
                    "Login.CompleteInBrowserStatus",
                    "GitHub sign-in completed.");
            }

            stopwatch.Stop();
            TrackLoginOutcome(TelemetryTaxonomy.Results.Success, stopwatch.Elapsed);
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
            ShowLoginError(JitHub.Core.Helpers.UserFacingError.For(
                ex,
                JitHub.Core.Helpers.UserFacingErrorKind.SignIn,
                "sign-in"));
            TrackLoginOutcome(
                TelemetryTaxonomy.Results.Error,
                stopwatch.Elapsed,
                TelemetryTaxonomy.ErrorKinds.Launch);
        }
        catch (DeviceFlowException ex)
        {
            stopwatch.Stop();
            ShowLoginError(ex.Code switch
            {
                "access_denied" => GetText("Login.DeviceDeniedError", "GitHub authorization was cancelled. Try again."),
                "expired_token" => GetText("Login.DeviceExpiredError", "The GitHub sign-in code expired. Try again."),
                "device_flow_disabled" => GetText("Login.DeviceDisabledError", "GitHub device sign-in is unavailable for this app."),
                "insufficient_scope" => GetText("Login.DevicePermissionError", "Approve JitHub's requested permissions on GitHub, then try again."),
                _ => GetText("Login.DeviceError", "GitHub could not complete sign-in. Try again.")
            });
            TrackLoginOutcome(
                TelemetryTaxonomy.Results.Error,
                stopwatch.Elapsed,
                TelemetryTaxonomy.ErrorKinds.Authentication);
        }
        catch (HttpRequestException)
        {
            stopwatch.Stop();
            ShowLoginError(GetText("Login.DeviceError", "GitHub could not complete sign-in. Try again."));
            TrackLoginOutcome(
                TelemetryTaxonomy.Results.Error,
                stopwatch.Elapsed,
                TelemetryTaxonomy.ErrorKinds.Network);
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
            // After success the page navigates home, so the action must not reappear first.
            IsLoginEnabled = !_authService.Authenticated;
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
