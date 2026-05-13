using System;
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
    public static readonly DependencyProperty AccentColorProperty =
        DependencyProperty.Register(nameof(AccentColor), typeof(Color?), typeof(MarkdownTheme),
            new PropertyMetadata(null));

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
    public IDictionary<string, ElementStyleOverride> Overrides { get; } = new Dictionary<string, ElementStyleOverride>();

    /// <summary>
    /// Bumped whenever the theme changes (overrides assigned, system theme switch).
    /// Used to invalidate text-layout caches.
    /// </summary>
    public int Revision { get; internal set; }

    public event EventHandler? Changed;

    public void Invalidate()
    {
        Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
