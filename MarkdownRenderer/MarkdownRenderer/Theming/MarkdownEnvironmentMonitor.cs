using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.System;
using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.ViewManagement;
using MarkdownRenderer.Diagnostics;

namespace MarkdownRenderer.Theming;

/// <summary>Receives shared window-environment changes without being rooted by the monitor.</summary>
internal interface IMarkdownEnvironmentListener
{
    void OnMarkdownEnvironmentChanged(
        in MarkdownEnvironmentSnapshot snapshot,
        MarkdownEnvironmentChange changes);
}

/// <summary>
/// One native environment observer per <see cref="XamlRoot"/>. Controls hold a
/// lightweight lease; the monitor holds only weak references back to controls.
/// </summary>
internal sealed class MarkdownEnvironmentMonitor
{
    private static readonly ConditionalWeakTable<XamlRoot, MarkdownEnvironmentMonitor> Monitors = new();

    private readonly XamlRoot _xamlRoot;
    private readonly DispatcherQueue _dispatcher;
    private readonly List<ListenerEntry> _listeners = new();
    private readonly object _gate = new();
    private UISettings? _uiSettings;
    private ThemeSettings? _themeSettings;
    private FrameworkElement? _rootElement;
    private long _languageCallbackToken;
    private long _flowDirectionCallbackToken;
    private int _nextListenerId;
    private int _refreshQueued;
    private bool _isAttached;
    private MarkdownEnvironmentSnapshot _snapshot;

    private MarkdownEnvironmentMonitor(XamlRoot xamlRoot, DispatcherQueue dispatcher)
    {
        _xamlRoot = xamlRoot;
        _dispatcher = dispatcher;
    }

    /// <summary>Acquires the shared monitor for a loaded element's XamlRoot.</summary>
    public static Subscription? Acquire(
        FrameworkElement host,
        IMarkdownEnvironmentListener listener)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(listener);

        var xamlRoot = host.XamlRoot;
        var dispatcher = host.DispatcherQueue;
        if (xamlRoot is null || dispatcher is null)
            return null;

