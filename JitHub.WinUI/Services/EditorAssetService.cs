using System;
using System.IO;
using System.Threading.Tasks;

namespace JitHub.Services;

public sealed class EditorAssetService
{
    private string? _resolvedEditorRootPath;

    public Task<string> GetEditorRootPathAsync()
    {
        if (!string.IsNullOrWhiteSpace(_resolvedEditorRootPath) &&
            File.Exists(Path.Combine(_resolvedEditorRootPath, "index.html")))
        {
            return Task.FromResult(_resolvedEditorRootPath);
        }

        string editorRootPath = Path.Combine(AppContext.BaseDirectory, "Assets", "dist");
        string editorIndexPath = Path.Combine(editorRootPath, "index.html");
        if (!File.Exists(editorIndexPath))
        {
            throw new FileNotFoundException(
                "Embedded editor assets were not found. Run .\\sync-vscode-assets.ps1 before building, publishing, or debugging JitHub.WinUI.",
                editorIndexPath);
        }

        _resolvedEditorRootPath = editorRootPath;
        return Task.FromResult(editorRootPath);
    }
}
