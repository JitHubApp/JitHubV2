using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Produces a deterministic, source-like description of the consumer-visible
/// metadata in a compiled assembly. This deliberately reads reflection metadata
/// from the project-reference outputs rather than scanning source text.
/// </summary>
internal static class PublicApiSurface
{
    internal const int FormatVersion = 1;

    private const BindingFlags DeclaredInstanceAndStatic =
        BindingFlags.Public |
        BindingFlags.NonPublic |
        BindingFlags.Instance |
        BindingFlags.Static |
        BindingFlags.DeclaredOnly;

    private static readonly NullabilityInfoContext Nullability = new();

    internal static string Create(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        string assemblyName = assembly.GetName().Name
            ?? throw new InvalidOperationException("The assembly has no simple name.");
        Type[] exportedTypes = assembly
            .GetTypes()
            .Where(IsConsumerVisibleType)
            .OrderBy(static type => TypeSortKey(type), StringComparer.Ordinal)
            .ToArray();

        var output = new StringBuilder(capacity: System.Math.Max(4_096, exportedTypes.Length * 256));
        output.AppendLine("# MarkdownRenderer public API baseline");
        output.Append("# Format: ").AppendLine(FormatVersion.ToString(CultureInfo.InvariantCulture));
        output.Append("# Assembly: ").AppendLine(assemblyName);
        output.AppendLine("# Generated from compiled assembly metadata; update with eng/Update-PublicApiBaselines.ps1.");
        output.AppendLine();
        output.Append("assembly ").AppendLine(assemblyName);

        foreach (Type type in exportedTypes)
        {
            output.AppendLine();
            AppendType(output, type);
        }

        return Normalize(output.ToString());
    }

    internal static IReadOnlyList<string> FindForbiddenContractTypes(
        Assembly assembly,
        IReadOnlyCollection<string> forbiddenNamespacePrefixes,
        IReadOnlyCollection<string> forbiddenRendererTypeNames)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(forbiddenNamespacePrefixes);
        ArgumentNullException.ThrowIfNull(forbiddenRendererTypeNames);