        var monitor = Monitors.GetValue(xamlRoot, root => new MarkdownEnvironmentMonitor(root, dispatcher));
        return monitor.AddListener(listener);
    }

    private Subscription AddListener(IMarkdownEnvironmentListener listener)
    {
        if (!_dispatcher.HasThreadAccess)
            throw new InvalidOperationException("Markdown environment subscriptions must be acquired on the XAML thread.");

        int id;
        lock (_gate)
        {
            PruneListenersNoLock();
            id = ++_nextListenerId;
            _listeners.Add(new ListenerEntry(id, listener));
        }

        if (!_isAttached)
            Attach();

        return new Subscription(this, id, _snapshot);
    }

    private void Attach()
    {
        if (_isAttached)
            return;

        _isAttached = true;
        try
        {
            var uiSettings = new UISettings();
            uiSettings.ColorValuesChanged += OnColorValuesChanged;
            try
            {
                uiSettings.TextScaleFactorChanged += OnTextScaleFactorChanged;
            }
            catch
            {
                uiSettings.ColorValuesChanged -= OnColorValuesChanged;
                throw;
            }

            _uiSettings = uiSettings;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownEnvironmentMonitor] UISettings subscription failed: {ex.Message}");
            _uiSettings = null;
        }

        try
        {
            if (_xamlRoot.ContentIslandEnvironment is { } island)
            {
                _themeSettings = ThemeSettings.CreateForWindowId(island.AppWindowId);
                _themeSettings.Changed += OnThemeSettingsChanged;
            }
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownEnvironmentMonitor] ThemeSettings subscription failed: {ex.Message}");
            _themeSettings = null;
        }

        _xamlRoot.Changed += OnXamlRootChanged;
        AttachRootElement(_xamlRoot.Content as FrameworkElement);
        _snapshot = CaptureSnapshot().WithRevisions(revision: 1, brushRevision: 1, layoutRevision: 1);
    }

    private void Detach()
    {
        if (!_isAttached)
            return;

        _isAttached = false;
        Interlocked.Exchange(ref _refreshQueued, 0);

        try { _xamlRoot.Changed -= OnXamlRootChanged; }
        catch (Exception ex) { LogDetachFailure(nameof(XamlRoot.Changed), ex); }

        AttachRootElement(null);

        if (_uiSettings is { } uiSettings)
        {
            try { uiSettings.ColorValuesChanged -= OnColorValuesChanged; }
            catch (Exception ex) { LogDetachFailure(nameof(UISettings.ColorValuesChanged), ex); }
            try { uiSettings.TextScaleFactorChanged -= OnTextScaleFactorChanged; }
            catch (Exception ex) { LogDetachFailure(nameof(UISettings.TextScaleFactorChanged), ex); }
        }

        if (_themeSettings is { } themeSettings)
        {
            try { themeSettings.Changed -= OnThemeSettingsChanged; }
            catch (Exception ex) { LogDetachFailure(nameof(ThemeSettings.Changed), ex); }
        }

        _uiSettings = null;
        _themeSettings = null;
    }

    private static void LogDetachFailure(string eventName, Exception exception)
        => MarkdownDiagnostics.WriteLine(
            $"[MarkdownEnvironmentMonitor] {eventName} detach failed: {exception.Message}");

    private void AttachRootElement(FrameworkElement? rootElement)
    {
        if (ReferenceEquals(_rootElement, rootElement))
            return;

        if (_rootElement is { } oldRoot)
        {
            oldRoot.ActualThemeChanged -= OnRootActualThemeChanged;
            if (_languageCallbackToken != 0)
                oldRoot.UnregisterPropertyChangedCallback(FrameworkElement.LanguageProperty, _languageCallbackToken);
            if (_flowDirectionCallbackToken != 0)
                oldRoot.UnregisterPropertyChangedCallback(FrameworkElement.FlowDirectionProperty, _flowDirectionCallbackToken);
        }

        _rootElement = rootElement;
        _languageCallbackToken = 0;
        _flowDirectionCallbackToken = 0;

        if (rootElement is null)
            return;

        rootElement.ActualThemeChanged += OnRootActualThemeChanged;
        _languageCallbackToken = rootElement.RegisterPropertyChangedCallback(
            FrameworkElement.LanguageProperty,
            OnRootEnvironmentPropertyChanged);
        _flowDirectionCallbackToken = rootElement.RegisterPropertyChangedCallback(
            FrameworkElement.FlowDirectionProperty,
            OnRootEnvironmentPropertyChanged);
    }

    private void OnColorValuesChanged(UISettings sender, object args) => QueueRefresh();

    private void OnTextScaleFactorChanged(UISettings sender, object args) => QueueRefresh();

    private void OnThemeSettingsChanged(ThemeSettings sender, object args) => QueueRefresh();

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => QueueRefresh();

    private void OnRootActualThemeChanged(FrameworkElement sender, object args) => QueueRefresh();

    private void OnRootEnvironmentPropertyChanged(DependencyObject sender, DependencyProperty property)
        => QueueRefresh();

    private void QueueRefresh()
    {
        if (!_isAttached || Interlocked.Exchange(ref _refreshQueued, 1) != 0)
            return;

        if (_dispatcher.HasThreadAccess)
        {
            Refresh();
            return;
        }

        if (!_dispatcher.TryEnqueue(Refresh))
            Interlocked.Exchange(ref _refreshQueued, 0);
    }

    private void Refresh()
    {
        Interlocked.Exchange(ref _refreshQueued, 0);
        if (!_isAttached)
            return;

        AttachRootElement(_xamlRoot.Content as FrameworkElement);
        var next = CaptureSnapshot();
        var changes = next.ChangesFrom(_snapshot);
        if (changes == MarkdownEnvironmentChange.None)
            return;

        long revision = _snapshot.Revision + 1;
        long brushRevision = _snapshot.BrushRevision +
            (((changes & MarkdownEnvironmentChange.Brushes) != 0) ? 1 : 0);
        long layoutRevision = _snapshot.LayoutRevision +
            (((changes & MarkdownEnvironmentChange.Relayout) != 0) ? 1 : 0);
        _snapshot = next.WithRevisions(revision, brushRevision, layoutRevision);
        NotifyListeners(_snapshot, changes);
    }

    private MarkdownEnvironmentSnapshot CaptureSnapshot()
    {
        var rootElement = _rootElement;
        double textScale = ReadTextScaleFactor();
        string language = string.IsNullOrWhiteSpace(rootElement?.Language)
            ? CultureInfo.CurrentUICulture.Name
            : rootElement.Language;

        return new MarkdownEnvironmentSnapshot(
            Revision: 0,
            BrushRevision: 0,
            LayoutRevision: 0,
            Theme: (int)(rootElement?.ActualTheme ?? ElementTheme.Default),
            IsHighContrast: ReadHighContrast(),
            TextScaleFactor: textScale > 0 ? textScale : 1.0,
            Language: language ?? string.Empty,
            FlowDirection: (int)(rootElement?.FlowDirection ?? Microsoft.UI.Xaml.FlowDirection.LeftToRight),
            RasterizationScale: Math.Max(0.1, _xamlRoot.RasterizationScale),
            Width: Math.Max(0, rootElement?.ActualWidth ?? 0),
            AccentColor: ReadColor(UIColorType.Accent),
            ForegroundColor: ReadColor(UIColorType.Foreground),
            BackgroundColor: ReadColor(UIColorType.Background));
    }

    private double ReadTextScaleFactor()
    {
        try { return _uiSettings?.TextScaleFactor ?? 1.0; }
        catch { return 1.0; }
    }

    private bool ReadHighContrast()
    {
        try { return _themeSettings?.HighContrast == true; }
        catch { return false; }
    }

    private uint ReadColor(UIColorType colorType)
    {
        try { return ToArgb(_uiSettings?.GetColorValue(colorType) ?? default); }
        catch { return 0; }
    }

    private static uint ToArgb(Color color)
        => ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    private void NotifyListeners(
        in MarkdownEnvironmentSnapshot snapshot,
        MarkdownEnvironmentChange changes)
    {
        List<IMarkdownEnvironmentListener> listeners = new();
        bool hasListeners;
        lock (_gate)
        {
            for (int i = _listeners.Count - 1; i >= 0; i--)
            {
                if (_listeners[i].Listener.TryGetTarget(out var listener))
                    listeners.Add(listener);
                else
                    _listeners.RemoveAt(i);
            }

            hasListeners = _listeners.Count != 0;
        }

        foreach (var listener in listeners)
        {
            try { listener.OnMarkdownEnvironmentChanged(snapshot, changes); }
            catch (Exception ex)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownEnvironmentMonitor] Listener failed: {ex.Message}");
            }
        }

        if (!hasListeners)
            Detach();
    }

    private void RemoveListener(int id)
    {
        bool hasListeners;
        lock (_gate)
        {
            for (int i = _listeners.Count - 1; i >= 0; i--)
            {
                if (_listeners[i].Id == id || !_listeners[i].Listener.TryGetTarget(out _))
                    _listeners.RemoveAt(i);
            }

            hasListeners = _listeners.Count != 0;
        }

        if (!hasListeners)
        {
            if (_dispatcher.HasThreadAccess)
                Detach();
            else
                _dispatcher.TryEnqueue(Detach);
        }
    }

    private void PruneListenersNoLock()
    {
        for (int i = _listeners.Count - 1; i >= 0; i--)
        {
            if (!_listeners[i].Listener.TryGetTarget(out _))
                _listeners.RemoveAt(i);
        }
    }

    private readonly record struct ListenerEntry(
        int Id,
        WeakReference<IMarkdownEnvironmentListener> Listener)
    {
        public ListenerEntry(int id, IMarkdownEnvironmentListener listener)
            : this(id, new WeakReference<IMarkdownEnvironmentListener>(listener))
        {
        }
    }

    internal sealed class Subscription : IDisposable
    {
        private MarkdownEnvironmentMonitor? _owner;
        private readonly int _listenerId;

        internal Subscription(
            MarkdownEnvironmentMonitor owner,
            int listenerId,
            MarkdownEnvironmentSnapshot snapshot)
        {
            _owner = owner;
            _listenerId = listenerId;
            Snapshot = snapshot;
        }

        /// <summary>Gets the environment captured when the lease was acquired.</summary>
        public MarkdownEnvironmentSnapshot Snapshot { get; }

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.RemoveListener(_listenerId);
    }
}
