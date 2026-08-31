using System.Threading.RateLimiting;
using JitHub.Web;
using JitHub.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
const string GithubAuthRateLimitPolicy = "github-auth";
bool isDevelopment = builder.Environment.IsDevelopment();
int permitLimit = isDevelopment ? 1000 : 10;
ForwardedHeaderTrustPolicy forwardedHeaderTrust = ForwardedHeaderTrustPolicy.Load(builder.Configuration);
OAuthRedirectUriPolicy oauthRedirectPolicy = OAuthRedirectUriPolicy.Load(builder.Configuration, builder.Environment);
OAuthHandoffBackendSelection oauthHandoffBackend = OAuthHandoffBackendRegistration.Configure(
    builder.Services,
    builder.Configuration,
    isDevelopment);

builder.Services.AddRazorComponents();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(oauthRedirectPolicy);
builder.Services.AddSingleton<OAuthHandoffStore>();
builder.Services.AddHttpClient<GithubAuthService>(client =>
{
    client.BaseAddress = new Uri("https://github.com/");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("JitHub.Web");
});
if (forwardedHeaderTrust.IsEnabled)
{
    builder.Services.Configure<ForwardedHeadersOptions>(forwardedHeaderTrust.Apply);
}
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy<string>(GithubAuthRateLimitPolicy, httpContext =>
    {
        string partitionKey = ResolveCallerIdentity(httpContext);
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromHours(1),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });
    });
});

WebApplication app = builder.Build();

if (oauthHandoffBackend.FallbackReason is { } fallbackReason)
{
    app.Logger.LogWarning(
        "OAuth handoffs are using the bounded process-local backend. {FallbackReason}",
        fallbackReason);
}

if (oauthRedirectPolicy.ConfigurationError is { } callbackConfigurationError)
{
    app.Logger.LogWarning(
        "GitHub OAuth callbacks are disabled. {ConfigurationError}",
        callbackConfigurationError);
}

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

if (forwardedHeaderTrust.IsEnabled)
{
    app.UseForwardedHeaders();
}
else
{
    app.Logger.LogInformation(
        "Forwarded headers are disabled. Configure exact {Section}:KnownProxies or {Section}:KnownNetworks entries when running behind a trusted reverse proxy.",
        ForwardedHeaderTrustPolicy.ConfigurationSectionName,
        ForwardedHeaderTrustPolicy.ConfigurationSectionName);
}
app.UseHttpsRedirection();
app.UseAntiforgery();
app.UseRateLimiter();
app.MapStaticAssets();
app.MapGet("/healthz", () => TypedResults.Text("ok", "text/plain"));

RouteGroupBuilder api = app.MapGroup("/api")
    .RequireRateLimiting(GithubAuthRateLimitPolicy);

api.MapPost("/GithubCodeToHandoff", async Task<IResult> (
    OAuthHandoffCreateRequest request,
    HttpContext httpContext,
    GithubAuthService githubAuth,
    OAuthRedirectUriPolicy redirectPolicy,
    OAuthHandoffStore handoffStore,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    SetNoStore(httpContext);
    try
    {
        string effectiveRedirectUri = redirectPolicy.RequireAllowed(request.RedirectUri);
        string token = await githubAuth.ExchangeCodeForTokenAsync(request.TempCode, effectiveRedirectUri, cancellationToken);
        string handoff = await handoffStore.CreateAsync(
            token,
            request.State ?? string.Empty,
            cancellationToken);
        return TypedResults.Json(new OAuthHandoffCreatedResponse(handoff));
    }
    catch (InvalidOperationException ex)
    {
        logger.LogWarning(ex, "GitHub OAuth handoff creation failed.");
        return TypedResults.Json(
            new WebErrorMessage { Message = "We could not complete sign-in. Please try again." },
            statusCode: StatusCodes.Status400BadRequest);
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return TypedResults.Json(
            new WebErrorMessage { Message = "GitHub request timed out." },
            statusCode: StatusCodes.Status504GatewayTimeout);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unhandled error while creating a GitHub OAuth handoff.");
        return TypedResults.Json(
            new WebErrorMessage { Message = "An internal error occurred." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
}).DisableAntiforgery();

api.MapPost("/RedeemGithubHandoff", async Task<IResult> (
    OAuthHandoffRedeemRequest request,
    HttpContext httpContext,
    OAuthHandoffStore handoffStore,
    CancellationToken cancellationToken) =>
{
    SetNoStore(httpContext);
    string? token = await handoffStore.RedeemAsync(
        request.Handoff,
        request.State,
        request.Verifier,
        cancellationToken);
    return token is not null
        ? Results.Json(new OAuthHandoffRedeemedResponse(token))
        : Results.Json(
            new WebErrorMessage { Message = "This sign-in handoff is invalid or has expired." },
            statusCode: StatusCodes.Status400BadRequest);
}).DisableAntiforgery();

app.MapRazorComponents<App>()
    .WithStaticAssets();

app.Run();

static void SetNoStore(HttpContext httpContext)
{
    httpContext.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
    httpContext.Response.Headers.Pragma = "no-cache";
    httpContext.Response.Headers.Expires = "0";
}

static string ResolveCallerIdentity(HttpContext httpContext)
{
    if (httpContext.Connection.RemoteIpAddress is { } remoteIpAddress)
    {
        return remoteIpAddress.ToString();
    }

    return "unknown";
}

internal sealed class WebErrorMessage
{
    public string Message { get; set; } = string.Empty;
}
