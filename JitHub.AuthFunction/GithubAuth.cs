using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Octokit;

namespace JitHub.AuthFunction
{
    public static class GithubAuth
    {
        [FunctionName("GithubCodeToToken")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req,
            ILogger log)
        {

            string temporaryCode = req.Query["tempCode"];

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);

            temporaryCode = temporaryCode ?? data?.tempCode;

            string clientId = Environment.GetEnvironmentVariable("JithubClientId");
            string appSecret = Environment.GetEnvironmentVariable("JithubAppSecret");


            var request = new OauthTokenRequest(clientId, appSecret, temporaryCode);

            var gitHubClient = new GitHubClient(new ProductHeaderValue("JitHub"));
            var token = await gitHubClient.Oauth.CreateAccessToken(request);

            var response = new { token = token };

            return new JsonResult(response);
        }
    }
}
