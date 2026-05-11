using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using JitHub.WinUI.ViewModels.CodeViewer;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.CodeViewer.Renderers;

/// <summary>
/// Renders JSON with optional pretty-printing via a rich/plain toggle.
/// DataContext must be a <see cref="RepoFilePreviewViewModel"/>.
/// </summary>
public sealed partial class JsonPreview : UserControl
{
    private readonly DispatcherQueue _dispatcher;
    private string? _lastText;

    public JsonPreview()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        DataContextChanged += OnDataContextChanged;
    }

    private RepoFilePreviewViewModel? ViewModel => DataContext as RepoFilePreviewViewModel;

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        SyncSegmented();
        UpdateContent();
    }

    private void SyncSegmented()
    {
        ViewModeSegmented.SelectedIndex = (ViewModel?.ShowRichPreview ?? true) ? 0 : 1;
    }

    private void ViewModeSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null) return;
        bool wantsRich = ViewModeSegmented.SelectedIndex == 0;
        if (vm.ShowRichPreview != wantsRich)
            vm.ShowRichPreview = wantsRich;
        UpdateContent();
    }

    private void UpdateContent()
    {
        var vm = ViewModel;
        var text = vm?.Text ?? string.Empty;
        var rich = vm?.ShowRichPreview ?? true;
        _lastText = text;

        if (!rich)
        {
            Editor.Text = text;
            return;
        }

        // Pretty-print on background thread
        Task.Run(() =>
        {
            string pretty;
            try
            {
                using var doc = JsonDocument.Parse(text);
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    doc.RootElement.WriteTo(writer);
                }

                pretty = Encoding.UTF8.GetString(stream.ToArray());
            }
            catch
            {
                pretty = text; // fall back to raw on parse failure
            }
            return pretty;
        }).ContinueWith(t =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                // Only apply if the text hasn't changed while we were parsing
                if (_lastText == text)
                    Editor.Text = t.Result;
            });
        }, TaskScheduler.Default);
    }
}
