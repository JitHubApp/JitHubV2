using System.Reflection;
using Xunit;

namespace MarkdownRenderer.PublicApi.Tests;

/// <summary>
/// The policy tests run against every renderer assembly this project
/// references, so a new shipping assembly cannot silently escape them.
/// </summary>
public sealed class ShippingAssemblySetTests
{
    [Fact]
    public void ShippingAssemblies_AreEveryRendererAssemblyInTheTestOutput()
    {
        string[] expected = ShippingAssemblies.All
            .Select(static assembly => assembly.GetName().Name!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string testAssembly = typeof(ShippingAssemblySetTests).Assembly.GetName().Name!;

        string[] built = Directory
            .EnumerateFiles(AppContext.BaseDirectory, "MarkdownRenderer*.dll", SearchOption.TopDirectoryOnly)
            .Select(TryGetManagedAssemblyName)
            .OfType<string>()
            .Where(name => name != testAssembly)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected.Length, expected.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(expected, built);
    }

    private static string? TryGetManagedAssemblyName(string path)
    {
        try
        {
            return AssemblyName.GetAssemblyName(path).Name;
        }
        catch (BadImageFormatException)
        {
            // A native payload copied next to the managed assemblies.
            return null;
        }
    }
}
