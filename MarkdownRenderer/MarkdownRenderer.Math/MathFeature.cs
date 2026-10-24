namespace MarkdownRenderer.Math;

/// <summary>Package identity for host capability discovery.</summary>
public static class MathFeature
{
    /// <summary>Gets the package identity used for capability discovery.</summary>
    public const string PackageId = "MarkdownRenderer.Math";

    /// <summary>Gets the Core parser feature enabled by this package.</summary>
    public const string ParserFeatureId = MarkdownRenderer.Extensions.MarkdownExtensionFeatures.DollarMath;

    /// <summary>Gets the stable renderer-owned contract version.</summary>
    public const int ContractVersion = 1;

    /// <summary>Indicates that this package contains the audited native formula processor.</summary>
    public const bool IncludesNativeFormulaProcessor = true;

    /// <summary>Gets the pinned CSharpMath source revision used by the internal engine.</summary>
    public const string CSharpMathRevision = "2d7dec98695dac6944027d90c73ec50fe45c5964";
}
