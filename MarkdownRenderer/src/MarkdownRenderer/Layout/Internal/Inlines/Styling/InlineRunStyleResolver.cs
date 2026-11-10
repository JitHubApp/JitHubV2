using System;
using System.Collections.Generic;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;

namespace MarkdownRenderer.Layout.Internal.Inlines.Styling;

/// <summary>
/// Resolves the theme style of an inline run, including heading
/// inheritance, nested role modifiers, relative script sizes and nested
/// link span styles. Results are cached by the owning InlineContainerBox.
/// </summary>
internal static class InlineRunStyleResolver
{
    internal static ElementStyle ResolveRunStyle(InlineContainerBox box, InlineRun run)
    {
        ElementStyle resolved = ResolveBaseRunStyle(
            box,
            run,
            out string headingKey,
            out bool inheritsHeading,
            out float? relativeInlineCodeScale,
            out int modifierStart);
        float? absoluteInlineCodeFontSize =
            run.ElementKey == MarkdownElementKeys.CodeInline &&
            box.Context.ThemeSnapshot.HasRelativeInlineCodeFontSizeRules &&
            relativeInlineCodeScale is null
                ? resolved.FontSize
                : null;
        resolved = ApplyRunStyleModifiers(
            box,
            run,
            resolved,
            modifierStart,
            out float? scriptModifierContainingFontSize);
        float scriptFontSize = resolved.FontSize;
        bool appliedScriptFontSize = absoluteInlineCodeFontSize is null &&
            TryResolveRelativeScriptFontSize(
                box,
                run,
                headingKey,
                inheritsHeading,
                resolved,
                scriptModifierContainingFontSize,
                relativeInlineCodeScale.HasValue ||
                    run is LinkRun { InlineStyleMap: { } linkStyleMap } &&
                    HasRelativeInlineCodeStylePath(box, run, linkStyleMap),
                out scriptFontSize);
        if (absoluteInlineCodeFontSize is float absoluteCodeSize)
            resolved = ThemeSnapshot.WithFontSize(resolved, absoluteCodeSize);
        if (appliedScriptFontSize)
            resolved = ThemeSnapshot.WithFontSize(resolved, scriptFontSize);
        if (relativeInlineCodeScale is float inlineCodeScale)
        {
            // A CodeInline run is the leaf after its containing Markdown or
            // safe-HTML script scopes. Resolve those scopes first, then apply
            // the inline-code percentage exactly once against that computed
            // containing size. Keeping the scale separately also prevents
            // intermediate style modifiers from erasing its provenance.
            float containingFontSize = appliedScriptFontSize
                ? scriptFontSize
                : GetContainingRunFontSize(box, headingKey, inheritsHeading);
            resolved = ThemeSnapshot.WithFontSize(
                resolved,
                InlineNestedStyleModifier.ResolveRelativeInlineCodeFontSize(containingFontSize, inlineCodeScale));
        }

        return resolved;
    }

