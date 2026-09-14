using System.Reflection;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Html;
using MarkdownRenderer.Math;
using MarkdownRenderer.Mermaid;
using MarkdownRenderer.Svg.ThorVG;
using MarkdownRenderer.SyntaxHighlighting.TextMate;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.All;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common;
using Xunit;
using Xunit.Sdk;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class PublicApiBaselineTests
{
    private const string UpdateVariable = "MARKDOWNRENDERER_UPDATE_PUBLIC_API_BASELINES";
    private const string DirectoryVariable = "MARKDOWNRENDERER_PUBLIC_API_BASELINE_DIRECTORY";
    private const string ResourcePrefix = "MarkdownRenderer.PublicApiBaselines.";

    private static readonly IReadOnlyDictionary<string, Assembly> ShippingAssemblies =
        new Dictionary<string, Assembly>(StringComparer.Ordinal)
        {
            ["MarkdownRenderer.Core"] = typeof(MarkdownEngine).Assembly,
            ["MarkdownRenderer"] = typeof(MarkdownScrollView).Assembly,
            ["MarkdownRenderer.Gfm"] = typeof(GfmExtensions).Assembly,
            ["MarkdownRenderer.Html"] = typeof(SafeHtmlFeature).Assembly,
            ["MarkdownRenderer.GitHub"] = typeof(GitHubReadmeExtensions).Assembly,
            ["MarkdownRenderer.Math"] = typeof(MathFeature).Assembly,
            ["MarkdownRenderer.Mermaid"] = typeof(MermaidRenderer).Assembly,
            ["MarkdownRenderer.Svg.ThorVG"] = typeof(ThorVgFeature).Assembly,
            ["MarkdownRenderer.SyntaxHighlighting.TextMate"] = typeof(TextMateSyntaxHighlightingExtensions).Assembly,
            ["MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common"] = typeof(CommonTextMateGrammarProvider).Assembly,
            ["MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.All"] = typeof(AllTextMateGrammarProvider).Assembly,
        };

    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "Markdig.",
        "Microsoft.Graphics.Canvas.",
        "CSharpMath.",
        "Typography.",
        "TextMateSharp.",
        "Merman.",
        "MarkdownRenderer.Accessibility.",
        "MarkdownRenderer.Diagnostics.",
        "MarkdownRenderer.Layout.",
        "MarkdownRenderer.Native.",
        "MarkdownRenderer.Parsing.",
        "MarkdownRenderer.Rendering.",
        "MarkdownRenderer.Utilities.",
    ];

    private static readonly HashSet<string> ForbiddenRendererTypeNames = new(StringComparer.Ordinal)
    {
        "MarkdownRenderer.Accessibility.MarkdownAutomationPeer",
        "MarkdownRenderer.Accessibility.MarkdownNodePeer",
        "MarkdownRenderer.Accessibility.MarkdownSemanticDocument",
        "MarkdownRenderer.Accessibility.MarkdownSemanticNode",
        "MarkdownRenderer.Layout.BlockBox",
        "MarkdownRenderer.Layout.LayoutBuilder",
        "MarkdownRenderer.Layout.LayoutSnapshot",
        "MarkdownRenderer.Layout.MarkdownLayoutContext",
        "MarkdownRenderer.Parsing.IMarkdownNodeRenderer",
        "MarkdownRenderer.Parsing.IMarkdownNodeRendererErased",
        "MarkdownRenderer.Parsing.MarkdownExtensionRegistry",
        "MarkdownRenderer.Parsing.MarkdownNodeRenderer",
        "MarkdownRenderer.Theming.ThemeSnapshot",
    };

    public static IEnumerable<object[]> AssemblyNames()
        => ShippingAssemblies.Keys.Order(StringComparer.Ordinal).Select(static name => new object[] { name });

    [Theory]
    [MemberData(nameof(AssemblyNames))]
    public void PublicApiBaselines_MatchBuiltAssemblies(string assemblyName)
    {
        Assembly assembly = ShippingAssemblies[assemblyName];
        Assert.Equal(assemblyName, assembly.GetName().Name);

        string actual = PublicApiSurface.Create(assembly);
        if (IsExplicitUpdate())
        {
            string directory = Environment.GetEnvironmentVariable(DirectoryVariable)
                ?? throw new XunitException($"{DirectoryVariable} must name the explicit update output directory.");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, assemblyName + ".txt"), actual);
            return;
        }

        string resourceName = ResourcePrefix + assemblyName + ".txt";
        using Stream stream = typeof(PublicApiBaselineTests).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new XunitException(
                $"Missing embedded public API baseline '{resourceName}'. " +
                "Use eng/Update-PublicApiBaselines.ps1 -Update and review the resulting diff.");
        using var reader = new StreamReader(stream);
        string expected = PublicApiSurface.Normalize(reader.ReadToEnd());
        AssertBaselineMatches(assemblyName, expected, actual);
    }

    [Fact]
    public void ShippingAssemblySet_IsExactAndContainsEveryBuiltPublicPackageAssembly()
    {
        string[] expected =
        [
            "MarkdownRenderer",
            "MarkdownRenderer.Core",
            "MarkdownRenderer.Gfm",
            "MarkdownRenderer.GitHub",
            "MarkdownRenderer.Html",
            "MarkdownRenderer.Math",
            "MarkdownRenderer.Mermaid",
            "MarkdownRenderer.Svg.ThorVG",
            "MarkdownRenderer.SyntaxHighlighting.TextMate",
            "MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.All",
            "MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common",
        ];

        Assert.Equal(expected, ShippingAssemblies.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(expected.Length, ShippingAssemblies.Values.Distinct().Count());

        string outputDirectory = Path.GetDirectoryName(typeof(PublicApiBaselineTests).Assembly.Location)
            ?? throw new XunitException("The test assembly has no output directory.");
        string[] builtManagedAssemblies = Directory
            .EnumerateFiles(outputDirectory, "MarkdownRenderer*.dll", SearchOption.TopDirectoryOnly)
            .Select(TryGetManagedAssemblyName)
            .Where(static name => name is not null && name != "MarkdownRenderer.GitHub.Tests")
            .Select(static name => name!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, builtManagedAssemblies);
    }

    [Fact]
    public void BaselineResources_AreExactAndHaveNoUnreviewedOrMissingAssemblyFiles()
    {
        string[] expected = ShippingAssemblies.Keys
            .Select(static name => ResourcePrefix + name + ".txt")
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] actual = typeof(PublicApiBaselineTests).Assembly
            .GetManifestResourceNames()
            .Where(static name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ContractAssemblies_ArePhysicalBuiltDllOutputs()
    {
        foreach ((string expectedName, Assembly assembly) in ShippingAssemblies)
        {
            Assert.Equal(expectedName, assembly.GetName().Name);
            Assert.False(assembly.IsDynamic);
            Assert.Equal(".dll", Path.GetExtension(assembly.Location), ignoreCase: true);
            Assert.True(File.Exists(assembly.Location), $"Built output is missing: {assembly.Location}");
            Assert.True(new FileInfo(assembly.Location).Length > 0, $"Built output is empty: {assembly.Location}");
        }
    }

    [Fact]
    public void ExactComparison_RejectsAdditionsRemovalsAndSignatureChanges()
    {
        const string original = "assembly Example\n\npublic class Example.Widget\n  public void Render(string? value);\n";
        string addition = original + "  public void Added();\n";
        string removal = original.Replace("  public void Render(string? value);\n", string.Empty, StringComparison.Ordinal);
        string signatureChange = original.Replace("string? value", "int value", StringComparison.Ordinal);

        Assert.Throws<XunitException>(() => AssertBaselineMatches("Example", original, addition));
        Assert.Throws<XunitException>(() => AssertBaselineMatches("Example", original, removal));
        Assert.Throws<XunitException>(() => AssertBaselineMatches("Example", original, signatureChange));
    }

    [Fact]
    public void Serializer_IsDeterministicForEveryBuiltAssembly()
    {
        foreach (Assembly assembly in ShippingAssemblies.Values)
            Assert.Equal(PublicApiSurface.Create(assembly), PublicApiSurface.Create(assembly));
    }

    [Fact]
    public void Serializer_PreservesNullableContractsAndNumericEnumValues()
    {
        string core = PublicApiSurface.Create(ShippingAssemblies["MarkdownRenderer.Core"]);
        string mermaid = PublicApiSurface.Create(ShippingAssemblies["MarkdownRenderer.Mermaid"]);

        Assert.Contains("public string? Language { get; init; }", core, StringComparison.Ordinal);
        Assert.Contains("Success = 0,", mermaid, StringComparison.Ordinal);
        Assert.DoesNotContain("value__", mermaid, StringComparison.Ordinal);
    }

    [Fact]
    public void Serializer_IncludesProtectedNestedTypesVarianceAndVirtualProperties()
    {
        string surface = PublicApiSurface.Create(typeof(PublicApiSurfaceFixture).Assembly);

        Assert.Contains(
            "protected delegate TResult? MarkdownRenderer.GitHub.Tests.PublicApiSurfaceFixture.Factory<out TResult>()",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "protected class MarkdownRenderer.GitHub.Tests.PublicApiSurfaceFixture.NestedContract",
            surface,
            StringComparison.Ordinal);
        Assert.Contains("protected virtual string? Value { get; }", surface, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InternalPublicApiSurfaceMarker), surface, StringComparison.Ordinal);
    }

    [Fact]
    public void ForbiddenContractScanner_DetectsTypesNestedInsideGenericSignatures()
    {
        IReadOnlyList<string> findings = PublicApiSurface.FindForbiddenContractTypes(
            typeof(PublicApiSurfaceFixture).Assembly,
            Array.Empty<string>(),
            [typeof(System.Text.StringBuilder).FullName!]);

        Assert.Contains(
            findings,
            static finding => finding.Contains(
                "PublicApiSurfaceFixture.ForbiddenLeak exposes System.Text.StringBuilder",
                StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(AssemblyNames))]
    public void PublicContracts_DoNotExposeThirdPartyOrRendererInternalTypes(string assemblyName)
    {
        IReadOnlyList<string> findings = PublicApiSurface.FindForbiddenContractTypes(
            ShippingAssemblies[assemblyName],
            ForbiddenNamespacePrefixes,
            ForbiddenRendererTypeNames);

        Assert.True(
            findings.Count == 0,
            $"{assemblyName} exposes forbidden implementation contracts:{Environment.NewLine}" +
            string.Join(Environment.NewLine, findings));
    }

    private static bool IsExplicitUpdate()
        => string.Equals(Environment.GetEnvironmentVariable(UpdateVariable), "1", StringComparison.Ordinal);

    private static string? TryGetManagedAssemblyName(string path)
    {
        try
        {
            return AssemblyName.GetAssemblyName(path).Name;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    private static void AssertBaselineMatches(string assemblyName, string expected, string actual)
    {
        expected = PublicApiSurface.Normalize(expected);
        actual = PublicApiSurface.Normalize(actual);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new XunitException(CreateDifferenceMessage(assemblyName, expected, actual));
    }

    private static string CreateDifferenceMessage(string assemblyName, string expected, string actual)
    {
        string[] expectedLines = expected.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string[] actualLines = actual.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string[] removed = expectedLines.Except(actualLines, StringComparer.Ordinal).Take(80).ToArray();
        string[] added = actualLines.Except(expectedLines, StringComparer.Ordinal).Take(80).ToArray();

        int commonLength = System.Math.Min(expectedLines.Length, actualLines.Length);
        int firstDifference = 0;
        while (firstDifference < commonLength &&
               string.Equals(expectedLines[firstDifference], actualLines[firstDifference], StringComparison.Ordinal))
        {
            firstDifference++;
        }

        var message = new System.Text.StringBuilder();
        message.Append("Public API baseline mismatch for ").Append(assemblyName).AppendLine(".")
            .Append("Expected ").Append(expectedLines.Length).Append(" non-empty lines; actual ")
            .Append(actualLines.Length).AppendLine(".")
            .Append("First differing line: ").AppendLine((firstDifference + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (removed.Length != 0)
        {
            message.AppendLine("Removed or changed baseline entries:");
            foreach (string line in removed)
                message.Append("- ").AppendLine(line);
        }

        if (added.Length != 0)
        {
            message.AppendLine("Added or changed assembly entries:");
            foreach (string line in added)
                message.Append("+ ").AppendLine(line);
        }

        message.AppendLine("Run eng/Update-PublicApiBaselines.ps1 -Update only for an intentional, reviewed API change.");
        return message.ToString();
    }
}

internal interface InternalPublicApiSurfaceMarker
{
}

public class PublicApiSurfaceFixture : InternalPublicApiSurfaceMarker
{
    protected delegate TResult Factory<out TResult>();

    protected virtual IReadOnlyList<System.Text.StringBuilder> ForbiddenLeak => [];

    protected class NestedContract
    {
        protected virtual string? Value => null;
    }
}
