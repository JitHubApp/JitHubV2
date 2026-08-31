using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public interface IAuthHandoffClient
{
    Task<string?> RedeemAsync(
        string? authorizationCallbackUrl,
        string handoff,
        string state,
        string verifier,
        CancellationToken cancellationToken = default);
}

public sealed partial class AuthHandoffClient : IAuthHandoffClient, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public AuthHandoffClient()
        : this(new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false
        }), ownsClient: true)
    {
    }

    internal AuthHandoffClient(HttpClient httpClient, bool ownsClient = false)
    {
        _httpClient = httpClient;
        _ownsClient = ownsClient;
    }

    public async Task<string?> RedeemAsync(
        string? authorizationCallbackUrl,
        string handoff,
        string state,
        string verifier,
        CancellationToken cancellationToken = default)
    {
        Uri endpoint = CreateRedemptionEndpoint(authorizationCallbackUrl);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
            endpoint,
            new RedeemRequest(handoff, state, verifier),
            AuthHandoffJsonContext.Default.RedeemRequest,
            timeout.Token);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Gone)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        RedeemResponse? payload = await response.Content.ReadFromJsonAsync(
            AuthHandoffJsonContext.Default.RedeemResponse,
            timeout.Token);
        return string.IsNullOrWhiteSpace(payload?.Token) ? null : payload.Token;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    internal static Uri CreateRedemptionEndpoint(string? authorizationCallbackUrl)
    {
        if (!Uri.TryCreate(authorizationCallbackUrl, UriKind.Absolute, out Uri? callback) ||
            (!string.Equals(callback.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
             !(string.Equals(callback.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && callback.IsLoopback)))
        {
            throw new InvalidOperationException("The OAuth callback must use HTTPS, or HTTP on loopback for development.");
        }

        return new UriBuilder(callback.Scheme, callback.Host, callback.IsDefaultPort ? -1 : callback.Port)
        {
            Path = "/api/RedeemGithubHandoff"
        }.Uri;
    }

    private sealed record RedeemRequest(string Handoff, string State, string Verifier);

    private sealed record RedeemResponse(string Token);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(RedeemRequest), TypeInfoPropertyName = "RedeemRequest")]
    [JsonSerializable(typeof(RedeemResponse), TypeInfoPropertyName = "RedeemResponse")]
    private sealed partial class AuthHandoffJsonContext : JsonSerializerContext
    {
    }
}
