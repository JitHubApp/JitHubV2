using System;
using Microsoft.Graphics.Canvas.Text;

namespace MarkdownRenderer.Layout.Internal.Text;

/// <summary>
/// Turns off the OpenType features that let a code font replace what was
/// typed: standard, contextual and discretionary ligatures and contextual
/// alternates. With them on, a programming font such as JetBrains Mono
/// draws <c>-&gt;</c> as an arrow, <c>=&gt;</c> as a double arrow and
/// <c>-&gt;&gt;</c> as a dash and guillemet, so code no longer reads as its
/// source. This matches CSS <c>font-variant-ligatures: none</c>.
/// </summary>
/// <remarks>
/// One instance serves one styling pass over one layout. It creates its
/// native typography on first use and releases its own reference when
/// disposed; each layout keeps the reference DirectWrite took when the
/// typography was set on it.
/// </remarks>
internal sealed class CodeTextTypography : IDisposable
{
    private static readonly CanvasTypographyFeatureName[] DisabledFeatures =
    [
        CanvasTypographyFeatureName.StandardLigatures,
        CanvasTypographyFeatureName.ContextualLigatures,
        CanvasTypographyFeatureName.DiscretionaryLigatures,
        CanvasTypographyFeatureName.ContextualAlternates,
    ];

    // DirectWrite applies a font's pair kerning by default only while a range
    // has no typography. Setting any typography turns it off (measured: a
    // Hebrew Segoe UI run widens by about half a DIP), so kerning is enabled
    // explicitly to keep code advances identical apart from the ligatures.
    private static readonly CanvasTypographyFeatureName[] RetainedDefaultFeatures =
    [
        CanvasTypographyFeatureName.Kerning,
    ];

    private CanvasTypography? _typography;

    /// <summary>
    /// Applies code typography to a range of <paramref name="layout"/>.
    /// </summary>
    /// <param name="layout">The layout to update.</param>
    /// <param name="start">The first UTF-16 position of the code text.</param>
    /// <param name="length">The UTF-16 length of the code text.</param>
    internal void Apply(CanvasTextLayout layout, int start, int length)
    {
        if (length <= 0)
            return;

        layout.SetTypography(start, length, _typography ??= Create());
    }

    public void Dispose()
    {
        CanvasTypography? typography = _typography;
        _typography = null;
        typography?.Dispose();
    }

    private static CanvasTypography Create()
    {
        var typography = new CanvasTypography();
        try
        {
            foreach (CanvasTypographyFeatureName feature in RetainedDefaultFeatures)
                typography.AddFeature(feature, 1);
            foreach (CanvasTypographyFeatureName feature in DisabledFeatures)
                typography.AddFeature(feature, 0);
            return typography;
        }
        catch
        {
            typography.Dispose();
            throw;
        }
    }
}
