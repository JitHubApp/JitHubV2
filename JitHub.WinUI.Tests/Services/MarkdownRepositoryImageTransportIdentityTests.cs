using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class MarkdownRepositoryImageTransportIdentityTests
{
    [Fact]
    public void NestedCodeServerDocumentUsesExistingNormalizedPathIdentity()
    {
        const string source = "./assets/screenshots/editor.png";
        MarkdownImageResolveContext context = CreateContext(
            documentPath: @"docs\server//README.md");
        var resourceUri = new Uri(
            "https://raw.githubusercontent.com/coder/code-server/main/docs/server/assets/screenshots/editor.png");

        MarkdownRepositoryImageTransportIdentity identity = Assert.IsType<MarkdownRepositoryImageTransportIdentity>(
            MarkdownRepositoryImageTransportIdentity.TryCreate(source, context, resourceUri));
        MarkdownImageResolutionAuditDiagnostic expected = MarkdownImageResolutionAuditDiagnostic.Create(
            source,
            context,
            MarkdownImageResolution.Unavailable);

        Assert.Equal("repository", identity.DocumentContextKind);
        Assert.Equal("relative", identity.SourceKind);
        Assert.Equal(expected.DocumentPathSha256, identity.DocumentPathSha256);
        Assert.Equal(expected.DocumentRefIdentitySha256, identity.DocumentRefIdentitySha256);
        Assert.Equal(Hash("document-path\0docs/server/README.md"), identity.DocumentPathSha256);
        Assert.Equal(Hash("document-ref\0coder\0code-server\0main"), identity.DocumentRefIdentitySha256);
        Assert.Equal(Hash("uri-path\0" + resourceUri.AbsolutePath), identity.ResourcePathSha256);
        Assert.Equal("github-raw", identity.ResourceHostRoute);
    }

    [Fact]
    public void RootRefAndDocumentChangesProduceDistinctHashIdentities()
    {
        const string source = "./assets/screenshot.png";
        MarkdownRepositoryImageTransportIdentity root = CreateIdentity(
            source,
            CreateContext("README.md", "main"),
            "https://raw.githubusercontent.com/coder/code-server/main/assets/screenshot.png");
        MarkdownRepositoryImageTransportIdentity nested = CreateIdentity(
            source,
            CreateContext("docs/README.md", "main"),
            "https://raw.githubusercontent.com/coder/code-server/main/docs/assets/screenshot.png");
        MarkdownRepositoryImageTransportIdentity otherRef = CreateIdentity(
            source,
            CreateContext("README.md", "release"),
            "https://raw.githubusercontent.com/coder/code-server/release/assets/screenshot.png");

        Assert.Equal(root.SourceSha256, nested.SourceSha256);
        Assert.Equal(root.SourceSha256, otherRef.SourceSha256);
        Assert.NotEqual(root.DocumentPathSha256, nested.DocumentPathSha256);
        Assert.NotEqual(root.DocumentRefIdentitySha256, otherRef.DocumentRefIdentitySha256);
        Assert.NotEqual(root.ResourcePathSha256, otherRef.ResourcePathSha256);
    }

    [Fact]
    public void ParallelResolutionsKeepTheirDocumentAndResourceIdentitiesSeparate()
    {
        const int count = 64;
        const string source = "./assets/screenshot.png";
        var results = new MarkdownRepositoryImageTransportIdentity?[count];

        Parallel.For(0, count, index =>
        {
            string path = $"docs/section-{index}/README.md";
            string gitRef = $"release-{index}";
            string uri = $"https://raw.githubusercontent.com/coder/code-server/{gitRef}/docs/section-{index}/assets/screenshot.png";
            results[index] = MarkdownRepositoryImageTransportIdentity.TryCreate(
                source,
                CreateContext(path, gitRef),
                new Uri(uri));
        });

        var observedPaths = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            MarkdownRepositoryImageTransportIdentity identity = Assert.IsType<MarkdownRepositoryImageTransportIdentity>(
                results[index]);
            MarkdownImageResolutionAuditDiagnostic expected = MarkdownImageResolutionAuditDiagnostic.Create(
                source,
                CreateContext($"docs/section-{index}/README.md", $"release-{index}"),
                MarkdownImageResolution.Unavailable);

            Assert.Equal(expected.DocumentPathSha256, identity.DocumentPathSha256);
            Assert.Equal(expected.DocumentRefIdentitySha256, identity.DocumentRefIdentitySha256);
            Assert.Equal(1, observedPaths.AddOrUpdate(identity.DocumentPathSha256!, 1, static (_, value) => value + 1));
        }
    }

    [Fact]
    public void SerializedIdentityContainsNoSourceDocumentQueryOrCredentialText()
    {
        const string sourceSecret = "source-token-should-not-serialize";
        const string uriSecret = "uri-token-should-not-serialize";
        const string source = "./assets/screenshot.png?token=source-token-should-not-serialize";
        MarkdownImageResolveContext context = CreateContext("docs/private/README.md", "private-ref-name");
        var resourceUri = new Uri(
            "https://raw.githubusercontent.com/coder/code-server/private-ref-name/docs/private/assets/screenshot.png?token=uri-token-should-not-serialize#private-fragment");

        MarkdownRepositoryImageTransportIdentity identity = CreateIdentity(source, context, resourceUri.AbsoluteUri);
        string json = JsonSerializer.Serialize(identity, MarkdownTransportIdentityTestJsonContext.Default.MarkdownRepositoryImageTransportIdentity);

        Assert.DoesNotContain(sourceSecret, json, StringComparison.Ordinal);
        Assert.DoesNotContain(uriSecret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("screenshot.png", json, StringComparison.Ordinal);
        Assert.DoesNotContain("docs/private", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-ref-name", json, StringComparison.Ordinal);
        Assert.DoesNotContain("coder", json, StringComparison.Ordinal);
        Assert.DoesNotContain("code-server", json, StringComparison.Ordinal);
        Assert.DoesNotContain("raw.githubusercontent.com", json, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", json, StringComparison.Ordinal);

        MarkdownRepositoryImageTransportIdentity samePathDifferentQuery = CreateIdentity(
            source,
            context,
            "https://raw.githubusercontent.com/coder/code-server/private-ref-name/docs/private/assets/screenshot.png?different=secret#other");
        Assert.Equal(identity.ResourcePathSha256, samePathDifferentQuery.ResourcePathSha256);
    }

    [Fact]
    public void ResourceUserInfoIsRejectedInsteadOfBecomingAuditMaterial()
    {
        MarkdownRepositoryImageTransportIdentity? identity = MarkdownRepositoryImageTransportIdentity.TryCreate(
            "./assets/screenshot.png",
            CreateContext("README.md"),
            new Uri("https://user:password@raw.githubusercontent.com/coder/code-server/main/assets/screenshot.png"));

        Assert.Null(identity);
    }

    [Fact]
    public void OversizedSourceResourcePathDocumentPathAndRefReturnNoIdentity()
    {
        MarkdownImageResolveContext validContext = CreateContext("README.md");
        var validResource = new Uri("https://raw.githubusercontent.com/coder/code-server/main/assets/image.png");

        Assert.Null(MarkdownRepositoryImageTransportIdentity.TryCreate(
            new string('s', 4097), validContext, validResource));
        Assert.Null(MarkdownRepositoryImageTransportIdentity.TryCreate(
            "./assets/image.png",
            validContext,
            new Uri("https://raw.githubusercontent.com/" + new string('p', 4096))));
        Assert.Null(MarkdownRepositoryImageTransportIdentity.TryCreate(
            "./assets/image.png",
            CreateContext(new string('d', 2049)),
            validResource));
        Assert.Null(MarkdownRepositoryImageTransportIdentity.TryCreate(
            "./assets/image.png",
            CreateContext("README.md", new string('r', 2049)),
            validResource));
        Assert.Null(MarkdownRepositoryImageTransportIdentity.TryCreate(
            "./assets/image.png",
            new MarkdownImageResolveContext(
                null,
                DocumentPath: new string('d', 2049)),
            validResource));
        Assert.Null(MarkdownRepositoryImageTransportIdentity.TryCreate(
            "./assets/image.png",
            new MarkdownImageResolveContext(
                new Uri("https://github.com/coder/code-server/" + new string('b', 2049))),
            validResource));
        Assert.Null(MarkdownRepositoryImageTransportIdentity.TryCreate(
            "./assets/image.png",
            new MarkdownImageResolveContext(
                null,
                DocumentPath: "README.md",
                DocumentSource: new MarkdownDocumentSource(
                    "doc-id",
                    "coder",
                    "code-server",
                    "main",
                    new string('d', 2049))),
            validResource));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("repository")]
    [InlineData("ref")]
    public void OversizedRepositoryIdentityComponentReturnsNoIdentity(string component)
    {
        string value = new('x', 2049);
        var documentSource = new MarkdownDocumentSource(
            "doc-id",
            component == "owner" ? value : "coder",
            component == "repository" ? value : "code-server",
            component == "ref" ? value : "main",
            "README.md");

        Assert.Null(MarkdownRepositoryImageTransportIdentity.TryCreate(
            "./assets/image.png",
            new MarkdownImageResolveContext(null, "README.md", DocumentSource: documentSource),
            new Uri("https://raw.githubusercontent.com/coder/code-server/main/assets/image.png")));
    }

    [Theory]
    [InlineData("http://raw.githubusercontent.com/coder/code-server/main/assets/image.png")]
    [InlineData("https://example.com/coder/code-server/main/assets/image.png")]
    [InlineData("https://raw.githubusercontent.com:444/coder/code-server/main/assets/image.png")]
    public void NonHttpsUntrustedHostOrNonDefaultPortReturnsNoIdentity(string uri)
    {
        Assert.Null(MarkdownRepositoryImageTransportIdentity.TryCreate(
            "./assets/image.png",
            CreateContext("README.md"),
            new Uri(uri)));
    }

    [Fact]
    public void RelativeResourceUriReturnsNoIdentity()
    {
        Assert.Null(MarkdownRepositoryImageTransportIdentity.TryCreate(
            "./assets/image.png",
            CreateContext("README.md"),
            new Uri("assets/image.png", UriKind.Relative)));
    }

    [Fact]
    public void ExplicitDefaultHttpsPortAndPartialDocumentContextAreAccepted()
    {
        MarkdownRepositoryImageTransportIdentity identity = CreateIdentity(
            "./assets/image.png",
            new MarkdownImageResolveContext(null),
            "https://raw.githubusercontent.com:443/coder/code-server/main/assets/image.png");

        Assert.Equal("missing", identity.DocumentContextKind);
        Assert.Null(identity.DocumentPathSha256);
        Assert.Null(identity.DocumentRefIdentitySha256);
        Assert.Equal("github-raw", identity.ResourceHostRoute);
    }

    private static MarkdownImageResolveContext CreateContext(
        string documentPath,
        string gitRef = "main")
    {
        var documentSource = new MarkdownDocumentSource(
            $"repository-file:coder/code-server:{gitRef}:{documentPath}",
            "coder",
            "code-server",
            gitRef,
            documentPath);
        var baseUri = new Uri(
            $"https://github.com/coder/code-server/blob/{Uri.EscapeDataString(gitRef)}/{documentPath.Replace('\\', '/')}");
        return new MarkdownImageResolveContext(
            baseUri,
            documentPath,
            DocumentSource: documentSource);
    }

    private static MarkdownRepositoryImageTransportIdentity CreateIdentity(
        string source,
        MarkdownImageResolveContext context,
        string resourceUri) =>
        Assert.IsType<MarkdownRepositoryImageTransportIdentity>(
            MarkdownRepositoryImageTransportIdentity.TryCreate(source, context, new Uri(resourceUri)));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

[JsonSerializable(typeof(MarkdownRepositoryImageTransportIdentity))]
internal sealed partial class MarkdownTransportIdentityTestJsonContext : JsonSerializerContext;
