using System;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Layout.Internal.Boxes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Embeds;

/// <summary>A block-level hosted element created by an <see cref="EmbedBox"/> factory.</summary>
internal sealed class BlockEmbedPlan : EmbedPlan
{
    public EmbedBox Box = null!;
    public override object LogicalOwner => Box;
    public override void Realize(EmbedPlanContext context)
    {
        if (Realized is not null) return;
        long generation = context.Published.Generation;
        try
        {
            var fe = Box.Factory.CreateBlock(Box.SourceBlock);
            if (context.Lifetime.IsDisposed || context.Lifetime.IsUnloaded || generation != context.Published.Generation)
            {
                try { Box.Factory.RecycleBlock(Box.SourceBlock, fe); } catch { }
                return;
            }
            Realized = fe;
            Box.RealizedElement = fe;
            UpdatePlacement();
            if (!ReferenceEquals(Realized, fe) || context.Lifetime.IsDisposed || context.Lifetime.IsUnloaded)
                return;
            fe.KeyDown += context.HostedKeyDown;
            context.Surface.Overlay!.Children.Add(fe);
            // EmbedHitTargets is rebuilt by the next visible-realization pass.
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] EmbedBox factory threw: {ex.Message}");
        }
    }
    public override void Derealize(EmbedPlanContext context)
    {
        if (Realized is null) return;
        var fe = Realized;
        Realized = null;
        Box.RealizedElement = null;
        fe.KeyDown -= context.HostedKeyDown;
        try { context.Surface.Overlay!.Children.Remove(fe); } catch { }
        try { Box.Factory.RecycleBlock(Box.SourceBlock, fe); } catch { }
        // EmbedHitTargets is rebuilt from realized plans by each visible-realization pass.
    }

    public override bool IsSameLogicalEmbed(EmbedPlan other)
        => other is BlockEmbedPlan block && ReferenceEquals(Box, block.Box);

    public override void UpdatePlacement() => UpdatePlacement(snapEdges: false);
}
