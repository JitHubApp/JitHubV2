[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $AssemblyPath,

    [Parameter(Mandatory = $true)]
    [string] $XmlPath
)

$ErrorActionPreference = 'Stop'

$metadataHelperTypeName = 'MarkdownRenderer.PackageTools.PublicXmlDocumentationMetadata'
$metadataHelperType = [System.Management.Automation.PSTypeName]::new($metadataHelperTypeName).Type
if ($null -eq $metadataHelperType) {
    # The helper only decodes ECMA-335 metadata signatures into compiler XML-doc
    # IDs. It never loads or reflects over the target assembly.
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;

namespace MarkdownRenderer.PackageTools
{
    public sealed class PublicXmlDocumentationMetadata : ISignatureTypeProvider<string, object>
    {
        public static int GetTypeDefinitionToken(TypeDefinitionHandle handle) =>
            MetadataTokens.GetToken(handle);

        public static bool IsVisibleMethod(MethodAttributes attributes) =>
            IsVisibleAccess((int)attributes);

        public static bool IsVisibleField(FieldAttributes attributes) =>
            IsVisibleAccess((int)attributes);

        public static bool IsVisibleProperty(MetadataReader reader, PropertyDefinitionHandle handle)
        {
            PropertyAccessors accessors = reader.GetPropertyDefinition(handle).GetAccessors();
            return IsVisibleAccessor(reader, accessors.Getter) ||
                IsVisibleAccessor(reader, accessors.Setter) ||
                IsVisibleAccessorList(reader, accessors.Others);
        }

        public static bool IsVisibleEvent(MetadataReader reader, EventDefinitionHandle handle)
        {
            EventAccessors accessors = reader.GetEventDefinition(handle).GetAccessors();
            return IsVisibleAccessor(reader, accessors.Adder) ||
                IsVisibleAccessor(reader, accessors.Remover) ||
                IsVisibleAccessor(reader, accessors.Raiser) ||
                IsVisibleAccessorList(reader, accessors.Others);
        }

        public static string GetMethodDocumentationId(
            MetadataReader reader,
            string declaringType,
            MethodDefinitionHandle handle)
        {
            MethodDefinition method = reader.GetMethodDefinition(handle);
            string metadataName = reader.GetString(method.Name);
            string name = metadataName switch
            {
                ".ctor" => "#ctor",
                ".cctor" => "#cctor",
                _ => EncodeMemberName(metadataName),
            };

            int genericArity = method.GetGenericParameters().Count;
            if (genericArity != 0)
                name += "``" + genericArity.ToString(System.Globalization.CultureInfo.InvariantCulture);

            MethodSignature<string> signature = method.DecodeSignature(Instance, genericContext: null!);
            string id = "M:" + declaringType + "." + name;
            if (signature.ParameterTypes.Length != 0)
                id += "(" + string.Join(",", signature.ParameterTypes) + ")";
            if (metadataName is "op_Implicit" or "op_Explicit" or "op_CheckedExplicit")
                id += "~" + signature.ReturnType;
            return id;
        }

        public static string GetPropertyDocumentationId(
            MetadataReader reader,
            string declaringType,
            PropertyDefinitionHandle handle)
        {
            PropertyDefinition property = reader.GetPropertyDefinition(handle);
            string name = EncodeMemberName(reader.GetString(property.Name));
            MethodSignature<string> signature = property.DecodeSignature(Instance, genericContext: null!);
            string id = "P:" + declaringType + "." + name;
            if (signature.ParameterTypes.Length != 0)
                id += "(" + string.Join(",", signature.ParameterTypes) + ")";
            return id;
        }

        public static string GetFieldDocumentationId(
            MetadataReader reader,
            string declaringType,
            FieldDefinitionHandle handle) =>
            "F:" + declaringType + "." + reader.GetString(reader.GetFieldDefinition(handle).Name);

        public static string GetEventDocumentationId(
            MetadataReader reader,
            string declaringType,
            EventDefinitionHandle handle) =>
            "E:" + declaringType + "." + EncodeMemberName(reader.GetString(reader.GetEventDefinition(handle).Name));

        private static string EncodeMemberName(string metadataName)
        {
            int interfaceSeparator = metadataName.IndexOf("::", StringComparison.Ordinal);
            int start = interfaceSeparator < 0 ? 0 : interfaceSeparator + 2;
            return metadataName.Substring(start).Replace('.', '#').Replace('<', '{').Replace('>', '}');
        }

        private static bool IsVisibleAccess(int attributes) =>
            (attributes & 7) is 4 or 5 or 6;

        private static bool IsVisibleAccessor(MetadataReader reader, MethodDefinitionHandle handle) =>
            !handle.IsNil && IsVisibleMethod(reader.GetMethodDefinition(handle).Attributes);

        private static bool IsVisibleAccessorList(
            MetadataReader reader,
            ImmutableArray<MethodDefinitionHandle> handles)
        {
            foreach (MethodDefinitionHandle handle in handles)
            {
                if (IsVisibleAccessor(reader, handle))
                    return true;
            }
            return false;
        }

        private static readonly PublicXmlDocumentationMetadata Instance = new();

        public string GetArrayType(string elementType, ArrayShape shape)
        {
            if (shape.Rank <= 0)
                throw new BadImageFormatException("An array signature has a non-positive rank.");

            var suffix = new StringBuilder("[0:");
            for (int dimension = 1; dimension < shape.Rank; dimension++)
            {
                suffix.Append(",0:");
            }
            suffix.Append(']');
            return elementType + suffix;
        }

        public string GetByReferenceType(string elementType) => elementType + "@";

        public string GetFunctionPointerType(MethodSignature<string> signature)
        {
            string parameters = signature.ParameterTypes.Length == 0
                ? string.Empty
                : "(" + string.Join(",", signature.ParameterTypes) + ")";
            return "=FUNC:" + signature.ReturnType + parameters;
        }

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        {
            string[] typeSegments = genericType.Split('.');
            var result = new StringBuilder(genericType.Length + typeArguments.Sum(static argument => argument.Length + 1));
            int argumentIndex = 0;

            for (int segmentIndex = 0; segmentIndex < typeSegments.Length; segmentIndex++)
            {
                if (segmentIndex != 0)
                    result.Append('.');

                string segment = typeSegments[segmentIndex];
                int arityMarker = segment.LastIndexOf('`');
                if (arityMarker < 0 ||
                    !int.TryParse(
                        segment.AsSpan(arityMarker + 1),
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out int arity))
                {
                    result.Append(segment);
                    continue;
                }

                if (arity <= 0 || argumentIndex + arity > typeArguments.Length)
                    throw new BadImageFormatException("A generic type signature has inconsistent arity.");

                result.Append(segment.AsSpan(0, arityMarker));
                result.Append('{');
                for (int index = 0; index < arity; index++)
                {
                    if (index != 0)
                        result.Append(',');
                    result.Append(typeArguments[argumentIndex++]);
                }
                result.Append('}');
            }

            if (argumentIndex != typeArguments.Length)
                throw new BadImageFormatException("A generic type signature has arguments without matching type arity.");
            return result.ToString();
        }

        public string GetGenericMethodParameter(object genericContext, int index) =>
            "``" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public string GetGenericTypeParameter(object genericContext, int index) =>
            "`" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetPinnedType(string elementType) => elementType;

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Void => "System.Void",
            PrimitiveTypeCode.Boolean => "System.Boolean",
            PrimitiveTypeCode.Char => "System.Char",
            PrimitiveTypeCode.SByte => "System.SByte",
            PrimitiveTypeCode.Byte => "System.Byte",
            PrimitiveTypeCode.Int16 => "System.Int16",
            PrimitiveTypeCode.UInt16 => "System.UInt16",
            PrimitiveTypeCode.Int32 => "System.Int32",
            PrimitiveTypeCode.UInt32 => "System.UInt32",
            PrimitiveTypeCode.Int64 => "System.Int64",
            PrimitiveTypeCode.UInt64 => "System.UInt64",
            PrimitiveTypeCode.Single => "System.Single",
            PrimitiveTypeCode.Double => "System.Double",
            PrimitiveTypeCode.String => "System.String",
            PrimitiveTypeCode.TypedReference => "System.TypedReference",
            PrimitiveTypeCode.IntPtr => "System.IntPtr",
            PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
            PrimitiveTypeCode.Object => "System.Object",
            _ => throw new BadImageFormatException("An unknown primitive signature type was encountered."),
        };

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            TypeDefinition definition = reader.GetTypeDefinition(handle);
            string name = reader.GetString(definition.Name);
            TypeDefinitionHandle declaringType = definition.GetDeclaringType();
            if (!declaringType.IsNil)
                return GetTypeFromDefinition(reader, declaringType, rawTypeKind) + "." + name;

            string ns = reader.GetString(definition.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            TypeReference reference = reader.GetTypeReference(handle);
            string name = reader.GetString(reference.Name);
            if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
                return GetTypeFromReference(reader, (TypeReferenceHandle)reference.ResolutionScope, rawTypeKind) + "." + name;

            string ns = reader.GetString(reference.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public string GetTypeFromSpecification(
            MetadataReader reader,
            object genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}
'@ -ErrorAction Stop
}

if (-not (Test-Path -LiteralPath $AssemblyPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $XmlPath -PathType Leaf)) {
    throw 'The compiled assembly and XML documentation must exist before filtering.'
}

$assemblyStream = [System.IO.File]::OpenRead($AssemblyPath)
try {
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($assemblyStream)
    try {
        $reader = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $typesByHandle = [System.Collections.Generic.Dictionary[int, object]]::new()
        foreach ($handle in $reader.TypeDefinitions) {
            $definition = $reader.GetTypeDefinition($handle)
            $typesByHandle[[MarkdownRenderer.PackageTools.PublicXmlDocumentationMetadata]::GetTypeDefinitionToken($handle)] = [pscustomobject]@{
                Handle = $handle
                Definition = $definition
                Name = $reader.GetString($definition.Name)
                Namespace = $reader.GetString($definition.Namespace)
                Visibility = ([int] $definition.Attributes -band 7)
            }
        }

        function Get-TypeInfo($handle) {
            $entry = $typesByHandle[[MarkdownRenderer.PackageTools.PublicXmlDocumentationMetadata]::GetTypeDefinitionToken($handle)]
            if ($null -eq $entry) {
                throw 'A type definition was missing from the metadata index.'
            }
            if ($null -ne $entry.PSObject.Properties['FullName']) {
                return $entry
            }

            $declaring = $entry.Definition.GetDeclaringType()
            if ($declaring.IsNil) {
                $entry | Add-Member -NotePropertyName FullName -NotePropertyValue $(
                    if ($entry.Namespace) { "$($entry.Namespace).$($entry.Name)" }
                    else { $entry.Name })
                $entry | Add-Member -NotePropertyName Visible -NotePropertyValue (
                    $entry.Visibility -eq 1)
            }
            else {
                $parent = Get-TypeInfo $declaring
                $entry | Add-Member -NotePropertyName FullName -NotePropertyValue (
                    "$($parent.FullName).$($entry.Name)")
                $entry | Add-Member -NotePropertyName Visible -NotePropertyValue (
                    $parent.Visible -and $entry.Visibility -in 2, 4, 7)
            }
            return $entry
        }

        $types = @($reader.TypeDefinitions | ForEach-Object { Get-TypeInfo $_ })
        $memberVisibility = [System.Collections.Generic.Dictionary[string, bool]]::new(
            [System.StringComparer]::Ordinal)

        function Add-MemberVisibility([string] $id, [bool] $visible) {
            if ($memberVisibility.ContainsKey($id)) {
                # XML documentation IDs intentionally omit most return types and
                # calling-convention details. If malformed or hand-authored IL
                # maps multiple definitions to one ID, keep its documentation
                # whenever any matching definition is externally visible.
                $memberVisibility[$id] = $memberVisibility[$id] -or $visible
                return
            }
            $memberVisibility.Add($id, $visible)
        }

        foreach ($type in $types) {
            Add-MemberVisibility "T:$($type.FullName)" $type.Visible

            foreach ($fieldHandle in $type.Definition.GetFields()) {
                $field = $reader.GetFieldDefinition($fieldHandle)
                $id = [MarkdownRenderer.PackageTools.PublicXmlDocumentationMetadata]::GetFieldDocumentationId(
                    $reader, $type.FullName, $fieldHandle)
                Add-MemberVisibility $id ($type.Visible -and
                    [MarkdownRenderer.PackageTools.PublicXmlDocumentationMetadata]::IsVisibleField($field.Attributes))
            }

            foreach ($methodHandle in $type.Definition.GetMethods()) {
                $method = $reader.GetMethodDefinition($methodHandle)
                $id = [MarkdownRenderer.PackageTools.PublicXmlDocumentationMetadata]::GetMethodDocumentationId(
                    $reader, $type.FullName, $methodHandle)
                Add-MemberVisibility $id ($type.Visible -and
                    [MarkdownRenderer.PackageTools.PublicXmlDocumentationMetadata]::IsVisibleMethod($method.Attributes))
            }

            foreach ($propertyHandle in $type.Definition.GetProperties()) {
                $id = [MarkdownRenderer.PackageTools.PublicXmlDocumentationMetadata]::GetPropertyDocumentationId(
                    $reader, $type.FullName, $propertyHandle)
                Add-MemberVisibility $id ($type.Visible -and
                    [MarkdownRenderer.PackageTools.PublicXmlDocumentationMetadata]::IsVisibleProperty($reader, $propertyHandle))
            }

            foreach ($eventHandle in $type.Definition.GetEvents()) {
                $id = [MarkdownRenderer.PackageTools.PublicXmlDocumentationMetadata]::GetEventDocumentationId(
                    $reader, $type.FullName, $eventHandle)
                Add-MemberVisibility $id ($type.Visible -and
                    [MarkdownRenderer.PackageTools.PublicXmlDocumentationMetadata]::IsVisibleEvent($reader, $eventHandle))
            }
        }
    }
    finally {
        $pe.Dispose()
    }
}
finally {
    $assemblyStream.Dispose()
}

$document = [System.Xml.XmlDocument]::new()
$document.XmlResolver = $null
$document.Load($XmlPath)
$members = @($document.SelectNodes('/doc/members/member'))
$removed = 0
foreach ($member in $members) {
    $id = [string] $member.GetAttribute('name')
    if ($id.Length -lt 3 -or $id[1] -ne ':') {
        throw "Unexpected XML documentation member ID '$id'."
    }

    if (-not $memberVisibility.ContainsKey($id)) {
        throw "XML documentation member '$id' has no exact metadata definition."
    }
    if (-not $memberVisibility[$id]) {
        [void] $member.ParentNode.RemoveChild($member)
        $removed++
    }
}

$temporaryPath = "$XmlPath.public.$PID.tmp"
try {
    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Indent = $false
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $settings.CloseOutput = $true
    $writer = [System.Xml.XmlWriter]::Create($temporaryPath, $settings)
    try {
        $document.Save($writer)
    }
    finally {
        $writer.Dispose()
    }

    Move-Item -LiteralPath $temporaryPath -Destination $XmlPath -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}

Write-Host "Packaged public XML documentation: retained $($members.Count - $removed) of $($members.Count) members."
