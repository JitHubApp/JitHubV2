using System;
using System.Text;
using System.Threading.Tasks;
using JitHub.WinUI.ViewModels.CodeViewer;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.CodeViewer.Renderers;

/// <summary>
/// Renders binary files as a 16-byte-per-row hex dump with ASCII gutter.
/// DataContext must be a <see cref="RepoFilePreviewViewModel"/>.
/// </summary>
public sealed partial class HexPreview : UserControl
{
    private const int BytesPerRow = 16;

    private readonly DispatcherQueue _dispatcher;
    private byte[]? _lastBytes;

    public HexPreview()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        DataContextChanged += OnDataContextChanged;
    }

    private RepoFilePreviewViewModel? ViewModel => DataContext as RepoFilePreviewViewModel;

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        var vm = ViewModel;
        var bytes = vm?.Bytes;
        _lastBytes = bytes;

        HeaderText.Text = bytes is { Length: > 0 }
            ? $"{bytes.Length:N0} bytes"
            : "0 bytes";

        if (bytes is not { Length: > 0 })
        {
            HexEditor.Text = string.Empty;
            return;
        }

        Task.Run(() => BuildHexDump(bytes))
            .ContinueWith(t =>
            {
                _dispatcher.TryEnqueue(() =>
                {
                    if (_lastBytes == bytes)
                        HexEditor.Text = t.Result;
                });
            }, TaskScheduler.Default);
    }

    private static string BuildHexDump(byte[] bytes)
    {
        // Pre-allocate: each row has offset(8) + 2 spaces + 3*16 hex + 1 space + 16 ascii + newline
        // ~75 chars per row, one row per 16 bytes
        int rows = (bytes.Length + BytesPerRow - 1) / BytesPerRow;
        var sb = new StringBuilder(rows * 76);

        // Header line
        sb.Append("Offset   ");
        for (int i = 0; i < BytesPerRow; i++)
            sb.Append($"{i:X2} ");
        sb.Append(" ASCII\n");
        sb.Append(new string('-', 76));
        sb.Append('\n');

        for (int row = 0; row < rows; row++)
        {
            int offset = row * BytesPerRow;
            sb.Append($"{offset:X8}  ");

            int count = Math.Min(BytesPerRow, bytes.Length - offset);

            // Hex section
            for (int i = 0; i < count; i++)
                sb.Append($"{bytes[offset + i]:X2} ");

            // Pad if last row is partial
            for (int i = count; i < BytesPerRow; i++)
                sb.Append("   ");

            sb.Append(' ');

            // ASCII section
            for (int i = 0; i < count; i++)
            {
                byte b = bytes[offset + i];
                sb.Append(b >= 32 && b < 127 ? (char)b : '.');
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }
}
