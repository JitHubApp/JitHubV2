using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace MarkdownRenderer.PublicApi.Tests;

/// <summary>
/// The public-API analyzer's optional-parameter rules (RS0026, RS0027) treat
/// every same-named method in a static class as one overload set. Feature
/// extension classes such as GfmExtensions extend several receivers with one
/// name, so they suppress those rules at class level. This test applies the
/// rules where they matter, to each receiver's own overloads, for every
/// extension method the packages ship.
/// </summary>
public sealed class ExtensionOverloadContractTests
{
    [Fact]
    public void ExtensionOverloads_FollowTheOptionalParameterRulesForEachReceiver()
    {
        var groups = ShippingAssemblies.ExportedTypes()
            .Where(static type => type.IsAbstract && type.IsSealed)
            .SelectMany(static type => ConsumerSurface.Methods(type)
                .Where(static method => method.IsDefined(typeof(ExtensionAttribute), inherit: false)))
            .GroupBy(static method => (
                Type: method.DeclaringType!,
                method.Name,
                Receiver: method.GetParameters()[0].ParameterType))
            .ToArray();

        Assert.NotEmpty(groups);
        var findings = new List<string>();
        foreach (var group in groups)
        {
            MethodInfo[] withOptional = group
                .Where(static method => method.GetParameters().Any(static parameter => parameter.IsOptional))
                .ToArray();
            if (withOptional.Length == 0)
                continue;

            string receiver = $"{ConsumerSurface.Name(group.Key.Type)}.{group.Key.Name}(this {ConsumerSurface.Name(group.Key.Receiver)})";
            if (withOptional.Length > 1)
                findings.Add($"{receiver}: {withOptional.Length} overloads take optional parameters (RS0026).");

            int longest = group.Max(static method => method.GetParameters().Length);
            if (withOptional.Any(method => method.GetParameters().Length < longest))
                findings.Add($"{receiver}: an overload with optional parameters is not the longest overload (RS0027).");
        }

        Assert.Empty(findings);
    }
}
