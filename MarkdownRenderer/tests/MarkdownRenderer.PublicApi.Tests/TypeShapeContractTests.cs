using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace MarkdownRenderer.PublicApi.Tests;

/// <summary>
/// Design rules for exported types that PublicAPI.*.txt does not express:
/// structs are readonly with default layout, classes are sealed or static,
/// and equality is implemented as a pair. Binary compatibility of a released
/// surface (base types, sealing, attributes, enum storage) is package
/// validation's job once a baseline version has shipped.
/// </summary>
public sealed class TypeShapeContractTests
{
    // The obsolete compatibility facade that MarkdownScrollView and
    // MarkdownDocumentView derive from. Step 2 of the structure cleanup
    // deletes it, and this list with it.
    private static readonly string[] ReviewedInheritableClasses =
    [
        "MarkdownRenderer.Controls.MarkdownRendererControl",
    ];

    private const BindingFlags DeclaredInstance =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    [Fact]
    public void ValueTypes_AreReadOnlyStructs()
    {
        Type[] valueTypes = ShippingAssemblies.ExportedTypes()
            .Where(static type => type.IsValueType && !type.IsEnum)
            .ToArray();

        Assert.NotEmpty(valueTypes);
        Assert.All(valueTypes, static type =>
        {
            Assert.True(type.IsDefined(typeof(IsReadOnlyAttribute), inherit: false), $"{type} is not a readonly struct.");
            Assert.False(type.IsByRefLike, $"{type} is a ref struct.");
        });
    }

    [Fact]
    public void ExportedTypes_UseDefaultLayout()
    {
        Assert.All(ShippingAssemblies.ExportedTypes().Where(static type => !type.IsInterface && !type.IsEnum), static type =>
        {
            StructLayoutAttribute layout = type.StructLayoutAttribute!;
            LayoutKind expected = type.IsValueType ? LayoutKind.Sequential : LayoutKind.Auto;
            Assert.True(
                layout.Value == expected && layout.Pack == 0 && layout.Size == 0 && layout.CharSet == CharSet.Ansi,
                $"{type} declares a non-default layout: {layout.Value}, Pack={layout.Pack}, Size={layout.Size}, CharSet={layout.CharSet}.");
        });
    }

    [Fact]
    public void Classes_AreSealedOrStatic_ExceptTheReviewedBaseClasses()
    {
        IEnumerable<string> inheritable = ShippingAssemblies.ExportedTypes()
            .Where(static type => type.IsClass && !type.IsSealed && !typeof(Delegate).IsAssignableFrom(type))
            .Select(ConsumerSurface.Name);

        ReviewedContract.AssertMatches("The set of inheritable exported classes", ReviewedInheritableClasses, inheritable);
    }

    [Fact]
    public void ClassesWithoutInstanceSurface_AreStatic()
    {
        string[] findings = ShippingAssemblies.ExportedTypes()
            .Where(static type => type.IsClass && type.IsSealed && !type.IsAbstract && !typeof(Delegate).IsAssignableFrom(type))
            .Where(static type =>
                !ConsumerSurface.Constructors(type).Any(static constructor => !constructor.IsStatic) &&
                !type.GetMembers(DeclaredInstance).Any(static member => member switch
                {
                    MethodBase method => ConsumerSurface.IsVisible(method),
                    PropertyInfo property => ConsumerSurface.IsVisible(property.GetMethod) || ConsumerSurface.IsVisible(property.SetMethod),
                    EventInfo @event => ConsumerSurface.IsVisible(@event.AddMethod),
                    FieldInfo field => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly,
                    _ => false,
                }))
            .Select(ConsumerSurface.Name)
            .ToArray();

        Assert.True(findings.Length == 0, "Declare these classes static: " + string.Join(", ", findings));
    }

    [Fact]
    public void EquatableTypes_AreExactlyThoseThatOverrideObjectEquals()
    {
        string[] findings = ShippingAssemblies.ExportedTypes()
            .Where(static type => !type.IsInterface && !type.IsEnum)
            .Select(static type => (
                Type: type,
                Equatable: type.GetInterfaces().Contains(typeof(IEquatable<>).MakeGenericType(type)),
                OverridesEquals: type.GetMethod(nameof(Equals), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, [typeof(object)]) is not null))
            .Where(static fact => fact.Equatable != fact.OverridesEquals)
            .Select(static fact => $"{ConsumerSurface.Name(fact.Type)} (IEquatable<T>: {fact.Equatable}, overrides Equals(object): {fact.OverridesEquals})")
            .ToArray();

        Assert.True(findings.Length == 0, "Pair IEquatable<T> with an Equals(object) override: " + string.Join(", ", findings));
    }
}
