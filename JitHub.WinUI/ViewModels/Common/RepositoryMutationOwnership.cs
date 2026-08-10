using System;
using System.Security.Cryptography;
using System.Text;

namespace JitHub.WinUI.ViewModels.Common;

public readonly record struct RepositoryMutationOwnership(
    string AccountUserId,
    string AuthSessionFingerprint,
    long RepositoryId,
    long Generation,
    long MutationVersion)
{
    public static string CreateSessionFingerprint(string accessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)));
    }

    public bool CanPublish(
        string currentAccountUserId,
        string currentAuthSessionFingerprint,
        long currentRepositoryId,
        long currentGeneration,
        long currentMutationVersion) =>
        string.Equals(AccountUserId, currentAccountUserId, StringComparison.Ordinal) &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(AuthSessionFingerprint),
            Encoding.ASCII.GetBytes(currentAuthSessionFingerprint)) &&
        RepositoryId == currentRepositoryId &&
        Generation == currentGeneration &&
        MutationVersion == currentMutationVersion;
}
