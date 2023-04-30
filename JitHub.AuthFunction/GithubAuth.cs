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
    public static async Task<OauthToken> Detokenize(string code)
    {
        string clientId = Environment.GetEnvironmentVariable("JithubClientId", EnvironmentVariableTarget.Process);
        string appSecret = Environment.GetEnvironmentVariable("JithubAppSecret", EnvironmentVariableTarget.Process);

        if (clientId == null || appSecret == null)
        {
            throw new Exception("Missing client information");
        }

        try
        {
            var request = new OauthTokenRequest(clientId, appSecret, code);
            var gitHubClient = new GitHubClient(new ProductHeaderValue("JitHub"));
            var token = await gitHubClient.Oauth.CreateAccessToken(request);

            if (token == null || string.IsNullOrWhiteSpace(token.AccessToken) || !string.IsNullOrWhiteSpace(token.Error))
            {
                throw new Exception("Github returned missing token information");
            }

            return token;
        }
        catch
        {
            throw new Exception("Github request error");

        }
    }

    public static string ProcessRequest(HttpRequest req)
    {
        string temporaryCode = req.Query["tempCode"];
        temporaryCode = temporaryCode ?? req.Headers["tempCode"];

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
            string code = ProcessRequest(req);
            var token = await Detokenize(code);

            return new OkObjectResult(token.AccessToken);
        }

        catch (Exception ex)
        {
            return new BadRequestObjectResult($"{ex}");
        }
    }
}
