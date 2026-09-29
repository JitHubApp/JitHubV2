using System.Reflection;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Html;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Math;
using MarkdownRenderer.Mermaid;
using MarkdownRenderer.Svg.Resvg;
using MarkdownRenderer.SyntaxHighlighting.TextMate;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.All;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common;
using MarkdownRenderer.Theming;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class StablePublicApiTests
{
    private static readonly string[] ForbiddenTypePrefixes =
    [
        "Markdig.",
        "Microsoft.Graphics.Canvas.",
        "CSharpMath.",
        "TextMateSharp.",
        "Merman.",
    ];

    [Fact]
    public void ExportedContracts_DoNotExposeRendererImplementationTypes()
    {
        Assembly[] assemblies =
        [
            typeof(MarkdownEngine).Assembly,
            typeof(MarkdownScrollView).Assembly,
            typeof(GfmExtensions).Assembly,
            typeof(GitHubReadmeExtensions).Assembly,
            typeof(SafeHtmlFeature).Assembly,
            typeof(MathFeature).Assembly,
            typeof(MermaidRenderer).Assembly,
            typeof(ResvgMarkdownSvgRenderer).Assembly,
            typeof(TextMateSyntaxHighlightingExtensions).Assembly,
            typeof(CommonTextMateGrammarProvider).Assembly,
            typeof(AllTextMateGrammarProvider).Assembly,
        ];

        var findings = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Assembly assembly in assemblies.Distinct())
        {
            foreach (Type type in assembly.GetExportedTypes())
            {
                Inspect(type.BaseType, $"{type.FullName} base", findings);
                foreach (Type contract in type.GetInterfaces())
                    Inspect(contract, $"{type.FullName} interface", findings);

                const BindingFlags declaredPublic =
                    BindingFlags.Public |
                    BindingFlags.Instance |
                    BindingFlags.Static |
                    BindingFlags.DeclaredOnly;

                foreach (FieldInfo field in type.GetFields(declaredPublic))
                    Inspect(field.FieldType, $"{type.FullName}.{field.Name}", findings);
                foreach (PropertyInfo property in type.GetProperties(declaredPublic))
                {
                    Inspect(property.PropertyType, $"{type.FullName}.{property.Name}", findings);
                    foreach (ParameterInfo parameter in property.GetIndexParameters())
                        Inspect(parameter.ParameterType, $"{type.FullName}.{property.Name} index", findings);
                }
                foreach (EventInfo @event in type.GetEvents(declaredPublic))
                    Inspect(@event.EventHandlerType, $"{type.FullName}.{@event.Name}", findings);
                foreach (ConstructorInfo constructor in type.GetConstructors(declaredPublic))
                    InspectParameters(constructor.GetParameters(), $"{type.FullName} constructor", findings);
                foreach (MethodInfo method in type.GetMethods(declaredPublic))
                {
                    Inspect(method.ReturnType, $"{type.FullName}.{method.Name} return", findings);
                    InspectParameters(method.GetParameters(), $"{type.FullName}.{method.Name}", findings);
                    foreach (Type argument in method.GetGenericArguments())
                    {
                        foreach (Type constraint in argument.GetGenericParameterConstraints())
                            Inspect(constraint, $"{type.FullName}.{method.Name} constraint", findings);
                    }
                }
            }
        }

        Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));
    }

    [Fact]
    public void DeclarativeExtensionContracts_AreRendererOwnedAndPresentInBuiltCoreAssembly()
    {
        Assembly core = typeof(MarkdownEngine).Assembly;

        Assert.Same(core, typeof(IMarkdownExtension).Assembly);
        Assert.Same(core, typeof(MarkdownExtensionBuilder).Assembly);
        Assert.Same(core, typeof(MarkdownExtensionSet).Assembly);
        Assert.NotNull(typeof(MarkdownExtensionSet).GetProperty(
            nameof(MarkdownExtensionSet.Empty),
            BindingFlags.Public | BindingFlags.Static));
        Assert.DoesNotContain(core.GetExportedTypes(), type =>
            type.FullName?.Contains("BlockBox", StringComparison.Ordinal) == true ||
            type.FullName?.Contains("MarkdownLayoutContext", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void StyleSheet_UsesDefensiveOrderedSnapshotsInBuiltAssembly()
    {
        var rule = new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.Body),
            new ElementStyleOverride());
        var source = new List<MarkdownStyleRule> { rule };
        var original = new MarkdownStyleSheet(source);

        source.Clear();
        MarkdownStyleSheet appended = original.WithRule(new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.Link),
            new ElementStyleOverride()));

        Assert.Single(original.Rules);
        Assert.Equal(2, appended.Rules.Count);
        Assert.Null(typeof(MarkdownStyleSheet).GetProperty(nameof(MarkdownStyleSheet.Rules))!.SetMethod);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<MarkdownStyleRule>)original.Rules).Add(rule));
    }

    [Fact]
    public void ResourceKeyConstants_AreStableAndUniqueInBuiltAssembly()
    {
        string[] values = typeof(MarkdownResourceKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && !field.IsInitOnly && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        Assert.NotEmpty(values);
        Assert.Equal(values.Length, values.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(MarkdownResourceKeys.DocumentSurfaceBrush, values);
        Assert.Contains(MarkdownResourceKeys.SelectionBackgroundBrush, values);
        Assert.Contains(MarkdownResourceKeys.FocusVisualBrush, values);
    }

    [Fact]
    public void StringProviderKeys_CoverTargetCommandsAndAccessibleMediaStates()
    {
        string[] values = typeof(MarkdownStringKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && !field.IsInitOnly && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        Assert.Equal(values.Length, values.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(MarkdownStringKeys.CopyLink, values);
        Assert.Contains(MarkdownStringKeys.CopyImage, values);
        Assert.Contains(MarkdownStringKeys.CopyCode, values);
        Assert.Contains(MarkdownStringKeys.CopyTable, values);
        Assert.Contains(MarkdownStringKeys.ImageLoading, values);
        Assert.Contains(MarkdownStringKeys.ImageError, values);
        Assert.Contains(MarkdownStringKeys.DiagramName, values);
        Assert.Contains(MarkdownStringKeys.SelectionStartHandle, values);
        Assert.Contains(MarkdownStringKeys.SelectionEndHandle, values);
        Assert.Contains(MarkdownStringKeys.SelectionHandleHelp, values);
    }

    [Fact]
    public void CompatibilityFacadeAndLegacyCustomizationContractsRemainInBuiltWinUiAssembly()
    {
        Type compatibilityFacade = typeof(MarkdownScrollView).BaseType!;

        Assert.Equal("MarkdownRenderer.Controls.MarkdownRendererControl", compatibilityFacade.FullName);
        Assert.NotNull(compatibilityFacade.GetCustomAttribute<ObsoleteAttribute>());
        Assert.True(compatibilityFacade.IsAssignableFrom(typeof(MarkdownScrollView)));
        const string invalidateThemeResourcesName = "InvalidateThemeResources";
        MethodInfo? invalidateThemeResources = compatibilityFacade.GetMethod(
            invalidateThemeResourcesName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.NotNull(invalidateThemeResources);
        Assert.Equal(typeof(void), invalidateThemeResources.ReturnType);
        Assert.Empty(invalidateThemeResources.GetParameters());
        Assert.NotNull(typeof(MarkdownScrollView).GetMethod(
            invalidateThemeResourcesName));
        Assert.NotNull(typeof(MarkdownDocumentView).GetMethod(
            invalidateThemeResourcesName));
        Assert.Equal("Body", MarkdownStyleRole.FromElementKey("Body").Name);
        Assert.Equal("Body", MarkdownStyleRole.Body.Name);
        Assert.NotNull(typeof(MarkdownRenderer.Parsing.MarkdownExtensionRegistry));
        Assert.NotNull(typeof(MarkdownTheme));
    }

    [Fact]
    public void ThemeOverrideStorage_PreservesArbitraryLegacyKeysThroughBothInsertionPaths()
    {
        int indexedChangedCount = 0;
        int addedChangedCount = 0;
        var indexed = new MarkdownTheme.OverrideCollection(() => indexedChangedCount++);
        var added = new MarkdownTheme.OverrideCollection(() => addedChangedCount++);
        string[] legacyKeys =
        [
            "Document",
            "Selection",
            " Extension.Widget ",
            MarkdownElementKeys.Class(new string('x', 129)),
            "Bad\u0001Role",
        ];

        foreach (string key in legacyKeys)
        {
            indexed[key] = new ElementStyleOverride { FontSize = 17 };
            added.Add(key, new ElementStyleOverride { FontSize = 18 });
        }

        Assert.Equal(legacyKeys.Length, indexed.Count);
        Assert.Equal(legacyKeys.Length, added.Count);
        Assert.Equal(legacyKeys.Length, indexedChangedCount);
        Assert.Equal(legacyKeys.Length, addedChangedCount);
        foreach (string key in legacyKeys)
        {
            Assert.Equal(17f, indexed[key].FontSize);
            Assert.Equal(18f, added[key].FontSize);
        }
    }

    private static void InspectParameters(
        IEnumerable<ParameterInfo> parameters,
        string owner,
        ISet<string> findings)
    {
        foreach (ParameterInfo parameter in parameters)
            Inspect(parameter.ParameterType, $"{owner} parameter '{parameter.Name}'", findings);
    }

    private static void Inspect(Type? type, string owner, ISet<string> findings)
    {
        if (type is null)
            return;

        if (type.HasElementType)
            Inspect(type.GetElementType(), owner, findings);
        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
                Inspect(argument, owner, findings);
        }

        string identity = type.FullName ?? type.Name;
        if (ForbiddenTypePrefixes.Any(prefix => identity.StartsWith(prefix, StringComparison.Ordinal)))
            findings.Add($"{owner} exposes {identity}");
    }
}
