using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JitHub.WinUI;

namespace JitHub.Services;

internal static class AuthLifecycleScenario
{
    public const string Cancel = "auth-cancel";
    public const string DeviceSuccess = "auth-device-success";
    public const string CancelAfterApproval = "auth-cancel-after-approval";
    public const string ExpiredToken = "auth-expired-token";
    public const string NotificationReconnect = "auth-notification-reconnect";
    public const string OfflineLaunch = "auth-offline-launch";
    public const string ExpiredOfflineRecovery = "auth-expired-offline-recovery";
    public const string MultiAccountCleanup = "auth-multi-account-cleanup";
}

internal sealed partial class AuthLifecycleAutomationContext
{
    internal const long PrimaryUserId = 101;
    internal const long SecondaryUserId = 202;
    internal const string PrimaryToken = "automation-primary-token";
    internal const string SecondaryToken = "automation-secondary-token";
    internal const string ExpiredToken = "automation-expired-token";

    private static readonly HashSet<string> KnownScenarios = new(StringComparer.OrdinalIgnoreCase)
    {
        AuthLifecycleScenario.Cancel,
        AuthLifecycleScenario.DeviceSuccess,
        AuthLifecycleScenario.CancelAfterApproval,
        AuthLifecycleScenario.ExpiredToken,
        AuthLifecycleScenario.NotificationReconnect,
        AuthLifecycleScenario.OfflineLaunch,
        AuthLifecycleScenario.ExpiredOfflineRecovery,
        AuthLifecycleScenario.MultiAccountCleanup
    };

    private readonly object _markerGate = new();

    private AuthLifecycleAutomationContext(string scenario, string localFolderPath)
    {
        Scenario = scenario;
        RootPath = Path.Combine(localFolderPath, "AuthLifecycle");
        Directory.CreateDirectory(RootPath);
        CredentialPath = Path.Combine(RootPath, "credentials.vault");
        MarkerPath = Path.Combine(RootPath, "scenario-state.ndjson");
        SeedMarkerPath = Path.Combine(RootPath, "seeded");
    }

    public string Scenario { get; }

    public string RootPath { get; }

    public string CredentialPath { get; }

    public string MarkerPath { get; }

    private string SeedMarkerPath { get; }

    public static bool IsKnownScenario(string? scenario) =>
        !string.IsNullOrWhiteSpace(scenario) && KnownScenarios.Contains(scenario.Trim());

    public static AuthLifecycleAutomationContext? TryCreate(LaunchOptions options)
    {
        if (!IsKnownScenario(options.Scenario) ||
            !AppDataPathPolicy.TryGetAutomationRoots(out string localFolderPath, out _))
        {
            return null;
        }

        return new AuthLifecycleAutomationContext(options.Scenario!.Trim(), localFolderPath);
    }

