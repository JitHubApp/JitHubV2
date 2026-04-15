using System.Net;
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

builder.Services.AddRazorComponents();
builder.Services.AddHttpClient<GithubAuthService>(client =>
{
    client.BaseAddress = new Uri("https://github.com/");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("JitHub.Web");
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    AddAzurePrivateProxyNetwork(options, "10.0.0.0", 8, 104);
    AddAzurePrivateProxyNetwork(options, "172.16.0.0", 12, 108);
    AddAzurePrivateProxyNetwork(options, "192.168.0.0", 16, 112);
});
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

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseForwardedHeaders();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();
app.UseRateLimiter();

RouteGroupBuilder api = app.MapGroup("/api")
    .RequireRateLimiting(GithubAuthRateLimitPolicy);

api.MapGet("/GithubCodeToToken", async Task<IResult> (
    string? tempCode,
    GithubAuthService githubAuth,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    try
    {
        string token = await githubAuth.ExchangeCodeForTokenAsync(tempCode, cancellationToken);
        return TypedResults.Text(token, "text/plain");
    }
    catch (InvalidOperationException ex)
    {
        return TypedResults.Json(
            new WebErrorMessage { Message = ex.Message },
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
        logger.LogError(ex, "Unhandled error while exchanging a GitHub OAuth code for a token.");
        return TypedResults.Json(
            new WebErrorMessage { Message = "An internal error occurred." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapRazorComponents<App>();

app.Run();

static string ResolveCallerIdentity(HttpContext httpContext)
{
    if (httpContext.Connection.RemoteIpAddress is { } remoteIpAddress)
    {
        return remoteIpAddress.ToString();
    }

    return "unknown";
}

static void AddAzurePrivateProxyNetwork(ForwardedHeadersOptions options, string address, int prefixLength, int mappedPrefixLength)
{
    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse($"{address}/{prefixLength}"));
    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse($"::ffff:{address}/{mappedPrefixLength}"));
}

internal sealed class WebErrorMessage
{
    public string Message { get; set; } = string.Empty;
}
