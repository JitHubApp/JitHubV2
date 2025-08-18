using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Octokit;
using System.Net;

namespace JitHub.Auth;

class ErrorMessage
{
    public string Message { get; set; }
}

public class GithubAuth
{
    private readonly ILogger _logger;

    public GithubAuth(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<GithubAuth>();
    }

    public async Task<OauthToken> Detokenize(string code)
    {
        string clientId = Environment.GetEnvironmentVariable("JithubClientId");
        string appSecret = Environment.GetEnvironmentVariable("JithubAppSecret");

        if (clientId == null)
        {
            throw new Exception("Missing client information");
        }

        if (appSecret == null)
        {
            throw new Exception("Missing app secret");
        }

        _logger.LogInformation("Processed secrets from env variables");

        OauthToken token;
        try
        {
            var request = new OauthTokenRequest(clientId, appSecret, code);
            _logger.LogInformation("Github request created");

            var gitHubClient = new GitHubClient(new ProductHeaderValue("JitHub"));
            _logger.LogInformation("Github client created");

            token = await gitHubClient.Oauth.CreateAccessToken(request);
            _logger.LogInformation("request made from token");

        }
        catch (Exception ex)
        {
            _logger.LogInformation($"{ex}");
            throw new Exception("Github request error");
        }

        if (token == null || string.IsNullOrWhiteSpace(token.AccessToken) || !string.IsNullOrWhiteSpace(token.Error))
        {
            _logger.LogInformation($"token missing information error: {token.Error}, error description: {token.ErrorDescription}");
            throw new Exception($"Github returned missing token information");
        }

        return token;
    }


    public string ProcessRequest(HttpRequestData req)
    {
        var queries = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var temporaryCode = queries["tempCode"];
        _logger.LogInformation($"processed a request with tempCode:{temporaryCode}");

        if (String.IsNullOrWhiteSpace(temporaryCode))
        {
            throw new Exception("Missing temporary code");
        }
        return temporaryCode;
    }

    [Function("GithubCodeToToken")]
    public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "options")] HttpRequestData req)
    {
        // Handle CORS preflight in dev
#if DEBUG
        if (string.Equals(req.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            var preflight = req.CreateResponse(HttpStatusCode.NoContent);
            AddDevCorsHeaders(preflight, req);
            return preflight;
        }
#endif

        try
        {
            string code = ProcessRequest(req);
            var token = await Detokenize(code);
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteStringAsync(token.AccessToken);
#if DEBUG
            AddDevCorsHeaders(response, req);
#endif
            return response;
        }
        catch (Exception ex)
        {
            var response = req.CreateResponse(HttpStatusCode.BadRequest);
            await response.WriteAsJsonAsync(new ErrorMessage { Message = ex.Message});
#if DEBUG
            AddDevCorsHeaders(response, req);
#endif
            return response;
        }
    }

#if DEBUG
    private static void AddDevCorsHeaders(HttpResponseData response, HttpRequestData req)
    {
        var origin = req.Headers.TryGetValues("Origin", out var values) ? values.FirstOrDefault() : "*";
        response.Headers.Add("Access-Control-Allow-Origin", string.IsNullOrEmpty(origin) || origin == "null" ? "*" : origin);
        response.Headers.Add("Vary", "Origin");
        response.Headers.Add("Access-Control-Allow-Methods", "GET, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "*, Authorization, Content-Type");
        response.Headers.Add("Access-Control-Max-Age", "86400");
    }
#endif
}
