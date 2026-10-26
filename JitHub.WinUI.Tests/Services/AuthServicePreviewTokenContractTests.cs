using Xunit;

namespace JitHub.WinUI.Tests.Services;

/// <summary>
/// The production README audit launches the app in a preview page override
/// with a short-lived token in JITHUB_README_AUDIT_GITHUB_TOKEN.
/// GetToken resolved that token, but GetValidTokenAsync returned the public
/// sentinel unconditionally. Repository pages read their README through
/// GetValidTokenAsync, so CI audit runs intermittently showed the signed-out
/// preview README instead of the repository's (66 of 84 failures on
/// October 26). Both token paths must resolve the preview override the same way.
/// </summary>
public sealed class AuthServicePreviewTokenContractTests
{
    [Fact]
    public void BothTokenPathsResolveThePreviewOverrideToken()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Services", "AuthService.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        foreach (string signature in new[]
                 {
                     "public string? GetToken(long userId)",
                     "public async Task<string?> GetValidTokenAsync(long userId, CancellationToken cancellationToken = default)",
                 })
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, $"'{signature}' was not found.");
            int previewStart = source.IndexOf("if (Program.CurrentLaunchOptions.IsPublicPreviewOverride)", start, StringComparison.Ordinal);
            Assert.True(previewStart > start, $"'{signature}' has no preview-override branch.");
            string branch = source[previewStart..source.IndexOf('}', previewStart)];
            Assert.Contains("Program.CurrentLaunchOptions.ResolvePreviewAccessToken(GitHubClientService.PublicAccessToken)", branch, StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
