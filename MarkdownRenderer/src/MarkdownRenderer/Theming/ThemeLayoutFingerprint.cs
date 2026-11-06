using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Theming;

/// <summary>
/// The FNV-1a hash of every theme value that affects layout, so a snapshot
/// change that only touches colors can keep the current layout.
/// </summary>
internal static class ThemeLayoutFingerprint
{
    internal static ulong Compute(
        IReadOnlyDictionary<string, ElementStyle> styles,
        float minimumInteractiveSize,
        Thickness documentPadding,
        float blockSpacing,
        byte layoutPolicy,
        bool hasRelativeScriptFontSizeRules,
        bool hasRelativeInlineCodeFontSizeRules)
    {
        const ulong offset = 14695981039346656037UL;
        var keys = new List<string>(styles.Keys);
        keys.Sort(StringComparer.Ordinal);

        // Salt the seed so the effective layout policy participates in the
        // fingerprint without feeding an extra four-byte word through Mix.
        ulong hash = offset ^ layoutPolicy;
        // Even if later absolute declarations mask a relative rule's simple
        // style marker, the policy flag still selects different script
        // shaping and per-run resolution paths. Preserve historical hashes
        // for all snapshots without this opt-in policy.
        if (hasRelativeScriptFontSizeRules)
            hash = Mix(hash, 0x52534350u); // "RSCP"
        // A later absolute declaration may mask the percentage but still opts
        // heading code out of the legacy preserve-containing-size behavior.
        if (hasRelativeInlineCodeFontSizeRules)
            hash = Mix(hash, 0x52494350u); // "RICP"
        hash = Mix(hash, BitConverter.SingleToUInt32Bits(minimumInteractiveSize));
        hash = Mix(hash, documentPadding);
        hash = Mix(hash, BitConverter.SingleToUInt32Bits(blockSpacing));
        foreach (string key in keys)
        {
            hash = Mix(hash, key);
            ElementStyle style = styles[key];
            hash = Mix(hash, style.FontFamily);
            hash = Mix(hash, BitConverter.SingleToUInt32Bits(style.FontSize));
            if (style.RelativeFontSizeScale is float relativeScale)
            {
                hash = Mix(hash, 0x52534331u); // "RSC1"
                hash = Mix(hash, BitConverter.SingleToUInt32Bits(relativeScale));
            }
            hash = Mix(hash, style.FontWeight.Weight);
            hash = Mix(hash, (uint)style.FontStyle);
            hash = Mix(hash, BitConverter.SingleToUInt32Bits(style.AccentBarThickness));
            hash = Mix(hash, style.AccentBarAffectsContentLayout ? 1u : 0u);
            hash = Mix(hash, BitConverter.SingleToUInt32Bits(style.BorderThickness));
            hash = Mix(hash, BitConverter.SingleToUInt32Bits(style.CornerRadius));
            hash = Mix(hash, BitConverter.SingleToUInt32Bits(style.ListIndent));
            hash = Mix(hash, BitConverter.SingleToUInt32Bits(style.NestedListIndent));
            hash = Mix(hash, style.Underline ? 1u : 0u);
            hash = Mix(hash, style.Strikethrough ? 1u : 0u);
            hash = Mix(hash, BitConverter.SingleToUInt32Bits(style.LineHeightMultiplier));
            hash = Mix(hash, style.Margin);
            hash = Mix(hash, style.Padding);
        }

        return hash;
    }

    private static ulong Mix(ulong hash, string? value)
    {
        value ??= string.Empty;
        for (int i = 0; i < value.Length; i++)
        {
            hash ^= value[i];
            hash *= 1099511628211UL;
        }

        // Preserve sequence boundaries ("ab", "c") != ("a", "bc").
        hash ^= 0xff;
        return hash * 1099511628211UL;
    }

    private static ulong Mix(ulong hash, uint value)
    {
        for (int shift = 0; shift < 32; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= 1099511628211UL;
        }

        return hash;
    }

    private static ulong Mix(ulong hash, Thickness value)
    {
        hash = Mix(hash, BitConverter.DoubleToUInt64Bits(value.Left));
        hash = Mix(hash, BitConverter.DoubleToUInt64Bits(value.Top));
        hash = Mix(hash, BitConverter.DoubleToUInt64Bits(value.Right));
        return Mix(hash, BitConverter.DoubleToUInt64Bits(value.Bottom));
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        for (int shift = 0; shift < 64; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= 1099511628211UL;
        }

        return hash;
    }
}