        var findings = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Type type in assembly.GetTypes().Where(IsConsumerVisibleType))
        {
            InspectContractType(type, $"{DisplayTypeIdentity(type)} declaration", findings);
            InspectAttributes(type.CustomAttributes, $"{DisplayTypeIdentity(type)} declaration", findings);
            InspectContractType(type.BaseType, $"{DisplayTypeIdentity(type)} base type", findings);
            foreach (Type contract in type.GetInterfaces().Where(IsConsumerVisibleType))
                InspectContractType(contract, $"{DisplayTypeIdentity(type)} interface", findings);

            foreach (Type genericParameter in type.GetGenericArguments().Where(static argument => argument.IsGenericParameter))
            {
                foreach (Type constraint in genericParameter.GetGenericParameterConstraints())
                    InspectContractType(constraint, $"{DisplayTypeIdentity(type)} generic constraint", findings);
            }

            foreach (FieldInfo field in type.GetFields(DeclaredInstanceAndStatic).Where(IsVisible))
            {
                InspectContractType(field.FieldType, $"{DisplayTypeIdentity(type)}.{field.Name}", findings);
                InspectAttributes(field.CustomAttributes, $"{DisplayTypeIdentity(type)}.{field.Name}", findings);
            }

            foreach (PropertyInfo property in type.GetProperties(DeclaredInstanceAndStatic).Where(IsVisible))
            {
                InspectContractType(property.PropertyType, $"{DisplayTypeIdentity(type)}.{property.Name}", findings);
                InspectAttributes(property.CustomAttributes, $"{DisplayTypeIdentity(type)}.{property.Name}", findings);
                foreach (ParameterInfo parameter in property.GetIndexParameters())
                {
                    InspectContractType(parameter.ParameterType, $"{DisplayTypeIdentity(type)}.{property.Name} indexer", findings);
                    InspectAttributes(parameter.CustomAttributes, $"{DisplayTypeIdentity(type)}.{property.Name} indexer", findings);
                }
            }

            foreach (EventInfo @event in type.GetEvents(DeclaredInstanceAndStatic).Where(IsVisible))
            {
                InspectContractType(@event.EventHandlerType, $"{DisplayTypeIdentity(type)}.{@event.Name}", findings);
                InspectAttributes(@event.CustomAttributes, $"{DisplayTypeIdentity(type)}.{@event.Name}", findings);
            }

            foreach (ConstructorInfo constructor in type.GetConstructors(DeclaredInstanceAndStatic).Where(IsVisible))
            {
                InspectAttributes(constructor.CustomAttributes, $"{DisplayTypeIdentity(type)} constructor", findings);
                InspectParameters(constructor.GetParameters(), $"{DisplayTypeIdentity(type)} constructor", findings);
            }

            foreach (MethodInfo method in type.GetMethods(DeclaredInstanceAndStatic).Where(IsVisible))
            {
                InspectContractType(method.ReturnType, $"{DisplayTypeIdentity(type)}.{method.Name} return", findings);
                InspectAttributes(method.CustomAttributes, $"{DisplayTypeIdentity(type)}.{method.Name}", findings);
                InspectAttributes(method.ReturnParameter.CustomAttributes, $"{DisplayTypeIdentity(type)}.{method.Name} return", findings);
                InspectParameters(method.GetParameters(), $"{DisplayTypeIdentity(type)}.{method.Name}", findings);
                foreach (Type genericParameter in method.GetGenericArguments().Where(static argument => argument.IsGenericParameter))
                {
                    foreach (Type constraint in genericParameter.GetGenericParameterConstraints())
                        InspectContractType(constraint, $"{DisplayTypeIdentity(type)}.{method.Name} generic constraint", findings);
                }
            }
        }

        return findings.ToArray();

        void InspectParameters(IEnumerable<ParameterInfo> parameters, string owner, ISet<string> target)
        {
            foreach (ParameterInfo parameter in parameters)
            {
                InspectContractType(parameter.ParameterType, $"{owner} parameter '{parameter.Name}'", target);
                InspectAttributes(parameter.CustomAttributes, $"{owner} parameter '{parameter.Name}'", target);
            }
        }

        void InspectAttributes(
            IEnumerable<CustomAttributeData> attributes,
            string owner,
            ISet<string> target)
        {
            foreach (CustomAttributeData attribute in attributes)
                InspectContractType(attribute.AttributeType, $"{owner} attribute", target);
        }

        void InspectContractType(Type? candidate, string owner, ISet<string> target)
        {
            if (candidate is null)
                return;

            while (candidate.HasElementType)
                candidate = candidate.GetElementType()!;

            if (candidate.IsGenericType)
            {
                foreach (Type argument in candidate.GetGenericArguments())
                    InspectContractType(argument, owner, target);
            }

            if (candidate.IsGenericParameter)
                return;

            string identity = candidate.FullName ?? candidate.Name;
            if (forbiddenNamespacePrefixes.Any(prefix => identity.StartsWith(prefix, StringComparison.Ordinal)) ||
                forbiddenRendererTypeNames.Contains(identity))
            {
                target.Add($"{owner} exposes {identity}");
            }
        }
    }

    internal static string Normalize(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd('\n') + "\n";

    private static void AppendType(StringBuilder output, Type type)
    {
        string attributes = FormatContractAttributes(type);
        if (attributes.Length != 0)
            output.Append(attributes.AsSpan(1)).Append(' ');

        if (IsDelegate(type))
        {
            MethodInfo invoke = type.GetMethod("Invoke", DeclaredInstanceAndStatic)
                ?? throw new InvalidOperationException($"Delegate '{type}' has no Invoke method.");
            output.Append(TypeAccessibility(type)).Append(" delegate ")
                .Append(FormatReturnType(invoke))
                .Append(' ')
                .Append(FormatDeclaredTypeName(type))
                .Append('(')
                .Append(FormatParameters(invoke.GetParameters(), invoke.IsDefined(typeof(ExtensionAttribute), false)))
                .Append(')')
                .Append(FormatGenericConstraints(type.GetGenericArguments()))
                .Append(FormatScopedContractAttributes("return", invoke.ReturnParameter))
                .AppendLine(";");
            return;
        }

        output.Append(TypeAccessibility(type)).Append(' ');
        if (type.IsEnum)
        {
            output.Append("enum ").Append(FormatDeclaredTypeName(type));
            Type underlyingType = Enum.GetUnderlyingType(type);
            if (underlyingType != typeof(int))
                output.Append(" : ").Append(FormatType(underlyingType));
        }
        else if (type.IsInterface)
        {
            output.Append("interface ").Append(FormatDeclaredTypeName(type));
            AppendBaseContracts(output, type, includeBaseType: false);
        }
        else if (type.IsValueType)
        {
            if (type.IsDefined(typeof(IsReadOnlyAttribute), false))
                output.Append("readonly ");
            if (type.IsByRefLike)
                output.Append("ref ");
            output.Append("struct ").Append(FormatDeclaredTypeName(type));
            AppendBaseContracts(output, type, includeBaseType: false);
            AppendLayout(output, type);
        }
        else
        {
            if (type.IsAbstract && type.IsSealed)
                output.Append("static ");
            else
            {
                if (type.IsAbstract)
                    output.Append("abstract ");
                if (type.IsSealed)
                    output.Append("sealed ");
            }

            output.Append("class ").Append(FormatDeclaredTypeName(type));
            AppendBaseContracts(output, type, includeBaseType: true);
            if (type.StructLayoutAttribute?.Value is LayoutKind.Explicit or LayoutKind.Sequential)
                AppendLayout(output, type);
        }

        output.Append(FormatGenericConstraints(type.GetGenericArguments())).AppendLine();

        if (type.IsEnum)
        {
            Type underlyingType = Enum.GetUnderlyingType(type);
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static).OrderBy(static field => field.Name, StringComparer.Ordinal))
            {
                output.Append("  ")
                    .Append(field.Name)
                    .Append(" = ")
                    .Append(FormatConstant(field.GetRawConstantValue(), underlyingType))
                    .Append(FormatContractAttributes(field))
                    .AppendLine(",");
            }

            return;
        }

        var members = new List<string>();
        foreach (ConstructorInfo constructor in type.GetConstructors(DeclaredInstanceAndStatic).Where(IsVisible))
            members.Add(FormatConstructor(type, constructor));
        foreach (FieldInfo field in type.GetFields(DeclaredInstanceAndStatic).Where(IsVisible))
            members.Add(FormatField(field));
        foreach (PropertyInfo property in type.GetProperties(DeclaredInstanceAndStatic).Where(IsVisible))
            members.Add(FormatProperty(property));
        foreach (EventInfo @event in type.GetEvents(DeclaredInstanceAndStatic).Where(IsVisible))
            members.Add(FormatEvent(@event));
        foreach (MethodInfo method in type.GetMethods(DeclaredInstanceAndStatic).Where(IsVisible))
        {
            if (method.IsSpecialName && !method.Name.StartsWith("op_", StringComparison.Ordinal))
                continue;
            members.Add(FormatMethod(method));
        }

        foreach (string member in members.OrderBy(static line => line, StringComparer.Ordinal))
            output.Append("  ").AppendLine(member);
    }

    private static void AppendBaseContracts(StringBuilder output, Type type, bool includeBaseType)
    {
        var contracts = new List<string>();
        if (includeBaseType && type.BaseType is not null && type.BaseType != typeof(object))
            contracts.Add(FormatType(type.BaseType));

        contracts.AddRange(GetDirectInterfaces(type).Select(static contract => FormatType(contract)));
        if (contracts.Count != 0)
            output.Append(" : ").AppendJoin(", ", contracts.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    private static void AppendLayout(StringBuilder output, Type type)
    {
        StructLayoutAttribute? layout = type.StructLayoutAttribute;
        output.Append(" [layout: ")
            .Append(layout?.Value.ToString() ?? "Auto")
            .Append("; pack: ")
            .Append((layout?.Pack ?? 0).ToString(CultureInfo.InvariantCulture))
            .Append("; size: ")
            .Append((layout?.Size ?? 0).ToString(CultureInfo.InvariantCulture))
            .Append("; charset: ")
            .Append(layout?.CharSet.ToString() ?? "None")
            .Append(']');
    }

    private static string FormatConstructor(Type declaringType, ConstructorInfo constructor)
    {
        var output = new StringBuilder();
        AppendMemberPrefix(output, constructor);
        output.Append(GetSimpleTypeName(declaringType))
            .Append('(')
            .Append(FormatParameters(constructor.GetParameters(), isExtensionMethod: false))
            .Append(')')
            .Append(FormatContractAttributes(constructor))
            .Append(';');
        return output.ToString();
    }

    private static string FormatField(FieldInfo field)
    {
        var output = new StringBuilder();
        output.Append(Accessibility(field)).Append(' ');
        if (field.IsLiteral)
            output.Append("const ");
        else
        {
            if (field.IsStatic)
                output.Append("static ");
            if (field.IsInitOnly)
                output.Append("readonly ");
        }

        output.Append(FormatType(field.FieldType, CreateNullability(field)))
            .Append(' ')
            .Append(field.Name);
        if (field.IsLiteral)
            output.Append(" = ").Append(FormatConstant(field.GetRawConstantValue(), field.FieldType));
        output.Append(FormatContractAttributes(field)).Append(';');
        return output.ToString();
    }

    private static string FormatProperty(PropertyInfo property)
    {
        MethodInfo? getter = property.GetMethod;
        MethodInfo? setter = property.SetMethod;
        MethodBase visibleAccessor = MostVisible(getter, setter)
            ?? throw new InvalidOperationException($"Visible property '{property}' has no visible accessor.");

        var output = new StringBuilder();
        AppendMemberPrefix(output, visibleAccessor);
        AppendPolymorphism(output, (MethodInfo)visibleAccessor);
        output.Append(FormatType(property.PropertyType, CreateNullability(property))).Append(' ');

        ParameterInfo[] indexParameters = property.GetIndexParameters();
        if (indexParameters.Length == 0)
            output.Append(property.Name);
        else
            output.Append("this[").Append(FormatParameters(indexParameters, isExtensionMethod: false)).Append(']');

        output.Append(" { ");
        AppendAccessor(output, getter, visibleAccessor, "get", isInitOnly: false);
        AppendAccessor(output, setter, visibleAccessor, IsInitOnly(setter) ? "init" : "set", IsInitOnly(setter));
        output.Append('}')
            .Append(FormatContractAttributes(property))
            .Append(getter is null ? string.Empty : FormatScopedContractAttributes("return", getter.ReturnParameter));
        return output.ToString();
    }

    private static string FormatEvent(EventInfo @event)
    {
        MethodInfo? add = @event.AddMethod;
        MethodInfo? remove = @event.RemoveMethod;
        MethodBase visibleAccessor = MostVisible(add, remove)
            ?? throw new InvalidOperationException($"Visible event '{@event}' has no visible accessor.");

        var output = new StringBuilder();
        AppendMemberPrefix(output, visibleAccessor);
        AppendPolymorphism(output, (MethodInfo)visibleAccessor);
        output.Append("event ")
            .Append(FormatType(@event.EventHandlerType ?? typeof(void), CreateNullability(@event)))
            .Append(' ')
            .Append(@event.Name)
            .Append(" { ");
        AppendAccessor(output, add, visibleAccessor, "add", isInitOnly: false);
        AppendAccessor(output, remove, visibleAccessor, "remove", isInitOnly: false);
        output.Append('}').Append(FormatContractAttributes(@event));
        return output.ToString();
    }

    private static string FormatMethod(MethodInfo method)
    {
        var output = new StringBuilder();
        AppendMemberPrefix(output, method);
        AppendPolymorphism(output, method);

        output.Append(FormatReturnType(method)).Append(' ').Append(method.Name);
        Type[] genericArguments = method.GetGenericArguments();
        if (genericArguments.Length != 0)
            output.Append('<').AppendJoin(", ", genericArguments.Select(static argument => argument.Name)).Append('>');

        output.Append('(')
            .Append(FormatParameters(method.GetParameters(), method.IsDefined(typeof(ExtensionAttribute), false)))
            .Append(')')
            .Append(FormatGenericConstraints(genericArguments))
            .Append(FormatContractAttributes(method))
            .Append(FormatScopedContractAttributes("return", method.ReturnParameter))
            .Append(';');
        return output.ToString();
    }

    private static string FormatReturnType(MethodInfo method)
    {
        Type returnType = method.ReturnType;
        if (!returnType.IsByRef)
            return FormatType(returnType, CreateNullability(method.ReturnParameter));

        string prefix = HasReadOnlyModifier(method.ReturnParameter) ? "ref readonly " : "ref ";
        NullabilityInfo? returnNullability = CreateNullability(method.ReturnParameter);
        return prefix + FormatType(
            returnType.GetElementType()!,
            returnNullability?.ElementType ?? returnNullability);
    }

    private static string FormatParameters(IReadOnlyList<ParameterInfo> parameters, bool isExtensionMethod)
    {
        var formatted = new string[parameters.Count];
        for (int index = 0; index < parameters.Count; index++)
        {
            ParameterInfo parameter = parameters[index];
            var output = new StringBuilder();
            if (isExtensionMethod && index == 0)
                output.Append("this ");
            if (parameter.IsDefined(typeof(ParamArrayAttribute), false))
                output.Append("params ");

            Type parameterType = parameter.ParameterType;
            NullabilityInfo? nullability = CreateNullability(parameter);
            if (parameterType.IsByRef)
            {
                if (parameter.IsOut)
                    output.Append("out ");
                else if (parameter.IsIn || HasReadOnlyModifier(parameter))
                    output.Append("in ");
                else
                    output.Append("ref ");
                parameterType = parameterType.GetElementType()!;
                nullability = nullability?.ElementType ?? nullability;
            }

            output.Append(FormatType(parameterType, nullability))
                .Append(' ')
                .Append(parameter.Name ?? $"arg{parameter.Position.ToString(CultureInfo.InvariantCulture)}");

            if (parameter.HasDefaultValue)
                output.Append(" = ").Append(FormatConstant(parameter.DefaultValue, parameterType));

            string attributes = FormatContractAttributes(parameter);
            if (attributes.Length != 0)
                output.Append(attributes);
            formatted[index] = output.ToString();
        }

        return string.Join(", ", formatted);
    }

    private static string FormatType(Type type, NullabilityInfo? nullability = null)
    {
        if (type.IsByRef || type.IsPointer)
        {
            string suffix = type.IsPointer ? "*" : "&";
            return FormatType(type.GetElementType()!, nullability?.ElementType) + suffix;
        }

        if (type.IsArray)
        {
            string ranks = type.GetArrayRank() == 1
                ? "[]"
                : "[" + new string(',', type.GetArrayRank() - 1) + "]";
            return FormatType(type.GetElementType()!, nullability?.ElementType) + ranks + NullableSuffix(type, nullability);
        }

        if (type.IsGenericParameter)
            return type.Name + NullableSuffix(type, nullability);

        Type? nullableUnderlyingType = Nullable.GetUnderlyingType(type);
        if (nullableUnderlyingType is not null)
            return FormatType(nullableUnderlyingType, nullability?.GenericTypeArguments.FirstOrDefault()) + "?";

        if (TryGetKeyword(type, out string keyword))
            return keyword + NullableSuffix(type, nullability);

        string name = DisplayTypeIdentity(type);
        if (type.IsGenericType)
        {
            name = RemoveGenericArity(DisplayTypeIdentity(type.GetGenericTypeDefinition()).Replace('+', '.'));
            Type[] arguments = type.GetGenericArguments();
            name += "<" + string.Join(", ", arguments.Select((argument, index) =>
                FormatType(argument, nullability?.GenericTypeArguments.ElementAtOrDefault(index)))) + ">";
        }
        else
        {
            name = name.Replace('+', '.');
        }

        return name + NullableSuffix(type, nullability);
    }

    private static string FormatDeclaredTypeName(Type type)
    {
        string name = RemoveGenericArity(DisplayTypeIdentity(type).Replace('+', '.'));
        Type[] arguments = type.GetGenericArguments();
        return arguments.Length == 0
            ? name
            : name + "<" + string.Join(", ", arguments.Select(FormatGenericParameterDeclaration)) + ">";
    }

    private static string GetSimpleTypeName(Type type)
    {
        string name = type.Name;
        int marker = name.IndexOf('`');
        return marker < 0 ? name : name[..marker];
    }

    private static string RemoveGenericArity(string name)
    {
        var output = new StringBuilder(name.Length);
        for (int index = 0; index < name.Length; index++)
        {
            char character = name[index];
            if (character == '`')
            {
                while (index + 1 < name.Length && char.IsDigit(name[index + 1]))
                    index++;
                continue;
            }

            output.Append(character);
        }

        return output.ToString();
    }

    private static IReadOnlyList<Type> GetDirectInterfaces(Type type)
    {
        var candidates = new HashSet<Type>(type.GetInterfaces().Where(IsConsumerVisibleType));
        if (type.BaseType is not null)
            candidates.ExceptWith(type.BaseType.GetInterfaces().Where(IsConsumerVisibleType));

        Type[] snapshot = candidates.ToArray();
        foreach (Type candidate in snapshot)
        {
            foreach (Type other in snapshot)
            {
                if (candidate != other && other.GetInterfaces().Contains(candidate))
                {
                    candidates.Remove(candidate);
                    break;
                }
            }
        }

        return candidates.OrderBy(static contract => DisplayTypeIdentity(contract), StringComparer.Ordinal).ToArray();
    }

    private static string FormatGenericConstraints(IEnumerable<Type> genericArguments)
    {
        var output = new StringBuilder();
        foreach (Type argument in genericArguments.Where(static candidate => candidate.IsGenericParameter))
        {
            var constraints = new List<string>();
            GenericParameterAttributes attributes = argument.GenericParameterAttributes;
            GenericParameterAttributes special = attributes & GenericParameterAttributes.SpecialConstraintMask;
            if ((special & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
                constraints.Add("class");
            if ((special & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0)
            {
                bool isUnmanaged = argument.CustomAttributes.Any(static attribute =>
                    attribute.AttributeType.FullName == "System.Runtime.CompilerServices.IsUnmanagedAttribute");
                constraints.Add(isUnmanaged ? "unmanaged" : "struct");
            }

            constraints.AddRange(argument.GetGenericParameterConstraints()
                .Where(static constraint => constraint != typeof(ValueType))
                .Select(static constraint => FormatType(constraint)));

            if ((special & GenericParameterAttributes.DefaultConstructorConstraint) != 0 &&
                (special & GenericParameterAttributes.NotNullableValueTypeConstraint) == 0)
            {
                constraints.Add("new()");
            }

            if (constraints.Count != 0)
            {
                output.Append(" where ")
                    .Append(argument.Name)
                    .Append(" : ")
                    .AppendJoin(", ", constraints);
            }
        }

        return output.ToString();
    }

    private static string FormatGenericParameterDeclaration(Type argument)
    {
        GenericParameterAttributes variance =
            argument.GenericParameterAttributes & GenericParameterAttributes.VarianceMask;
        return variance switch
        {
            GenericParameterAttributes.Covariant => "out " + argument.Name,
            GenericParameterAttributes.Contravariant => "in " + argument.Name,
            _ => argument.Name,
        };
    }

    private static void AppendMemberPrefix(StringBuilder output, MethodBase method)
    {
        output.Append(Accessibility(method)).Append(' ');
        if (method.IsStatic)
            output.Append("static ");
    }

    private static void AppendAccessor(
        StringBuilder output,
        MethodInfo? accessor,
        MethodBase propertyAccessibility,
        string keyword,
        bool isInitOnly)
    {
        _ = isInitOnly;
        if (accessor is null || !IsVisible(accessor))
            return;

        string accessorAccessibility = Accessibility(accessor);
        string ownerAccessibility = Accessibility(propertyAccessibility);
        if (!string.Equals(accessorAccessibility, ownerAccessibility, StringComparison.Ordinal))
            output.Append(accessorAccessibility).Append(' ');
        output.Append(keyword).Append("; ");
    }

    private static MethodBase? MostVisible(params MethodInfo?[] accessors)
        => accessors
            .Where(static accessor => accessor is not null && IsVisible(accessor))
            .OrderByDescending(static accessor => AccessibilityRank(accessor!))
            .FirstOrDefault();

    private static int AccessibilityRank(MethodBase method)
        => method.IsPublic ? 5
            : method.IsFamilyOrAssembly ? 4
            : method.IsFamily ? 3
            : 0;

    private static string Accessibility(MethodBase method)
        => method.IsPublic ? "public"
            : method.IsFamilyOrAssembly ? "protected internal"
            : method.IsFamily ? "protected"
            : throw new InvalidOperationException($"Member '{method}' is not consumer-visible.");

    private static string Accessibility(FieldInfo field)
        => field.IsPublic ? "public"
            : field.IsFamilyOrAssembly ? "protected internal"
            : field.IsFamily ? "protected"
            : throw new InvalidOperationException($"Field '{field}' is not consumer-visible.");

    private static bool IsVisible(MethodBase method)
        => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

    private static bool IsVisible(FieldInfo field)
        => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly;

    private static bool IsVisible(PropertyInfo property)
        => IsVisibleAccessor(property.GetMethod) || IsVisibleAccessor(property.SetMethod);

    private static bool IsVisible(EventInfo @event)
        => IsVisibleAccessor(@event.AddMethod) || IsVisibleAccessor(@event.RemoveMethod);

    private static bool IsVisibleAccessor(MethodBase? method)
        => method is not null && IsVisible(method);

    private static bool IsConsumerVisibleType(Type type)
    {
        if (!type.IsNested)
            return type.IsPublic;

        bool nestedVisibility = type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamORAssem;
        return nestedVisibility && type.DeclaringType is not null && IsConsumerVisibleType(type.DeclaringType);
    }

    private static string TypeAccessibility(Type type)
    {
        if (!type.IsNested || type.IsNestedPublic)
            return "public";
        if (type.IsNestedFamORAssem)
            return "protected internal";
        if (type.IsNestedFamily)
            return "protected";
        throw new InvalidOperationException($"Type '{type}' is not consumer-visible.");
    }

    private static bool IsOverride(MethodInfo method)
        => method.IsVirtual && method.GetBaseDefinition() != method;

    private static void AppendPolymorphism(StringBuilder output, MethodInfo method)
    {
        if (method.IsAbstract)
        {
            output.Append("abstract ");
        }
        else if (IsOverride(method))
        {
            if (method.IsFinal)
                output.Append("sealed ");
            output.Append("override ");
        }
        else if (method.IsVirtual && !method.IsFinal)
        {
            output.Append("virtual ");
        }
    }

    private static bool IsInitOnly(MethodInfo? setter)
        => setter?.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)) == true;

    private static bool HasReadOnlyModifier(ParameterInfo parameter)
        => parameter.GetRequiredCustomModifiers().Any(static modifier =>
            modifier == typeof(InAttribute) || modifier == typeof(IsReadOnlyAttribute));

    private static bool IsDelegate(Type type)
        => type.BaseType == typeof(MulticastDelegate);

    private static string NullableSuffix(Type type, NullabilityInfo? nullability)
        => !type.IsValueType && nullability?.ReadState == NullabilityState.Nullable ? "?" : string.Empty;

    private static NullabilityInfo? CreateNullability(FieldInfo field)
    {
        try { return Nullability.Create(field); }
        catch (InvalidOperationException) { return null; }
    }

    private static NullabilityInfo? CreateNullability(PropertyInfo property)
    {
        try { return Nullability.Create(property); }
        catch (InvalidOperationException) { return null; }
    }

    private static NullabilityInfo? CreateNullability(EventInfo @event)
    {
        try { return Nullability.Create(@event); }
        catch (InvalidOperationException) { return null; }
    }

    private static NullabilityInfo? CreateNullability(ParameterInfo parameter)
    {
        try { return Nullability.Create(parameter); }
        catch (InvalidOperationException) { return null; }
    }

    private static string FormatConstant(object? value, Type declaredType)
    {
        if (value is null)
            return "null";
        if (value == Missing.Value)
            return "<missing>";
        if (value == DBNull.Value)
            return "<dbnull>";
        if (value is string text)
            return "\"" + Escape(text) + "\"";
        if (value is char character)
            return "'" + Escape(character.ToString()) + "'";
        if (value is bool boolean)
            return boolean ? "true" : "false";
        if (declaredType.IsEnum)
            return FormatType(declaredType) + "." + (Enum.GetName(declaredType, value) ?? Convert.ToString(value, CultureInfo.InvariantCulture));
        if (value is float single)
            return single.ToString("R", CultureInfo.InvariantCulture) + "F";
        if (value is double @double)
            return @double.ToString("R", CultureInfo.InvariantCulture) + "D";
        if (value is decimal @decimal)
            return @decimal.ToString(CultureInfo.InvariantCulture) + "M";
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.ToString() ?? "null";
    }

    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

    private static string FormatContractAttributes(MemberInfo member)
        => FormatContractAttributes(CustomAttributeData.GetCustomAttributes(member));

    private static string FormatContractAttributes(ParameterInfo parameter)
        => FormatContractAttributes(CustomAttributeData.GetCustomAttributes(parameter));

    private static string FormatContractAttributes(IEnumerable<CustomAttributeData> attributes)
    {
        IReadOnlyList<string> values = GetContractAttributes(attributes);
        return values.Count == 0
            ? string.Empty
            : " " + string.Join(" ", values);
    }

    private static string FormatScopedContractAttributes(string scope, ParameterInfo parameter)
    {
        IReadOnlyList<string> values = GetContractAttributes(CustomAttributeData.GetCustomAttributes(parameter));
        return values.Count == 0
            ? string.Empty
            : " [" + scope + ": " + string.Join(", ", values.Select(static value => value[1..^1])) + "]";
    }

    private static IReadOnlyList<string> GetContractAttributes(IEnumerable<CustomAttributeData> attributes)
    {
        var values = new List<string>();
        foreach (CustomAttributeData attribute in attributes)
        {
            string? name = attribute.AttributeType.FullName;
            switch (name)
            {
                case "System.ObsoleteAttribute":
                case "System.Diagnostics.CodeAnalysis.RequiresDynamicCodeAttribute":
                case "System.Diagnostics.CodeAnalysis.RequiresUnreferencedCodeAttribute":
                case "System.Diagnostics.CodeAnalysis.ExperimentalAttribute":
                case "System.Runtime.Versioning.SupportedOSPlatformAttribute":
                case "System.Runtime.Versioning.UnsupportedOSPlatformAttribute":
                case "System.Runtime.Versioning.ObsoletedOSPlatformAttribute":
                case "System.ComponentModel.EditorBrowsableAttribute":
                case "System.Diagnostics.CodeAnalysis.AllowNullAttribute":
                case "System.Diagnostics.CodeAnalysis.DisallowNullAttribute":
                case "System.Diagnostics.CodeAnalysis.DoesNotReturnAttribute":
                case "System.Diagnostics.CodeAnalysis.DoesNotReturnIfAttribute":
                case "System.Diagnostics.CodeAnalysis.MaybeNullAttribute":
                case "System.Diagnostics.CodeAnalysis.MaybeNullWhenAttribute":
                case "System.Diagnostics.CodeAnalysis.MemberNotNullAttribute":
                case "System.Diagnostics.CodeAnalysis.MemberNotNullWhenAttribute":
                case "System.Diagnostics.CodeAnalysis.NotNullAttribute":
                case "System.Diagnostics.CodeAnalysis.NotNullIfNotNullAttribute":
                case "System.Diagnostics.CodeAnalysis.NotNullWhenAttribute":
                case "System.Diagnostics.CodeAnalysis.StringSyntaxAttribute":
                case "System.Runtime.CompilerServices.CallerArgumentExpressionAttribute":
                case "System.Runtime.CompilerServices.CallerFilePathAttribute":
                case "System.Runtime.CompilerServices.CallerLineNumberAttribute":
                case "System.Runtime.CompilerServices.CallerMemberNameAttribute":
                case "System.Runtime.CompilerServices.DynamicAttribute":
                case "System.Runtime.CompilerServices.EnumeratorCancellationAttribute":
                case "System.Runtime.CompilerServices.ScopedRefAttribute":
                case "System.Runtime.CompilerServices.TupleElementNamesAttribute":
                case "System.Runtime.InteropServices.MarshalAsAttribute":
                case "System.Runtime.InteropServices.FieldOffsetAttribute":
                    values.Add(FormatAttribute(attribute));
                    break;
                case "System.FlagsAttribute":
                    values.Add("[Flags]");
                    break;
                case "System.Runtime.CompilerServices.RequiredMemberAttribute":
                    values.Add("[RequiredMember]");
                    break;
                case "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute":
                    values.Add("[SetsRequiredMembers]");
                    break;
            }
        }

        values.Sort(StringComparer.Ordinal);
        return values;
    }

    private static string FormatAttribute(CustomAttributeData attribute)
    {
        string name = attribute.AttributeType.Name;
        if (name.EndsWith("Attribute", StringComparison.Ordinal))
            name = name[..^"Attribute".Length];

        IEnumerable<string> constructorArguments = attribute.ConstructorArguments.Select(FormatAttributeArgument);
        IEnumerable<string> namedArguments = attribute.NamedArguments
            .OrderBy(static argument => argument.MemberName, StringComparer.Ordinal)
            .Select(static argument => argument.MemberName + " = " + FormatAttributeArgument(argument.TypedValue));
        string arguments = string.Join(", ", constructorArguments.Concat(namedArguments));
        return arguments.Length == 0 ? $"[{name}]" : $"[{name}({arguments})]";
    }

    private static string FormatAttributeArgument(CustomAttributeTypedArgument argument)
    {
        if (argument.Value is IReadOnlyCollection<CustomAttributeTypedArgument> values)
            return "[" + string.Join(", ", values.Select(FormatAttributeArgument)) + "]";
        return FormatConstant(argument.Value, argument.ArgumentType);
    }

    private static bool TryGetKeyword(Type type, out string keyword)
    {
        string? value = type == typeof(void) ? "void"
            : type == typeof(bool) ? "bool"
            : type == typeof(byte) ? "byte"
            : type == typeof(sbyte) ? "sbyte"
            : type == typeof(short) ? "short"
            : type == typeof(ushort) ? "ushort"
            : type == typeof(int) ? "int"
            : type == typeof(uint) ? "uint"
            : type == typeof(long) ? "long"
            : type == typeof(ulong) ? "ulong"
            : type == typeof(nint) ? "nint"
            : type == typeof(nuint) ? "nuint"
            : type == typeof(float) ? "float"
            : type == typeof(double) ? "double"
            : type == typeof(decimal) ? "decimal"
            : type == typeof(char) ? "char"
            : type == typeof(string) ? "string"
            : type == typeof(object) ? "object"
            : null;
        keyword = value ?? string.Empty;
        return value is not null;
    }

    private static string DisplayTypeIdentity(Type type)
        => type.FullName ?? type.Name;

    private static string TypeSortKey(Type type)
        => DisplayTypeIdentity(type);
}
