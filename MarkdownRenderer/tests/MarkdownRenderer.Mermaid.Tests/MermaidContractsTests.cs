using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using MarkdownRenderer.Mermaid;
using System.Runtime.InteropServices;

namespace MarkdownRenderer.Mermaid.Tests;

public sealed class MermaidContractsTests
{
    [Fact]
    public void DefaultBudgetsMatchTheSecurePackContract()
    {
        MermaidRenderBudgets budgets = MermaidRenderBudgets.Default;

        Assert.Equal(256 * 1024, budgets.MaxSourceBytes);
        Assert.Equal(2_000, budgets.MaxNodes);
        Assert.Equal(4_000, budgets.MaxEdges);
        Assert.Equal(64, budgets.MaxDepth);
        Assert.Equal(16 * 1024, budgets.MaxLabelBytes);
        Assert.Equal(16 * 1024 * 1024, budgets.MaxSceneBytes);
        Assert.Equal(64L * 1024 * 1024, budgets.MaxWorkingMemoryBytes);
        Assert.Equal(TimeSpan.FromSeconds(2), budgets.Deadline);
        Assert.Equal(4, budgets.MaxConcurrentRenders);
        Assert.Equal(16, budgets.MaxOutstandingRenders);
        Assert.Equal(4L * 1024 * 1024, budgets.MaxOutstandingSourceBytes);
        Assert.Equal(32L * 1024 * 1024, MermaidRenderOptions.Default.SceneCacheBudgetBytes);
    }

    [Fact]
    public void SceneCacheBudgetCanBeDisabledButRejectsUnboundedValues()
    {
        Assert.True((MermaidRenderOptions.Default with { SceneCacheBudgetBytes = 0 }).TryValidate(out _));
        Assert.False((MermaidRenderOptions.Default with { SceneCacheBudgetBytes = -1 }).TryValidate(out string? error));
        Assert.Contains(nameof(MermaidRenderOptions.SceneCacheBudgetBytes), error, StringComparison.Ordinal);
    }

    [Fact]
    public void BudgetIncreaseMustBeExplicit()
    {
        var implicitIncrease = new MermaidRenderOptions
        {
            Budgets = MermaidRenderBudgets.Default with { MaxNodes = 2_001 },
        };
        var explicitIncrease = implicitIncrease with { AllowBudgetIncreases = true };

        Assert.False(implicitIncrease.TryValidate(out string? error));
        Assert.Contains("AllowBudgetIncreases", error, StringComparison.Ordinal);
        Assert.True(explicitIncrease.TryValidate(out error));
        Assert.Null(error);
    }

    [Fact]
    public void AdmissionBudgetsMustContainConcurrencyAndOneMaximumSource()
    {
        var tooFewOutstanding = MermaidRenderOptions.Default with
        {
            Budgets = MermaidRenderBudgets.Default with
            {
                MaxOutstandingRenders = MermaidRenderBudgets.DefaultMaxConcurrentRenders - 1,
            },
        };
        var tooFewSourceBytes = MermaidRenderOptions.Default with
        {
            Budgets = MermaidRenderBudgets.Default with
            {
                MaxOutstandingSourceBytes = MermaidRenderBudgets.DefaultMaxSourceBytes - 1,
            },
        };

        Assert.False(tooFewOutstanding.TryValidate(out _));
        Assert.False(tooFewSourceBytes.TryValidate(out _));
    }

    [Fact]
    public void NativeAbiStructuresHavePinnedCrossArchitectureLayout()
    {
        Assert.Equal(24, Marshal.SizeOf<NativeEngineOptions>());
        Assert.Equal(48, Marshal.SizeOf<NativeRenderOptions>());
        Assert.Equal(8, Marshal.OffsetOf<NativeEngineOptions>(nameof(NativeEngineOptions.MaxWorkingMemoryBytes)).ToInt32());
        Assert.Equal(36, Marshal.OffsetOf<NativeRenderOptions>(nameof(NativeRenderOptions.DeadlineMilliseconds)).ToInt32());
    }

    [Fact]
    public void InvalidSourceRangeCanBeInspectedWithoutOverflow()
    {
        var range = new MermaidSourceRange(int.MaxValue, 1);

        Assert.False(range.IsValid);
    }

    [Fact]
    public async Task ElkAlwaysReturnsOriginalCodeBlockFallback()
    {
        const string source = "flowchart LR\n  A-->B";
        using var renderer = new MermaidRenderer(new MermaidRenderOptions { Layout = MermaidLayoutMode.Elk });

        MermaidRenderResult result = await renderer.RenderAsync(source);

        Assert.Equal(MermaidRenderStatus.UnsupportedLayout, result.Status);
        Assert.True(result.ShouldUseFallback);
        Assert.Equal("mermaid", result.Fallback.Language);
        Assert.Equal(source, result.Fallback.Source);
    }

