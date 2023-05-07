using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Octokit;

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
    public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequestData req)
    {
        try
        {
            string code = ProcessRequest(req);
            var token = await Detokenize(code);
            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await response.WriteStringAsync(token.AccessToken);
            return response;
        }

        catch (Exception ex)
        {
            var response = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await response.WriteAsJsonAsync(new ErrorMessage { Message = ex.Message});
            return response;
        }
    }
}
