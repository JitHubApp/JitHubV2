using System;
using System.Threading.Tasks;
using MarkdownRenderer.Accessibility.Internal.Peers;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Embeds;

/// <summary>
/// The copy button for one code block. The button is pooled, so a plan can
/// adopt a realized button from the plan it replaces instead of recreating it.
/// </summary>
internal sealed class CodeBlockActionPlan
{
    public CodeBlockBox Box = null!;
    public Rect Rect;
    public CodeBlockHostedElementKind Kind;
    public FocusableItem? FocusIdentity;
    public FrameworkElement? Realized;
    private RoutedEventHandler? _clickHandler;
    private int _feedbackVersion;
    private bool _showsCopiedFeedback;

    public void Realize(EmbedPlanContext context)
    {
        if (Realized is not null) return;
        long generation = context.Published.Generation;
        var (button, alreadyAttached) = context.Host.RentCodeBlockCopyButton();
        if (context.Lifetime.IsDisposed || context.Lifetime.IsUnloaded || generation != context.Published.Generation)
            return;
        AutomationProperties.SetAutomationId(button, MarkdownAutomationIdentity.ForCodeCopy(Box));
        if (context.Lifetime.IsDisposed || context.Lifetime.IsUnloaded || generation != context.Published.Generation)
            return;
        AttachHandlers(button, context);
        button.KeyDown += context.HostedKeyDown;
        Realized = button;
        Box.RealizedCopyButton = button;
        UpdatePlacement();
        if (!ReferenceEquals(Realized, button) || context.Lifetime.IsDisposed || context.Lifetime.IsUnloaded)
            return;
        if (!alreadyAttached)
            context.Surface.Overlay!.Children.Add(button);
        button.Visibility = Visibility.Visible;
        context.Host.RestoreFocusedCodeBlockActionIfNeeded(this);
    }

    public void Derealize(EmbedPlanContext context)
    {
        if (Realized is null) return;
        var fe = Realized;
        bool resetFeedback = _showsCopiedFeedback;
        Box.RealizedCopyButton = null;
        Realized = null;
        _showsCopiedFeedback = false;
        _feedbackVersion++;
        if (fe is Button button)
        {
            if (_clickHandler is not null)
                button.Click -= _clickHandler;
            button.KeyDown -= context.HostedKeyDown;
        }

        context.Host.PreserveLogicalFocusBeforeDerealizingCodeBlockAction(fe);
        if (resetFeedback && fe is Button copiedButton)
            context.Host.SetCodeBlockCopyButtonState(copiedButton, copied: false);
        if (fe is Button pooledButton)
            context.Host.ReturnCodeBlockCopyButton(pooledButton);
        else
        {
            try { context.Surface.Overlay!.Children.Remove(fe); } catch { }
        }
    }

    public void AdoptRealizedFrom(CodeBlockActionPlan oldPlan, EmbedPlanContext context)
    {
        if (oldPlan.Realized is null) return;

        var fe = oldPlan.Realized;
        RoutedEventHandler? oldClickHandler = oldPlan._clickHandler;
        oldPlan._clickHandler = null;
        oldPlan._feedbackVersion++;
        oldPlan._showsCopiedFeedback = false;
        oldPlan.Box.RealizedCopyButton = null;
        oldPlan.Realized = null;
        Realized = fe;
        Box.RealizedCopyButton = fe;
        if (fe is Button button)
        {
            if (oldClickHandler is not null)
                button.Click -= oldClickHandler;
            button.KeyDown -= context.HostedKeyDown;
            AttachHandlers(button, context);
            button.KeyDown += context.HostedKeyDown;
            AutomationProperties.SetAutomationId(button, MarkdownAutomationIdentity.ForCodeCopy(Box));
            if (!ReferenceEquals(Realized, fe) || context.Lifetime.IsDisposed || context.Lifetime.IsUnloaded)
                return;
            context.Host.SetCodeBlockCopyButtonState(button, copied: false);
            if (!ReferenceEquals(Realized, fe) || context.Lifetime.IsDisposed || context.Lifetime.IsUnloaded)
                return;
        }
        UpdatePlacement();
    }

    public bool AdoptRealizedFromRasterCandidate(
        CodeBlockActionPlan oldPlan,
        EmbedPlanContext context,
        LayoutSnapshot candidate,
        long generation,
        long layoutRevision,
        long hostedFocusPlanEpoch)
    {
        if (!context.Host.IsCurrentRasterCodeActionPublication(
                candidate, generation, layoutRevision, hostedFocusPlanEpoch) ||
            oldPlan.Realized is not Button button)
            return false;

        FrameworkElement fe = button;
        RoutedEventHandler? oldClickHandler = oldPlan._clickHandler;
        oldPlan._clickHandler = null;
        oldPlan._feedbackVersion++;
        oldPlan._showsCopiedFeedback = false;
        oldPlan.Box.RealizedCopyButton = null;
        oldPlan.Realized = null;
        Realized = fe;
        Box.RealizedCopyButton = fe;

        if (oldClickHandler is not null)
            button.Click -= oldClickHandler;
        button.KeyDown -= context.HostedKeyDown;
        AttachHandlers(button, context);
        button.KeyDown += context.HostedKeyDown;

        AutomationProperties.SetAutomationId(button, MarkdownAutomationIdentity.ForCodeCopy(Box));
        if (!IsCurrentRasterActionOwner(context.Host, candidate, generation, layoutRevision, hostedFocusPlanEpoch, fe))
            return false;

        if (!context.Host.SetCodeBlockCopyButtonStateForRasterCandidate(
                this, button, fe, candidate, generation, layoutRevision, hostedFocusPlanEpoch))
            return false;

        return UpdatePlacementForRasterCandidate(
            context, candidate, generation, layoutRevision, hostedFocusPlanEpoch, fe);
    }

