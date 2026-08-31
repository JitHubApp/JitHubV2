using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models;
using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubRepositoryCreationTests
{
    [Fact]
    public void LicenseChoices_DefaultToNoLicenseWithoutATemplateName()
    {
        License selectedByDefault = License.GetLicenses().First();

        Assert.Equal("No license", selectedByDefault.DiaplayName);
        Assert.True(selectedByDefault.IsNoLicense);
        Assert.Null(selectedByDefault.TemplateName);
        Assert.Equal("No license file will be created. You can add one later.", selectedByDefault.ConsequenceText);
    }

    [Fact]
    public void LicenseChoices_OnlyExposeTemplateNameAfterExplicitLicenseChoice()
    {
        License mit = License.GetLicenses().Single(license => license.Name == "mit");

        Assert.False(mit.IsNoLicense);
        Assert.Equal("mit", mit.TemplateName);
        Assert.Equal("GitHub will create a LICENSE file using MIT License.", mit.ConsequenceText);
    }

    [Fact]
    public async Task CreateRepository_DefaultPayload_OmitsLicenseTemplateExactly()
    {
        CaptureHttpHandler handler = new();
        using HttpClient httpClient = new(handler);
        GitHubClientService client = new(httpClient);

        await client.CreateRepositoryAsync(
            "token",
            new GitHubRepositoryCreateOptions
            {
                Name = "sample",
                Visibility = "public"
            });

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://api.github.com/user/repos", handler.RequestUri?.AbsoluteUri);
        Assert.Equal(
            "{\"name\":\"sample\",\"private\":false,\"visibility\":\"public\",\"auto_init\":false}",
            handler.Body);
        Assert.DoesNotContain("license_template", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateRepository_ExplicitLicensePayload_IncludesChosenTemplateExactly()
    {
        CaptureHttpHandler handler = new();
        using HttpClient httpClient = new(handler);
        GitHubClientService client = new(httpClient);

        await client.CreateRepositoryAsync(
            "token",
            new GitHubRepositoryCreateOptions
            {
                Name = "sample",
                Visibility = "private",
                Private = true,
                AutoInit = true,
                LicenseTemplate = "mit"
            });

        Assert.Equal(
            "{\"name\":\"sample\",\"private\":true,\"visibility\":\"private\",\"auto_init\":true,\"license_template\":\"mit\"}",
            handler.Body);
    }

    private sealed class CaptureHttpHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }
}
