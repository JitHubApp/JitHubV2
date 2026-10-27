using System.Text.RegularExpressions;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class WindowsWorkerTokenIsolationContractTests
{
    [Fact]
    public void RestrictedCodeSidIsPassedAsARestrictingSid()
    {
        string source = File.ReadAllText(FindWorkerSourcePath());

        Assert.Matches(
            new Regex(
                "GetSidBytes\\(\"S-1-5-12\"\\);\\s*fixed \\(byte\\* restrictedCodeSidPointer = restrictedCodeSid\\)\\s*\\{\\s*var restrictingSid = new SidAndAttributes\\s*\\{\\s*Sid = \\(nint\\)restrictedCodeSidPointer,\\s*Attributes = 0,\\s*\\};",
                RegexOptions.CultureInvariant),
            source);
        Assert.Matches(
            new Regex(
                "CreateRestrictedToken\\(\\s*sourceToken,\\s*DisableMaxPrivilege,\\s*0,\\s*0,\\s*0,\\s*0,\\s*1,\\s*\\(nint\\)\\(&restrictingSid\\),\\s*out SafeTokenHandle restrictedToken\\)",
                RegexOptions.CultureInvariant),
            source);
    }

    private static string FindWorkerSourcePath()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string sourcePath = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                "MarkdownRenderer.Svg.Resvg",
                "Internal",
                "WindowsWorkerProcess.cs");
            if (File.Exists(sourcePath))
                return sourcePath;
        }

        throw new FileNotFoundException(
            "The repository source for the restricted SVG worker token contract was not found.");
    }
}