    // The heading-inherited or run-own base style, and where the remaining
    // modifier keys start.
    private static ElementStyle ResolveBaseRunStyle(
        InlineContainerBox box,
        InlineRun run,
        out string headingKey,
        out bool inheritsHeading,
        out float? relativeInlineCodeScale,
        out int modifierStart)
    {
        // A link or formatting run inside a heading inherits the heading's
        // typography. Its own role supplies only the nested decoration/color;
        // resolving Link as the base style would silently turn linked heading
        // text back into body-sized text.
        headingKey = GetHeadingStyleKey(box, run);
        inheritsHeading = headingKey.Length > 0;
        var key = inheritsHeading
            ? headingKey
            : string.IsNullOrEmpty(run.ElementKey) ? box.ElementKey : run.ElementKey;
        IReadOnlyList<string> baseAliases = inheritsHeading
            ? box.StyleAliasKeys
            : box.GetEffectiveRunAliases(run.InlineIndex);
        ElementStyle resolved = box.Context.ThemeSnapshot.GetStyle(
            key,
            box.StyleContextKeys,
            baseAliases,
            box.CodeLanguage,
            box.StyleState);
        relativeInlineCodeScale = !inheritsHeading &&
            run.ElementKey == MarkdownElementKeys.CodeInline
                ? resolved.RelativeFontSizeScale
                : null;
        modifierStart = 0;
        if (inheritsHeading && !string.IsNullOrEmpty(run.ElementKey) &&
            !string.Equals(run.ElementKey, headingKey, StringComparison.Ordinal))
        {
            ElementStyle ownStyle = box.Context.ThemeSnapshot.GetStyle(
                run.ElementKey,
                box.StyleContextKeys,
                box.GetEffectiveRunAliases(run.InlineIndex),
                box.CodeLanguage,
                box.StyleState);
            if (run.ElementKey == MarkdownElementKeys.CodeInline)
                relativeInlineCodeScale = ownStyle.RelativeFontSizeScale;
            resolved = InlineNestedStyleModifier.ApplyNestedStyleModifier(
                resolved,
                ownStyle,
                run.ElementKey,
                preserveCodeFontSize: inheritsHeading &&
                    run.ElementKey == MarkdownElementKeys.CodeInline &&
                    !box.Context.ThemeSnapshot.HasRelativeInlineCodeFontSizeRules,
                deferRelativeInlineCodeFontSize: relativeInlineCodeScale.HasValue);
        }
        else if (!inheritsHeading && string.IsNullOrEmpty(run.ElementKey) &&
                 run.StyleModifierKeys.Count > 0)
        {
            resolved = box.Context.ThemeSnapshot.GetStyle(
                run.StyleModifierKeys[0],
                box.StyleContextKeys,
                box.GetEffectiveRunAliases(run.InlineIndex),
                box.CodeLanguage,
                box.StyleState);
            modifierStart = 1;
        }

        return resolved;
    }

    // Applies each remaining modifier in order, recording the font size that
    // contains the first script modifier.
    private static ElementStyle ApplyRunStyleModifiers(
        InlineContainerBox box,
        InlineRun run,
        ElementStyle resolved,
        int modifierStart,
        out float? scriptModifierContainingFontSize)
    {
        scriptModifierContainingFontSize = null;
        for (int index = modifierStart; index < run.StyleModifierKeys.Count; index++)
        {
            string modifierKey = run.StyleModifierKeys[index];
            if (scriptModifierContainingFontSize is null && IsScriptStyleKey(modifierKey))
                scriptModifierContainingFontSize = resolved.FontSize;

            ElementStyle modifier = box.Context.ThemeSnapshot.GetStyle(
                modifierKey,
                box.StyleContextKeys,
                box.GetEffectiveRunAliases(run.InlineIndex),
                box.CodeLanguage,
                box.StyleState);
            resolved = InlineNestedStyleModifier.ApplyNestedStyleModifier(resolved, modifier, modifierKey);
        }
        return resolved;
    }

    private static float GetContainingRunFontSize(InlineContainerBox box, string headingKey, bool inheritsHeading)
    {
        if (!inheritsHeading)
            return box.GetContainerStyle().FontSize;

        // The run's class/ID aliases describe this inline descendant. A
        // heading selector must use the containing block's aliases instead;
        // otherwise a class on <code> can accidentally change the heading
        // size used as its percentage basis.
        return box.Context.ThemeSnapshot.GetStyle(
            headingKey,
            box.StyleContextKeys,
            box.StyleAliasKeys,
            box.CodeLanguage,
            box.StyleState).FontSize;
    }

