using JitHub.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
using System.Threading.RateLimiting;

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
bool isDevelopment = builder.Environment.IsDevelopment();
int limit = isDevelopment ? 1000 : 10;
const string GithubAuthRateLimitPolicy = "github-auth";
HashSet<string> allowedOrigins = new(
    builder.Configuration
        .GetSection("Cors:AllowedOrigins")
        .GetChildren()
        .Select(section => NormalizeOrigin(section.Value))
        .Where(origin => origin is not null)
        .Cast<string>(),
    StringComparer.OrdinalIgnoreCase);
string[] allowedOriginSuffixes =
    builder.Configuration
        .GetSection("Cors:AllowedOriginSuffixes")
        .GetChildren()
        .Select(section => section.Value?.Trim())
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Cast<string>()
        .ToArray();

builder.Services.AddHttpClient<GithubAuth>(client =>
{
    client.BaseAddress = new Uri("https://github.com/");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("JitHub.Auth");
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, JitHubAuthJsonSerializerContext.Default);
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
            PermitLimit = limit,
            Window = TimeSpan.FromHours(1),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });
    });
});
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (isDevelopment)
        {
            policy.AllowAnyOrigin()
                  .AllowAnyHeader()
                  .AllowAnyMethod();
            return;
        }

        policy.SetIsOriginAllowed(origin => IsAllowedOrigin(origin, allowedOrigins, allowedOriginSuffixes))
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

WebApplication app = builder.Build();

app.UseForwardedHeaders();
app.UseCors();
app.UseRateLimiter();

RouteGroupBuilder api = app.MapGroup("/api")
    .RequireRateLimiting(GithubAuthRateLimitPolicy);

api.MapGet("/GithubCodeToToken", async Task<IResult> (string? tempCode, GithubAuth githubAuth, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    try
    {
        string token = await githubAuth.DetokenizeAsync(tempCode, cancellationToken);
        return TypedResults.Text(token, "text/plain");
    }
    catch (InvalidOperationException ex)
    {
        return TypedResults.Json(
            new ErrorMessage { Message = ex.Message },
            JitHubAuthJsonSerializerContext.Default.ErrorMessage,
            statusCode: StatusCodes.Status400BadRequest);
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return TypedResults.Json(
            new ErrorMessage { Message = "GitHub request timed out." },
            JitHubAuthJsonSerializerContext.Default.ErrorMessage,
            statusCode: StatusCodes.Status504GatewayTimeout);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unhandled error while exchanging a GitHub OAuth code for a token.");
        return TypedResults.Json(
            new ErrorMessage { Message = "An internal error occurred." },
            JitHubAuthJsonSerializerContext.Default.ErrorMessage,
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.Run();

static string ResolveCallerIdentity(HttpContext httpContext)
{
    if (httpContext.Connection.RemoteIpAddress is { } remoteIpAddress)
    {
        return remoteIpAddress.ToString();
    }

    return "unknown";
}

static bool IsAllowedOrigin(string origin, HashSet<string> allowedOrigins, string[] allowedOriginSuffixes)
{
    string? normalizedOrigin = NormalizeOrigin(origin);
    if (normalizedOrigin is null || !Uri.TryCreate(normalizedOrigin, UriKind.Absolute, out Uri? uri))
    {
        return false;
    }

    return allowedOrigins.Contains(normalizedOrigin)
        || allowedOriginSuffixes.Any(suffix => uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}

static string? NormalizeOrigin(string? origin)
{
    if (string.IsNullOrWhiteSpace(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
    {
        return null;
    }

    if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    return uri.GetLeftPart(UriPartial.Authority);
}

static void AddAzurePrivateProxyNetwork(ForwardedHeadersOptions options, string address, int prefixLength, int mappedPrefixLength)
{
    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse($"{address}/{prefixLength}"));
    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse($"::ffff:{address}/{mappedPrefixLength}"));
}
