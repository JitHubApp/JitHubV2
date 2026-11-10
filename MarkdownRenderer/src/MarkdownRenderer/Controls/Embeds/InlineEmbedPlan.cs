using System;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Embeds;

/// <summary>An inline hosted element created by an <see cref="InlineEmbedRun"/> factory.</summary>
internal sealed class InlineEmbedPlan : EmbedPlan
{
    public InlineContainerBox Icb = null!;
    public InlineEmbedRun Run = null!;
    public override object LogicalOwner => Icb;
    public override void Realize(EmbedPlanContext context)
    {
        if (Realized is not null) return;
        long generation = context.Published.Generation;
        try
        {
            var fe = Run.ElementFactory();
            if (context.Lifetime.IsDisposed || context.Lifetime.IsUnloaded || generation != context.Published.Generation)
            {
                try { Run.Recycle?.Invoke(fe); } catch { }
                return;
            }
            Realized = fe;
            Run.RealizedElement = fe;
            UpdatePlacement();
            if (!ReferenceEquals(Realized, fe) || context.Lifetime.IsDisposed || context.Lifetime.IsUnloaded)
                return;
            fe.KeyDown += context.HostedKeyDown;
            context.Surface.Overlay!.Children.Add(fe);
            // EmbedHitTargets is rebuilt by the next visible-realization pass.
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] inline embed factory threw: {ex.Message}");
        }
    }
    public override void Derealize(EmbedPlanContext context)
    {
        if (Realized is null) return;
        var fe = Realized;
        Realized = null;
        Run.RealizedElement = null;
        fe.KeyDown -= context.HostedKeyDown;
        try { context.Surface.Overlay!.Children.Remove(fe); } catch { }
        try { Run.Recycle?.Invoke(fe); }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] inline embed Recycle threw: {ex.Message}");
        }
    }

    public override bool IsSameLogicalEmbed(EmbedPlan other)
        => other is InlineEmbedPlan inline &&
           ReferenceEquals(Icb, inline.Icb) &&
           ReferenceEquals(Run, inline.Run);

}
