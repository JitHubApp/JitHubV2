using System.Runtime.InteropServices;
using System.Reflection.PortableExecutable;
using Xunit;

namespace MarkdownRenderer.Tests;

public class NativeAssetPackagingTests
{
    public static TheoryData<string, Machine> NativeAssets => new()
    {
        { "win-x86", Machine.I386 },
        { "win-x64", Machine.Amd64 },
        { "win-arm64", Machine.Arm64 },
    };

    [Fact]
    public void ThorVgDll_CopiesNextToTestAssembly()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "thorvg.dll");

        Assert.True(File.Exists(path), $"Expected ThorVG native asset at {path}");
        Assert.True(new FileInfo(path).Length > 0);
    }

    [Fact]
    public void ThorVgDll_MatchesCurrentTestArchitecture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "thorvg.dll");
        using var stream = File.OpenRead(path);
        using var reader = new PEReader(stream);

        var expected = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => Machine.Amd64,
            Architecture.Arm64 => Machine.Arm64,
            _ => reader.PEHeaders.CoffHeader.Machine,
        };

        Assert.Equal(expected, reader.PEHeaders.CoffHeader.Machine);
    }

    [Theory]
    [MemberData(nameof(NativeAssets))]
    public void ThorVgDll_ExistsForEverySupportedArchitecture(string runtime, Machine expected)
    {
        string repoAsset = Path.Combine(FindCoreProjectRoot(), "native", runtime, "thorvg.dll");

        Assert.True(File.Exists(repoAsset), $"Expected ThorVG native asset at {repoAsset}");
        Assert.True(new FileInfo(repoAsset).Length > 0);

        using var stream = File.OpenRead(repoAsset);
        using var reader = new PEReader(stream);
        Assert.Equal(expected, reader.PEHeaders.CoffHeader.Machine);
    }

    private static string FindCoreProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "MarkdownRenderer");
            if (File.Exists(Path.Combine(candidate, "MarkdownRenderer.csproj")))
                return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the MarkdownRenderer project root.");
    }
}
