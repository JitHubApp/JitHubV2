using StackExchange.Redis;

namespace JitHub.Web.Services;

internal static class OAuthHandoffBackendRegistration
{
    internal const string RedisConnectionStringName = "OAuthHandoffRedis";
    internal const string EncryptionKeySetting = "OAuthHandoff:EncryptionKey";

    public static OAuthHandoffBackendSelection Configure(
        IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        string? redisConnection = configuration.GetConnectionString(RedisConnectionStringName);
        string? encryptionKeyText = configuration[EncryptionKeySetting];
        bool hasRedis = !string.IsNullOrWhiteSpace(redisConnection);
        bool hasEncryptionKey = !string.IsNullOrWhiteSpace(encryptionKeyText);

        if (!hasRedis || !hasEncryptionKey)
        {
            string? reason = isDevelopment && !hasRedis && !hasEncryptionKey
                ? null
                : "Redis configuration is absent or incomplete. Pending sign-ins expire after two minutes and may be lost during restart or scale-out.";
            return UseInMemoryBackend(services, reason);
        }

        byte[] encryptionKey;
        try
        {
            encryptionKey = Convert.FromBase64String(encryptionKeyText!);
        }
        catch (FormatException)
        {
            return UseInMemoryBackend(
                services,
                "The configured handoff encryption key is invalid. Pending sign-ins expire after two minutes and may be lost during restart or scale-out.");
        }

        if (encryptionKey.Length != 32)
        {
            return UseInMemoryBackend(
                services,
                "The configured handoff encryption key has an invalid length. Pending sign-ins expire after two minutes and may be lost during restart or scale-out.");
        }

        ConfigurationOptions redisOptions;
        try
        {
            redisOptions = ConfigurationOptions.Parse(redisConnection!);
        }
        catch (ArgumentException)
        {
            return UseInMemoryBackend(
                services,
                "The configured Redis connection is invalid. Pending sign-ins expire after two minutes and may be lost during restart or scale-out.");
        }

        redisOptions.AbortOnConnectFail = false;
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOptions));
        services.AddSingleton<IOAuthHandoffBackend>(serviceProvider =>
            new RedisOAuthHandoffBackend(
                serviceProvider.GetRequiredService<IConnectionMultiplexer>(),
                encryptionKey));
        return new OAuthHandoffBackendSelection(UsesRedis: true, FallbackReason: null);
    }

    private static OAuthHandoffBackendSelection UseInMemoryBackend(
        IServiceCollection services,
        string? reason)
    {
        services.AddSingleton<IOAuthHandoffBackend, InMemoryOAuthHandoffBackend>();
        return new OAuthHandoffBackendSelection(UsesRedis: false, reason);
    }
}

internal sealed record OAuthHandoffBackendSelection(
    bool UsesRedis,
    string? FallbackReason);
