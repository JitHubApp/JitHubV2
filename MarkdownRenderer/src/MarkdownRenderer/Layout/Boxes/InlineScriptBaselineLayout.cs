using System;
using System.Collections.Generic;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Builds the per-character script baseline geometry for an inline
/// container from the superscript and subscript roles of its runs and
/// nested link spans.
/// </summary>
internal static class InlineScriptBaselineLayout
{
    internal static ScriptBaselineGeometry BuildScriptBaselineGeometry(InlineContainerBox box, float containerFontSize)
    {
        float safeFontSize = float.IsFinite(containerFontSize) && containerFontSize > 0f
            ? containerFontSize
            : 16f;
        var builder = new ScriptBaselineGeometry.Builder();
        int cumulative = 0;
        foreach (InlineRun run in box.RunList)
        {
            int length = run.TextLength;
            if (length <= 0)
                continue;

            if (run is not (InlineEmbedRun or InlineImageRun or InlineVectorSceneRun or InlineSpacerRun))
            {
                float runOffset = GetScriptRoleOffset(run, safeFontSize);
                if (run is LinkRun { InlineStyleMap: { } styleMap } && styleMap.Count > 0)
                {
                    int cursor = 0;
                    for (int index = 0; index < styleMap.Count; index++)
                    {
                        LinkInlineStyleSpan span = styleMap.GetSpan(index);
                        int gapLength = span.Start - cursor;
                        builder.Add(cumulative + cursor, gapLength, runOffset);
                        float nestedOffset = GetScriptPathOffset(span.Path, safeFontSize);
                        builder.Add(
                            cumulative + span.Start,
                            span.Length,
                            ClampScriptOffset(runOffset + nestedOffset, safeFontSize));
                        cursor = span.Start + span.Length;
                    }

                    builder.Add(cumulative + cursor, length - cursor, runOffset);
                }
                else
                {
                    builder.Add(cumulative, length, runOffset);
                }
            }

            cumulative = checked(cumulative + length);
        }

        return builder.Build();
    }

    private static float GetScriptRoleOffset(InlineRun run, float containerFontSize)
    {
        float offset = 0f;
        IReadOnlyList<string> ancestry = run.ScriptStyleAncestryKeys;
        if (ancestry.Count > 0)
        {
            for (int index = 0; index < ancestry.Count; index++)
                offset += GetScriptRoleOffset(ancestry[index], containerFontSize);
        }
        else
        {
            if (InlineContainerBox.IsScriptStyleKey(run.ElementKey))
                offset += GetScriptRoleOffset(run.ElementKey, containerFontSize);
            for (int index = 0; index < run.StyleModifierKeys.Count; index++)
                offset += GetScriptRoleOffset(run.StyleModifierKeys[index], containerFontSize);
            if (offset == 0f && run is LinkRun { IsSuperscript: true })
                offset = GetScriptRoleOffset(MarkdownElementKeys.Superscript, containerFontSize);
        }

        return ClampScriptOffset(offset, containerFontSize);
    }

    private static float GetScriptPathOffset(LinkInlineStylePath path, float containerFontSize)
    {
        float offset = 0f;
        for (LinkInlineStylePath? current = path; current is not null; current = current.Parent)
            offset += GetScriptRoleOffset(current.StyleKey, containerFontSize);
        return ClampScriptOffset(offset, containerFontSize);
    }

    private static float GetScriptRoleOffset(string role, float containerFontSize)
        => role switch
        {
            MarkdownElementKeys.Superscript => -containerFontSize * 0.30f,
            MarkdownElementKeys.Subscript => containerFontSize * 0.20f,
            _ => 0f,
        };

    private static float ClampScriptOffset(float offset, float containerFontSize)
        => Math.Clamp(offset, -containerFontSize * 0.85f, containerFontSize * 0.60f);
}