    internal static AuthLifecycleAutomationContext CreateForTests(string scenario, string localFolderPath)
    {
        if (!IsKnownScenario(scenario))
        {
            throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        return new AuthLifecycleAutomationContext(scenario, localFolderPath);
    }

    public void Seed(ISettingService settings, IAccountService account, IAuthCredentialStore credentials)
    {
        if (File.Exists(SeedMarkerPath))
        {
            return;
        }

        switch (Scenario)
        {
            case AuthLifecycleScenario.ExpiredToken:
                account.SaveUser(PrimaryUserId);
                credentials.SaveAccountToken(PrimaryUserId, ExpiredToken);
                break;
            case AuthLifecycleScenario.NotificationReconnect:
            case AuthLifecycleScenario.OfflineLaunch:
                account.SaveUser(PrimaryUserId);
                credentials.SaveAccountToken(PrimaryUserId, PrimaryToken);
                break;
            case AuthLifecycleScenario.ExpiredOfflineRecovery:
                account.SaveUser(PrimaryUserId);
                credentials.SaveAccountSession(PrimaryUserId, new GitHubTokenSession(
                    "automation-expired-access", "automation-recovery-refresh",
                    DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddMonths(6)));
                break;
            case AuthLifecycleScenario.MultiAccountCleanup:
                account.SaveUser(PrimaryUserId);
                credentials.SaveAccountToken(PrimaryUserId, PrimaryToken);
                credentials.SaveAccountToken(SecondaryUserId, SecondaryToken);
                break;
        }

        File.WriteAllText(SeedMarkerPath, Scenario, Encoding.UTF8);
        Record("scenario.seeded", Scenario);
    }

    public IExternalUriLauncher CreateUriLauncher() => new AuthLifecycleExternalUriLauncher(this);

    public IGitHubDeviceFlowClient CreateDeviceFlowClient() => new AuthLifecycleDeviceFlowClient(this);

    public HttpMessageHandler CreateHttpMessageHandler() => new AuthLifecycleHttpMessageHandler(this);

    public void Record(string name, string? value = null)
    {
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
        string entry = $"{DateTimeOffset.UtcNow:O}\t{name}\t{encoded}{Environment.NewLine}";
        lock (_markerGate)
        {
            File.AppendAllText(MarkerPath, entry, Encoding.UTF8);
        }
    }

    private sealed class AuthLifecycleDeviceFlowClient : IGitHubDeviceFlowClient
    {
        private readonly AuthLifecycleAutomationContext _context;

        public AuthLifecycleDeviceFlowClient(AuthLifecycleAutomationContext context) => _context = context;

        public Task<DeviceAuthorizationChallenge> RequestCodeAsync(
            string clientId,
            IReadOnlyCollection<string> scopes,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _context.Record("device.code.requested", string.Join(' ', scopes));
            return Task.FromResult(new DeviceAuthorizationChallenge(
                "automation-device-code", "JITH-UB01", new Uri("https://github.com/login/device"),
                DateTimeOffset.UtcNow.AddMinutes(15), TimeSpan.FromSeconds(1)));
        }

        public async Task<GitHubTokenSession> PollAsync(
            string clientId,
            DeviceAuthorizationChallenge challenge,
            CancellationToken cancellationToken = default)
        {
            if (_context.Scenario is AuthLifecycleScenario.DeviceSuccess or
                AuthLifecycleScenario.CancelAfterApproval)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                _context.Record("device.approved");
                return new GitHubTokenSession("automation-device-token", "automation-refresh-token",
                    DateTimeOffset.UtcNow.AddHours(8), DateTimeOffset.UtcNow.AddMonths(6));
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }

        public Task<GitHubTokenSession> RefreshAsync(
            string clientId,
            string refreshToken,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_context.Scenario == AuthLifecycleScenario.ExpiredOfflineRecovery &&
                !File.Exists(Path.Combine(_context.RootPath, "network-restored")))
            {
                _context.Record("refresh.offline");
                return Task.FromException<GitHubTokenSession>(
                    new HttpRequestException("The deterministic auth lifecycle transport is offline."));
            }

            _context.Record("refresh.completed");
            return Task.FromResult(new GitHubTokenSession(
                "automation-refreshed-token", "automation-rotated-refresh-token",
                DateTimeOffset.UtcNow.AddHours(8), DateTimeOffset.UtcNow.AddMonths(6)));
        }
    }

    internal sealed class AuthLifecycleExternalUriLauncher : IExternalUriLauncher
    {
        private readonly AuthLifecycleAutomationContext _context;

        public AuthLifecycleExternalUriLauncher(AuthLifecycleAutomationContext context)
        {
            _context = context;
        }

        public Task<bool> LaunchAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(uri);
            cancellationToken.ThrowIfCancellationRequested();
            _context.Record("oauth.launch.requested", uri.AbsoluteUri);
            if (string.Equals(_context.Scenario, AuthLifecycleScenario.Cancel, StringComparison.OrdinalIgnoreCase))
            {
                _context.Record("oauth.launch.cancelled");
                throw new OperationCanceledException("The GitHub sign-in launch was cancelled.");
            }

            _context.Record("oauth.launch.completed");
            return Task.FromResult(true);
        }
    }

