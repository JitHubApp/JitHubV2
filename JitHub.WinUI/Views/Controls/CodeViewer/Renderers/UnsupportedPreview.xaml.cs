using System;
using JitHub.Models.CodeViewer;
using JitHub.WinUI.ViewModels.CodeViewer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace JitHub.WinUI.Views.Controls.CodeViewer.Renderers;

/// <summary>
/// Shown when a file is too large or an unsupported type.
/// DataContext must be a <see cref="RepoFilePreviewViewModel"/>.
/// </summary>
public sealed partial class UnsupportedPreview : UserControl
{
    public UnsupportedPreview()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private RepoFilePreviewViewModel? ViewModel => DataContext as RepoFilePreviewViewModel;

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        var vm = ViewModel;
        if (vm is null) return;

        BodyText.Text = vm.Kind switch
        {
            RepoFilePreviewKind.TooLarge => "This file is too large to preview here.",
            _ => "We don't support previewing this file type yet.",
        };

        var ext = string.Empty;
        if (vm.CurrentFile?.Path is { } path)
        {
            var dot = path.LastIndexOf('.');
            if (dot >= 0) ext = path[(dot + 1)..];
        }

        MetaText.Text = $"{FormatBytes(vm.ByteSize)}{(ext.Length > 0 ? $"  ·  .{ext}" : string.Empty)}";
    }

    private async void OpenOnGitHub_Click(object sender, RoutedEventArgs e)
    {
        var url = ViewModel?.GitHubBlobUrl;
        if (!string.IsNullOrEmpty(url))
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = ViewModel?.GitHubBlobUrl;
        if (!string.IsNullOrEmpty(url))
        {
            var package = new DataPackage();
            package.SetText(url);
            Clipboard.SetContent(package);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024):F1} MB";
    }
}
