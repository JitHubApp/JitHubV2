using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Verifies the release workflow's source wiring for independent package-pass
/// rebuilds and failure evidence. This is not a run of the hosted workflow.
/// </summary>
public sealed class PackageRebuildReproducibilityWorkflowContractTests
{
    [Fact]
    public void EachPackagePassRebuildsTheShippingGraphAndPreservesGeneratorEvidence()
    {
        string workflow = File.ReadAllText(FindWorkflow());
        int stepStart = workflow.IndexOf(
            "      - name: Pack every shipping package twice into clean directories",
            StringComparison.Ordinal);
        int stepEnd = workflow.IndexOf("\n      - name:", stepStart + 1, StringComparison.Ordinal);
        Assert.True(stepStart >= 0 && stepEnd > stepStart);
        string step = workflow[stepStart..stepEnd];

        int passLoop = step.IndexOf("foreach ($packageDirectory in $packageDirectories)", StringComparison.Ordinal);
        int rebuildLoop = step.IndexOf("foreach ($project in $projects)", passLoop, StringComparison.Ordinal);
        int build = step.IndexOf("& dotnet build $project `", rebuildLoop, StringComparison.Ordinal);
        int snapshot = step.IndexOf("$generatedRoot $projectName", build, StringComparison.Ordinal);
        int report = step.IndexOf("shipping-graph-rebuild.json", snapshot, StringComparison.Ordinal);
        int failure = step.IndexOf("if ($null -ne $rebuildFailure) { throw $rebuildFailure }", report, StringComparison.Ordinal);
        int packLoop = step.IndexOf("foreach ($project in $projects)", failure, StringComparison.Ordinal);
        int pack = step.IndexOf("& dotnet pack $project `", packLoop, StringComparison.Ordinal);

        Assert.True(passLoop >= 0);
        Assert.True(rebuildLoop > passLoop);
        Assert.True(build > rebuildLoop);
        Assert.True(snapshot > build);
        Assert.True(report > snapshot);
        Assert.True(failure > report);
        Assert.True(packLoop > failure && pack > packLoop);

        string rebuildCommand = step[build..step.IndexOf("-m:1", build, StringComparison.Ordinal)];
        Assert.Contains("-target:Rebuild", rebuildCommand, StringComparison.Ordinal);
        Assert.Contains("--no-restore", rebuildCommand, StringComparison.Ordinal);
        Assert.Contains("--disable-build-servers", rebuildCommand, StringComparison.Ordinal);
        Assert.Contains("BuildProjectReferences=false", rebuildCommand, StringComparison.Ordinal);
        Assert.Contains("EmitCompilerGeneratedFiles=true", rebuildCommand, StringComparison.Ordinal);
        Assert.Contains("UseSharedCompilation=false", rebuildCommand, StringComparison.Ordinal);
        Assert.Contains("BuildInParallel=false", rebuildCommand, StringComparison.Ordinal);
        Assert.Contains("-bl:$binlogPath", rebuildCommand, StringComparison.Ordinal);

        string packCommand = step[pack..step.IndexOf("-m:1", pack, StringComparison.Ordinal)];
        Assert.Contains("--no-build", packCommand, StringComparison.Ordinal);
        Assert.Contains("--no-restore", packCommand, StringComparison.Ordinal);

        Assert.Contains("$passName = Split-Path -Leaf $packageDirectory", step, StringComparison.Ordinal);
        Assert.Contains("$binlogPath = Join-Path $binlogDirectory", step, StringComparison.Ordinal);
        Assert.Contains("$projectCapturePath = Join-Path $generatedRoot $projectName", step, StringComparison.Ordinal);
        Assert.Contains("$rebuildResults.Add([pscustomobject][ordered]@{", step, StringComparison.Ordinal);
        Assert.Contains("package-rebuild-evidence", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("CompilerGeneratedFilesOutputPath =", step, StringComparison.Ordinal);
        foreach (string project in new[]
                 {
                     "MarkdownRenderer.Core.csproj",
                     "MarkdownRenderer.csproj",
                     "MarkdownRenderer.Performance.csproj",
                     "MarkdownRenderer.Package.csproj",
                     "MarkdownRenderer.Gfm.csproj",
                     "MarkdownRenderer.Html.csproj",
                     "MarkdownRenderer.Math.csproj",
                     "MarkdownRenderer.Mermaid.csproj",
                     "MarkdownRenderer.Svg.Resvg.csproj",
                     "MarkdownRenderer.SyntaxHighlighting.TextMate.csproj",
                     "MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common.csproj",
                     "MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.All.csproj",
                     "MarkdownRenderer.GitHub.csproj",
                     "MarkdownRenderer.All.csproj"
                 })
        {
            Assert.Contains(project, step, StringComparison.Ordinal);
        }
        AssertShippingProjectListIsClosedAndTopological(FindWorkflow(), step);

        int uploadStart = workflow.IndexOf(
            "      - name: Upload package rebuild reproducibility evidence",
            StringComparison.Ordinal);
        int uploadEnd = workflow.IndexOf("\n      - name:", uploadStart + 1, StringComparison.Ordinal);
        Assert.True(uploadStart >= 0 && uploadEnd > uploadStart);
        string upload = workflow[uploadStart..uploadEnd];
        Assert.Contains("if: always()", upload, StringComparison.Ordinal);
        Assert.Contains("package-rebuild-evidence/**", upload, StringComparison.Ordinal);
    }

    private static string FindWorkflow()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string path = Path.Combine(
                directory.FullName,
                ".github",
                "workflows",
                "markdown-renderer-preview.yml");
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException("Could not locate the MarkdownRenderer preview workflow from the test base directory.");
    }

