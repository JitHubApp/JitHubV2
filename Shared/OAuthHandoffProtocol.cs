using System;
using System.Security.Cryptography;

namespace JitHub.Security;

public static class OAuthHandoffProtocol
{
    public const string ProductionStatePrefix = "WINUI3V3_";
    public const string DevelopmentStatePrefix = "WINUI3V3DEBUG_";

    public static string CreateState(bool development, out string verifier)
    {
        verifier = CreateBase64UrlSecret(64);
        string challenge = CreateChallenge(verifier);
        string nonce = CreateBase64UrlSecret(32);
        string prefix = development ? DevelopmentStatePrefix : ProductionStatePrefix;
        return $"{prefix}{nonce}.{challenge}";
    }

    public static bool TryGetChallenge(string? state, out string challenge)
    {
        challenge = string.Empty;
        if (string.IsNullOrWhiteSpace(state))
        {
            return false;
        }

        string value = state.Trim();
        string? prefix = value.StartsWith(ProductionStatePrefix, StringComparison.Ordinal)
            ? ProductionStatePrefix
            : value.StartsWith(DevelopmentStatePrefix, StringComparison.Ordinal)
                ? DevelopmentStatePrefix
                : null;
        if (prefix is null)
        {
            return false;
        }

        int separator = value.IndexOf('.', prefix.Length);
        if (separator <= prefix.Length || separator == value.Length - 1 ||
            value.IndexOf('.', separator + 1) >= 0)
        {
            return false;
        }

        string nonce = value[prefix.Length..separator];
        string candidate = value[(separator + 1)..];
        if (!IsBase64Url(nonce, expectedLength: 43) || !IsBase64Url(candidate, expectedLength: 43))
        {
            return false;
        }

        challenge = candidate;
        return true;
    }

    public static bool Verify(string verifier, string expectedChallenge)
    {
        if (string.IsNullOrWhiteSpace(verifier) || string.IsNullOrWhiteSpace(expectedChallenge))
        {
            return false;
        }

        byte[] actual = System.Text.Encoding.ASCII.GetBytes(CreateChallenge(verifier));
        byte[] expected = System.Text.Encoding.ASCII.GetBytes(expectedChallenge);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static string CreateChallenge(string verifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifier);
        byte[] digest = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(digest);
    }

    public static string CreateBase64UrlSecret(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);
        return Base64UrlEncode(RandomNumberGenerator.GetBytes(byteCount));
    }

    private static bool IsBase64Url(string value, int expectedLength) =>
        value.Length == expectedLength &&
        value.AsSpan().IndexOfAnyExcept(
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".AsSpan()) < 0;

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
