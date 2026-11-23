using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Core.Models.GitHub.Users;
using JitHub.Core.Services.Authentication;
using JitHub.Core.Services.GitHub.Transport;
using JitHub.Core.Services.Telemetry;
using JitHub.Core.Tests.TestSupport;
using JitHub.Core.ViewModels.Authentication;
using Xunit;

namespace JitHub.Core.Tests.ViewModels.Authentication;

public sealed class LoginPageViewModelTests
{
    [Fact]
    public async Task StartLoginAsync_UnexpectedLauncherFailureIsVisibleAndRetryable()
    {
        TestAuthService authService = new()
        {
            AuthenticateHandler = () => throw new NotSupportedException("private failure detail")
        };
        LoginPageViewModel viewModel = CreateViewModel(authService);

        await viewModel.StartLoginAsync();

        Assert.True(viewModel.HasLoginError);
        Assert.Equal(
            "JitHub could not open GitHub sign-in. Check your default browser and try again.",
            viewModel.LoginErrorMessage);
        Assert.DoesNotContain("private failure detail", viewModel.LoginErrorMessage, StringComparison.Ordinal);
        Assert.True(viewModel.IsLoginEnabled);
        Assert.Equal(viewModel.SignInDescription, viewModel.StatusText);
    }

    [Fact]
    public async Task StartLoginAsync_KnownLaunchFailureUsesSafeActionableMessage()
    {
        TestAuthService authService = new()
        {
            AuthenticateHandler = () => throw new InvalidOperationException("Unable to open the GitHub sign-in page.")
        };
        LoginPageViewModel viewModel = CreateViewModel(authService);

        await viewModel.StartLoginAsync();

        Assert.True(viewModel.HasLoginError);
        Assert.Equal("JitHub could not sign you in. Try again.", viewModel.LoginErrorMessage);
        Assert.DoesNotContain("Unable to open", viewModel.LoginErrorMessage, StringComparison.Ordinal);
        Assert.True(viewModel.IsLoginEnabled);
    }

    [Fact]
    public async Task StartLoginAsync_SuccessShowsDeviceCompletionState()
    {
        RecordingTelemetryService telemetry = new();
        LoginPageViewModel viewModel = CreateViewModel(new TestAuthService(), telemetry);

        await viewModel.StartLoginAsync();

        Assert.False(viewModel.HasLoginError);
        Assert.Empty(viewModel.LoginErrorMessage);
        Assert.Equal(
            "GitHub sign-in completed.",
            viewModel.StatusText);
        Assert.True(viewModel.IsLoginEnabled);
        Assert.Equal(
            [TelemetryTaxonomy.Results.Started, TelemetryTaxonomy.Results.Success],
            telemetry.Events
                .Where(static entry => entry.Name == "auth.action.executed")
                .Select(static entry => entry.Properties["result"]));
    }

    [Fact]
    public async Task StartLoginAsync_CancellationCompletesStartedActionExactlyOnce()
    {
        RecordingTelemetryService telemetry = new();
        LoginPageViewModel viewModel = CreateViewModel(
            new TestAuthService
            {
                AuthenticateHandler = static () => Task.FromCanceled(new CancellationToken(canceled: true))
            },
            telemetry);

        await viewModel.StartLoginAsync();

        Assert.Equal(
            [TelemetryTaxonomy.Results.Started, TelemetryTaxonomy.Results.Cancelled],
            telemetry.Events
                .Where(static entry => entry.Name == "auth.action.executed")
                .Select(static entry => entry.Properties["result"]));
        RecordedTelemetryEvent terminal = telemetry.Events.Last();
        Assert.Equal(TelemetryTaxonomy.ErrorKinds.Cancelled, terminal.Properties["error_kind"]);
        Assert.False(viewModel.HasLoginError);
    }

