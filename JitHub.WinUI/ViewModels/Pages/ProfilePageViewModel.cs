using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models.GitHub;
using JitHub.Services;
using Microsoft.UI.Xaml.Media.Imaging;

namespace JitHub.WinUI.ViewModels.Pages;

public sealed partial class ProfilePageViewModel : ViewModelBase
{
    private readonly IAuthService _authService;
    private Uri? _profileUri;

    public ProfilePageViewModel()
    {
        _authService = GetService<IAuthService>();
        StatusText = GetString("Profile.LoadingStatus", "Loading your GitHub profile...");
        DisplayNameText = GetString("Profile.DefaultDisplayName", "GitHub user");
        LoginText = "@login";
    }

    public string PageTitle => GetString("pageTitle_DeveloperProfileView", "Profile");

    public string RefreshButtonText => GetString("refreshAppBarButton.Label", "Refresh");

    public string OpenOnGitHubButtonText => GetString("Profile.OpenOnGitHubButton", "Open on GitHub");

    public string SessionNote => GetString(
        "Profile.SessionNote",
        "JitHub now restores your GitHub identity directly through the WinUI host, so profile data stays consistent with the active desktop session.");

    public Uri? ProfileUri => _profileUri;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DisplayNameText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LoginText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BioText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DetailsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial BitmapImage? AvatarImageSource { get; set; }

    [ObservableProperty]
    public partial bool IsOpenProfileEnabled { get; set; }

    public async Task LoadProfileAsync(bool forceRefresh)
    {
        StatusText = GetString("Profile.LoadingStatus", "Loading your GitHub profile...");
        IsOpenProfileEnabled = false;

        try
        {
            GitHubUser? user = forceRefresh
                ? await _authService.RefreshAuthenticatedUserAsync()
                : _authService.AuthenticatedUser ?? await _authService.RefreshAuthenticatedUserAsync();

            if (user is null)
            {
                StatusText = GetString(
                    "Profile.UnavailableStatus",
                    "GitHub profile details are not available. Please sign in again.");
                return;
            }

            ApplyProfile(user);
            StatusText = GetString("Profile.LoadedStatus", "GitHub profile loaded.");
        }
        catch (GitHubAuthenticationException)
        {
            StatusText = GetString(
                "Common.AuthInvalid",
                "GitHub authentication is no longer valid. Please sign in again.");
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            StatusText = ex.Message;
        }
        catch (HttpRequestException)
        {
            StatusText = GetString(
                "Profile.NetworkError",
                "JitHub could not reach GitHub to refresh your profile.");
        }
    }

    private void ApplyProfile(GitHubUser user)
    {
        DisplayNameText = string.IsNullOrWhiteSpace(user.Name)
            ? user.Login
            : user.Name;
        LoginText = $"@{user.Login}";
        BioText = string.IsNullOrWhiteSpace(user.Bio)
            ? GetString("Profile.BioUnavailable", "No public GitHub bio is available.")
            : user.Bio;
        StatsText = FormatString(
            "Profile.StatsText",
            "{0} public repositories  •  {1} followers  •  {2} following",
            user.PublicRepos,
            user.Followers,
            user.Following);

        List<string> details = [];
        if (!string.IsNullOrWhiteSpace(user.Company))
        {
            details.Add(user.Company);
        }

        if (!string.IsNullOrWhiteSpace(user.Location))
        {
            details.Add(user.Location);
        }

        if (!string.IsNullOrWhiteSpace(user.Blog))
        {
            details.Add(user.Blog);
        }

        DetailsText = details.Count == 0
            ? GetString("Profile.DetailsUnavailable", "No extra public profile details are available.")
            : string.Join("  •  ", details);

        AvatarImageSource = Uri.TryCreate(user.AvatarUrl, UriKind.Absolute, out Uri? avatarUri)
            ? new BitmapImage(avatarUri)
            : null;

        _profileUri = Uri.TryCreate(user.HtmlUrl, UriKind.Absolute, out Uri? profileUri) ? profileUri : null;
        IsOpenProfileEnabled = _profileUri is not null;
    }
}
