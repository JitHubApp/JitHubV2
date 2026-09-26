using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.All;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Internal;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
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
            CodeBlockThemeVariant.Dark,
            CancellationToken.None));

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
            CodeBlockThemeVariant.Dark,
            CancellationToken.None));

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
    public void CompatibilityFactory_DiscoversInstalledCommonPack()
    {
        var provider = TextMateGrammarProviderFactory.TryCreateDefault();

        Assert.IsType<CommonTextMateGrammarProvider>(provider);
        (provider as IDisposable)?.Dispose();
    }

    [Fact]
    public void DiscoveryCompatibilityConstructorOwnsProviderAndIsObsolete()
    {
        ConstructorInfo explicitConstructor = typeof(TextMateCodeBlockSyntaxHighlighter)
            .GetConstructor([typeof(ITextMateGrammarProvider)])!;
        ConstructorInfo compatibilityConstructor = typeof(TextMateCodeBlockSyntaxHighlighter)
            .GetConstructor(Type.EmptyTypes)!;

        Assert.NotNull(compatibilityConstructor.GetCustomAttribute<ObsoleteAttribute>());
        Assert.NotNull(compatibilityConstructor.GetCustomAttribute<RequiresUnreferencedCodeAttribute>());
        Assert.Null(explicitConstructor.GetCustomAttribute<RequiresUnreferencedCodeAttribute>());

        foreach (string methodName in new[] { nameof(TextMateGrammarProviderFactory.TryCreateDefault), nameof(TextMateGrammarProviderFactory.CreateDefault) })
        {
            MethodInfo method = typeof(TextMateGrammarProviderFactory).GetMethod(methodName)!;
            Assert.NotNull(method.GetCustomAttribute<RequiresUnreferencedCodeAttribute>());
        }
    }

    [Fact]
    public void CompatibilityExtensionsAreObsoleteAndHaveDeterministicOwnership()
    {
        MethodInfo[] extensionMethods = typeof(TextMateSyntaxHighlightingExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == nameof(TextMateSyntaxHighlightingExtensions.UseTextMateSyntaxHighlighting))
            .ToArray();
        MethodInfo[] providerOverloads = extensionMethods
            .Where(method =>
                method.GetParameters().Length == 2 &&
                method.GetParameters()[1].ParameterType == typeof(ITextMateGrammarProvider))
            .ToArray();
        MethodInfo[] viewOverloads = extensionMethods
            .Where(method => method.GetParameters()[0].ParameterType != typeof(MarkdownRendererControlBuilder))
            .ToArray();

        Assert.Equal(3, providerOverloads.Length);
        Assert.All(providerOverloads, method => Assert.NotNull(method.GetCustomAttribute<ObsoleteAttribute>()));
        MethodInfo[] parameterlessOverloads = extensionMethods
            .Where(method => method.GetParameters().Length == 1)
            .ToArray();
        Assert.Equal(3, parameterlessOverloads.Length);
        Assert.All(parameterlessOverloads, method =>
        {
            Assert.NotNull(method.GetCustomAttribute<ObsoleteAttribute>());
            Assert.NotNull(method.GetCustomAttribute<RequiresUnreferencedCodeAttribute>());
        });
        Assert.Equal(6, viewOverloads.Length);
        Assert.DoesNotContain(viewOverloads, method =>
            method.GetParameters()[0].ParameterType.FullName == "MarkdownRenderer.Controls.MarkdownRendererControl");
        Assert.Equal(3, extensionMethods.Count(method =>
            method.GetParameters().Length == 2 &&
            method.GetParameters()[1].ParameterType == typeof(TextMateCodeBlockSyntaxHighlighter)));

        var provider = new CountingProvider();
#pragma warning disable CS0618
        var builder = new MarkdownRendererControlBuilder()
            .UseTextMateSyntaxHighlighting(provider);
#pragma warning restore CS0618
        FieldInfo factoryField = typeof(MarkdownRendererControlBuilder).GetField(
            "_ownedCodeHighlighterFactory",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var factory = Assert.IsType<Func<MarkdownRenderer.Hosting.ICodeHighlighter>>(
            factoryField.GetValue(builder));
        using var highlighter = Assert.IsType<TextMateCodeBlockSyntaxHighlighter>(factory());
        Assert.False(highlighter.OwnsProvider);
    }

    [Fact]
    public void OwnedViewHelperDisposesHighlighterWhenOwnershipIsNotTransferred()
    {
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(new CountingProvider());
        MethodInfo configureOwnedView = typeof(TextMateSyntaxHighlightingExtensions).GetMethod(
            "ConfigureOwnedView",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
        {
            _ = configureOwnedView.Invoke(null, [new object(), highlighter]);
        });

        Assert.IsType<ArgumentException>(exception.InnerException);
        Assert.Throws<ObjectDisposedException>(() => _ = highlighter.Revision);
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
    public async Task Highlighter_InvokesSynchronousProviderOnWorkerThread()
    {
        var provider = new RecordingProvider();
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(provider);
        Exception? failure = null;
        bool callerWasThreadPool = true;

        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                callerWasThreadPool = Thread.CurrentThread.IsThreadPoolThread;
                _ = highlighter.HighlightAsync(CreateRequest("csharp", "var value = 1;"))
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                completed.Set();
            }
        });

        thread.Start();
        Assert.True(completed.Wait(TimeSpan.FromSeconds(5)));
        thread.Join();

        Assert.Null(failure);
        Assert.False(callerWasThreadPool);
        Assert.True(provider.WasThreadPoolThread);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task IdenticalConcurrentRequests_AreDeduplicatedWithoutSharingCallerCancellation()
    {
        var provider = new ControlledProvider(observeCancellation: true);
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(provider);
        using var firstCancellation = new CancellationTokenSource();

        Task<CodeBlockHighlightResult?> first = highlighter.HighlightAsync(
            CreateRequest("csharp", "var value = 1;", firstCancellation.Token)).AsTask();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task<CodeBlockHighlightResult?> second = highlighter.HighlightAsync(
            CreateRequest("csharp", "var value = 1;")).AsTask();
        await firstCancellation.CancelAsync();

        Assert.Null(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(provider.WorkCancellation.IsCancellationRequested);

        provider.Release.TrySetResult();
        CodeBlockHighlightResult? result = await second.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(result);
        Assert.Single(result.Spans);
        Assert.Equal(1, provider.InvocationCount);
    }

    [Fact]
    public async Task LastCanceledWaiter_CancelsSharedProviderWorkPromptly()
    {
        var provider = new ControlledProvider(observeCancellation: true);
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(provider);
        using var cancellation = new CancellationTokenSource();

        Task<CodeBlockHighlightResult?> work = highlighter.HighlightAsync(
            CreateRequest("csharp", "var value = 1;", cancellation.Token)).AsTask();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stopwatch = Stopwatch.StartNew();
        await cancellation.CancelAsync();

        Assert.Null(await work.WaitAsync(TimeSpan.FromSeconds(2)));
        await provider.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CompletedResult_IsCachedAndWeightedBudgetEvictsLeastRecentlyUsedEntry()
    {
        var provider = new CountingProvider();
        var options = new TextMateHighlighterOptions(cacheBudgetBytes: 330);
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(provider, options);

        _ = await highlighter.HighlightAsync(CreateRequest("csharp", "a"));
        _ = await highlighter.HighlightAsync(CreateRequest("csharp", "a"));
        Assert.Equal(1, provider.InvocationCount);

        _ = await highlighter.HighlightAsync(CreateRequest("csharp", "second"));
        _ = await highlighter.HighlightAsync(CreateRequest("csharp", "a"));
        Assert.Equal(3, provider.InvocationCount);
    }

    [Theory]
    [InlineData("12345", 4, 10, 10)]
    [InlineData("a\nb\nc", 20, 2, 10)]
    [InlineData("12345", 20, 10, 4)]
    public async Task InputOutsideBudget_FallsBackWithoutInvokingProvider(
        string code,
        int maximumCodeLength,
        int maximumLineCount,
        int maximumLineLength)
    {
        var provider = new CountingProvider();
        var options = new TextMateHighlighterOptions(
            maximumCodeLength,
            maximumLineCount,
            maximumLineLength,
            maximumSpanCount: 10,
            cacheBudgetBytes: 0);
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(provider, options);

        CodeBlockHighlightResult? result = await highlighter.HighlightAsync(
            CreateRequest("csharp", code));

        Assert.Same(CodeBlockHighlightResult.Empty, result);
        Assert.Equal(0, provider.InvocationCount);
    }

    [Fact]
    public async Task ExcessiveProviderOutput_FallsBackAtomically()
    {
        var provider = new StaticProvider(new TextMateGrammarHighlightResult(
        [
            new TextMateGrammarHighlightSpan(0, 1, 0xFFFF0000),
            new TextMateGrammarHighlightSpan(1, 1, 0xFF00FF00),
        ]));
        var options = new TextMateHighlighterOptions(maximumSpanCount: 1);
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(provider, options);

        CodeBlockHighlightResult? result = await highlighter.HighlightAsync(
            CreateRequest("csharp", "ab"));

        Assert.Same(CodeBlockHighlightResult.Empty, result);
    }

    [Fact]
    public async Task OutOfRangeProviderSpan_FallsBackAtomically()
    {
        var provider = new StaticProvider(new TextMateGrammarHighlightResult(
        [
            new TextMateGrammarHighlightSpan(0, 1, 0xFFFF0000),
            new TextMateGrammarHighlightSpan(2, 2, 0xFF00FF00),
        ]));
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(provider);

        CodeBlockHighlightResult? result = await highlighter.HighlightAsync(
            CreateRequest("csharp", "abc"));

        Assert.Same(CodeBlockHighlightResult.Empty, result);
    }

    [Fact]
    public async Task OverlappingProviderSpans_FallBackAtomically()
    {
        var provider = new StaticProvider(new TextMateGrammarHighlightResult(
        [
            new TextMateGrammarHighlightSpan(0, 2, 0xFFFF0000),
            new TextMateGrammarHighlightSpan(1, 2, 0xFF00FF00),
        ]));
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(provider);

        CodeBlockHighlightResult? result = await highlighter.HighlightAsync(
            CreateRequest("csharp", "abc"));

        Assert.Same(CodeBlockHighlightResult.Empty, result);
    }

    [Fact]
    public async Task ProviderFailure_FallsBackToUnhighlightedCode()
    {
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(new ThrowingProvider());

        CodeBlockHighlightResult? result = await highlighter.HighlightAsync(
            CreateRequest("csharp", "var value = 1;"));

        Assert.Same(CodeBlockHighlightResult.Empty, result);
    }

    [Fact]
    public async Task DisposingOwnedHighlighterCancelsWorkThenDisposesProvider()
    {
        var provider = new ControlledProvider(observeCancellation: true);
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(
            provider,
            options: null,
            ownsProvider: true);
        Task<CodeBlockHighlightResult?> work = highlighter.HighlightAsync(
            CreateRequest("csharp", "value")).AsTask();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        highlighter.Dispose();

        await provider.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(await work.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(SpinWait.SpinUntil(() => provider.IsDisposed, TimeSpan.FromSeconds(2)));
        Assert.Throws<ObjectDisposedException>(() => _ = highlighter.Revision);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            highlighter.HighlightAsync(CreateRequest("csharp", "later")).AsTask());
    }

    [Fact]
    public async Task DisposeDoesNotBlockOnCancellationCallbacksOrDisposeTheirTokenEarly()
    {
        var provider = new BlockingCancellationProvider();
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(
            provider,
            options: null,
            ownsProvider: true);
        Task<CodeBlockHighlightResult?> work = highlighter.HighlightAsync(
            CreateRequest("csharp", "value")).AsTask();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stopwatch = Stopwatch.StartNew();
        highlighter.Dispose();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        await provider.CallbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(provider.IsDisposed);
        Assert.False(work.IsCompleted);

        provider.ReleaseCallback.Set();
        Assert.Null(await work.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(SpinWait.SpinUntil(() => provider.IsDisposed, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task DisposeDoesNotBlockBehindProviderRevisionAndDefersOwnedProviderDisposal()
    {
        var provider = new BlockingRevisionProvider();
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(
            provider,
            options: null,
            ownsProvider: true);
        Task<int> revision = Task.Run(() => highlighter.Revision);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stopwatch = Stopwatch.StartNew();
        highlighter.Dispose();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.False(provider.IsDisposed);

        provider.Release.Set();
        Assert.Equal(7, await revision.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(SpinWait.SpinUntil(() => provider.IsDisposed, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void ProviderRevisionMayDisposeOwnerReentrantlyWithoutDeadlock()
    {
        TextMateCodeBlockSyntaxHighlighter? highlighter = null;
        var provider = new ReentrantRevisionProvider(() => highlighter!.Dispose());
        highlighter = new TextMateCodeBlockSyntaxHighlighter(
            provider,
            options: null,
            ownsProvider: true);

        Assert.Equal(11, highlighter.Revision);
        Assert.True(provider.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => _ = highlighter.Revision);
    }

    [Fact]
    public void DisposingBorrowingHighlighterLeavesProviderAlive()
    {
        var provider = new ControlledProvider(observeCancellation: true);
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(provider);

        highlighter.Dispose();

        Assert.False(highlighter.OwnsProvider);
        Assert.False(provider.IsDisposed);
        provider.Dispose();
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
        using var provider = new BundledTextMateGrammarProvider(
            ["csharp"],
            revision: 1,
            new TextMateHighlighterOptions(),
            _ => blockingOptions,
            static (_, _) => "source.cs");
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
        using var provider = new BundledTextMateGrammarProvider(
            ["csharp"],
            revision: 1,
            new TextMateHighlighterOptions(),
            _ => registryOptions,
            static (_, _) => "source.cs",
            (checkpoint, lineIndex) =>
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

    private static CodeBlockHighlightRequest CreateRequest(
        string language,
        string code,
        CancellationToken cancellationToken = default) =>
        new(language, code, CodeBlockThemeVariant.Dark, cancellationToken);

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

    private sealed class RecordingProvider : ITextMateGrammarProvider
    {
        public bool WasThreadPoolThread { get; private set; }

        public ValueTask<TextMateGrammarHighlightResult?> HighlightAsync(
            TextMateGrammarHighlightRequest request,
            CancellationToken cancellationToken)
        {
            WasThreadPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            return ValueTask.FromResult<TextMateGrammarHighlightResult?>(
                TextMateGrammarHighlightResult.Empty);
        }
    }

    private sealed class CountingProvider : ITextMateGrammarProvider
    {
        public int InvocationCount { get; private set; }

        public ValueTask<TextMateGrammarHighlightResult?> HighlightAsync(
            TextMateGrammarHighlightRequest request,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            return ValueTask.FromResult<TextMateGrammarHighlightResult?>(
                new TextMateGrammarHighlightResult(
                [
                    new TextMateGrammarHighlightSpan(0, 1, 0xFFFF0000),
                ]));
        }
    }

    private sealed class StaticProvider(TextMateGrammarHighlightResult result)
        : ITextMateGrammarProvider
    {
        public ValueTask<TextMateGrammarHighlightResult?> HighlightAsync(
            TextMateGrammarHighlightRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<TextMateGrammarHighlightResult?>(result);
    }

    private sealed class ThrowingProvider : ITextMateGrammarProvider
    {
        public ValueTask<TextMateGrammarHighlightResult?> HighlightAsync(
            TextMateGrammarHighlightRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic provider failure");
    }

    private sealed class ControlledProvider(bool observeCancellation) : ITextMateGrammarProvider, IDisposable
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Canceled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int InvocationCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public CancellationToken WorkCancellation { get; private set; }

        public async ValueTask<TextMateGrammarHighlightResult?> HighlightAsync(
            TextMateGrammarHighlightRequest request,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            WorkCancellation = cancellationToken;
            Started.TrySetResult();
            try
            {
                if (observeCancellation)
                    await Release.Task.WaitAsync(cancellationToken);
                else
                    await Release.Task;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Canceled.TrySetResult();
                throw;
            }

            return new TextMateGrammarHighlightResult(
            [
                new TextMateGrammarHighlightSpan(0, 1, 0xFFFF0000),
            ]);
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class BlockingCancellationProvider : ITextMateGrammarProvider, IDisposable
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CallbackStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim ReleaseCallback { get; } = new();

        public bool IsDisposed { get; private set; }

        public async ValueTask<TextMateGrammarHighlightResult?> HighlightAsync(
            TextMateGrammarHighlightRequest request,
            CancellationToken cancellationToken)
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                CallbackStarted.TrySetResult();
                if (!ReleaseCallback.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Cancellation callback was not released.");
                throw new InvalidOperationException("Synthetic cancellation callback failure.");
            });
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return TextMateGrammarHighlightResult.Empty;
        }

        public void Dispose()
        {
            IsDisposed = true;
            ReleaseCallback.Dispose();
        }
    }

    private sealed class BlockingRevisionProvider : ITextMateGrammarProvider, IDisposable
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Release { get; } = new();

        public bool IsDisposed { get; private set; }

        public int Revision
        {
            get
            {
                Started.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The revision checkpoint was not released.");
                return 7;
            }
        }

        public ValueTask<TextMateGrammarHighlightResult?> HighlightAsync(
            TextMateGrammarHighlightRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<TextMateGrammarHighlightResult?>(TextMateGrammarHighlightResult.Empty);

        public void Dispose()
        {
            IsDisposed = true;
            Release.Dispose();
        }
    }

    private sealed class ReentrantRevisionProvider(Action onRevision) : ITextMateGrammarProvider, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public int Revision
        {
            get
            {
                onRevision();
                return 11;
            }
        }

        public ValueTask<TextMateGrammarHighlightResult?> HighlightAsync(
            TextMateGrammarHighlightRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<TextMateGrammarHighlightResult?>(TextMateGrammarHighlightResult.Empty);

        public void Dispose() => IsDisposed = true;
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
