using System.Security.Cryptography;
using System.Text.Json;
using StackExchange.Redis;

namespace JitHub.Web.Services;

internal sealed class RedisOAuthHandoffBackend : IOAuthHandoffBackend
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const byte PayloadVersion = 1;
    private const string KeyPrefix = "jithub:oauth-handoff:v1:";

    private readonly IDatabase _database;
    private readonly byte[] _encryptionKey;

    public RedisOAuthHandoffBackend(
        IConnectionMultiplexer connectionMultiplexer,
        byte[] encryptionKey)
    {
        ArgumentNullException.ThrowIfNull(connectionMultiplexer);
        ArgumentNullException.ThrowIfNull(encryptionKey);
        if (encryptionKey.Length != 32)
        {
            throw new ArgumentException("OAuth handoff encryption key must be 32 bytes.", nameof(encryptionKey));
        }

        _database = connectionMultiplexer.GetDatabase();
        _encryptionKey = encryptionKey.ToArray();
    }

    public async Task<bool> TryCreateAsync(
        string id,
        OAuthHandoffEntry entry,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(entry);
        string protectedPayload = Protect(plaintext);
        bool created = await _database.StringSetAsync(
            KeyPrefix + id,
            protectedPayload,
            lifetime,
            when: When.NotExists);
        cancellationToken.ThrowIfCancellationRequested();
        return created;
    }

    public async Task<OAuthHandoffEntry?> ConsumeAsync(
        string id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RedisValue protectedPayload = await _database.StringGetDeleteAsync(KeyPrefix + id);
        cancellationToken.ThrowIfCancellationRequested();
        if (!protectedPayload.HasValue)
        {
            return null;
        }

        try
        {
            byte[] plaintext = Unprotect((string)protectedPayload!);
            return JsonSerializer.Deserialize<OAuthHandoffEntry>(plaintext);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        {
            return null;
        }
    }

    private string Protect(ReadOnlySpan<byte> plaintext)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagSize];
        using var aes = new AesGcm(_encryptionKey, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        byte[] payload = new byte[1 + NonceSize + TagSize + ciphertext.Length];
        payload[0] = PayloadVersion;
        nonce.CopyTo(payload.AsSpan(1, NonceSize));
        tag.CopyTo(payload.AsSpan(1 + NonceSize, TagSize));
        ciphertext.CopyTo(payload.AsSpan(1 + NonceSize + TagSize));
        return Convert.ToBase64String(payload);
    }

    private byte[] Unprotect(string protectedPayload)
    {
        byte[] payload = Convert.FromBase64String(protectedPayload);
        if (payload.Length < 1 + NonceSize + TagSize || payload[0] != PayloadVersion)
        {
            throw new CryptographicException("Unsupported OAuth handoff payload.");
        }

        ReadOnlySpan<byte> nonce = payload.AsSpan(1, NonceSize);
        ReadOnlySpan<byte> tag = payload.AsSpan(1 + NonceSize, TagSize);
        ReadOnlySpan<byte> ciphertext = payload.AsSpan(1 + NonceSize + TagSize);
        byte[] plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(_encryptionKey, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
