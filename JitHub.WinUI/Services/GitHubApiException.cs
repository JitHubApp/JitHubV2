using System;
using System.Net;

namespace JitHub.Services;

public class GitHubApiException : Exception
{
    public GitHubApiException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode StatusCode { get; }
}

public sealed class GitHubAuthenticationException : GitHubApiException
{
    public GitHubAuthenticationException(string message)
        : base(HttpStatusCode.Unauthorized, message)
    {
    }
}