    private static bool HasRelativeInlineCodeStylePath(InlineContainerBox box, InlineRun run, LinkInlineStyleMap map)
    {
        if (!box.Context.ThemeSnapshot.HasRelativeInlineCodeFontSizeRules)
            return false;

        bool hasScriptScope = run.ScriptStyleAncestryKeys.Count > 0 ||
            IsScriptStyleKey(run.ElementKey) ||
            run is LinkRun { IsSuperscript: true };
        for (int index = 0; !hasScriptScope && index < run.StyleModifierKeys.Count; index++)
            hasScriptScope = IsScriptStyleKey(run.StyleModifierKeys[index]);
        if (!hasScriptScope)
            return false;

        for (int spanIndex = 0; spanIndex < map.Count; spanIndex++)
        {
            for (LinkInlineStylePath? current = map.GetSpan(spanIndex).Path;
                 current is not null;
                 current = current.Parent)
            {
                if (current.StyleKey != MarkdownElementKeys.CodeInline)
                    continue;

                ElementStyle codeStyle = box.Context.ThemeSnapshot.GetStyle(
                    current.StyleKey,
                    box.StyleContextKeys,
                    current.StyleAliases,
                    box.CodeLanguage,
                    box.StyleState);
                if (codeStyle.RelativeFontSizeScale.HasValue)
                    return true;
            }
        }

        return false;
    }

    internal static ElementStyle[] ResolveLinkInlineSpanStyles(InlineContainerBox box, LinkRun link, LinkInlineStyleMap map)
    {
        var spanStyles = new ElementStyle[map.Count];
        ElementStyle linkStyle = box.GetRunStyle(link);
        bool preserveHeadingCodeFontSize = GetHeadingStyleKey(box, link).Length > 0;
        var pathScratch = new List<LinkInlineStylePath>();
        for (int index = 0; index < spanStyles.Length; index++)
        {
            spanStyles[index] = ResolveLinkInlineStylePath(
                box,
                linkStyle,
                map.GetSpan(index).Path,
                preserveHeadingCodeFontSize,
                pathScratch);
        }
        return spanStyles;
    }

    private static ElementStyle ResolveLinkInlineStylePath(
        InlineContainerBox box,
        ElementStyle basis,
        LinkInlineStylePath path,
        bool preserveHeadingCodeFontSize,
        List<LinkInlineStylePath> pathScratch)
    {
        // Paths are immutable parent-linked chains built from nested inline
        // containers. Resolve them iteratively so adversarially deep Markdown
        // nesting cannot add another recursive layout-time walk. The scratch
        // list is reused for each span in this map and is discarded after the
        // one-time style-cache fill.
        pathScratch.Clear();
        for (LinkInlineStylePath? current = path; current is not null; current = current.Parent)
            pathScratch.Add(current);

        for (int index = pathScratch.Count - 1; index >= 0; index--)
        {
            LinkInlineStylePath current = pathScratch[index];
            ElementStyle modifier = box.Context.ThemeSnapshot.GetStyle(
                current.StyleKey,
                box.StyleContextKeys,
                current.StyleAliases,
                box.CodeLanguage,
                box.StyleState);
            ElementStyle containingStyle = basis;
            ElementStyle resolved = InlineNestedStyleModifier.ApplyNestedStyleModifier(
                basis,
                modifier,
                current.StyleKey,
                preserveCodeFontSize: preserveHeadingCodeFontSize &&
                    current.StyleKey == MarkdownElementKeys.CodeInline &&
                    !box.Context.ThemeSnapshot.HasRelativeInlineCodeFontSizeRules);
            if (IsScriptStyleKey(current.StyleKey))
            {
                float fontSize = modifier.RelativeFontSizeScale is float scale
                    ? Math.Max(ThemeSnapshot.MinimumRelativeScriptFontSize, containingStyle.FontSize * scale)
                    : modifier.FontSize;
                resolved = ThemeSnapshot.WithFontSize(resolved, fontSize);
            }
            basis = resolved;
        }
        return basis;
    }

