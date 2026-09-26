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

    internal RendererLifecycleProbeWindow()
    {
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
