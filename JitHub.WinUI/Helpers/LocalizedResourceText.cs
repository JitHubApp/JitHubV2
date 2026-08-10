using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Windows.ApplicationModel.Resources;

namespace JitHub.WinUI.Helpers;

internal static class LocalizedResourceText
{
    private static readonly object LoaderGate = new();
    private static readonly TimeSpan LoaderRetryDelay = TimeSpan.FromSeconds(5);
    private static ResourceLoader? _loader;
    private static Func<ResourceLoader?> _resourceLoaderFactory = CreateResourceLoader;
    private static long _nextLoaderRetryUtcTicks;

    public static string Get(string resourceKey, string fallback) => GetString(resourceKey, fallback);

    public static string GetString(string resourceKey, string fallback)
    {
        if (string.IsNullOrWhiteSpace(resourceKey))
        {
            return fallback;
        }

        ResourceLoader? loader = GetOrCreateResourceLoader();
        if (loader is null)
        {
            return fallback;
        }

        try
        {
            string value = loader.GetString(NormalizeResourceKey(resourceKey));
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
        catch (COMException)
        {
            // Unpackaged development and isolated automation can lose their PRI
            // resource map while the process is still running. Every caller
            // supplies canonical fallback text, so keep the UI usable.
            ResetResourceLoaderAfterFailure(loader);
            return fallback;
        }
    }

    public static string Format(string resourceKey, string fallback, params object?[] arguments)
    {
        string format = GetString(resourceKey, fallback);
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }

    private static string NormalizeResourceKey(string resourceKey)
    {
        return resourceKey.Replace('.', '/');
    }

    private static ResourceLoader? CreateResourceLoader()
    {
        if (JitHub.Services.Markdown.MarkdownLifecycleAutomationBridge.IsResourceMapForcedAbsent)
        {
            return null;
        }

        // ResourceLoader can terminate an isolated test host before managed exception
        // handling runs because that process has no Windows application resource map.
        // The production executable owns the PRI context; every other host uses the
        // caller-provided fallback unless a test explicitly installs a loader factory.
        if (!string.Equals(
                Assembly.GetEntryAssembly()?.GetName().Name,
                "JitHub.WinUI",
                StringComparison.Ordinal))
        {
            return null;
        }

        return new ResourceLoader();
    }

    private static ResourceLoader? GetOrCreateResourceLoader()
    {
        ResourceLoader? loader = Volatile.Read(ref _loader);
        if (loader is not null)
        {
            return loader;
        }

        long now = DateTime.UtcNow.Ticks;
        if (now < Interlocked.Read(ref _nextLoaderRetryUtcTicks))
        {
            return null;
        }

        lock (LoaderGate)
        {
            loader = _loader;
            if (loader is not null)
            {
                return loader;
            }

            now = DateTime.UtcNow.Ticks;
            if (now < _nextLoaderRetryUtcTicks)
            {
                return null;
            }

            try
            {
                loader = _resourceLoaderFactory();
                if (loader is null)
                {
                    Interlocked.Exchange(
                        ref _nextLoaderRetryUtcTicks,
                        DateTime.UtcNow.Add(LoaderRetryDelay).Ticks);
                    return null;
                }

                Volatile.Write(ref _loader, loader);
                Interlocked.Exchange(ref _nextLoaderRetryUtcTicks, 0);
                return loader;
            }
            catch (COMException)
            {
                Interlocked.Exchange(
                    ref _nextLoaderRetryUtcTicks,
                    DateTime.UtcNow.Add(LoaderRetryDelay).Ticks);
                return null;
            }
        }
    }

    internal static IDisposable OverrideResourceLoaderFactoryForTests(Func<ResourceLoader?> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        lock (LoaderGate)
        {
            Func<ResourceLoader?> previousFactory = _resourceLoaderFactory;
            ResourceLoader? previousLoader = _loader;
            long previousRetryTicks = _nextLoaderRetryUtcTicks;

            _resourceLoaderFactory = factory;
            Volatile.Write(ref _loader, null);
            Interlocked.Exchange(ref _nextLoaderRetryUtcTicks, 0);

            return new RestoreAction(() =>
            {
                lock (LoaderGate)
                {
                    _resourceLoaderFactory = previousFactory;
                    Volatile.Write(ref _loader, previousLoader);
                    Interlocked.Exchange(ref _nextLoaderRetryUtcTicks, previousRetryTicks);
                }
            });
        }
    }

    private static void ResetResourceLoaderAfterFailure(ResourceLoader failedLoader)
    {
        lock (LoaderGate)
        {
            if (!ReferenceEquals(_loader, failedLoader))
            {
                return;
            }

            Volatile.Write(ref _loader, null);
            Interlocked.Exchange(
                ref _nextLoaderRetryUtcTicks,
                DateTime.UtcNow.Add(LoaderRetryDelay).Ticks);
        }
    }

    private sealed class RestoreAction(Action restore) : IDisposable
    {
        private Action? _restore = restore;

        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
