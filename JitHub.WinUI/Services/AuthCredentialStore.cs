using System;
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

    void SaveAccountToken(long userId, string token);

    void RemoveAccountToken(long userId);

    string? GetPendingToken();

    void SavePendingToken(string token);

    void RemovePendingToken();

    string? GetPendingState();

    void SavePendingState(string state);

    void RemovePendingState();

    string? GetPendingVerifier();

    void SavePendingVerifier(string verifier);

    void RemovePendingVerifier();
}

public sealed class AuthCredentialStore : IAuthCredentialStore
{
    internal const string PendingTokenUserName = "__pending__";
    internal const string PendingStateUserName = "__pending_state__";
    internal const string PendingVerifierUserName = "__pending_verifier__";

    private readonly ICredentialVaultBackend _vault;
    private readonly IAppConfig _appConfig;

    public AuthCredentialStore(ICredentialVaultBackend vault, IAppConfig appConfig)
    {
        _vault = vault;
        _appConfig = appConfig;
    }

    public string? GetAccountToken(long userId) => userId <= 0
        ? null
        : _vault.Retrieve(Resource, userId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public void SaveAccountToken(long userId, string token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        _vault.Store(Resource, userId.ToString(System.Globalization.CultureInfo.InvariantCulture), token);
    }

    public void RemoveAccountToken(long userId)
    {
        if (userId > 0)
        {
            _vault.Remove(Resource, userId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    public string? GetPendingToken() => _vault.Retrieve(Resource, PendingTokenUserName);

    public void SavePendingToken(string token) => _vault.Store(Resource, PendingTokenUserName, token);

    public void RemovePendingToken() => _vault.Remove(Resource, PendingTokenUserName);

    public string? GetPendingState() => _vault.Retrieve(Resource, PendingStateUserName);

    public void SavePendingState(string state) => _vault.Store(Resource, PendingStateUserName, state);

    public void RemovePendingState() => _vault.Remove(Resource, PendingStateUserName);

    public string? GetPendingVerifier() => _vault.Retrieve(Resource, PendingVerifierUserName);

    public void SavePendingVerifier(string verifier) => _vault.Store(Resource, PendingVerifierUserName, verifier);

    public void RemovePendingVerifier() => _vault.Remove(Resource, PendingVerifierUserName);

    private string Resource => _appConfig.Credential.ClientId;
}