    private static void AssertShippingProjectListIsClosedAndTopological(string workflowPath, string step)
    {
        string repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(workflowPath)!, "..", ".."));
        MatchCollection matches = Regex.Matches(
            step,
            @"(?m)^\s*'(?<path>\.\\MarkdownRenderer\\[^']+\.csproj)'\s*$");
        string[] projectPaths = matches
            .Select(match => Path.GetFullPath(Path.Combine(repoRoot, match.Groups["path"].Value)))
            .ToArray();
        Assert.Equal(14, projectPaths.Length);

        var shippingProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string projectPath in projectPaths)
            Assert.True(shippingProjects.Add(projectPath), $"Duplicate shipping project: {projectPath}");

        var rebuiltProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string projectPath in projectPaths)
        {
            Assert.True(File.Exists(projectPath), $"Shipping project was not found: {projectPath}");
            XDocument project = XDocument.Load(projectPath);
            foreach (XElement reference in project.Descendants().Where(
                         element => element.Name.LocalName == "ProjectReference"))
            {
                string? include = reference.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include))
                    continue;
                Assert.False(
                    include.IndexOfAny(new[] { '*', '?' }) >= 0 ||
                    include.Contains("$(", StringComparison.Ordinal) ||
                    include.Contains("@(", StringComparison.Ordinal) ||
                    include.Contains("%(", StringComparison.Ordinal),
                    $"Unsupported MSBuild expression or wildcard in ProjectReference '{include}' from {projectPath}.");

                string referencePath = Path.GetFullPath(
                    Path.Combine(Path.GetDirectoryName(projectPath)!, include));
                Assert.True(
                    shippingProjects.Contains(referencePath),
                    $"Shipping list omits direct reference {Path.GetFileName(referencePath)} from {Path.GetFileName(projectPath)}.");
                Assert.True(
                    rebuiltProjects.Contains(referencePath),
                    $"Shipping list is not topological: {Path.GetFileName(projectPath)} references " +
                    $"{Path.GetFileName(referencePath)} before it is rebuilt.");
            }

            Assert.True(rebuiltProjects.Add(projectPath));
        }
    }
}
