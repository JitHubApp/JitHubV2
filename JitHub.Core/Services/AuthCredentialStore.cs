using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Windows.Security.Credentials;

namespace JitHub.Services;

public interface ICredentialVaultBackend
{
    string? Retrieve(string resource, string userName);

    void Store(string resource, string userName, string secret);

    void Remove(string resource, string userName);
}

public sealed class WindowsCredentialVaultBackend : ICredentialVaultBackend
{
    private const uint ElementNotFoundHResult = 0x80070490;
    private readonly PasswordVault _vault = new();

    public string? Retrieve(string resource, string userName)
    {
        try
        {
            PasswordCredential credential = _vault.Retrieve(resource, userName);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception exception) when ((uint)exception.HResult == ElementNotFoundHResult)
        {
            return null;
        }
    }

    public void Store(string resource, string userName, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        Remove(resource, userName);
        _vault.Add(new PasswordCredential(resource, userName, secret));
    }

    public void Remove(string resource, string userName)
    {
        try
        {
            PasswordCredential credential = _vault.Retrieve(resource, userName);
            _vault.Remove(credential);
        }
        catch (Exception exception) when ((uint)exception.HResult == ElementNotFoundHResult)
        {
        }
    }
}

public interface IAuthCredentialStore
{
    string? GetAccountToken(long userId);

    GitHubTokenSession? GetAccountSession(long userId);

    void SaveAccountToken(long userId, string token);

    void SaveAccountSession(long userId, GitHubTokenSession session);

    void RemoveAccountToken(long userId);

}

public sealed class AuthCredentialStore : IAuthCredentialStore
{
    private const string SessionPrefix = "jithub-session-v2:";
    private readonly ICredentialVaultBackend _vault;
    private readonly IAppConfig _appConfig;

    public AuthCredentialStore(ICredentialVaultBackend vault, IAppConfig appConfig)
    {
        _vault = vault;
        _appConfig = appConfig;
    }

    public string? GetAccountToken(long userId) => GetAccountSession(userId)?.AccessToken;

    public GitHubTokenSession? GetAccountSession(long userId)
    {
        if (userId <= 0)
        {
            return null;
        }

        string? stored = _vault.Retrieve(Resource, userId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        if (!stored.StartsWith(SessionPrefix, StringComparison.Ordinal))
        {
            return new GitHubTokenSession(stored, null, null, null);
        }

        try
        {
            byte[] json = Convert.FromBase64String(stored[SessionPrefix.Length..]);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string? access = root.GetProperty("access").GetString();
            if (string.IsNullOrWhiteSpace(access))
            {
                return null;
            }

            string? refresh = root.TryGetProperty("refresh", out JsonElement refreshValue)
                ? refreshValue.GetString()
                : null;
            List<string> grantedScopes = [];
            if (root.TryGetProperty("scopes", out JsonElement scopes) && scopes.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement scope in scopes.EnumerateArray())
                {
                    if (scope.ValueKind == JsonValueKind.String && scope.GetString() is { Length: > 0 } value)
                    {
                        grantedScopes.Add(value);
                    }
                }
            }
            return new GitHubTokenSession(
                access,
                refresh,
                ReadExpiry(root, "accessExpiresAt"),
                ReadExpiry(root, "refreshExpiresAt"),
                grantedScopes);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    public void SaveAccountToken(long userId, string token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        _vault.Store(Resource, userId.ToString(System.Globalization.CultureInfo.InvariantCulture), token);
    }

    public void SaveAccountSession(long userId, GitHubTokenSession session)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.AccessToken);

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("access", session.AccessToken);
            writer.WriteString("refresh", session.RefreshToken);
            writer.WriteStartArray("scopes");
            foreach (string scope in session.GrantedScopes ?? [])
            {
                writer.WriteStringValue(scope);
            }
            writer.WriteEndArray();
            if (session.AccessTokenExpiresAt is { } accessExpiry)
            {
                writer.WriteString("accessExpiresAt", accessExpiry);
            }

            if (session.RefreshTokenExpiresAt is { } refreshExpiry)
            {
                writer.WriteString("refreshExpiresAt", refreshExpiry);
            }
            writer.WriteEndObject();
        }

        string serialized = SessionPrefix + Convert.ToBase64String(stream.ToArray());
        _vault.Store(Resource, userId.ToString(System.Globalization.CultureInfo.InvariantCulture), serialized);
    }

    private static DateTimeOffset? ReadExpiry(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
        value.TryGetDateTimeOffset(out DateTimeOffset expiry)
            ? expiry
            : null;

    public void RemoveAccountToken(long userId)
    {
        if (userId > 0)
        {
            _vault.Remove(Resource, userId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private string Resource => _appConfig.Credential.ClientId;
}
