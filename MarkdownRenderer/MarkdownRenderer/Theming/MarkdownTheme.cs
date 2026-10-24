using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MarkdownRenderer.Theming;

/// <summary>
/// A consumer-customizable theme. Holds per-element-key style overrides. The
/// effective style at render time merges defaults (from Win11 typography &amp;
/// theme brushes resolved against current ActualTheme) with these overrides.
/// </summary>
public sealed partial class MarkdownTheme : DependencyObject
{
    private int _updateDepth;
    private bool _hasDeferredChange;

    /// <summary>Dependency property backing <see cref="AccentColor"/>.</summary>
    public static readonly DependencyProperty AccentColorProperty =
        DependencyProperty.Register(nameof(AccentColor), typeof(Color?), typeof(MarkdownTheme),
            new PropertyMetadata(null, (d, _) => ((MarkdownTheme)d).NotifyChanged()));

    /// <summary>Optional accent color used by links and related highlights.</summary>
    public Color? AccentColor
    {
        get => (Color?)GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    /// <summary>Dependency property backing <see cref="SurfaceColor"/>.</summary>
    public static readonly DependencyProperty SurfaceColorProperty =
        DependencyProperty.Register(nameof(SurfaceColor), typeof(Color?), typeof(MarkdownTheme),
            new PropertyMetadata(null, (d, _) => ((MarkdownTheme)d).NotifyChanged()));

    /// <summary>
    /// Optional document surface color used to clear the markdown canvas.
    /// High-contrast themes continue to use the system window color.
    /// </summary>
    public Color? SurfaceColor
    {
        get => (Color?)GetValue(SurfaceColorProperty);
        set => SetValue(SurfaceColorProperty, value);
    }

    /// <summary>
    /// Per-element overrides keyed by <see cref="MarkdownElementKeys"/>, including
    /// extension-defined element, context, class, and identifier aliases. These
    /// legacy lookup keys are not constrained to the <see cref="MarkdownStyleRole"/>
    /// resource-role namespace.
    /// Each override may set any subset of style fields; unset fields fall
    /// through to the resolver's Win11 defaults.
    /// </summary>
    public IDictionary<string, ElementStyleOverride> Overrides { get; }

    /// <summary>Initializes a new markdown theme.</summary>
    public MarkdownTheme()
    {
        Overrides = new OverrideCollection(NotifyChanged);
    }

    /// <summary>
    /// Bumped whenever the theme changes (overrides assigned, system theme switch).
    /// Used to invalidate text-layout caches.
    /// </summary>
    public int Revision { get; internal set; }

    /// <summary>Raised when a theme property or override changes.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Forces consumers to rebuild style snapshots after advanced mutations to
    /// this theme. Use <see cref="Controls.MarkdownRendererControl.InvalidateThemeResources"/>
    /// after changing the keys in a WinUI resource dictionary.
    /// </summary>
    public void Invalidate()
        => NotifyChanged();

    /// <summary>
    /// Advances the theme revision for a renderer-observed environment change
    /// without invalidating resource-key discovery; the dictionary key set did
    /// not change merely because the active Light/Dark/HighContrast selection did.
    /// </summary>
    internal void NotifyEnvironmentChanged()
        => NotifyChanged();

    /// <summary>
    /// Defers change notifications until a group of theme mutations is complete.
    /// </summary>
    public IDisposable BeginUpdate()
    {
        _updateDepth++;
        return new UpdateScope(this);
    }

    internal IReadOnlyDictionary<string, ElementStyleOverride> GetOverridesSnapshot()
        => ((OverrideCollection)Overrides).Snapshot();

    private void NotifyChanged()
    {
        if (_updateDepth > 0)
        {
            _hasDeferredChange = true;
            return;
        }

        Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void EndUpdate()
    {
        if (_updateDepth <= 0)
            return;

        _updateDepth--;
        if (_updateDepth == 0 && _hasDeferredChange)
        {
            _hasDeferredChange = false;
            NotifyChanged();
        }
    }

    private sealed class UpdateScope : IDisposable
    {
        private MarkdownTheme? _owner;

        public UpdateScope(MarkdownTheme owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            var owner = _owner;
            if (owner is null)
                return;

            _owner = null;
            owner.EndUpdate();
        }
    }

    internal sealed class OverrideCollection : IDictionary<string, ElementStyleOverride>
    {
        private readonly Action _notifyChanged;
        private readonly Dictionary<string, ElementStyleOverride> _items = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        internal OverrideCollection(Action notifyChanged)
        {
            _notifyChanged = notifyChanged ?? throw new ArgumentNullException(nameof(notifyChanged));
        }

        public ElementStyleOverride this[string key]
        {
            get
            {
                lock (_gate)
                    return _items[key];
            }
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                lock (_gate)
                    _items[key] = value;
                _notifyChanged();
            }
        }

        public ICollection<string> Keys
        {
            get
            {
                lock (_gate)
                    return new List<string>(_items.Keys);
            }
        }

        public ICollection<ElementStyleOverride> Values
        {
            get
            {
                lock (_gate)
                    return new List<ElementStyleOverride>(_items.Values);
            }
        }

        public int Count
        {
            get
            {
                lock (_gate)
                    return _items.Count;
            }
        }

        public bool IsReadOnly => false;

        public void Add(string key, ElementStyleOverride value)
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
                _items.Add(key, value);
            _notifyChanged();
        }

        public bool ContainsKey(string key)
        {
            lock (_gate)
                return _items.ContainsKey(key);
        }

        public bool Remove(string key)
        {
            bool removed;
            lock (_gate)
                removed = _items.Remove(key);
            if (removed)
                _notifyChanged();
            return removed;
        }

        public bool TryGetValue(string key, out ElementStyleOverride value)
        {
            lock (_gate)
                return _items.TryGetValue(key, out value!);
        }

        public void Add(KeyValuePair<string, ElementStyleOverride> item)
            => Add(item.Key, item.Value);

        public void Clear()
        {
            bool changed;
            lock (_gate)
            {
                changed = _items.Count > 0;
                _items.Clear();
            }
            if (changed)
                _notifyChanged();
        }

        public bool Contains(KeyValuePair<string, ElementStyleOverride> item)
        {
            lock (_gate)
                return ((ICollection<KeyValuePair<string, ElementStyleOverride>>)_items).Contains(item);
        }

        public void CopyTo(KeyValuePair<string, ElementStyleOverride>[] array, int arrayIndex)
        {
            KeyValuePair<string, ElementStyleOverride>[] snapshot;
            lock (_gate)
            {
                snapshot = new KeyValuePair<string, ElementStyleOverride>[_items.Count];
                ((ICollection<KeyValuePair<string, ElementStyleOverride>>)_items).CopyTo(snapshot, 0);
            }

            snapshot.CopyTo(array, arrayIndex);
        }

        public bool Remove(KeyValuePair<string, ElementStyleOverride> item)
        {
            bool removed;
            lock (_gate)
                removed = ((ICollection<KeyValuePair<string, ElementStyleOverride>>)_items).Remove(item);
            if (removed)
                _notifyChanged();
            return removed;
        }

        public IEnumerator<KeyValuePair<string, ElementStyleOverride>> GetEnumerator()
            => Snapshot().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        internal IReadOnlyDictionary<string, ElementStyleOverride> Snapshot()
        {
            lock (_gate)
                return new Dictionary<string, ElementStyleOverride>(_items, StringComparer.Ordinal);
        }
    }
}
