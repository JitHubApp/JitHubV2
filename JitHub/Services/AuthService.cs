using CommunityToolkit.Mvvm.ComponentModel;
using Octokit;
using System;
using System.Threading.Tasks;
using Windows.Security.Credentials;
using System.Collections.Generic;

namespace JitHub.Services
{
    public class AuthToken
    {
        public string TokenType { get; set; }
        public string AccessToken { get; set; }
        public IReadOnlyList<string> Scope { get; set; }
        public string Error { get; set; }
        public string ErrorDescription { get; set; }
        public string ErrorUri { get; set; }

    }

    public class AuthService : ObservableObject, IAuthService
    {
        private IAppConfig _appConfigService;
        private IAccountService _accountService;
        private IGitHubService _githubService;
        private NavigationService _navigationService;
        private PasswordVault _passwordVault;
        private User _authenticatedUser;
        private bool _authenticated = false;

        public bool Authenticated
        {
            get => _authenticated;
            set => SetProperty(ref _authenticated, value);
        }

        public User AuthenticatedUser
        {
            get => _authenticatedUser;
            set => SetProperty(ref _authenticatedUser, value);
        }

        public AuthService(IAppConfig appConfigService, IAccountService accountService, IGitHubService gitHubService, NavigationService navigationService)
        {
            _appConfigService = appConfigService;
            _accountService = accountService;
            _githubService = gitHubService;
            _navigationService = navigationService;
            _passwordVault = new PasswordVault();
            try
            {
                var token = GetToken(_accountService.GetUser());
                _githubService.GitHubClient.Credentials = new Credentials(token);
                Authenticated = true;
                AuthenticatedUser = _githubService.GitHubClient.User.Current().GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Authenticated = false;
            }
        }

        public async Task Authenticate()
        {
            string clientId = _appConfigService.Credential.ClientId;

            OauthLoginRequest request = new OauthLoginRequest(clientId)
            {
                Scopes = { "user", "repo", "delete_repo" },
            };

            Uri oauthLoginUrl = _githubService.GitHubClient.Oauth.GetGitHubLoginUrl(request);

            await Windows.System.Launcher.LaunchUriAsync(oauthLoginUrl);
        }

        public async Task<bool> Authorize(string response)
        {
            try
            {
                string responseData = response.Substring(response.IndexOf("token"));

                string[] keyValPairs = responseData.Split('=');
                string token = keyValPairs[1].Split('&')[0];

                string clientId = _appConfigService.Credential.ClientId;

                if (token != null)
                {
                    _githubService.GitHubClient.Credentials = new Credentials(token);
                    await SaveToken(token, clientId);
                    Authenticated = true;
                    AuthenticatedUser = await _githubService.GitHubClient.User.Current();
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> SaveToken(string token, string clientId)
        {
            try
            {
                User user = await _githubService.GitHubClient.User.Current();
                _passwordVault.Add(new PasswordCredential(clientId, user.Id.ToString(), token));
                _accountService.SaveUser(user.Id);

                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool CheckAuth(long userId)
        {
            try
            {
                var token = GetToken(userId);
                return token != null;
            }
            catch
            {
                return false;
            }
        }

        public string GetToken(long userId)
        {
            try
            {
                var credentialList = _passwordVault.FindAllByUserName(userId.ToString());
                if (credentialList.Count > 0)
                {
                    credentialList[0].RetrievePassword();
                    return credentialList[0].Password;
                }
                else
                {
                    return null;
                }
            }
            catch
            {
                return null;
            }
        }

        public void SignOut()
        {
            _accountService.RemoveUser();
            Authenticated = false;
            _navigationService.Unauthorized();
        }
    }
}