    [Fact]
    public async Task StartLoginAsync_FailureCompletesStartedActionAfterFailure()
    {
        RecordingTelemetryService telemetry = new();
        LoginPageViewModel viewModel = CreateViewModel(
            new TestAuthService
            {
                AuthenticateHandler = static () => Task.FromException(
                    new InvalidOperationException("Unable to open the GitHub sign-in page."))
            },
            telemetry);

        await viewModel.StartLoginAsync();

        Assert.Equal(
            [TelemetryTaxonomy.Results.Started, TelemetryTaxonomy.Results.Error],
            telemetry.Events
                .Where(static entry => entry.Name == "auth.action.executed")
                .Select(static entry => entry.Properties["result"]));
        RecordedTelemetryEvent terminal = telemetry.Events.Last();
        Assert.Equal(TelemetryTaxonomy.ErrorKinds.Launch, terminal.Properties["error_kind"]);
        Assert.True(viewModel.HasLoginError);
    }

    [Fact]
    public void PrepareForDisplay_ClearsPriorFailure()
    {
        RecordingTelemetryService telemetry = new();
        LoginPageViewModel viewModel = CreateViewModel(new TestAuthService(), telemetry);
        viewModel.HasLoginError = true;
        viewModel.LoginErrorMessage = "old failure";
        viewModel.IsLoginEnabled = false;

        viewModel.PrepareForDisplay();

        Assert.False(viewModel.HasLoginError);
        Assert.Empty(viewModel.LoginErrorMessage);
        Assert.True(viewModel.IsLoginEnabled);
        Assert.Equal(viewModel.SignInDescription, viewModel.StatusText);
        Assert.Contains(telemetry.Events, static entry => entry.Name == "auth.opened");
    }

    [Theory]
    [InlineData(AuthSessionRecoveryState.Expired, "expired")]
    public void PrepareForDisplay_ExplainsRecoverableAuthState(
        AuthSessionRecoveryState recoveryState,
        string expectedText)
    {
        LoginPageViewModel viewModel = CreateViewModel(new TestAuthService { RecoveryState = recoveryState });

        viewModel.PrepareForDisplay();

        Assert.True(viewModel.HasLoginError);
        Assert.Contains(expectedText, viewModel.LoginErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.IsLoginEnabled);
    }

    [Fact]
    public async Task StartLoginAsync_NetworkFailureShowsDeviceError()
    {
        LoginPageViewModel viewModel = CreateViewModel(new TestAuthService
        {
            AuthenticateHandler = () => Task.FromException(new HttpRequestException("offline"))
        });

        await viewModel.StartLoginAsync();

        Assert.True(viewModel.HasLoginError);
        Assert.Equal("GitHub could not complete sign-in. Try again.", viewModel.LoginErrorMessage);
    }

    [Fact]
    public async Task StartLoginAsync_MissingPermissionsShowsApprovalGuidance()
    {
        LoginPageViewModel viewModel = CreateViewModel(new TestAuthService
        {
            AuthenticateHandler = () => Task.FromException(DeviceFlowException.For("insufficient_scope"))
        });

        await viewModel.StartLoginAsync();

        Assert.True(viewModel.HasLoginError);
        Assert.Equal("Approve JitHub's requested permissions on GitHub, then try again.", viewModel.LoginErrorMessage);
    }

    [Fact]
    public async Task OfflineSession_RetriesAndReturnsToAuthenticatedState()
    {
        TestAuthService authService = new() { RecoveryState = AuthSessionRecoveryState.Offline };
        authService.RefreshHandler = () =>
        {
            authService.Authenticated = true;
            authService.RecoveryState = AuthSessionRecoveryState.None;
            return Task.FromResult<GitHubUser?>(new GitHubUser { Id = 42, Login = "octocat" });
        };
        LoginPageViewModel viewModel = CreateViewModel(authService);

        viewModel.PrepareForDisplay();
        Assert.True(viewModel.IsSavedSessionRecoveryPending);
        Assert.Contains("automatically", viewModel.StatusText, StringComparison.Ordinal);

        Assert.True(await viewModel.RetrySavedSessionAsync());
        Assert.False(viewModel.IsSavedSessionRecoveryPending);
        Assert.False(viewModel.HasLoginError);
    }

