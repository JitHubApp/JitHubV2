using System.Reflection;

namespace MarkdownRenderer.Math.Internal;

/// <summary>
/// Generated from the audited font allowlist in ThirdParty/PROVENANCE.json.
/// Keeping the names in a closed switch avoids runtime resource discovery and
/// makes trimming and NativeAOT behavior deterministic.
/// </summary>
internal static class MathFontResources
{
    private const string Prefix = "CSharpMath.Rendering.Reference_Fonts.";

    internal const int CatalogUncompressedBytes = 786_744;

    internal const int MaximumCatalogUncompressedBytes = 1024 * 1024;

    internal static Stream Open(string fileName)
    {
        (string ResourceName, int Length) resource = fileName switch
        {
            "AMS-Capital-Blackboard-Bold.otf" => (Prefix + "AMS-Capital-Blackboard-Bold.otf", 8_716),
            "cyrillic-modern-nmr10.otf" => (Prefix + "cyrillic-modern-nmr10.otf", 44_292),
            "latinmodern-math.otf" => (Prefix + "latinmodern-math.otf", 733_736),
            _ => throw new InvalidOperationException($"The math font '{fileName}' is not registered."),
        };

        Stream stream = typeof(MathFontResources).Assembly.GetManifestResourceStream(resource.ResourceName)
            ?? throw new InvalidOperationException($"The registered math font resource '{resource.ResourceName}' is missing.");
        if (stream.Length != resource.Length || CatalogUncompressedBytes > MaximumCatalogUncompressedBytes)
        {
            stream.Dispose();
            throw new InvalidOperationException("The registered math font catalog does not match its generated size budget.");
        }

        return stream;
    }
}
