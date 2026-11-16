using System.Text.RegularExpressions;
using JitHub.WinUI.Tests.TestSupport;
using Xunit;

namespace JitHub.WinUI.Tests.Helpers;

public sealed class AsyncUiBoundaryContractTests
{
    [Fact]
    public void ProductUi_DoesNotUseUnobservedAsyncVoidBoundaries()
    {
        foreach (ProductSourceFile file in ProductSourceTree.EnumerateFiles("*.cs"))
        {
            string source = File.ReadAllText(file.FullPath);
            string relativePath = file.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            Assert.DoesNotMatch(
                new Regex(@"\basync\s+void\b", RegexOptions.CultureInvariant),
                source);
            Assert.DoesNotMatch(
                new Regex(@"\+=\s*async\b", RegexOptions.CultureInvariant),
                source);
            Assert.DoesNotMatch(
                new Regex(@"TryEnqueue\s*\(\s*async\b", RegexOptions.CultureInvariant),
                source);
            Assert.False(
                source.Contains("Tick += async", StringComparison.Ordinal),
                $"{relativePath} attaches an async-void timer callback.");

            if (relativePath.StartsWith($"Views{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                Assert.DoesNotMatch(
                    new Regex(
                        @"_\s*=(?!\s*await\b)\s*(?:(?!;).){0,500}\b[A-Za-z_][A-Za-z0-9_]*Async\s*\(",
                        RegexOptions.CultureInvariant | RegexOptions.Singleline),
                    source);
                Assert.DoesNotContain("_ = Task.Run(", source, StringComparison.Ordinal);
                Assert.DoesNotContain(".ContinueWith(", source, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void UiTaskGuard_ContainsCancellationAndReportsUnexpectedFailures()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Helpers",
            "UiTaskGuard.cs"));

        Assert.Contains("catch (OperationCanceledException)", source, StringComparison.Ordinal);
        Assert.Contains("catch (Exception exception)", source, StringComparison.Ordinal);
        Assert.Contains("App.LogHandledException(exception, category)", source, StringComparison.Ordinal);
        Assert.Contains("_ = ObserveAsync(task, category, onFailure)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ConverterReversePaths_DoNotThrowForUnsupportedBindings()
    {
        string productRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI");
        string[] converterSources = Directory
            .EnumerateFiles(Path.Combine(productRoot, "Converters"), "*.cs", SearchOption.AllDirectories)
            .Append(Path.Combine(
                productRoot,
                "Views",
                "Controls",
                "Profile",
                "ProfileHexColorBrushConverter.cs"))
            .ToArray();

        foreach (string path in converterSources)
        {
            string source = File.ReadAllText(path);
            Assert.DoesNotMatch(
                new Regex(
                    @"ConvertBack[\s\S]*?throw\s+new\s+(?:NotImplementedException|NotSupportedException)",
                    RegexOptions.CultureInvariant),
                source);
            Assert.DoesNotMatch(
                new Regex(
                    @"ConvertBack[^\r\n]*=>\s*\r?\n\s*return\b",
                    RegexOptions.CultureInvariant),
                source);
        }
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
