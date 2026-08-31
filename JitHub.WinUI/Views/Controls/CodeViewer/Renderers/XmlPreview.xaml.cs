using System;
using System.Threading.Tasks;
using System.Xml.Linq;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using JitHub.WinUI.ViewModels.CodeViewer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.CodeViewer.Renderers;

/// <summary>
/// Renders XML with optional pretty-printing via a rich/plain toggle.
/// DataContext must be a <see cref="RepoFilePreviewViewModel"/>.
/// </summary>
public sealed partial class XmlPreview : UserControl
{
    private string? _lastText;

    public event Action<string, string>? ActionCompleted;

    public XmlPreview()
    {
        InitializeComponent();
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
        {
            vm.ShowRichPreview = wantsRich;
            ActionCompleted?.Invoke(
                wantsRich ? RepoCodeTelemetryActions.XmlRichView : RepoCodeTelemetryActions.XmlPlainView,
                TelemetryTaxonomy.Results.Success);
        }
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

        UiTaskGuard.Run(async () =>
        {
            string pretty = await Task.Run(() =>
            {
                try
                {
                    return XDocument.Parse(text).ToString(SaveOptions.None);
                }
                catch (System.Xml.XmlException)
                {
                    return text;
                }
            });

            if (_lastText == text)
            {
                Editor.Text = pretty;
            }
        }, "ui-xml-preview");
    }
}
