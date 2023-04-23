using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Octokit;
using System.Text.Json;

namespace JitHub.AuthFunction
{
    public static class GithubAuth
    {
        public static async Task<OauthToken> Detokenize(string code)
        {

            try
            {
                string clientId = Environment.GetEnvironmentVariable("JithubClientId");
                string appSecret = Environment.GetEnvironmentVariable("JithubAppSecret");


                var request = new OauthTokenRequest(clientId, appSecret, code);

                var gitHubClient = new GitHubClient(new ProductHeaderValue("JitHub"));
                var token = await gitHubClient.Oauth.CreateAccessToken(request);

                return token;
            }

            catch
            {
                return null;
            }

        }

        public static string ProcessRequest(HttpRequest req)
        {
            try
            {
                string temporaryCode = req.Query["tempCode"];

                temporaryCode = temporaryCode ?? req.Headers["tempCode"];

                if (string.IsNullOrEmpty(temporaryCode))
                {
                    return null;
                }

                return temporaryCode;


            }

            catch
            {

                return null;

            }
        }

        [FunctionName("GithubCodeToToken")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req,
            ILogger log)
        {

            string tempCode = ProcessRequest(req);

            if (string.IsNullOrEmpty(tempCode))
            {
                return new JsonResult(new { error = "Bad Request, missing Github code" });
            }

            var token = await Detokenize(tempCode);

            if (token == null)
            {
                return new JsonResult(new { error = "Bad Request, Github request error" });
            }

            return new JsonResult(new { token = token });
        }
    }
}
