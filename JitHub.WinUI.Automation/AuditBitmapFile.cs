using System.Drawing;

namespace JitHub.WinUI.Automation;

/// <summary>
/// Owns a decoded evidence bitmap and its backing stream. GDI+ filename loading
/// rejects long Sandbox evidence paths even when modern .NET can open them.
/// Keep the stream alive for the full bitmap lifetime, including cache leases.
/// </summary>
internal sealed class AuditBitmapFile : IDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;

    private AuditBitmapFile(FileStream stream, Bitmap bitmap)
    {
        _stream = stream;
        Bitmap = bitmap;
    }

    internal Bitmap Bitmap { get; }

    internal static AuditBitmapFile Open(string path)
    {
        FileStream stream = File.OpenRead(path);
        Bitmap? bitmap = null;
        try
        {
            bitmap = new Bitmap(stream);
            return new AuditBitmapFile(stream, bitmap);
        }
        catch
        {
            try
            {
                bitmap?.Dispose();
            }
            finally
            {
                stream.Dispose();
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            Bitmap.Dispose();
        }
        finally
        {
            _stream.Dispose();
        }
    }
}