    [Fact]
    public async Task OfflineSession_ExpiredRefreshShowsSignInAction()
    {
        TestAuthService authService = new() { RecoveryState = AuthSessionRecoveryState.Offline };
        authService.RefreshHandler = () =>
        {
            authService.RecoveryState = AuthSessionRecoveryState.Expired;
            return Task.FromResult<GitHubUser?>(null);
        };
        LoginPageViewModel viewModel = CreateViewModel(authService);

        viewModel.PrepareForDisplay();
        Assert.False(await viewModel.RetrySavedSessionAsync());

        Assert.True(viewModel.HasLoginError);
        Assert.Contains("sign in again", viewModel.LoginErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.IsSavedSessionRecoveryPending);
    }

    [Fact]
    public void SessionRestore_HidesSignInAndSaysNoActionIsNeeded()
    {
        TestAuthService authService = new() { Activity = AuthSessionActivity.RestoringSession };
        LoginPageViewModel viewModel = CreateViewModel(authService);

        viewModel.PrepareForDisplay();
        viewModel.BeginObservingSessionActivity();

        Assert.True(viewModel.IsSessionWorkInProgress);
        Assert.False(viewModel.IsSignInActionVisible);
        Assert.Equal("Signing you back in", viewModel.CardTitle);
        Assert.Equal("You don't need to do anything.", viewModel.NoActionNeededText);
    }

    [Fact]
    public void SessionRestore_FailureReturnsToSignIn()
    {
        TestAuthService authService = new() { Activity = AuthSessionActivity.RestoringSession };
        LoginPageViewModel viewModel = CreateViewModel(authService);
        viewModel.PrepareForDisplay();
        viewModel.BeginObservingSessionActivity();

        authService.SetActivity(AuthSessionActivity.Idle);

        Assert.False(viewModel.IsSessionWorkInProgress);
        Assert.True(viewModel.IsSignInActionVisible);
        Assert.Equal(viewModel.SignInTitle, viewModel.CardTitle);
        Assert.Equal(viewModel.SignInDescription, viewModel.StatusText);
    }

    [Fact]
    public void SessionRestore_SlowRequestReturnsSignInAction()
    {
        TestAuthService authService = new() { Activity = AuthSessionActivity.RestoringSession };
        LoginPageViewModel viewModel = CreateViewModel(authService);
        viewModel.PrepareForDisplay();
        viewModel.BeginObservingSessionActivity();

        viewModel.ShowSlowSessionRestore();

        Assert.False(viewModel.IsSessionWorkInProgress);
        Assert.True(viewModel.IsSignInActionVisible);
        Assert.Contains("slow to respond", viewModel.StatusText, StringComparison.Ordinal);

        authService.SetActivity(AuthSessionActivity.Idle);

        Assert.Equal(viewModel.SignInDescription, viewModel.StatusText);
    }

    [Fact]
    public async Task StartLoginAsync_ApprovedSignInStaysBusyUntilNavigation()
    {
        TestAuthService authService = new();
        bool wasBusyDuringCompletion = false;
        LoginPageViewModel? viewModel = null;
        authService.AuthenticateHandler = () =>
        {
            authService.SetActivity(AuthSessionActivity.CompletingSignIn);
            wasBusyDuringCompletion = viewModel!.IsSessionWorkInProgress && !viewModel.IsSignInActionVisible;
            authService.Authenticated = true;
            authService.SetActivity(AuthSessionActivity.Idle);
            return Task.CompletedTask;
        };
        viewModel = CreateViewModel(authService);
        viewModel.PrepareForDisplay();
        viewModel.BeginObservingSessionActivity();

        await viewModel.StartLoginAsync();

        Assert.True(wasBusyDuringCompletion);
        Assert.True(viewModel.IsSessionWorkInProgress);
        Assert.False(viewModel.IsSignInActionVisible);
        Assert.False(viewModel.IsLoginEnabled);
        Assert.Equal("Finishing sign-in", viewModel.CardTitle);
    }