    [Fact]
    public void MissingNativeBinaryProbeIsDeterministic()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"missing-mermaid-{Guid.NewGuid():N}.dll");

        MermaidNativeRuntimeInfo runtime = MermaidNativeRuntime.ProbeCore(missing);

        Assert.Equal(MermaidNativeRuntimeStatus.NotInstalled, runtime.Status);
        Assert.False(runtime.IsAvailable);
    }

    [Fact]
    public void ExistingDllWithWrongExportsIsRejected()
    {
        string kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");

        MermaidNativeRuntimeInfo runtime = MermaidNativeRuntime.ProbeCore(kernel32);

        Assert.Equal(MermaidNativeRuntimeStatus.MissingExport, runtime.Status);
    }

    [Fact]
    public void CorruptNativeBinaryReturnsLoadFailure()
    {
        string path = Path.Combine(Path.GetTempPath(), $"corrupt-mermaid-{Guid.NewGuid():N}.dll");
        try
        {
            File.WriteAllBytes(path, "not a portable executable"u8.ToArray());

            MermaidNativeRuntimeInfo runtime = MermaidNativeRuntime.ProbeCore(path);

            Assert.Equal(MermaidNativeRuntimeStatus.LoadFailure, runtime.Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FontCatalogIsImmutableNormalizedAndStable()
    {
        var input = new List<string> { " Segoe UI ", "Cascadia Mono", "segoe ui" };
        var catalog = new MermaidFontCatalog(input);
        input[0] = "Changed";

        Assert.Equal(["Segoe UI", "Cascadia Mono"], catalog.Families);
        Assert.Equal(64, catalog.Fingerprint.Length);
        Assert.Equal(catalog.Fingerprint, new MermaidFontCatalog(["Segoe UI", "Cascadia Mono"]).Fingerprint);
    }

    [Fact]
    public async Task OversizeSourceIsRejectedBeforeNativeProbe()
    {
        var budgets = MermaidRenderBudgets.Default with { MaxSourceBytes = 8 };
        using var renderer = new MermaidRenderer(new MermaidRenderOptions { Budgets = budgets });

        MermaidRenderResult result = await renderer.RenderAsync("flowchart LR");

        Assert.Equal(MermaidRenderStatus.BudgetExceeded, result.Status);
        Assert.Equal("flowchart LR", result.Fallback.Source);
    }

    [Fact]
    public async Task DiagnosticsUseStableKeysRequestCultureAndImmutableArguments()
    {
        var provider = new RecordingStringProvider();
        var budgets = MermaidRenderBudgets.Default with { MaxSourceBytes = 8 };
        using var renderer = new MermaidRenderer(new MermaidRenderOptions
        {
            Budgets = budgets,
            StringProvider = provider,
        });

        MermaidRenderResult result = await renderer.RenderAsync("flowchart LR");

        MermaidDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("localized-source-limit", diagnostic.Message);
        Assert.Equal(MermaidStringKeys.SourceTooLarge, provider.ResourceKey);
        Assert.Equal(CultureInfo.CurrentUICulture.Name, provider.CultureName);
        Assert.Equal(new object?[] { 8 }, provider.Arguments);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<object?>)provider.ObservedArguments!).Add(9));
    }

    [Fact]
    public void RenderOptionsCloneAndFreezeAssignedDiagnosticCulture()
    {
        var supplied = new CultureInfo("fr-FR");
        string expectedSeparator = supplied.NumberFormat.NumberDecimalSeparator;
        var options = new MermaidRenderOptions { DiagnosticCulture = supplied };

        supplied.NumberFormat.NumberDecimalSeparator = "mutated";

        CultureInfo configured = Assert.IsType<CultureInfo>(options.DiagnosticCulture);
        Assert.NotSame(supplied, configured);
        Assert.True(configured.IsReadOnly);
        Assert.Equal(expectedSeparator, configured.NumberFormat.NumberDecimalSeparator);
        Assert.Throws<InvalidOperationException>(() =>
            configured.NumberFormat.NumberDecimalSeparator = "blocked");
    }

    [Fact]
    public async Task ThrowingStringProviderFallsBackWithoutSuppressingDiagnosticOrSource()
    {
        const string source = "flowchart LR\nA-->B";
        using var renderer = new MermaidRenderer(new MermaidRenderOptions
        {
            Layout = MermaidLayoutMode.Elk,
            StringProvider = new ThrowingStringProvider(),
        });

        MermaidRenderResult result = await renderer.RenderAsync(source);

        MermaidDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("MMR0002", diagnostic.Code);
        Assert.Equal("ELK layout is not included in MarkdownRenderer.Mermaid.", diagnostic.Message);
        Assert.Equal(source, result.Fallback.Source);
    }

    [Fact]
    public async Task EndToEndDeadlineIncludesRenderGateQueueTime()
    {
        const string source = "flowchart LR\nA-->B";
        var budgets = MermaidRenderBudgets.Default with
        {
            Deadline = TimeSpan.FromMilliseconds(75),
            MaxConcurrentRenders = 1,
        };
        using var renderer = new MermaidRenderer(new MermaidRenderOptions { Budgets = budgets });
        SemaphoreSlim gate = GetRenderGate(renderer);
        Assert.True(gate.Wait(0));

        try
        {
            var stopwatch = Stopwatch.StartNew();
            MermaidRenderResult result = await renderer.RenderAsync(source);
            stopwatch.Stop();

            Assert.Equal(MermaidRenderStatus.TimedOut, result.Status);
            Assert.Equal("MMR0007", Assert.Single(result.Diagnostics).Code);
            Assert.Equal(source, result.Fallback.Source);
            Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task CallerCancellationPropagatesWhileQueued()
    {
        const string source = "flowchart LR\nA-->B";
        var budgets = MermaidRenderBudgets.Default with
        {
            Deadline = TimeSpan.FromSeconds(1),
            MaxConcurrentRenders = 1,
        };
        using var renderer = new MermaidRenderer(new MermaidRenderOptions { Budgets = budgets });
        SemaphoreSlim gate = GetRenderGate(renderer);
        Assert.True(gate.Wait(0));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await renderer.RenderAsync(source, cancellation.Token));
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public void DisposeDefersSemaphoreDisposalUntilEnteredOperationLeaves()
    {
        var renderGate = new SemaphoreSlim(1, 1);
        var lifetime = new MermaidRenderLifetime(renderGate);
        MermaidRenderLifetime.Lease operation = lifetime.Enter();

        lifetime.Dispose();

        Assert.False(lifetime.IsRenderGateDisposed);
        Assert.True(renderGate.Wait(0));
        renderGate.Release();
        Assert.Throws<ObjectDisposedException>(() => lifetime.Enter());

        operation.Dispose();

        Assert.True(lifetime.IsRenderGateDisposed);
        Assert.Throws<ObjectDisposedException>(() => renderGate.Wait(0));
        lifetime.Dispose();
    }

    [Fact]
    public void EnteredOperationCanForkProducerLeaseAfterRendererDisposalWasRequested()
    {
        var renderGate = new SemaphoreSlim(1, 1);
        var lifetime = new MermaidRenderLifetime(renderGate);
        MermaidRenderLifetime.Lease request = lifetime.Enter();
        lifetime.Dispose();

        MermaidRenderLifetime.Lease producer = request.Fork();
        request.Dispose();

        Assert.False(lifetime.IsRenderGateDisposed);
        Assert.True(renderGate.Wait(0));
        renderGate.Release();

        producer.Dispose();
        Assert.True(lifetime.IsRenderGateDisposed);
        Assert.Throws<ObjectDisposedException>(() => renderGate.Wait(0));
    }

    [Fact]
    public async Task RenderAfterDisposeIsRejected()
    {
        var renderer = new MermaidRenderer();
        renderer.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await renderer.RenderAsync("flowchart LR\nA-->B"));
    }

    private static SemaphoreSlim GetRenderGate(MermaidRenderer renderer)
    {
        FieldInfo field = typeof(MermaidRenderer).GetField(
            "_renderGate",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<SemaphoreSlim>(field.GetValue(renderer));
    }

    private sealed class RecordingStringProvider : IMermaidStringProvider
    {
        internal string? ResourceKey { get; private set; }
        internal string? CultureName { get; private set; }
        internal object?[] Arguments { get; private set; } = [];
        internal IReadOnlyList<object?>? ObservedArguments { get; private set; }

        public string? GetString(
            string resourceKey,
            CultureInfo culture,
            IReadOnlyList<object?> arguments)
        {
            ResourceKey = resourceKey;
            CultureName = culture.Name;
            Arguments = arguments.ToArray();
            ObservedArguments = arguments;
            return "localized-source-limit";
        }
    }

    private sealed class ThrowingStringProvider : IMermaidStringProvider
    {
        public string? GetString(
            string resourceKey,
            CultureInfo culture,
            IReadOnlyList<object?> arguments) =>
            throw new InvalidOperationException("Provider failure must be contained.");
    }
}
