using Xunit;

namespace MarkdownRenderer.Math.Tests;

/// <summary>The built math assembly's public surface, embedded fonts and omitted vendor readers.</summary>
public sealed class MathAssemblySurfaceTests
{
    [Fact]
    public void BuiltAssemblyExportsOnlyRendererOwnedTypes()
    {
        Type[] exported = typeof(MathFormulaProcessor).Assembly.GetExportedTypes();

        Assert.DoesNotContain(exported, type =>
            type.Namespace?.StartsWith("CSharpMath", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(exported, type =>
            type.Namespace?.StartsWith("Typography", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(exported.SelectMany(GetPublicSignatureTypes), type =>
            type.Namespace?.StartsWith("CSharpMath", StringComparison.Ordinal) == true ||
            type.Namespace?.StartsWith("Typography", StringComparison.Ordinal) == true ||
            type.Namespace?.StartsWith("Microsoft.Graphics.Canvas", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void BuiltAssemblyContainsOnlyHashLockedMathFonts()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CSharpMath.Rendering.Reference_Fonts.AMS-Capital-Blackboard-Bold.otf"] =
                "9578b5b9c86e6ab03846080b9d6fa4f7bc6b3044ac15604b8e7bfd4330295dda",
            ["CSharpMath.Rendering.Reference_Fonts.cyrillic-modern-nmr10.otf"] =
                "5b8e360154685a1117e7f93542b89d5263db58f41a40d6df8f131c5fe5032c0c",
            ["CSharpMath.Rendering.Reference_Fonts.latinmodern-math.otf"] =
                "6075562b771f8b82f0c179e363389684f2dd09de30038269e2628e504bd7be0f",
        };
        System.Reflection.Assembly assembly = typeof(MathFormulaProcessor).Assembly;
        string[] actualNames = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected.Keys.OrderBy(name => name, StringComparer.Ordinal), actualNames);
        foreach ((string resourceName, string expectedHash) in expected)
        {
            using Stream stream = Assert.IsAssignableFrom<Stream>(
                assembly.GetManifestResourceStream(resourceName));
            string actualHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
            Assert.Equal(expectedHash, actualHash);
        }
    }

    [Fact]
    public void BuiltAssemblyOmitsUnsupportedGeneralFontReaders()
    {
        Type[] types = typeof(MathFormulaProcessor).Assembly.GetTypes();

        Assert.DoesNotContain(types, type =>
            type.Namespace?.Contains("WebFont", StringComparison.Ordinal) == true ||
            type.Namespace?.Contains("BitmapAndSvgFonts", StringComparison.Ordinal) == true ||
            type.Namespace?.Contains("Tables.Variations", StringComparison.Ordinal) == true ||
            type.Namespace?.Contains("TrueTypeInterperter", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(types, type => type.Name is
            "SvgTable" or "WoffReader" or "Woff2Reader" or "Glyf" or
            "TrueTypeInterpreter" or "BitmapFontGlyphSource");
    }

    private static IEnumerable<Type> GetPublicSignatureTypes(Type type)
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.DeclaredOnly;

        foreach (System.Reflection.MethodInfo method in type.GetMethods(flags))
        {
            yield return method.ReturnType;
            foreach (System.Reflection.ParameterInfo parameter in method.GetParameters())
                yield return parameter.ParameterType;
        }

        foreach (System.Reflection.PropertyInfo property in type.GetProperties(flags))
            yield return property.PropertyType;
        foreach (System.Reflection.FieldInfo field in type.GetFields(flags))
            yield return field.FieldType;
    }
}
