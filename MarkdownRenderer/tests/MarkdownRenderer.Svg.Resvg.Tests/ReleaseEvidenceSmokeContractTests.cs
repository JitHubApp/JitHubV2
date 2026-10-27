using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class ReleaseEvidenceSmokeContractTests
{
    [Fact]
    public void PackageConsumerIncludesEverySmokeSourceAndKeepsTrimWarningsFatal()
    {
        string rendererRoot = FindRendererRoot();
        string script = File.ReadAllText(Path.Combine(rendererRoot, "eng", "Invoke-SvgResvgReleaseEvidence.ps1"));
        Match projectText = Regex.Match(script, @"(?s)\$consumerProject\s*=\s*@'\r?\n(.*?)\r?\n'@");
        Assert.True(projectText.Success, "The package-consumer project definition must remain inspectable.");
        XElement project = XElement.Parse(projectText.Groups[1].Value);
        Assert.Equal("true", project.Descendants("TreatWarningsAsErrors").Single().Value);
        Assert.Equal("true", project.Descendants("ILLinkTreatWarningsAsErrors").Single().Value);
        Assert.Equal("true", project.Descendants("PublishAot").Single().Value);

        // The package-only consumer must compile the same smoke implementation,
        // including fixtures added later; a project-reference build alone does
        // not catch missing copies in the release evidence path.
        string smokeDirectory = Path.Combine(rendererRoot, "MarkdownRenderer.Svg.Resvg.AotSmoke");
        foreach (string source in Directory.EnumerateFiles(smokeDirectory, "*.cs"))
        {
            string fileName = Path.GetFileName(source);
            Assert.Contains($"'MarkdownRenderer.Svg.Resvg.AotSmoke\\{fileName}'", script);
            Assert.Contains($"(Join-Path $consumerDirectory '{fileName}')", script);
        }
    }

    private static string FindRendererRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "eng", "Invoke-SvgResvgReleaseEvidence.ps1")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("MarkdownRenderer source root was not found.");
    }
}
