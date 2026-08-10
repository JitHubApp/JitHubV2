using System;
using System.IO;
using Xunit;

namespace MarkdownRenderer.PixelTests;

public sealed class PixelComparerTests
{
    [Fact]
    public void SaveRgbaAsPng_SupportsDeepArtifactPaths()
    {
        string root = Path.Combine(Path.GetTempPath(), $"mdr-pixel-path-{Guid.NewGuid():N}");
        string directory = root;
        while (directory.Length < 280)
        {
            directory = Path.Combine(directory, "nested-artifact-segment");
        }

        string path = Path.Combine(directory, "pixel.png");
        try
        {
            PixelComparer.SaveRgbaAsPng([255, 0, 0, 255], 1, 1, path);

            Assert.True(File.Exists(path));
            var (rgba, width, height) = PixelComparer.LoadPngAsRgba(path);
            Assert.Equal(1, width);
            Assert.Equal(1, height);
            Assert.Equal([255, 0, 0, 255], rgba);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
