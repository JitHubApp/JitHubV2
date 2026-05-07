using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.WinUI.UI.Controls;
using CsvHelper;
using CsvHelper.Configuration;
using JitHub.WinUI.ViewModels.CodeViewer;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace JitHub.WinUI.Views.Controls.CodeViewer.Renderers;

/// <summary>
/// Renders CSV / TSV data in a DataGrid (rich) or raw code view (plain).
/// DataContext must be a <see cref="RepoFilePreviewViewModel"/>.
/// </summary>
public sealed partial class CsvPreview : UserControl
{
    private readonly DispatcherQueue _dispatcher;
    private string? _lastText;

    public CsvPreview()
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

        DataGrid.Visibility = rich ? Visibility.Visible : Visibility.Collapsed;
        PlainEditor.Visibility = rich ? Visibility.Collapsed : Visibility.Visible;

        if (!rich)
        {
            PlainEditor.Text = text;
            return;
        }

        // Detect delimiter from file extension
        char delimiter = ',';
        if (vm?.CurrentFile?.Path is { } path &&
            path.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase))
        {
            delimiter = '\t';
        }

        Task.Run(() => ParseCsv(text, delimiter))
            .ContinueWith(t =>
            {
                _dispatcher.TryEnqueue(() =>
                {
                    if (_lastText != text) return;
                    if (t.Exception is not null) return;

                    var (headers, rows) = t.Result;
                    PopulateDataGrid(headers, rows);
                });
            }, TaskScheduler.Default);
    }

    private static (string[] Headers, List<string[]> Rows) ParseCsv(string text, char delimiter)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter.ToString(),
            HasHeaderRecord = true,
            MissingFieldFound = null,
            BadDataFound = null,
        };

        using var reader = new StringReader(text);
        using var csv = new CsvReader(reader, config);

        csv.Read();
        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? [];

        var rows = new List<string[]>();
        while (csv.Read())
        {
            var row = new string[headers.Length];
            for (int i = 0; i < headers.Length; i++)
                row[i] = csv.GetField(i) ?? string.Empty;
            rows.Add(row);
        }

        return (headers, rows);
    }

    private void PopulateDataGrid(string[] headers, List<string[]> rows)
    {
        DataGrid.Columns.Clear();

        for (int i = 0; i < headers.Length; i++)
        {
            DataGrid.Columns.Add(new DataGridTextColumn
            {
                Header = headers[i],
                Binding = new Binding { Path = new PropertyPath($"[{i}]") },
                IsReadOnly = true,
            });
        }

        DataGrid.ItemsSource = rows;
    }
}
