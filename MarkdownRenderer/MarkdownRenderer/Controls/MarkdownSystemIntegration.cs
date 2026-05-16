using System;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.Resources.Core;
using Windows.Foundation.Collections;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Helpers for matching WinUI system integration behavior around layout direction.
/// </summary>
public static class MarkdownSystemIntegration
{
    public static readonly DependencyProperty UseSystemFlowDirectionProperty =
        DependencyProperty.RegisterAttached(
            "UseSystemFlowDirection",
            typeof(bool),
            typeof(MarkdownSystemIntegration),
            new PropertyMetadata(false, OnUseSystemFlowDirectionChanged));

    private static readonly ConditionalWeakTable<FrameworkElement, LayoutDirectionSubscription> _subscriptions = new();

    public static bool GetUseSystemFlowDirection(FrameworkElement element) =>
        (bool)element.GetValue(UseSystemFlowDirectionProperty);

    public static void SetUseSystemFlowDirection(FrameworkElement element, bool value) =>
        element.SetValue(UseSystemFlowDirectionProperty, value);

    public static void ApplySystemFlowDirection(FrameworkElement element)
    {
        if (element is null) throw new ArgumentNullException(nameof(element));
        element.FlowDirection = GetSystemFlowDirection();
    }

    public static FlowDirection GetSystemFlowDirection()
    {
        try
        {
            var qualifiers = ResourceManager.Current.DefaultContext.QualifierValues;
            if ((qualifiers.TryGetValue("LayoutDirection", out var value) ||
                 qualifiers.TryGetValue("LAYOUTDIRECTION", out value)) &&
                TryParseLayoutDirection(value, out var flowDirection))
            {
                return flowDirection;
            }
        }
        catch { }

        return FlowDirection.LeftToRight;
    }

    internal static bool TryParseLayoutDirection(string? qualifier, out FlowDirection flowDirection)
    {
        if (string.Equals(qualifier, "RTL", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(qualifier, "RightToLeft", StringComparison.OrdinalIgnoreCase))
        {
            flowDirection = FlowDirection.RightToLeft;
            return true;
        }

        if (string.Equals(qualifier, "LTR", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(qualifier, "LeftToRight", StringComparison.OrdinalIgnoreCase))
        {
            flowDirection = FlowDirection.LeftToRight;
            return true;
        }

        flowDirection = FlowDirection.LeftToRight;
        return false;
    }

    private static void OnUseSystemFlowDirectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        if (e.NewValue is true)
        {
            var subscription = _subscriptions.GetValue(element, static key => new LayoutDirectionSubscription(key));
            subscription.Attach();
        }
        else if (_subscriptions.TryGetValue(element, out var subscription))
        {
            subscription.Detach();
            _subscriptions.Remove(element);
        }
    }

    private sealed class LayoutDirectionSubscription
    {
        private readonly FrameworkElement _element;
        private ResourceContext? _context;
        private MapChangedEventHandler<string, string>? _handler;
        private bool _attached;

        public LayoutDirectionSubscription(FrameworkElement element)
        {
            _element = element;
        }

        public void Attach()
        {
            if (_attached) return;
            _attached = true;
            _element.Loaded += OnLoaded;
            _element.Unloaded += OnUnloaded;
            if (_element.IsLoaded) OnLoaded(_element, new RoutedEventArgs());
        }

        public void Detach()
        {
            _element.Loaded -= OnLoaded;
            _element.Unloaded -= OnUnloaded;
            Unsubscribe();
            _attached = false;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplySystemFlowDirection(_element);
            try
            {
                _context = ResourceManager.Current.DefaultContext;
                _handler = (_, _) => EnqueueApply();
                _context.QualifierValues.MapChanged += _handler;
            }
            catch { }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) => Unsubscribe();

        private void Unsubscribe()
        {
            if (_context is not null && _handler is not null)
            {
                try { _context.QualifierValues.MapChanged -= _handler; }
                catch { }
            }

            _context = null;
            _handler = null;
        }

        private void EnqueueApply()
        {
            var dispatcher = _element.DispatcherQueue;
            if (dispatcher is null) return;
            if (dispatcher.HasThreadAccess)
            {
                if (_element.IsLoaded) ApplySystemFlowDirection(_element);
            }
            else
            {
                dispatcher.TryEnqueue(() =>
                {
                    if (_element.IsLoaded) ApplySystemFlowDirection(_element);
                });
            }
        }
    }
}