    private static bool TryResolveRelativeScriptFontSize(
        InlineContainerBox box,
        InlineRun run,
        string headingKey,
        bool inheritsHeading,
        ElementStyle resolved,
        float? scriptModifierContainingFontSize,
        bool hasRelativeInlineCodeRule,
        out float fontSize)
    {
        fontSize = scriptModifierContainingFontSize ?? resolved.FontSize;
        ThemeSnapshot theme = box.Context.ThemeSnapshot;
        if (!theme.HasRelativeScriptFontSizeRules && !hasRelativeInlineCodeRule)
            return false;

        IReadOnlyList<string> scriptAncestry = run.ScriptStyleAncestryKeys;
        bool runElementIsScript = IsScriptStyleKey(run.ElementKey);
        bool hasScriptScope = scriptAncestry.Count > 0 || runElementIsScript ||
            run is LinkRun { IsSuperscript: true };
        for (int index = 0; !hasScriptScope && index < run.StyleModifierKeys.Count; index++)
            hasScriptScope = IsScriptStyleKey(run.StyleModifierKeys[index]);
        if (!hasScriptScope)
            return false;

        string[] aliases = box.GetEffectiveRunAliases(run.InlineIndex);
        if (hasRelativeInlineCodeRule)
        {
            // InlineCode is nested inside these run-level script ancestors;
            // start at the computed linked/heading role before any script
            // modifier has replaced its nominal size. A direct CodeInline run
            // uses its actual container because its initial resolved style is
            // already CodeInline.
            fontSize = run.ElementKey == MarkdownElementKeys.CodeInline
                ? GetContainingRunFontSize(box, headingKey, inheritsHeading)
                : scriptModifierContainingFontSize ?? resolved.FontSize;
        }
        else if (runElementIsScript ||
            (!inheritsHeading && string.IsNullOrEmpty(run.ElementKey) &&
             run.StyleModifierKeys.Count > 0 && IsScriptStyleKey(run.StyleModifierKeys[0])))
        {
            // Script-role defaults are nominal absolute sizes. Start from the
            // actual containing text style so relative rules compound through
            // headings, quotes, tables, and nested safe-HTML/Markdown scopes.
            string containingKey = inheritsHeading ? headingKey : box.ElementKey;
            fontSize = theme.GetStyle(
                containingKey,
                box.StyleContextKeys,
                box.StyleAliasKeys,
                box.CodeLanguage,
                box.StyleState).FontSize;
        }

        if (scriptAncestry.Count > 0)
        {
            for (int index = 0; index < scriptAncestry.Count; index++)
                ApplyScriptFontSize(box, theme, scriptAncestry[index], aliases, ref fontSize);
        }
        else
        {
            // Preserve compatibility for internally-created runs that did not
            // pass through the parser's ordered ancestry capture.
            if (runElementIsScript)
                ApplyScriptFontSize(box, theme, run.ElementKey, aliases, ref fontSize);

            for (int index = 0; index < run.StyleModifierKeys.Count; index++)
            {
                string modifierKey = run.StyleModifierKeys[index];
                if (!IsScriptStyleKey(modifierKey))
                    continue;
                ApplyScriptFontSize(box, theme, modifierKey, aliases, ref fontSize);
            }

            if (run is LinkRun { IsSuperscript: true })
                ApplyScriptFontSize(box, theme, MarkdownElementKeys.Superscript, aliases, ref fontSize);
        }

        return true;
    }

    private static void ApplyScriptFontSize(
        InlineContainerBox box,
        ThemeSnapshot theme,
        string scriptRole,
        IReadOnlyList<string> aliases,
        ref float fontSize)
    {
        ElementStyle scriptStyle = theme.GetStyle(
            scriptRole,
            box.StyleContextKeys,
            aliases,
            box.CodeLanguage,
            box.StyleState);
        fontSize = scriptStyle.RelativeFontSizeScale is float scale
            ? Math.Max(ThemeSnapshot.MinimumRelativeScriptFontSize, fontSize * scale)
            : scriptStyle.FontSize;
    }
    internal static bool IsScriptStyleKey(string key)
        => key is MarkdownElementKeys.Subscript or MarkdownElementKeys.Superscript;

    private static string GetHeadingStyleKey(InlineContainerBox box, InlineRun run)
        => !string.IsNullOrEmpty(run.SemanticHeadingKey)
            ? run.SemanticHeadingKey
            : IsHeadingElement(box.ElementKey) ? box.ElementKey : string.Empty;

    internal static bool IsHeadingElement(string elementKey)
        => elementKey is MarkdownElementKeys.Heading1
            or MarkdownElementKeys.Heading2
            or MarkdownElementKeys.Heading3
            or MarkdownElementKeys.Heading4
            or MarkdownElementKeys.Heading5
            or MarkdownElementKeys.Heading6;
}
