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
    /// <summary>Dependency property backing <see cref="AccentColor"/>.</summary>
    public static readonly DependencyProperty AccentColorProperty =
        DependencyProperty.Register(nameof(AccentColor), typeof(Color?), typeof(MarkdownTheme),
            new PropertyMetadata(null, (d, _) => ((MarkdownTheme)d).Invalidate()));

    /// <summary>Optional accent color used by links and related highlights.</summary>
    public Color? AccentColor
    {
        get => (Color?)GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    /// <summary>
    /// Per-element overrides keyed by <see cref="MarkdownElementKeys"/>.
    /// Each override may set any subset of style fields; unset fields fall
    /// through to the resolver's Win11 defaults.
    /// </summary>
    public IDictionary<string, ElementStyleOverride> Overrides { get; }

    /// <summary>Initializes a new markdown theme.</summary>
    public MarkdownTheme()
    {
        Overrides = new OverrideCollection(this);
    }

    /// <summary>
    /// Bumped whenever the theme changes (overrides assigned, system theme switch).
    /// Used to invalidate text-layout caches.
    /// </summary>
    public int Revision { get; internal set; }

    /// <summary>Raised when a theme property or override changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Forces consumers to rebuild style snapshots after advanced external mutations.</summary>
    public void Invalidate()
        => NotifyChanged();

    internal IReadOnlyDictionary<string, ElementStyleOverride> GetOverridesSnapshot()
        => ((OverrideCollection)Overrides).Snapshot();

    private void NotifyChanged()
    {
        Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class OverrideCollection : IDictionary<string, ElementStyleOverride>
    {
        private readonly MarkdownTheme _owner;
        private readonly Dictionary<string, ElementStyleOverride> _items = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public OverrideCollection(MarkdownTheme owner)
        {
            _owner = owner;
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
                lock (_gate)
                    _items[key] = value ?? throw new ArgumentNullException(nameof(value));
                _owner.NotifyChanged();
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
            lock (_gate)
                _items.Add(key, value ?? throw new ArgumentNullException(nameof(value)));
            _owner.NotifyChanged();
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
                _owner.NotifyChanged();
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
                _owner.NotifyChanged();
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
                _owner.NotifyChanged();
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
