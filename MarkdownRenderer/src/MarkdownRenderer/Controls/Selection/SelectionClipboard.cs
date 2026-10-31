using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Windows.Storage.Streams;
using Windows.ApplicationModel.DataTransfer;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Controls.Selection;

/// <summary>
/// Writes text, image and table targets to the clipboard.
/// </summary>
internal static class SelectionClipboard
{
    internal static bool CopyTextTargetToClipboard(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static async Task<bool> CopyImageTargetToClipboardAsync(
        string? imageUrl,
        CanvasBitmap? bitmap)
    {
        if (bitmap is null && string.IsNullOrWhiteSpace(imageUrl))
            return false;

        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            using var stream = new InMemoryRandomAccessStream();
            bool hasBitmap = false;
            if (bitmap is { } sourceBitmap)
            {
                await sourceBitmap.SaveAsync(
                    stream,
                    CanvasBitmapFileFormat.Png,
                    1f);
                stream.Seek(0);
                package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
                hasBitmap = true;
            }
            else if (Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri? uri) &&
                     uri.Scheme is "http" or "https" or "file" or "ms-appx")
            {
                package.SetBitmap(RandomAccessStreamReference.CreateFromUri(uri));
                hasBitmap = true;
            }

            if (!string.IsNullOrWhiteSpace(imageUrl))
                package.SetText(imageUrl);
            if (!hasBitmap && string.IsNullOrWhiteSpace(imageUrl))
                return false;

            Clipboard.SetContent(package);
            Clipboard.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool CopyTableTargetToClipboard(MarkdownTableClipboardPayload payload)
    {
        if (string.IsNullOrEmpty(payload.Text))
            return false;

        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(payload.Text);
            package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(payload.Html));
            Clipboard.SetContent(package);
            Clipboard.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static async Task<bool> CopyTableTargetToClipboardAsync(
        MarkdownTableClipboardSnapshot snapshot)
    {
        MarkdownTableClipboardPayload payload =
            await Task.Run(() => snapshot.Format()).ConfigureAwait(true);
        return CopyTableTargetToClipboard(payload);
    }

    internal static MarkdownTableClipboardSnapshot BuildTableClipboardSnapshot(
        Layout.Boxes.TableBox table)
    {
        var rows = new string[table.RowCount][];
        for (int row = 0; row < rows.Length; row++)
        {
            var cells = new string[table.ColumnCount];
            Array.Fill(cells, string.Empty);
            rows[row] = cells;
        }

        foreach (var cell in table.GetCellInfos())
            rows[cell.Row][cell.Column] = GetInlineClipboardText(cell.Box);

        return new MarkdownTableClipboardSnapshot(rows, table.HeaderRowCount);
    }

    internal static string GetInlineClipboardText(Layout.Boxes.InlineContainerBox box)
    {
        var text = new StringBuilder();
        foreach (InlineRun run in box.Runs)
        {
            text.Append(run is InlineImageRun or InlineEmbedRun or InlineVectorSceneRun
                ? run.AccessibleText
                : run.Text);
        }
        return text.ToString();
    }
}
