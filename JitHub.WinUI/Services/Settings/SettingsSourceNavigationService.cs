using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Models.NavArgs;
using JitHub.WinUI.Views.Pages;

namespace JitHub.Services;

public sealed class SettingsSourceNavigationService : ISettingsSourceNavigationService
{
    private readonly IAuthService _authService;
    private readonly IAccountService _accountService;
    private readonly IGitHubRepositoryQueryService _repositoryQueryService;
    private readonly NavigationService _navigationService;
    private readonly ITelemetryService _telemetryService;

    public SettingsSourceNavigationService(
        IAuthService authService,
        IAccountService accountService,
        IGitHubRepositoryQueryService repositoryQueryService,
        NavigationService navigationService,
        ITelemetryService telemetryService)
    {
        _authService = authService;
        _accountService = accountService;
        _repositoryQueryService = repositoryQueryService;
        _navigationService = navigationService;
        _telemetryService = SafeTelemetryService.Wrap(telemetryService);
    }

    public async Task<SettingsSourceNavigationOutcome> OpenAsync(
        CancellationToken cancellationToken = default)
    {
        long userId = _authService.AuthenticatedUser?.Id ?? _accountService.GetUser();
        string? token = _authService.GetToken(userId);
        if (string.IsNullOrWhiteSpace(token) ||
            (!GitHubAuthenticationConstants.IsPublicAccessToken(token) && userId <= 0))
        {
            Track("unavailable");
            return new(SettingsSourceNavigationResult.Unavailable);
        }

        try
        {
            string partition = GitHubAuthenticationConstants.IsPublicAccessToken(token)
                ? "public"
                : userId.ToString(CultureInfo.InvariantCulture);
            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            CachedResult<GitHubRepository> result = await _repositoryQueryService.GetRepositoryAsync(
                token,
                partition,
                "JitHubApp",
                "JitHubV2",
                QueryFetchPolicy.StaleFirst,
                GitHubRequestPriority.Visible,
                timeout.Token);
            GitHubRepository? repository = result.Value;
            if (repository is null)
            {
                Track("empty", result.CacheState);
                return new(SettingsSourceNavigationResult.Empty, result.CacheState);
            }

            _navigationService.NavigateTo(
                "JitHub",
                typeof(RepoDetailPage),
                new RepoDetailPageArgs(RepoPageType.CodePage, repository));
            Track("success", result.CacheState);
            return new(SettingsSourceNavigationResult.Success, result.CacheState);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open JitHub source repository: {ex}");
            Track("error");
            return new(SettingsSourceNavigationResult.Error);
        }
    }

    private void Track(string result, CacheState? cacheState = null)
    {
        Dictionary<string, string?> properties = new()
        {
            ["action"] = "open_source",
            ["result"] = result
        };
        if (cacheState is not null)
        {
            properties["cache_state"] = cacheState.Value.ToString().ToLowerInvariant();
        }

        _telemetryService.TrackEvent("settings.action.executed", properties);
    }
}
