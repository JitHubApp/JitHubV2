using System;
using System.Threading.Tasks;
using System.Xml.Linq;
using JitHub.WinUI.ViewModels.CodeViewer;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.CodeViewer.Renderers;

/// <summary>
/// Renders XML with optional pretty-printing via a rich/plain toggle.
/// DataContext must be a <see cref="RepoFilePreviewViewModel"/>.
/// </summary>
public sealed partial class XmlPreview : UserControl
{
    private readonly DispatcherQueue _dispatcher;
    private string? _lastText;

    public XmlPreview()
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

        Task.Run(() =>
        {
            string pretty;
            try
            {
                pretty = XDocument.Parse(text).ToString(SaveOptions.None);
            }
            catch
            {
                pretty = text;
            }
            return pretty;
        }).ContinueWith(t =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (_lastText == text)
                    Editor.Text = t.Result;
            });
        }, TaskScheduler.Default);
    }
}