    [Fact]
    public async Task StartLoginAsync_FailureAfterApprovalReturnsToSignIn()
    {
        TestAuthService authService = new();
        authService.AuthenticateHandler = () =>
        {
            authService.SetActivity(AuthSessionActivity.CompletingSignIn);
            authService.SetActivity(AuthSessionActivity.Idle);
            return Task.FromException(new HttpRequestException("offline"));
        };
        LoginPageViewModel viewModel = CreateViewModel(authService);
        viewModel.PrepareForDisplay();
        viewModel.BeginObservingSessionActivity();

        await viewModel.StartLoginAsync();

        Assert.False(viewModel.IsSessionWorkInProgress);
        Assert.True(viewModel.IsSignInActionVisible);
        Assert.True(viewModel.IsLoginEnabled);
        Assert.True(viewModel.HasLoginError);
    }

    [Fact]
    public async Task StartLoginAsync_DuringSlowRestoreKeepsRequestStatus()
    {
        TestAuthService authService = new() { Activity = AuthSessionActivity.RestoringSession };
        LoginPageViewModel viewModel = CreateViewModel(authService);
        string? statusAfterRestoreEnded = null;
        authService.AuthenticateHandler = () =>
        {
            authService.SetActivity(AuthSessionActivity.Idle);
            statusAfterRestoreEnded = viewModel.StatusText;
            return Task.CompletedTask;
        };
        viewModel.PrepareForDisplay();
        viewModel.BeginObservingSessionActivity();
        viewModel.ShowSlowSessionRestore();

        await viewModel.StartLoginAsync();

        Assert.Equal("Requesting a sign-in code from GitHub...", statusAfterRestoreEnded);
    }

    [Fact]
    public void EndObservingSessionActivity_StopsUpdates()
    {
        TestAuthService authService = new();
        LoginPageViewModel viewModel = CreateViewModel(authService);
        viewModel.BeginObservingSessionActivity();

        viewModel.EndObservingSessionActivity();
        authService.SetActivity(AuthSessionActivity.RestoringSession);

        Assert.False(viewModel.IsSessionWorkInProgress);
    }

    private static LoginPageViewModel CreateViewModel(
        IAuthService authService,
        ITelemetryService? telemetry = null) =>
        new(
            authService,
            static (_, fallback) => fallback,
            telemetry ?? new RecordingTelemetryService());

    private sealed class TestAuthService : IAuthService
    {
        public Func<Task> AuthenticateHandler { get; set; } = static () => Task.CompletedTask;
        public bool Authenticated { get; set; }
        public GitHubUser? AuthenticatedUser { get; set; }
        public AuthSessionRecoveryState RecoveryState { get; set; } = AuthSessionRecoveryState.None;
        public AuthSessionActivity Activity { get; set; }
        public event EventHandler? ActivityChanged;
        public void SetActivity(AuthSessionActivity activity)
        {
            Activity = activity;
            ActivityChanged?.Invoke(this, EventArgs.Empty);
        }
        public Func<Task<GitHubUser?>>? RefreshHandler { get; set; }
        public Task InitializeAsync() => Task.CompletedTask;
        public Task Authenticate() => AuthenticateHandler();
        public Task<bool> EnsureScopesAsync(params string[] scopes) => Task.FromResult(true);
        public Task<bool> Authorize(string response) => Task.FromResult(true);
        public Task<GitHubUser?> RefreshAuthenticatedUserAsync() =>
            RefreshHandler?.Invoke() ?? Task.FromResult(AuthenticatedUser);
        public string? GetToken(long userId) => null;
        public bool CheckAuth(long userId) => false;
        public void SignOut() { }
        public bool HandleAuthenticationFailure(GitHubAuthenticationException exception) { SignOut(); return true; }
    }
}
