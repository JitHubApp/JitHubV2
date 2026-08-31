using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Globalization;

namespace JitHub.WinUI.Helpers;

internal static partial class LocalizedResourceText
{
    private static readonly object LookupGate = new();
    private static readonly TimeSpan LookupRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly AsyncLocal<TestResourceLookupOverride?> TestLookupOverride = new();
    private static Func<string, string?>? _lookup;
    private static Func<Func<string, string?>?> _resourceLookupFactory = CreateResourceLookup;
    private static long _nextLookupRetryUtcTicks;
    private static int _formatFailureReported;
    private static int _lookupFailureReported;
    private static int _lookupInitializationFailureReported;

    public static string Get(string resourceKey, string fallback) => GetString(resourceKey, fallback);

    public static string GetString(string resourceKey, string fallback)
    {
        if (string.IsNullOrWhiteSpace(resourceKey))
        {
            return fallback;
        }

        Func<string, string?>? lookup = GetOrCreateResourceLookup();
        if (lookup is null)
        {
            return fallback;
        }

        try
        {
            string? value = lookup(NormalizeResourceKey(resourceKey));
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
        catch (Exception exception)
        {
            // Development and isolated automation can lose their PRI resource map
            // while the process is still running. Every caller supplies canonical
            // fallback text, so keep the UI usable and report the failed boundary.
            ReportOnce(ref _lookupFailureReported, exception, "ui-localization-lookup");
            ResetResourceLookupAfterFailure(lookup);
            return fallback;
        }
    }

    public static string Format(string resourceKey, string fallback, params object?[] arguments)
    {
        string format = GetString(resourceKey, fallback);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, arguments);
        }
        catch (FormatException exception)
        {
            ReportOnce(ref _formatFailureReported, exception, "ui-localization-format");
            try
            {
                return string.Format(CultureInfo.CurrentCulture, fallback, arguments);
            }
            catch (FormatException)
            {
                return fallback;
            }
        }
    }

    private static string NormalizeResourceKey(string resourceKey) => resourceKey.Replace('.', '/');

    private static Func<string, string?>? CreateResourceLookup()
    {
        if (JitHub.Services.Markdown.MarkdownLifecycleAutomationBridge.IsResourceMapForcedAbsent)
        {
            return null;
        }

        // MRT activation can terminate an isolated test host before managed exception
        // handling runs because that process has no Windows application resource map.
        // The production executable owns the PRI context; every other host uses the
        // caller-provided fallback unless a test explicitly installs a lookup factory.
        string? processPath = Environment.ProcessPath;
        if (processPath is null ||
            !string.Equals(Path.GetFileNameWithoutExtension(processPath), "JitHub.WinUI", StringComparison.Ordinal))
        {
            return null;
        }

        string resourceFilePath = Path.Combine(AppContext.BaseDirectory, "resources.pri");
        ResourceManager resourceManager = File.Exists(resourceFilePath)
            ? new ResourceManager(resourceFilePath)
            : new ResourceManager();
        ResourceMap? resourceMap = resourceManager.MainResourceMap.TryGetSubtree("Resources");
        if (resourceMap is null)
        {
            return null;
        }

        ResourceContext resourceContext = resourceManager.CreateResourceContext();
        string? languageOverride = ApplicationLanguages.PrimaryLanguageOverride;
        if (!string.IsNullOrWhiteSpace(languageOverride))
        {
            // The implicit context does not consistently observe an automation
            // language override for runtime-created strings. Use the same explicit
            // qualifier that XAML applies to x:Uid resources.
            resourceContext.QualifierValues["Language"] = languageOverride;
        }

        return resourceKey =>
        {
            ResourceCandidate? candidate = resourceMap.TryGetValue(resourceKey, resourceContext);
            GC.KeepAlive(resourceManager);
            return candidate?.ValueAsString;
        };
    }

    private static Func<string, string?>? GetOrCreateResourceLookup()
    {
        TestResourceLookupOverride? testOverride = TestLookupOverride.Value;
        if (testOverride is not null)
        {
            try
            {
                return testOverride.GetOrCreateLookup();
            }
            catch (Exception exception)
            {
                ReportOnce(
                    ref _lookupInitializationFailureReported,
                    exception,
                    "ui-localization-initialize");
                return null;
            }
        }

        Func<string, string?>? lookup = Volatile.Read(ref _lookup);
        if (lookup is not null)
        {
            return lookup;
        }

        long now = DateTime.UtcNow.Ticks;
        if (now < Interlocked.Read(ref _nextLookupRetryUtcTicks))
        {
            return null;
        }

        lock (LookupGate)
        {
            lookup = _lookup;
            if (lookup is not null)
            {
                return lookup;
            }

            now = DateTime.UtcNow.Ticks;
            if (now < _nextLookupRetryUtcTicks)
            {
                return null;
            }

            try
            {
                lookup = _resourceLookupFactory();
                if (lookup is null)
                {
                    Interlocked.Exchange(
                        ref _nextLookupRetryUtcTicks,
                        DateTime.UtcNow.Add(LookupRetryDelay).Ticks);
                    return null;
                }

                Volatile.Write(ref _lookup, lookup);
                Interlocked.Exchange(ref _nextLookupRetryUtcTicks, 0);
                return lookup;
            }
            catch (Exception exception)
            {
                ReportOnce(
                    ref _lookupInitializationFailureReported,
                    exception,
                    "ui-localization-initialize");
                Interlocked.Exchange(
                    ref _nextLookupRetryUtcTicks,
                    DateTime.UtcNow.Add(LookupRetryDelay).Ticks);
                return null;
            }
        }
    }

    internal static IDisposable OverrideResourceLookupFactoryForTests(
        Func<Func<string, string?>?> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        TestResourceLookupOverride? previousOverride = TestLookupOverride.Value;
        TestLookupOverride.Value = new TestResourceLookupOverride(factory);
        return new RestoreAction(() => TestLookupOverride.Value = previousOverride);
    }

    private static void ResetResourceLookupAfterFailure(Func<string, string?> failedLookup)
    {
        lock (LookupGate)
        {
            if (!ReferenceEquals(_lookup, failedLookup))
            {
                return;
            }

            Volatile.Write(ref _lookup, null);
            Interlocked.Exchange(
                ref _nextLookupRetryUtcTicks,
                DateTime.UtcNow.Add(LookupRetryDelay).Ticks);
        }
    }

    private static void ReportOnce(ref int reported, Exception exception, string category)
    {
        if (Interlocked.Exchange(ref reported, 1) == 0)
        {
            HandledFailureReporter.Report(exception, category);
        }
    }

    private sealed partial class RestoreAction(Action restore) : IDisposable
    {
        private Action? _restore = restore;

        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }

    private sealed class TestResourceLookupOverride(Func<Func<string, string?>?> factory)
    {
        private readonly object _gate = new();
        private Func<Func<string, string?>?>? _factory = factory;
        private Func<string, string?>? _lookup;

        public Func<string, string?>? GetOrCreateLookup()
        {
            Func<Func<string, string?>?>? factory = Volatile.Read(ref _factory);
            if (factory is null)
            {
                return _lookup;
            }

            lock (_gate)
            {
                factory = _factory;
                if (factory is null)
                {
                    return _lookup;
                }

                _factory = null;
                _lookup = factory();
                return _lookup;
            }
        }
    }
}
