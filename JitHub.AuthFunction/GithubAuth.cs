using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Octokit;

namespace JitHub.AuthFunction;
public static class GithubAuth
{
    public static async Task<OauthToken> Detokenize(string code, ILogger log)
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

        log.LogInformation("Processed secrets from env variables");

        OauthToken token;
        try
        {
            var request = new OauthTokenRequest(clientId, appSecret, code);
            log.LogInformation("Github request created");

            var gitHubClient = new GitHubClient(new ProductHeaderValue("JitHub"));
            log.LogInformation("Github client created");

            token = await gitHubClient.Oauth.CreateAccessToken(request);
            log.LogInformation("request made from token");

        }
        catch (Exception ex)
        {
            log.LogInformation($"{ex}");
            throw new Exception("Github request error");
        }

        if (token == null || string.IsNullOrWhiteSpace(token.AccessToken) || !string.IsNullOrWhiteSpace(token.Error))
        {
            log.LogInformation($"token missing information error: {token.Error}, error description: {token.ErrorDescription}");
            throw new Exception($"Github returned missing token information");
        }

        return token;
    }


    public static string ProcessRequest(HttpRequest req, ILogger log)
    {
        string temporaryCode = req.Query["tempCode"];
        temporaryCode = temporaryCode ?? req.Headers["tempCode"];
        log.LogInformation($"processed a request with tempCode:{temporaryCode}");

        if (String.IsNullOrWhiteSpace(temporaryCode))
        {
            throw new Exception("Missing temporary code");
        }
        return temporaryCode;
    }

    [FunctionName("GithubCodeToToken")]
    public static async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req,
        ILogger log)
    {
        try
        {
            string code = ProcessRequest(req, log);
            var token = await Detokenize(code, log);

            return new OkObjectResult(token.AccessToken);
        }

        catch (Exception ex)
        {
            return new BadRequestObjectResult($"{ex}");
        }
    }
}
