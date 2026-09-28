using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.WinUI.Tests.TestDoubles;
using JitHub.WinUI.ViewModels.Pages;
using Xunit;

namespace JitHub.WinUI.Tests.ViewModels;

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
    public void ActiveLoginPage_UsesCompiledBindingsAndStableAutomationIdentity()
    {
        string xamlPath = FindRepositoryFile("JitHub.WinUI", "Views", "Pages", "LoginPage.xaml");
        string xaml = File.ReadAllText(xamlPath);

        Assert.DoesNotContain("{Binding", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"LoginSignInButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"LoginErrorInfoBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("{x:Bind ViewModel.IsLoginEnabled, Mode=OneWay}", xaml, StringComparison.Ordinal);
    }

    private static LoginPageViewModel CreateViewModel(
        IAuthService authService,
        ITelemetryService? telemetry = null) =>
        new(
            authService,
            static (_, fallback) => fallback,
            telemetry ?? new RecordingTelemetryService());

    private static string FindRepositoryFile(params string[] segments)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine([current.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(segments)}.");
    }

    private sealed class TestAuthService : IAuthService
    {
        public Func<Task> AuthenticateHandler { get; init; } = static () => Task.CompletedTask;
        public bool Authenticated { get; set; }
        public GitHubUser? AuthenticatedUser { get; set; }
        public AuthSessionRecoveryState RecoveryState { get; set; } = AuthSessionRecoveryState.None;
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
    }
}
