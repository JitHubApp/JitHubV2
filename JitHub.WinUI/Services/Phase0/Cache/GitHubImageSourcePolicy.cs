using System;
using System.Globalization;
using System.Net;
using JitHub.Markdown;
using JitHub.Services.Markdown;

namespace JitHub.Services;

/// <summary>
/// Decides which image origins and redirect transitions the image service may
/// contact for each fetch scope.
/// </summary>
internal static class GitHubImageSourcePolicy
{
    internal static bool IsAllowedSource(string sourceUrl, GitHubImageFetchScope scope)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri? sourceUri) ||
            sourceUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(sourceUri.UserInfo) ||
            !IsGloballyRoutableDestination(sourceUri))
        {
            return false;
        }

        return scope == GitHubImageFetchScope.UserApprovedHttps ||
            MarkdownRemoteImagePolicy.IsTrustedGitHubHost(sourceUri.Host);
    }

    internal static bool IsAllowedRedirect(
        Uri currentUri,
        Uri nextUri,
        GitHubImageFetchScope scope)
    {
        if (IsAllowedSource(nextUri.ToString(), scope))
            return true;

        // GitHub serves README user attachments through a short-lived,
        // query-signed S3 URL. Keep the normal trusted-host boundary intact
        // and admit only this exact GitHub-owned transition; subsequent
        // redirects are evaluated independently and cannot inherit it.
        return scope == GitHubImageFetchScope.TrustedGitHub &&
            currentUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            IsGitHubUserAssetPath(currentUri.AbsolutePath) &&
            nextUri.Scheme == Uri.UriSchemeHttps &&
            string.IsNullOrEmpty(nextUri.UserInfo) &&
            nextUri.Host.Equals(
                "github-production-user-asset-6210df.s3.amazonaws.com",
                StringComparison.OrdinalIgnoreCase) &&
            nextUri.Query.Contains("X-Amz-Credential=", StringComparison.OrdinalIgnoreCase) &&
            nextUri.Query.Contains("X-Amz-Signature=", StringComparison.OrdinalIgnoreCase) &&
            IsGloballyRoutableDestination(nextUri);
    }

    private static bool IsGitHubUserAssetPath(string absolutePath)
    {
        if (absolutePath.StartsWith(
                "/user-attachments/assets/",
                StringComparison.OrdinalIgnoreCase))
        {
            return Guid.TryParse(absolutePath["/user-attachments/assets/".Length..], out _);
        }

        // GitHub's legacy README attachment form is
        // /{owner}/{repository}/assets/{numeric-user-id}/{asset-id}. It is
        // still used by many high-traffic repositories and redirects through
        // the same query-signed, GitHub-owned S3 endpoint as the newer form.
        string[] segments = absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 5 &&
            segments[0].Length > 0 &&
            segments[1].Length > 0 &&
            segments[2].Equals("assets", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(segments[3], NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
            Guid.TryParse(segments[4], out _);
    }

    internal static bool IsGloballyRoutableDestination(Uri uri)
    {
        string host = uri.Host.TrimEnd('.');
        if (uri.IsLoopback || GloballyRoutableAddressPolicy.IsSpecialUseHost(host))
        {
            return false;
        }

        return !IPAddress.TryParse(host, out IPAddress? address) ||
            GloballyRoutableAddressPolicy.IsGloballyRoutable(address);
    }
}
