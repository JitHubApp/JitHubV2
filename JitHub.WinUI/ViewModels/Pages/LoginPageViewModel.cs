using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Services;
using Microsoft.UI.Xaml.Data;

namespace JitHub.WinUI.ViewModels.Pages;

 [Bindable]
public sealed partial class LoginPageViewModel : ViewModelBase
{
    private readonly IAuthService _authService;

    public LoginPageViewModel()
    {
        _authService = GetService<IAuthService>();
        StatusText = SignInDescription;
    }

    public string AppTitle => "JitHub";

    public string HeroTitle => GetString("Login.HeroTitle", "A native GitHub experience for Windows.");

    public string HeroDescription => GetString(
        "Login.HeroDescription",
        "Browse repositories, issues, pull requests, and code in a desktop client that stays fast and keeps the browser-based GitHub sign-in flow.");

    public string CallbackDescription => GetString(
        "Login.CallbackDescription",
        "This WinUI 3 host already reuses the running app instance for jithub:// protocol callbacks, so GitHub sign-in returns to the existing window instead of opening a second one.");

    public string SignInTitle => GetString("Login.SignInTitle", "Sign in with GitHub");

    public string SignInDescription => GetString(
        "Login.SignInDescription",
        "JitHub opens GitHub in your browser.");

    public string ContinueWithGitHubButtonText => GetString("Login.ContinueWithGitHubButton", "Continue with GitHub");

    public bool IsAuthenticated => _authService.Authenticated;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoginEnabled { get; set; } = true;

    public void PrepareForDisplay()
    {
        StatusText = SignInDescription;
        IsLoginEnabled = true;
    }

    public async Task StartLoginAsync()
    {
        IsLoginEnabled = false;
        StatusText = GetString("Login.OpeningBrowserStatus", "Opening GitHub sign-in in your browser...");

        try
        {
            await _authService.Authenticate();
            StatusText = GetString(
                "Login.CompleteInBrowserStatus",
                "Finish sign-in in the browser. JitHub will return to this window automatically.");
        }
        catch (InvalidOperationException ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsLoginEnabled = true;
        }
    }
}
