using System.Reflection;
using System.Text.RegularExpressions;

namespace MarkdownRenderer.PublicApi.Tests;

/// <summary>
/// The members of an exported type that a consumer can see: public members,
/// plus protected ones, which a consumer reaches by deriving. Private and
/// internal members are filtered out and never inspected.
/// </summary>
internal static class ConsumerSurface
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.DeclaredOnly;

    public static IEnumerable<ConstructorInfo> Constructors(Type type)
        => type.GetConstructors(Declared).Where(IsVisible);

    /// <summary>Methods, including property and event accessors.</summary>
    public static IEnumerable<MethodInfo> Methods(Type type)
        => type.GetMethods(Declared).Where(IsVisible);

    public static IEnumerable<PropertyInfo> Properties(Type type)
        => type.GetProperties(Declared).Where(static property => IsVisible(property.GetMethod) || IsVisible(property.SetMethod));

    public static IEnumerable<EventInfo> Events(Type type)
        => type.GetEvents(Declared).Where(static @event => IsVisible(@event.AddMethod) || IsVisible(@event.RemoveMethod));

    public static IEnumerable<FieldInfo> Fields(Type type)
        => type.GetFields(Declared).Where(static field => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly);

    /// <summary>Protected nested types; public nested types are exported types themselves.</summary>
    public static IEnumerable<Type> ProtectedNestedTypes(Type type)
        => type.GetNestedTypes(BindingFlags.NonPublic).Where(static nested => nested.IsNestedFamily || nested.IsNestedFamORAssem);

    public static bool IsVisible(MethodBase? method)
        => method is not null && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly);

    /// <summary>A C#-like full name, such as <c>System.IComparable&lt;Ns.Type&gt;</c>.</summary>
    public static string Name(Type type)
    {
        if (type.IsGenericParameter)
            return type.Name;
        if (type.IsArray)
            return Name(type.GetElementType()!) + "[]";

        Type definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        string name = Regex.Replace((definition.FullName ?? definition.Name).Replace('+', '.'), "`[0-9]+", string.Empty);
        return type.IsGenericType
            ? $"{name}<{string.Join(", ", type.GetGenericArguments().Select(Name))}>"
            : name;
    }
}
