using JitHub.Markdown.Links;
using System.Text;
using System.Xml.Linq;
using JitHub.WinUI.Tests.TestSupport;
using Xunit;

namespace JitHub.WinUI.Tests;

public sealed class SecurityReleaseGateTests
{
    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void ReleaseBuild_EnforcesLockedAuditedDependencyPolicy()
    {
        string root = FindRepositoryRoot();
        string props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        string nativeAotProps = File.ReadAllText(Path.Combine(root, "eng", "NativeAot.props"));
        string project = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "JitHub.WinUI.csproj"));
        string script = File.ReadAllText(Path.Combine(root, "eng", "Verify-DependencySecurity.ps1"));

        Assert.Contains("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>", props, StringComparison.Ordinal);
        Assert.Contains("<NuGetLockFilePath>$(MSBuildProjectDirectory)\\obj\\$(Configuration)\\packages.lock.json</NuGetLockFilePath>", props, StringComparison.Ordinal);
        Assert.Contains("'$(Configuration)' != 'Release' and '$(Configuration)' != 'AotDebug'", props, StringComparison.Ordinal);
        Assert.Contains("<RestoreLockedMode>true</RestoreLockedMode>", props, StringComparison.Ordinal);
        Assert.Contains("'$(Configuration)' == 'Release' or '$(Configuration)' == 'AotDebug'", props, StringComparison.Ordinal);
        Assert.Contains("<NuGetLockFilePath>$(MSBuildProjectDirectory)\\obj\\$(Configuration)\\packages.lock.json</NuGetLockFilePath>", nativeAotProps, StringComparison.Ordinal);
        Assert.Contains("<RestoreLockedMode>true</RestoreLockedMode>", nativeAotProps, StringComparison.Ordinal);
        Assert.Contains("NU1901;NU1902;NU1903;NU1904", props, StringComparison.Ordinal);
        Assert.Contains("Verify-DependencySecurity.ps1", project, StringComparison.Ordinal);
        // The gate's test run is checked on the evaluated project by
        // ReleaseGate_RunsOnlyTheReleaseSecurityTestsWithLockedRestore.
        Assert.Contains("--locked-mode", script, StringComparison.Ordinal);
        Assert.Contains("-p:Configuration=Release", script, StringComparison.Ordinal);
        Assert.Contains("--vulnerable", script, StringComparison.Ordinal);
        Assert.Contains("--include-transitive", script, StringComparison.Ordinal);
        Assert.Contains("allowedPrereleasePackages", script, StringComparison.Ordinal);
        Assert.Contains("UriSchemeHttps", script, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void ReleaseGate_RunsOnlyTheReleaseSecurityTestsWithLockedRestore()
    {
        string root = FindRepositoryRoot();
        MsBuildEvaluation app = MsBuild.Evaluate(
            Path.Combine(root, "JitHub.WinUI", "JitHub.WinUI.csproj"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Configuration"] = "Release",
                ["Platform"] = "x64",
            },
            ["JitHubReleaseSecurityTestCommand"],
            []);
        string[][] commands = SplitCommandSequence(SplitCommandLine(app.GetProperty("JitHubReleaseSecurityTestCommand")));

        Assert.Equal(2, commands.Length);
        string[] testProjects = ["JitHub.Core.Tests", "JitHub.WinUI.Tests"];
        for (int index = 0; index < commands.Length; index++)
        {
            string[] command = commands[index];
            Assert.Equal(["dotnet", "test"], command.Take(2));
            Assert.Equal(
                Path.Combine(root, testProjects[index], testProjects[index] + ".csproj"),
                Path.GetFullPath(command[2]),
                ignoreCase: true);
            Assert.Equal("Release", ValueAfter(command, "-c"));
            Assert.Equal("Category=ReleaseSecurity", ValueAfter(command, "--filter"));
            Assert.Contains("-p:RestoreLockedMode=true", command);
            Assert.Contains("-p:SkipReleaseSecurityGate=true", command);
            Assert.DoesNotContain("--no-restore", command);
        }
    }

    // The gate runs one test project after another, joined by &&, so a failing
    // run stops the gate.
    private static string[][] SplitCommandSequence(string[] arguments)
    {
        List<string[]> commands = [];
        List<string> current = [];
        foreach (string argument in arguments)
        {
            if (argument == "&&")
            {
                commands.Add([.. current]);
                current.Clear();
            }
            else
            {
                current.Add(argument);
            }
        }

        commands.Add([.. current]);
        return [.. commands];
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void DependencyAudit_ReadsProjectFilesButNeverBuildOutputOrLinkedTrees()
    {
        string root = FindRepositoryRoot();
        string fixture = Path.Combine(Path.GetTempPath(), $"jithub-dependency-walk-{Guid.NewGuid():N}");
        string outside = fixture + "-outside";
        string repository = Path.Combine(fixture, "repo");
        try
        {
            string[] expected =
            [
                WriteFixtureFile(repository, "Directory.Packages.props"),
                WriteFixtureFile(repository, Path.Combine("App", "App.csproj")),
                WriteFixtureFile(repository, Path.Combine("src", "Lib", "Lib.csproj")),
                WriteFixtureFile(repository, Path.Combine("src", "Lib", "Directory.Packages.props")),
            ];
            foreach (string generated in new[] { "bin", "obj", "bin-x64", "OBJ-arm64", "artifacts", "_scratch", ".codex-artifacts", ".git" })
                WriteFixtureFile(repository, Path.Combine("src", "Lib", generated, "Generated.csproj"));
            WriteFixtureFile(outside, "Outside.csproj");
            CreateJunction(Path.Combine(repository, "linked"), outside);

            string[] listed = ListDependencyManifestFiles(
                    Path.Combine(root, "eng", "Get-DependencyManifestFiles.ps1"),
                    repository)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Assert.Equal(expected.Order(StringComparer.OrdinalIgnoreCase), listed, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            string link = Path.Combine(repository, "linked");
            if (Directory.Exists(link))
                Directory.Delete(link);
            if (Directory.Exists(fixture))
                Directory.Delete(fixture, recursive: true);
            if (Directory.Exists(outside))
                Directory.Delete(outside, recursive: true);
        }
    }

    private static string WriteFixtureFile(string directory, string relativePath)
    {
        string path = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project />");
        return path;
    }

    // A junction needs no symbolic-link privilege, unlike Directory.CreateSymbolicLink.
    private static void CreateJunction(string link, string target)
    {
        string[] output = RunProcess("cmd.exe", "/c", "mklink", "/J", link, target);
        Assert.True(Directory.Exists(link), string.Join(Environment.NewLine, output));
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    // Windows PowerShell, as the release gate in JitHub.WinUI.csproj runs the audit.
    private static string[] ListDependencyManifestFiles(string script, string root) =>
        RunProcess(
            "powershell.exe",
            "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
            $"& {QuotePowerShell(script)} -Root {QuotePowerShell(root)} | ForEach-Object {{ $_.FullName }}");

    private static string QuotePowerShell(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string[] RunProcess(string fileName, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(1)), $"{fileName} did not exit.");
        Assert.True(process.ExitCode == 0, $"{fileName} exited {process.ExitCode}: {error.Result}");
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void AppGraphRendererLocks_RecordEverySupportedRuntime()
    {
        // The JitHub release gate restores and audits only the app graph.
        // Feature packs outside that graph own their locks in their
        // package-specific release gates and must not couple this build to
        // unrelated restore-lane state. That every shipping renderer project
        // inherits its runtimes and AnyCPU target from MarkdownRenderer/src
        // is enforced by the structure gate (MRS0011); that every packed
        // managed asset is AnyCPU IL, and that MarkdownRenderer.All ships no
        // assembly, by MarkdownRenderer/eng/Test-ManagedPackageArchitecture.ps1.
        string root = FindRepositoryRoot();
        string[] appGraphProjects =
        [
            "MarkdownRenderer",
            "MarkdownRenderer.SyntaxHighlighting.TextMate",
            "MarkdownRenderer.SyntaxHighlighting.TextMate.Engine",
            "MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common",
        ];
        string[] expectedRids = ["win-arm64", "win-x64", "win-x86"];

        foreach (string project in appGraphProjects)
        {
            string lockPath = Path.Combine(root, "MarkdownRenderer", "src", project, "packages.lock.json");
            using System.Text.Json.JsonDocument lockFile = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(lockPath));
            string[] targetFrameworks = lockFile.RootElement
                .GetProperty("dependencies")
                .EnumerateObject()
                .Select(static property => property.Name)
                .ToArray();

            string neutralTarget = Assert.Single(
                targetFrameworks,
                static target => !target.Contains('/', StringComparison.Ordinal));
            string[] expectedTargets =
            [
                neutralTarget,
                .. expectedRids.Select(rid => $"{neutralTarget}/{rid}")
            ];
            Assert.Equal(
                expectedTargets.Order(StringComparer.Ordinal),
                targetFrameworks.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void PackageRestore_UsesOnlyReviewedPublicSources()
    {
        string root = FindRepositoryRoot();
        XDocument configuration = XDocument.Load(Path.Combine(root, "NuGet.config"));
        XElement source = Assert.Single(configuration.Root!
            .Element("packageSources")!
            .Elements("add"));

        Assert.Equal("nuget.org", source.Attribute("key")?.Value);
        Assert.Equal("https://api.nuget.org/v3/index.json", source.Attribute("value")?.Value);
        Assert.Equal("3", source.Attribute("protocolVersion")?.Value);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void StorePackage_RestoresNativeGraphBeforeVerifyingDependencyLedger()
    {
        string root = FindRepositoryRoot();
        string script = File.ReadAllText(Path.Combine(root, "eng", "Build-JitHubWinUIStorePackage.ps1"));
        int restoreIndex = script.IndexOf(
            "Restore-NativeAot.ps1') -Architecture $ledgerArchitecture",
            StringComparison.Ordinal);
        int verifyIndex = script.IndexOf(
            "Update-NativeAotDependencyLedger.ps1') -Verify",
            StringComparison.Ordinal);

        Assert.True(restoreIndex >= 0, "The Store package script must materialize a locked Native AOT graph.");
        Assert.True(verifyIndex > restoreIndex, "The dependency ledger must be verified after the Native AOT restore.");
        Assert.Contains("-Platform @($platforms)[0]", script, StringComparison.Ordinal);
        int testRestoreIndex = script.IndexOf("& dotnet restore $resolvedTestProjectPath", StringComparison.Ordinal);
        int releaseConfigurationIndex = script.IndexOf("-p:Configuration=Release", testRestoreIndex, StringComparison.Ordinal);
        int lockedModeIndex = script.IndexOf("--locked-mode", testRestoreIndex, StringComparison.Ordinal);
        Assert.True(
            testRestoreIndex >= 0 && releaseConfigurationIndex > testRestoreIndex && lockedModeIndex > releaseConfigurationIndex,
            "The Store package script must verify the canonical Release test lock file.");
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void StoreRelease_ValidatesPackageVersionBeforeToolchainSetup()
    {
        string root = FindRepositoryRoot();
        string storeWorkflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "jithub-store-release.yml"));
        string nativeAotWorkflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "native-aot.yml"));
        string prepareScript = File.ReadAllText(Path.Combine(root, "eng", "Prepare-JitHubStoreRelease.ps1"));
        string validatorScript = File.ReadAllText(Path.Combine(root, "eng", "Assert-JitHubStorePackageVersion.ps1"));
        string validatorTests = File.ReadAllText(Path.Combine(root, "eng", "Test-JitHubStorePackageVersion.ps1"));

        int validationIndex = storeWorkflow.IndexOf("- name: Validate Store package version", StringComparison.Ordinal);
        int toolchainIndex = storeWorkflow.IndexOf("- name: Set up .NET SDK", StringComparison.Ordinal);

        Assert.True(validationIndex >= 0 && toolchainIndex > validationIndex, "Store versions must be rejected before toolchain setup and packaging.");
        Assert.Contains("STORE_RELEASE_VERSION: ${{ inputs.release_version }}", storeWorkflow, StringComparison.Ordinal);
        Assert.Contains("Assert-JitHubStorePackageVersion.ps1 -Version \"$env:STORE_RELEASE_VERSION\"", storeWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("-Version \"${{ inputs.release_version }}\"", storeWorkflow, StringComparison.Ordinal);
        Assert.Contains("Assert-JitHubStorePackageVersion.ps1", prepareScript, StringComparison.Ordinal);
        Assert.Contains("must end in '.0'", validatorScript, StringComparison.Ordinal);
        Assert.Contains("65535", validatorScript, StringComparison.Ordinal);
        Assert.Contains("'3.0.0.2'", validatorTests, StringComparison.Ordinal);
        Assert.Contains("Test-JitHubStorePackageVersion.ps1", nativeAotWorkflow, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Category", "ReleaseSecurity")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///C:/Users/shared/.ssh/id_rsa")]
    [InlineData("ms-appx:///Assets/secret.txt")]
    [InlineData("ftp://example.test/archive")]
    [InlineData("http://example.test/plaintext")]
    public void MarkdownNavigation_DeniesUnsafeSchemes(string value)
    {
        bool resolved = MarkdownLinkNavigationPolicy.TryResolveLaunchUri(
            value,
            new Uri("https://github.com/JitHubApp/JitHubV2/"),
            out Uri? uri);

        Assert.False(resolved);
        Assert.Null(uri);
    }

    // Splits an Exec command line on spaces outside double quotes.
    private static string[] SplitCommandLine(string commandLine)
    {
        List<string> arguments = [];
        StringBuilder current = new();
        bool quoted = false;
        foreach (char character in commandLine)
        {
            if (character == '"')
            {
                quoted = !quoted;
            }
            else if (character == ' ' && !quoted)
            {
                if (current.Length > 0)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (current.Length > 0)
            arguments.Add(current.ToString());

        return [.. arguments];
    }

    private static string ValueAfter(string[] arguments, string option)
    {
        int index = Array.IndexOf(arguments, option);
        Assert.True(index >= 0 && index + 1 < arguments.Length, $"The command has no {option} value.");
        return arguments[index + 1];
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