    private void AttachHandlers(Button button, EmbedPlanContext context)
    {
        _clickHandler ??= (_, _) => context.Host.CopyCodeBlockToClipboard(this);
        button.Click += _clickHandler;
    }

    public bool IsSameLogicalAction(CodeBlockActionPlan other)
        => ReferenceEquals(Box, other.Box) && Kind == other.Kind;

    public void UpdatePlacement()
    {
        FrameworkElement? element = Realized;
        if (element is null) return;
        double left = Math.Round(Rect.X);
        double top = Math.Round(Rect.Y);
        double width = Math.Round(Rect.X + Rect.Width) - left;
        double height = Math.Round(Rect.Y + Rect.Height) - top;
        if (double.IsNaN(element.Width) || Math.Abs(element.Width - width) > 0.1)
            element.Width = width;
        if (!ReferenceEquals(Realized, element)) return;
        if (double.IsNaN(element.Height) || Math.Abs(element.Height - height) > 0.1)
            element.Height = height;
        if (!ReferenceEquals(Realized, element)) return;
        double currentLeft = Canvas.GetLeft(element);
        if (double.IsNaN(currentLeft) || Math.Abs(currentLeft - left) > 0.1)
            Canvas.SetLeft(element, left);
        if (!ReferenceEquals(Realized, element)) return;
        double currentTop = Canvas.GetTop(element);
        if (double.IsNaN(currentTop) || Math.Abs(currentTop - top) > 0.1)
            Canvas.SetTop(element, top);
        if (!ReferenceEquals(Realized, element)) return;
        if (Canvas.GetZIndex(element) != 2)
            Canvas.SetZIndex(element, 2);
    }

    private bool UpdatePlacementForRasterCandidate(
        EmbedPlanContext context,
        LayoutSnapshot candidate,
        long generation,
        long layoutRevision,
        long hostedFocusPlanEpoch,
        FrameworkElement element)
    {
        if (!IsCurrentRasterActionOwner(context.Host, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
            return false;

        double left = Math.Round(Rect.X);
        double top = Math.Round(Rect.Y);
        double width = Math.Round(Rect.X + Rect.Width) - left;
        double height = Math.Round(Rect.Y + Rect.Height) - top;
        if (double.IsNaN(element.Width) || Math.Abs(element.Width - width) > 0.1)
        {
            element.Width = width;
            if (!IsCurrentRasterActionOwner(context.Host, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
                return false;
        }
        if (double.IsNaN(element.Height) || Math.Abs(element.Height - height) > 0.1)
        {
            element.Height = height;
            if (!IsCurrentRasterActionOwner(context.Host, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
                return false;
        }

        double currentLeft = Canvas.GetLeft(element);
        if (double.IsNaN(currentLeft) || Math.Abs(currentLeft - left) > 0.1)
        {
            Canvas.SetLeft(element, left);
            if (!IsCurrentRasterActionOwner(context.Host, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
                return false;
        }
        double currentTop = Canvas.GetTop(element);
        if (double.IsNaN(currentTop) || Math.Abs(currentTop - top) > 0.1)
        {
            Canvas.SetTop(element, top);
            if (!IsCurrentRasterActionOwner(context.Host, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
                return false;
        }
        if (Canvas.GetZIndex(element) != 2)
        {
            Canvas.SetZIndex(element, 2);
            if (!IsCurrentRasterActionOwner(context.Host, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
                return false;
        }
        return true;
    }

    public bool IsCurrentRasterActionOwner(
        IEmbedPlanHost host,
        LayoutSnapshot candidate,
        long generation,
        long layoutRevision,
        long hostedFocusPlanEpoch,
        FrameworkElement element)
        => ReferenceEquals(Realized, element) &&
           ReferenceEquals(Box.RealizedCopyButton, element) &&
           host.IsCurrentRasterCodeActionPublication(
               candidate, generation, layoutRevision, hostedFocusPlanEpoch);

    public void ShowCopiedFeedback(EmbedPlanContext context)
    {
        if (Realized is not Button button)
            return;

        int version = ++_feedbackVersion;
        _showsCopiedFeedback = true;
        context.Host.SetCodeBlockCopyButtonState(button, copied: true);
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(1500).ConfigureAwait(false); }
            catch { return; }

            context.Dispatcher.TryEnqueue(() =>
            {
                if (context.Lifetime.IsUnloaded || _feedbackVersion != version)
                    return;
                if (Realized is Button realizedButton)
                {
                    context.Host.SetCodeBlockCopyButtonState(realizedButton, copied: false);
                    _showsCopiedFeedback = false;
                }
            });
        });
    }
}
