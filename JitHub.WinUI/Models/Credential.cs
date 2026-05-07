namespace JitHub.Models;

public class Credential
{
    public string ClientId { get; set; } = string.Empty;

    public string? DevelopmentClientId { get; set; }

    public string? AuthorizationCallbackUrl { get; set; }

    public string? DevelopmentAuthorizationCallbackUrl { get; set; }
}
