using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Globalization;

namespace JitHub.WinUI.Helpers;

internal static partial class LocalizedResourceText
{
    private delegate string? ResourceLookup(string resourceKey, CultureInfo? culture);

    private static readonly object LookupGate = new();
    private static readonly TimeSpan LookupRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly AsyncLocal<TestResourceLookupOverride?> TestLookupOverride = new();
    private static ResourceLookup? _lookup;
    private static Func<ResourceLookup?> _resourceLookupFactory = CreateResourceLookup;
    private static long _nextLookupRetryUtcTicks;
    private static int _formatFailureReported;
    private static int _lookupFailureReported;
    private static int _lookupInitializationFailureReported;

    public static string Get(string resourceKey, string fallback) => GetString(resourceKey, fallback);

    public static string GetString(string resourceKey, string fallback) =>
        GetString(resourceKey, fallback, culture: null);

    /// <summary>
    /// Resolves a resource for an explicit renderer culture. Passing a culture
    /// keeps visible text and UI Automation output on the same language path.
    /// </summary>
    public static string GetString(
        string resourceKey,
        string fallback,
        CultureInfo? culture)
    {
        if (string.IsNullOrWhiteSpace(resourceKey))
        {
            return fallback;
        }

        ResourceLookup? lookup = GetOrCreateResourceLookup();
        if (lookup is null)
        {
            return fallback;
        }

        try
        {
            string? value = lookup(NormalizeResourceKey(resourceKey), culture);
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

    private static ResourceLookup? CreateResourceLookup()
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

        object resourceGate = new();
        var contexts = new Dictionary<string, ResourceContext>(StringComparer.OrdinalIgnoreCase);
        return (resourceKey, culture) =>
        {
            string language = culture?.Name ?? ApplicationLanguages.PrimaryLanguageOverride;
            language = string.IsNullOrWhiteSpace(language) ? string.Empty : language;

            // ResourceContext is mutable and is not documented as safe for concurrent
            // access. Renderer callbacks may arrive concurrently, so serialize lookups
            // and keep one bounded context per language.
            lock (resourceGate)
            {
                if (!contexts.TryGetValue(language, out ResourceContext? resourceContext))
                {
                    if (contexts.Count >= 16)
                    {
                        contexts.Clear();
                    }

                    resourceContext = resourceManager.CreateResourceContext();
                    if (language.Length > 0)
                    {
                        resourceContext.QualifierValues["Language"] = language;
                    }

                    contexts.Add(language, resourceContext);
                }

                ResourceCandidate? candidate = resourceMap.TryGetValue(resourceKey, resourceContext);
                GC.KeepAlive(resourceManager);
                return candidate?.ValueAsString;
            }
        };
    }

    private static ResourceLookup? GetOrCreateResourceLookup()
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

        ResourceLookup? lookup = Volatile.Read(ref _lookup);
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
        TestLookupOverride.Value = new TestResourceLookupOverride(() =>
        {
            Func<string, string?>? lookup = factory();
            return lookup is null ? null : (key, _) => lookup(key);
        });
        return new RestoreAction(() => TestLookupOverride.Value = previousOverride);
    }

    internal static IDisposable OverrideCultureAwareResourceLookupForTests(
        Func<string, CultureInfo?, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        TestResourceLookupOverride? previousOverride = TestLookupOverride.Value;
        TestLookupOverride.Value = new TestResourceLookupOverride(
            () => (key, culture) => lookup(key, culture));
        return new RestoreAction(() => TestLookupOverride.Value = previousOverride);
    }

    private static void ResetResourceLookupAfterFailure(ResourceLookup failedLookup)
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

    private sealed class TestResourceLookupOverride(Func<ResourceLookup?> factory)
    {
        private readonly object _gate = new();
        private Func<ResourceLookup?>? _factory = factory;
        private ResourceLookup? _lookup;

        public ResourceLookup? GetOrCreateLookup()
        {
            Func<ResourceLookup?>? factory = Volatile.Read(ref _factory);
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
