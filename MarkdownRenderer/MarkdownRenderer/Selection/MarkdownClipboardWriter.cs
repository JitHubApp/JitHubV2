using System;
using Windows.ApplicationModel.DataTransfer;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Selection;

/// <summary>
/// Builds a markdown-source slice from a selection range and places it on the
/// system clipboard.
/// </summary>
public static class MarkdownClipboardWriter
{
    public static bool Copy(MarkdownSourceMap sourceMap, DocumentRange range)
    {
        var slice = sourceMap.Slice(range);
        if (string.IsNullOrEmpty(slice)) return false;

        var pkg = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        pkg.SetText(slice);
        try { Clipboard.SetContent(pkg); }
        catch (Exception) { return false; }
        return true;
    }
}
