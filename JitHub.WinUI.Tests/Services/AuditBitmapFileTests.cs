using System.Drawing;
using System.Drawing.Imaging;
using JitHub.WinUI.Automation;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class AuditBitmapFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jithub-audit-bitmap-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenPreservesPixelsAndStreamUntilDisposal(bool longPath)
    {
        string path = CreatePath(longPath);
        using (var image = new Bitmap(3, 2, PixelFormat.Format32bppArgb))
        {
            image.SetPixel(1, 1, Color.FromArgb(128, 21, 87, 142));
            using FileStream output = File.Create(path);
            image.Save(output, ImageFormat.Png);
        }

        var file = AuditBitmapFile.Open(path);
        try
        {
            Assert.Equal(3, file.Bitmap.Width);
            Assert.Equal(2, file.Bitmap.Height);
            Assert.Equal(Color.FromArgb(128, 21, 87, 142).ToArgb(), file.Bitmap.GetPixel(1, 1).ToArgb());
            // Decoding may remain lazy: drawing must still work after Open returns.
            using var destination = new Bitmap(3, 2, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(destination))
                graphics.DrawImageUnscaled(file.Bitmap, 0, 0);
            Assert.Equal(128, destination.GetPixel(1, 1).A);
            Assert.Throws<IOException>(() =>
            {
                using FileStream unexpected = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            });
        }
        finally
        {
            file.Dispose();
            file.Dispose();
        }

        // Neither successful decode nor repeated disposal leaves a file handle.
        using FileStream exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void InvalidImageReleasesTheOpenedFile()
    {
        string path = CreatePath(longPath: true);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        Assert.Throws<ArgumentException>(() => AuditBitmapFile.Open(path));
        using FileStream exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void MissingFileFailsWithoutCreatingEvidence()
    {
        string path = CreatePath(longPath: true);
        Assert.Throws<FileNotFoundException>(() => AuditBitmapFile.Open(path));
        Assert.False(File.Exists(path));
    }

    private string CreatePath(bool longPath)
    {
        string directory = longPath
            ? Path.Combine(_root, new string('a', 100), new string('b', 100), new string('c', 80))
            : _root;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "same-byte-source-tile-0000.png");
        if (longPath)
            Assert.True(path.Length > 260);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
