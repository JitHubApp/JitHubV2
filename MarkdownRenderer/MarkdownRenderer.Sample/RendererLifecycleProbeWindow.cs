using System;
using System.Threading.Tasks;
using MarkdownRenderer.Controls;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

// The probe checks the common implementation of both public viewport types.
#pragma warning disable MR1001

namespace MarkdownRenderer.Sample;

// Opt-in release probe: exercise the actual native child lifecycle, including
// reuse of the same view in a different host. No test path in production code.
internal sealed class RendererLifecycleProbeWindow : Window
{
    private readonly TextBlock _status = new() { Text = "Running lifecycle probe" };
    private readonly Grid _left = new();
    private readonly Grid _right = new();
    private readonly MarkdownScrollView _owned = new();
    private readonly MarkdownDocumentView _document = new();
    private readonly ScrollViewer _ancestor = new();
    private readonly Grid _selectionProbeHost = new();
    private CheckBox? _selectionProbeEnabled;
    private CheckBox? _selectionProbeAncestor;
    private TextBlock? _selectionProbeStatus;
    private bool _selectionProbeUsesAncestor;

    internal RendererLifecycleProbeWindow(bool selectionDisableProbe = false)
    {
        if (selectionDisableProbe)
        {
            BuildSelectionDisableProbe();
            return;
        }

        Title = "MarkdownRenderer lifecycle regression";
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new());
        root.ColumnDefinitions.Add(new());
        Grid.SetColumnSpan(_status, 2);
        AutomationProperties.SetAutomationId(_status, "LifecycleStatus");
        root.Children.Add(_status);
        Grid.SetRow(_left, 1);
        Grid.SetRow(_right, 1);
        Grid.SetColumn(_right, 1);
        root.Children.Add(_left);
        root.Children.Add(_right);
        const string text = "# Reload regression\n\nThe quick brown fox jumps over the lazy dog.\n\nSecond paragraph remains selectable after reload.\n\n[Native link](https://example.com)";
        _owned.Markdown = _document.Markdown = text;
        AutomationProperties.SetAutomationId(_owned, "OwnedRenderer");
        AutomationProperties.SetAutomationId(_document, "AncestorRenderer");
        RendererDisposalEvidence.Attach(_owned);
        RendererDisposalEvidence.Attach(_document);
        _ancestor.Content = _document;
        _left.Children.Add(_owned);
        _right.Children.Add(_ancestor);
        Content = root;
        root.Loaded += OnLoaded;
    }

    private void BuildSelectionDisableProbe()
    {
        Title = "MarkdownRenderer selection-disable probe";
        const string text = "# Selection disable probe\n\nKeyboard target: keyboardenabledword.\n\nTouch target: touchenabledword.\n\nThis document remains readable in both viewport ownership modes.";
        _owned.Markdown = _document.Markdown = text;
        AutomationProperties.SetAutomationId(_owned, "MarkdownRenderer");
        AutomationProperties.SetAutomationId(_document, "MarkdownRenderer");

        _selectionProbeEnabled = new CheckBox
        {
            Content = "Text selection enabled",
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(_selectionProbeEnabled, "LifecycleSelectionEnabled");
        _selectionProbeEnabled.Checked += (_, _) => ApplySelectionProbeEnabled(true);
        _selectionProbeEnabled.Unchecked += (_, _) => ApplySelectionProbeEnabled(false);

        _selectionProbeAncestor = new CheckBox
        {
            Content = "Use ancestor-owned viewport",
            IsChecked = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(_selectionProbeAncestor, "LifecycleAncestorViewport");
        _selectionProbeAncestor.Checked += (_, _) => SetSelectionProbeViewport(useAncestor: true);
        _selectionProbeAncestor.Unchecked += (_, _) => SetSelectionProbeViewport(useAncestor: false);

        var commands = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        commands.Children.Add(_selectionProbeEnabled);
        commands.Children.Add(_selectionProbeAncestor);

        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetRow(commands, 0);
        Grid.SetRow(_selectionProbeHost, 1);
        _selectionProbeStatus = _status;
        AutomationProperties.SetAutomationId(_status, "LifecycleSelectionStatus");
        root.Children.Add(commands);
        root.Children.Add(_selectionProbeHost);

        var close = new Button
        {
            Content = "Close probe",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(close, "LifecycleCloseProbe");
        close.Click += (_, _) => Close();
        Closed += (_, _) =>
        {
            // This opt-in lifecycle probe owns both renderer instances even
            // though only one is attached at a time. Dispose both on every
            // normal window-close path, as the primary sample window does.
            _ancestor.Content = null;
            _selectionProbeHost.Children.Clear();
            _owned.Dispose();
            _document.Dispose();
        };
        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(_status);
        Grid.SetColumn(close, 1);
        footer.Children.Add(close);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        SetSelectionProbeViewport(useAncestor: false);
        root.Loaded += OnSelectionProbeLoaded;
    }

    private MarkdownRendererControl ActiveSelectionProbeRenderer
        => _selectionProbeUsesAncestor ? _document : _owned;

    private void ApplySelectionProbeEnabled(bool enabled)
    {
        if (_selectionProbeEnabled is null)
            return;
        ActiveSelectionProbeRenderer.IsSelectionEnabled = enabled;
        UpdateSelectionProbeStatus();
    }

    private void SetSelectionProbeViewport(bool useAncestor)
    {
        if (_selectionProbeEnabled is null || _selectionProbeAncestor is null)
            return;

        _selectionProbeUsesAncestor = useAncestor;
        _selectionProbeHost.Children.Clear();
        _ancestor.Content = null;
        if (useAncestor)
        {
            _ancestor.Content = _document;
            _selectionProbeHost.Children.Add(_ancestor);
        }
        else
        {
            _selectionProbeHost.Children.Add(_owned);
        }

        ActiveSelectionProbeRenderer.IsSelectionEnabled = _selectionProbeEnabled.IsChecked == true;
        UpdateSelectionProbeStatus();
    }

    private async void OnSelectionProbeLoaded(object sender, RoutedEventArgs args)
    {
        ((FrameworkElement)sender).Loaded -= OnSelectionProbeLoaded;
        try
        {
            await WaitForCanvasAsync(ActiveSelectionProbeRenderer);
            UpdateSelectionProbeStatus();
        }
        catch (Exception ex) { _status.Text = "FAIL: " + ex.Message; }
    }

    private void UpdateSelectionProbeStatus()
    {
        if (_selectionProbeEnabled is null || _selectionProbeStatus is null)
            return;
        string viewport = _selectionProbeUsesAncestor ? "ancestor" : "owned";
        string selection = ActiveSelectionProbeRenderer.IsSelectionEnabled ? "enabled" : "disabled";
        _selectionProbeStatus.Text = $"viewport={viewport};selection={selection}";
        AutomationProperties.SetName(_selectionProbeStatus, _selectionProbeStatus.Text);
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        ((FrameworkElement)sender).Loaded -= OnLoaded;
        try
        {
            await WaitForCanvasAsync(_owned);
            await WaitForCanvasAsync(_document);
            for (int cycle = 0; cycle < 3; cycle++)
            {
                var previousOwned = FindCanvas(_owned);
                var previousDocument = FindCanvas(_document);
                _left.Children.Clear();
                _right.Children.Clear();
                // Remove the document itself, not only its ancestor viewport.
                _ancestor.Content = null;
                await WaitUntilAsync(() => !_owned.IsLoaded && !_document.IsLoaded);
                await Task.Delay(50);
                _ancestor.Content = _document;
                bool swap = cycle % 2 == 0;
                (swap ? _right : _left).Children.Add(_owned);
                (swap ? _left : _right).Children.Add(_ancestor);
                await WaitForCanvasAsync(_owned);
                await WaitForCanvasAsync(_document);
                if (ReferenceEquals(previousOwned, FindCanvas(_owned)) ||
                    ReferenceEquals(previousDocument, FindCanvas(_document)))
                    throw new InvalidOperationException("Reload reused a removed native canvas.");
            }
            _status.Text = "PASS: 3 reload cycles, both viewport modes";
        }
        catch (Exception ex) { _status.Text = "FAIL: " + ex.Message; }
    }

    private static Task WaitForCanvasAsync(MarkdownRendererControl view)
        => WaitUntilAsync(() => view.IsLoaded && view.CurrentSnapshot is not null &&
            FindCanvas(view) is { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 });

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        long deadline = Environment.TickCount64 + 15_000;
        while (!predicate())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("Native canvas did not complete its lifecycle transition.");
            await Task.Delay(25);
        }
    }

    private static CanvasVirtualControl? FindCanvas(DependencyObject element)
    {
        if (element is CanvasVirtualControl canvas) return canvas;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (FindCanvas(VisualTreeHelper.GetChild(element, i)) is { } child) return child;
        return null;
    }
}
