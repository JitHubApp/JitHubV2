using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.All;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Engine;
using MarkdownRenderer.Theming;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;
using Xunit;

namespace MarkdownRenderer.SyntaxHighlighting.TextMate.Tests;

public sealed class TextMateGrammarPackTests
{
    [Fact]
    public void Highlighter_ImplementsStableHostContract()
    {
        using var provider = new CommonTextMateGrammarProvider();
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(provider);

        Assert.IsAssignableFrom<ICodeHighlighter>(highlighter);
    }

    [Theory]
    [InlineData("csharp", "public sealed class Demo { }")]
    [InlineData("ts", "const value: string = \"ok\";")]
    [InlineData("python", "def hello():\n    return \"ok\"")]
    [InlineData("powershell", "Write-Host \"ok\"")]
    [InlineData("json", "{ \"ok\": true }")]
    [InlineData("markdown", "# Heading\n\n`code`")]
    [InlineData("css", ".demo { color: red; }")]
    [InlineData("diff", "diff --git a/demo b/demo\n--- a/demo\n+++ b/demo\n@@ -1 +1 @@\n-old\n+new")]
    [InlineData("xsl", "<xsl:stylesheet version=\"1.0\" />")]
    [InlineData("c", "int main(void) { return 0; }")]
    [InlineData("cpp", "namespace demo { class Widget {}; }")]
    [InlineData("cuda-cpp", "__global__ void kernel() {}")]
    [InlineData("dockerfile", "FROM scratch")]
    [InlineData("fsharp", "let value = 42")]
    [InlineData("go", "package main\nfunc main() {}")]
    [InlineData("hlsl", "float4 main() : SV_Target { return 1; }")]
    [InlineData("java", "public final class Demo { }")]
    [InlineData("lua", "local value = true")]
    [InlineData("objectivec", "@interface Demo : NSObject\n@end")]
    [InlineData("objectivecpp", "@interface Demo : NSObject\n@end")]
    [InlineData("php", "<?php echo \"ok\"; ?>")]
    [InlineData("ruby", "class Demo\nend")]
    [InlineData("rust", "fn main() { let value = true; }")]
    [InlineData("shaderlab", "Shader \"Demo\" { SubShader {} }")]
    [InlineData("sql", "SELECT * FROM demo;")]
    [InlineData("swift", "struct Demo { let value: Int }")]
    [InlineData("vb", "Dim value As Integer")]
    public async Task CommonProvider_TokenizesCuratedLanguages(string language, string code)
    {
        using var provider = new CommonTextMateGrammarProvider();
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(provider);

        var result = await highlighter.HighlightAsync(new CodeBlockHighlightRequest(
            language,
            code,
            MarkdownThemeVariant.Dark), CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotEmpty(result.Spans);
    }

    [Fact]
    public async Task CommonProvider_RejectsLanguageOutsideCuratedSet()
    {
        using var provider = new CommonTextMateGrammarProvider();
        var result = await provider.HighlightAsync(
            new TextMateGrammarHighlightRequest(
                "pascal",
                "program Demo; begin end.",
                TextMateGrammarThemeVariant.Dark),
            CancellationToken.None);

        Assert.Same(TextMateGrammarHighlightResult.Empty, result);
    }

    [Fact]
    public async Task CommonProvider_ClampsTextMateEndOfLineSentinelsToExactSource()
    {
        const string code = "diff --git a/demo b/demo\n-old\n+new";
        using var provider = new CommonTextMateGrammarProvider();

        TextMateGrammarHighlightResult? result = await provider.HighlightAsync(
            new TextMateGrammarHighlightRequest(
                "diff",
                code,
                TextMateGrammarThemeVariant.Dark),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotEmpty(result.Spans);
        Assert.All(result.Spans, span =>
            Assert.InRange(span.Start + span.Length, 1, code.Length));
    }

    [Fact]
    public async Task AllProvider_TokenizesLanguageOutsideCommonSet()
    {
        using var provider = new AllTextMateGrammarProvider();
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(provider);

        var result = await highlighter.HighlightAsync(new CodeBlockHighlightRequest(
            "pascal",
            "program Demo; begin writeln('hello'); end.",
            MarkdownThemeVariant.Dark), CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotEmpty(result.Spans);
    }

    [Fact]
    public async Task AlreadyCanceledRequest_ReturnsNull()
    {
        using var provider = new CommonTextMateGrammarProvider();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await provider.HighlightAsync(
            new TextMateGrammarHighlightRequest(
                "csharp",
                "public sealed class Demo { }",
                TextMateGrammarThemeVariant.Dark),
            cancellation.Token);

        Assert.Null(result);
    }

    [Fact]
    public void LeanEngine_ContainsNoGrammarOrThemeResources()
    {
        string[] resources = typeof(TextMateCodeBlockSyntaxHighlighter)
            .Assembly
            .GetManifestResourceNames();

        Assert.DoesNotContain(resources, name =>
            name.Contains("grammar", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("theme", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(TextMateCodeBlockSyntaxHighlighter).Assembly.GetReferencedAssemblies(),
            reference =>
                reference.Name?.Equals("TextMateSharp", StringComparison.OrdinalIgnoreCase) == true ||
                reference.Name?.Equals("TextMateSharp.Grammars", StringComparison.OrdinalIgnoreCase) == true ||
                reference.Name?.Equals("Onigwrap", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void CommonPack_EmbedsOnlyCuratedGrammarPayload()
    {
        Assembly assembly = typeof(CommonTextMateGrammarProvider).Assembly;
        string[] resources = assembly.GetManifestResourceNames();

        Assert.Equal(44, resources.Count(name =>
            name.StartsWith("TextMateSharp.Grammars.Resources.", StringComparison.Ordinal)));
        Assert.Contains(
            "TextMateSharp.Grammars.Resources.Grammars.csharp.syntaxes.csharp.tmLanguage.json",
            resources);
        Assert.DoesNotContain(resources, name =>
            name.Contains("Grammars.pascal.", StringComparison.Ordinal));
        Assert.DoesNotContain(
            assembly.GetReferencedAssemblies(),
            reference => reference.Name?.Equals(
                "TextMateSharp.Grammars",
                StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void AllPack_ResolvesTheCompletePinnedUpstreamResourceCorpus()
    {
        Assembly grammarAssembly = typeof(TextMateSharp.Grammars.RegistryOptions).Assembly;
        string[] resources = grammarAssembly.GetManifestResourceNames();

        Assert.Equal(317, resources.Length);
        Assert.Contains(
            "TextMateSharp.Grammars.Resources.Grammars.rust.syntaxes.rust.tmLanguage.json",
            resources);
        Assert.Contains("TextMateSharp.Grammars.Resources.Themes.dark_vs.json", resources);
        Assert.Contains("TextMateSharp.Grammars.Resources.Themes.light_vs.json", resources);
    }

    [Fact]
    public void StablePublicSurface_ExposesNoThirdPartyTypes()
    {
        Assembly[] assemblies =
        [
            typeof(TextMateCodeBlockSyntaxHighlighter).Assembly,
            typeof(CommonTextMateGrammarProvider).Assembly,
            typeof(AllTextMateGrammarProvider).Assembly,
        ];

        foreach (Assembly assembly in assemblies)
        {
            foreach (Type type in assembly.GetExportedTypes())
            {
                AssertStableType(type.BaseType);
                foreach (Type contract in type.GetInterfaces())
                    AssertStableType(contract);
                foreach (Type genericArgument in type.GetGenericArguments())
                {
                    foreach (Type constraint in genericArgument.GetGenericParameterConstraints())
                        AssertStableType(constraint);
                }

                foreach (ConstructorInfo constructor in type.GetConstructors())
                {
                    foreach (ParameterInfo parameter in constructor.GetParameters())
                        AssertStableType(parameter.ParameterType);
                }

                foreach (MethodInfo method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    AssertStableType(method.ReturnType);
                    foreach (ParameterInfo parameter in method.GetParameters())
                        AssertStableType(parameter.ParameterType);
                    foreach (Type genericArgument in method.GetGenericArguments())
                    {
                        foreach (Type constraint in genericArgument.GetGenericParameterConstraints())
                            AssertStableType(constraint);
                    }
                }

                foreach (PropertyInfo property in type.GetProperties(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    AssertStableType(property.PropertyType);
                }

                foreach (FieldInfo field in type.GetFields(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    AssertStableType(field.FieldType);
                }

                foreach (EventInfo eventInfo in type.GetEvents(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    AssertStableType(eventInfo.EventHandlerType);
                }
            }
        }
    }

    [Fact]
    public async Task CommonProvider_AppliesExplicitInputBudget()
    {
        var options = new TextMateHighlighterOptions(maximumCodeLength: 4);
        using var provider = new CommonTextMateGrammarProvider(options);

        TextMateGrammarHighlightResult? result = await provider.HighlightAsync(
            new TextMateGrammarHighlightRequest(
                "csharp",
                "12345",
                TextMateGrammarThemeVariant.Dark),
            CancellationToken.None);

        Assert.Same(TextMateGrammarHighlightResult.Empty, result);
    }

    [Fact]
    public async Task CommonProvider_ActiveWorkObservesCancellation()
    {
        using var provider = new CommonTextMateGrammarProvider();
        using var cancellation = new CancellationTokenSource();
        string code = string.Join(
            '\n',
            Enumerable.Repeat("namespace demo { template <typename T> class Widget {}; }", 2_000));

        Task<TextMateGrammarHighlightResult?> work = provider.HighlightAsync(
            new TextMateGrammarHighlightRequest(
                "cpp",
                code,
                TextMateGrammarThemeVariant.Dark),
            cancellation.Token).AsTask();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(5));

        TextMateGrammarHighlightResult? result = await work.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(result);
    }

    [Fact]
    public async Task BundledProvider_ChecksCancellationImmediatelyAfterGrammarLoad()
    {
        var innerOptions = new CuratedRegistryOptions(TextMateGrammarThemeVariant.Dark);
        using var blockingOptions = new BlockingRegistryOptions(innerOptions);
        using var provider = new TextMateSharpGrammarProvider(
            revision: 1,
            _ => blockingOptions,
            static (_, _) => "source.cs",
            allowedLanguageIds: ["csharp"],
            options: new TextMateHighlighterOptions());
        using var cancellation = new CancellationTokenSource();

        Task<TextMateGrammarHighlightResult?> work = provider.HighlightAsync(
            new TextMateGrammarHighlightRequest(
                "csharp",
                "public sealed class Demo { }",
                TextMateGrammarThemeVariant.Dark),
            cancellation.Token).AsTask();
        await blockingOptions.GrammarLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await cancellation.CancelAsync();
        blockingOptions.ReleaseGrammarLoad.Set();
        TextMateGrammarHighlightResult? result = await work.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(result);
    }

    [Fact]
    public async Task BundledProvider_ChecksCancellationImmediatelyAfterTokenizationSlice()
    {
        using var checkpointRelease = new ManualResetEventSlim();
        var checkpointReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var registryOptions = new CuratedRegistryOptions(TextMateGrammarThemeVariant.Dark);
        using var provider = new TextMateSharpGrammarProvider(
            revision: 1,
            _ => registryOptions,
            static (_, _) => "source.cs",
            allowedLanguageIds: ["csharp"],
            options: new TextMateHighlighterOptions(),
            checkpointObserver: (checkpoint, lineIndex) =>
            {
                if (checkpoint != TextMateProviderCheckpoint.AfterLineTokenizationSlice || lineIndex != 0)
                    return;

                checkpointReached.TrySetResult();
                if (!checkpointRelease.Wait(TimeSpan.FromSeconds(2)))
                    throw new TimeoutException("The tokenization checkpoint was not released.");
            });
        using var cancellation = new CancellationTokenSource();

        Task<TextMateGrammarHighlightResult?> work = provider.HighlightAsync(
            new TextMateGrammarHighlightRequest(
                "csharp",
                "public class First { }\npublic class Second { }",
                TextMateGrammarThemeVariant.Dark),
            cancellation.Token).AsTask();
        await checkpointReached.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await cancellation.CancelAsync();
        checkpointRelease.Set();
        TextMateGrammarHighlightResult? result = await work.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(result);
    }

    [Fact]
    public async Task TextMateLineTimeout_FallsBackAtomically()
    {
        var options = new TextMateHighlighterOptions(
            maximumLineLength: 100_000,
            maximumLineProcessingTime: TimeSpan.FromTicks(1));
        using var provider = new CommonTextMateGrammarProvider(options);
        string code = string.Concat(Enumerable.Repeat("template<typename T> class Widget {}; ", 1_000));

        TextMateGrammarHighlightResult? result = await provider.HighlightAsync(
            new TextMateGrammarHighlightRequest(
                "cpp",
                code,
                TextMateGrammarThemeVariant.Dark),
            CancellationToken.None);

        Assert.Same(TextMateGrammarHighlightResult.Empty, result);
    }

    private static void AssertStableType(Type? type)
    {
        if (type is null)
            return;

        if (type.IsArray || type.IsByRef || type.IsPointer)
        {
            AssertStableType(type.GetElementType());
            return;
        }

        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
                AssertStableType(argument);
        }

        string assemblyName = type.Assembly.GetName().Name ?? string.Empty;
        Assert.DoesNotContain("TextMateSharp", assemblyName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Onigwrap", assemblyName, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class BlockingRegistryOptions(IRegistryOptions inner) : IRegistryOptions, IDisposable
    {
        public TaskCompletionSource GrammarLoadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim ReleaseGrammarLoad { get; } = new();

        public IRawTheme GetTheme(string scopeName) => inner.GetTheme(scopeName);

        public IRawGrammar GetGrammar(string scopeName)
        {
            GrammarLoadStarted.TrySetResult();
            if (!ReleaseGrammarLoad.Wait(TimeSpan.FromSeconds(2)))
                throw new TimeoutException("The grammar-load checkpoint was not released.");
            return inner.GetGrammar(scopeName);
        }

        public ICollection<string> GetInjections(string scopeName) =>
            inner.GetInjections(scopeName);

        public IRawTheme GetDefaultTheme() => inner.GetDefaultTheme();

        public void Dispose() => ReleaseGrammarLoad.Dispose();
    }
}