internal sealed partial class AuthLifecycleHttpMessageHandler : HttpMessageHandler
    {
        private const string UserJson = "{\"login\":\"automation-user\",\"id\":101,\"name\":\"Automation User\",\"avatar_url\":\"\",\"html_url\":\"https://github.com/automation-user\",\"public_repos\":0,\"followers\":0,\"following\":0}";
        private readonly AuthLifecycleAutomationContext _context;

        public AuthLifecycleHttpMessageHandler(AuthLifecycleAutomationContext context)
        {
            _context = context;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? string.Empty;
            _context.Record("http.request", $"{request.Method.Method} {path}");

            if (string.Equals(_context.Scenario, AuthLifecycleScenario.OfflineLaunch, StringComparison.OrdinalIgnoreCase) &&
                !File.Exists(Path.Combine(_context.RootPath, "network-restored")))
            {
                _context.Record("http.offline", path);
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("The deterministic auth lifecycle transport is offline."));
            }

            string? token = request.Headers.Authorization?.Parameter;
            if (string.Equals(token, ExpiredToken, StringComparison.Ordinal))
            {
                _context.Record("http.unauthorized", path);
                return Task.FromResult(Json(HttpStatusCode.Unauthorized, "{\"message\":\"Bad credentials\"}"));
            }

            if (path.StartsWith("notifications", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_context.Scenario, AuthLifecycleScenario.NotificationReconnect, StringComparison.OrdinalIgnoreCase))
            {
                _context.Record("notifications.scope.required");
                return Task.FromResult(Json(HttpStatusCode.Forbidden, "{\"message\":\"Resource not accessible by integration\"}"));
            }

            if (string.Equals(path, "user", StringComparison.OrdinalIgnoreCase))
            {
                HttpResponseMessage response = Json(HttpStatusCode.OK, UserJson);
                string scopes = string.Equals(_context.Scenario, AuthLifecycleScenario.NotificationReconnect, StringComparison.OrdinalIgnoreCase)
                    ? "user, repo"
                    : "user, repo, notifications";
                response.Headers.TryAddWithoutValidation("X-OAuth-Scopes", scopes);
                return Task.FromResult(response);
            }

            if (path.StartsWith("search/issues", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("search/repositories", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"total_count\":0,\"incomplete_results\":false,\"items\":[]}"));
            }

            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(Json(HttpStatusCode.OK, "[]"));
            }

            return Task.FromResult(Json(HttpStatusCode.NoContent, string.Empty));
        }

        private static HttpResponseMessage Json(HttpStatusCode statusCode, string json)
        {
            var response = new HttpResponseMessage(statusCode);
            if (!string.IsNullOrEmpty(json))
            {
                response.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            return response;
        }
    }
}

internal sealed class FileCredentialVaultBackend : ICredentialVaultBackend
{
    private readonly string _path;
    private readonly object _gate = new();

    public FileCredentialVaultBackend(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public string? Retrieve(string resource, string userName)
    {
        lock (_gate)
        {
            return ReadValues().TryGetValue((resource, userName), out string? value) ? value : null;
        }
    }

    public void Store(string resource, string userName, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        lock (_gate)
        {
            Dictionary<(string Resource, string UserName), string> values = ReadValues();
            values[(resource, userName)] = secret;
            WriteValues(values);
        }
    }

    public void Remove(string resource, string userName)
    {
        lock (_gate)
        {
            Dictionary<(string Resource, string UserName), string> values = ReadValues();
            if (values.Remove((resource, userName)))
            {
                WriteValues(values);
            }
        }
    }

    internal IReadOnlyDictionary<(string Resource, string UserName), string> Snapshot()
    {
        lock (_gate)
        {
            return ReadValues();
        }
    }

    private Dictionary<(string Resource, string UserName), string> ReadValues()
    {
        Dictionary<(string Resource, string UserName), string> values = [];
        if (!File.Exists(_path))
        {
            return values;
        }

        foreach (string line in File.ReadAllLines(_path))
        {
            string[] parts = line.Split('\t');
            if (parts.Length != 3)
            {
                continue;
            }

            try
            {
                string resource = Decode(parts[0]);
                string userName = Decode(parts[1]);
                values[(resource, userName)] = Decode(parts[2]);
            }
            catch (FormatException)
            {
                // Ignore a torn entry; the next successful write repairs the file.
            }
        }

        return values;
    }

    private void WriteValues(IReadOnlyDictionary<(string Resource, string UserName), string> values)
    {
        string temporaryPath = $"{_path}.{Environment.ProcessId}.tmp";
        string[] lines = values
            .OrderBy(static pair => pair.Key.Resource, StringComparer.Ordinal)
            .ThenBy(static pair => pair.Key.UserName, StringComparer.Ordinal)
            .Select(static pair => $"{Encode(pair.Key.Resource)}\t{Encode(pair.Key.UserName)}\t{Encode(pair.Value)}")
            .ToArray();
        File.WriteAllLines(temporaryPath, lines, Encoding.UTF8);
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
}
