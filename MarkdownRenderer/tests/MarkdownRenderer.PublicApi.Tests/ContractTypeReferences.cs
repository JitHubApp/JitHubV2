using System.Reflection;

namespace MarkdownRenderer.PublicApi.Tests;

/// <summary>
/// Every type a consumer can name through an exported type: the type itself,
/// its base type, visible interfaces, generic constraints and attributes, and
/// the field, property, indexer, event, constructor, method, parameter and
/// return types of its consumer-visible members, including element types and
/// generic arguments at any depth.
/// </summary>
internal static class ContractTypeReferences
{
    public static IEnumerable<(string Owner, Type Type)> Of(Type exportedType)
    {
        string name = ConsumerSurface.Name(exportedType);
        var found = new List<(string, Type)>();

        Add(found, $"{name} declaration", exportedType);
        AddAttributes(found, $"{name} declaration", exportedType.CustomAttributes);
        Add(found, $"{name} base type", exportedType.BaseType);
        foreach (Type contract in exportedType.GetInterfaces().Where(static contract => contract.IsVisible))
            Add(found, $"{name} interface", contract);
        AddConstraints(found, $"{name} generic constraint", exportedType.GetGenericArguments());

        foreach (FieldInfo field in ConsumerSurface.Fields(exportedType))
        {
            Add(found, $"{name}.{field.Name}", field.FieldType);
            AddAttributes(found, $"{name}.{field.Name}", field.CustomAttributes);
        }

        foreach (PropertyInfo property in ConsumerSurface.Properties(exportedType))
        {
            Add(found, $"{name}.{property.Name}", property.PropertyType);
            AddAttributes(found, $"{name}.{property.Name}", property.CustomAttributes);
            AddParameters(found, $"{name}.{property.Name} indexer", property.GetIndexParameters());
        }

        foreach (EventInfo @event in ConsumerSurface.Events(exportedType))
        {
            Add(found, $"{name}.{@event.Name}", @event.EventHandlerType);
            AddAttributes(found, $"{name}.{@event.Name}", @event.CustomAttributes);
        }

        foreach (ConstructorInfo constructor in ConsumerSurface.Constructors(exportedType))
        {
            AddAttributes(found, $"{name} constructor", constructor.CustomAttributes);
            AddParameters(found, $"{name} constructor", constructor.GetParameters());
        }

        foreach (MethodInfo method in ConsumerSurface.Methods(exportedType))
        {
            Add(found, $"{name}.{method.Name} return", method.ReturnType);
            AddAttributes(found, $"{name}.{method.Name}", method.CustomAttributes);
            AddAttributes(found, $"{name}.{method.Name} return", method.ReturnParameter.CustomAttributes);
            AddParameters(found, $"{name}.{method.Name}", method.GetParameters());
            AddConstraints(found, $"{name}.{method.Name} generic constraint", method.GetGenericArguments());
        }

        foreach (Type nested in ConsumerSurface.ProtectedNestedTypes(exportedType))
            found.AddRange(Of(nested));

        return found;
    }

    private static void AddParameters(List<(string, Type)> found, string owner, IEnumerable<ParameterInfo> parameters)
    {
        foreach (ParameterInfo parameter in parameters)
        {
            Add(found, $"{owner} parameter '{parameter.Name}'", parameter.ParameterType);
            AddAttributes(found, $"{owner} parameter '{parameter.Name}'", parameter.CustomAttributes);
        }
    }

    private static void AddAttributes(List<(string, Type)> found, string owner, IEnumerable<CustomAttributeData> attributes)
    {
        foreach (CustomAttributeData attribute in attributes)
            Add(found, $"{owner} attribute", attribute.AttributeType);
    }

    private static void AddConstraints(List<(string, Type)> found, string owner, IEnumerable<Type> genericArguments)
    {
        foreach (Type parameter in genericArguments.Where(static argument => argument.IsGenericParameter))
        {
            foreach (Type constraint in parameter.GetGenericParameterConstraints())
                Add(found, owner, constraint);
        }
    }

    private static void Add(List<(string, Type)> found, string owner, Type? type)
    {
        if (type is null)
            return;

        while (type.HasElementType)
            type = type.GetElementType()!;

        if (type.IsGenericParameter)
            return;

        found.Add((owner, type));
        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
                Add(found, owner, argument);
        }
    }
}
