using Xunit;

namespace MarkdownRenderer.PublicApi.Tests;

/// <summary>
/// Public signatures expose renderer-owned contracts only: no third-party
/// implementation types (Markdig, Win2D, CSharpMath, Typography, TextMateSharp,
/// Merman) and no renderer-internal namespaces or types. The one exception is
/// listed in <see cref="AllowedThirdPartyNamespacePrefixes"/>.
/// </summary>
public sealed class ForbiddenContractTypeTests
{
    /// <summary>
    /// The TextMate engine package exists to share a TextMateSharp-backed
    /// provider between the grammar packs, which supply TextMateSharp registry
    /// options. It is the only package versioned with TextMateSharp; the lean
    /// integration and the grammar packs still expose no TextMateSharp type.
    /// </summary>
    private static readonly Dictionary<string, string[]> AllowedThirdPartyNamespacePrefixes = new(StringComparer.Ordinal)
    {
        ["MarkdownRenderer.SyntaxHighlighting.TextMate.Engine"] = ["TextMateSharp."],
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

    private static readonly HashSet<string> ForbiddenTypeNames = new(StringComparer.Ordinal)
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

    public static IEnumerable<object[]> AssemblyNames() => ShippingAssemblies.Names();

    [Theory]
    [MemberData(nameof(AssemblyNames))]
    public void PublicSignatures_DoNotExposeThirdPartyOrRendererInternalTypes(string assemblyName)
    {
        string[] allowed = AllowedThirdPartyNamespacePrefixes.GetValueOrDefault(assemblyName, []);
        string[] findings = Find(
            ShippingAssemblies.Get(assemblyName).GetExportedTypes(),
            ForbiddenNamespacePrefixes.Except(allowed, StringComparer.Ordinal).ToArray(),
            ForbiddenTypeNames);

        Assert.True(
            findings.Length == 0,
            $"{assemblyName} exposes forbidden contracts:{Environment.NewLine}{string.Join(Environment.NewLine, findings)}");
    }

    [Fact]
    public void ThirdPartyExceptions_NameShippingAssembliesThatActuallyUseThem()
    {
        // An exception must stay necessary: it names a shipping assembly whose
        // public signatures really do reference the allowed namespace.
        foreach ((string assemblyName, string[] prefixes) in AllowedThirdPartyNamespacePrefixes)
        {
            string[] uses = Find(ShippingAssemblies.Get(assemblyName).GetExportedTypes(), prefixes, new HashSet<string>());
            Assert.True(uses.Length > 0, $"{assemblyName} no longer exposes {string.Join(", ", prefixes)}; remove its exception.");
        }
    }

    [Fact]
    public void Scanner_FindsForbiddenTypesNestedInGenericArgumentsOfProtectedMembers()
    {
        string[] findings = Find(
            [typeof(ContractTypeReferencesFixture)],
            [],
            new HashSet<string>(StringComparer.Ordinal) { typeof(System.Text.StringBuilder).FullName! });

        Assert.Contains(
            $"{typeof(ContractTypeReferencesFixture).FullName}.ForbiddenLeak exposes System.Text.StringBuilder",
            findings);
    }

    private static string[] Find(IEnumerable<Type> exportedTypes, IReadOnlyCollection<string> prefixes, IReadOnlySet<string> names)
        => exportedTypes
            .SelectMany(ContractTypeReferences.Of)
            .Select(static reference => (reference.Owner, Identity: reference.Type.FullName ?? reference.Type.Name))
            .Where(reference =>
                names.Contains(reference.Identity) ||
                prefixes.Any(prefix => reference.Identity.StartsWith(prefix, StringComparison.Ordinal)))
            .Select(static reference => $"{reference.Owner} exposes {reference.Identity}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
