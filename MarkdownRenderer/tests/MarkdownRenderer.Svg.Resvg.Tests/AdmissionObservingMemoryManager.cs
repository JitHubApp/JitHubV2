using System.Buffers;

namespace MarkdownRenderer.Svg.Resvg.Tests;

/// <summary>
/// Lends caller-owned SVG bytes as borrowed memory and runs a callback on the
/// first span access, so admission tests can observe when the provider first
/// reads the caller buffer.
/// </summary>
internal sealed class AdmissionObservingMemoryManager(byte[] bytes, Action onFirstAccess) : MemoryManager<byte>
{
    private int _observed;
    public bool WasRead => Volatile.Read(ref _observed) != 0;
    public ReadOnlyMemory<byte> BorrowedMemory => CreateMemory(bytes.Length);
    public override Span<byte> GetSpan()
    {
        if (Interlocked.Exchange(ref _observed, 1) == 0)
            onFirstAccess();
        return bytes;
    }
    public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
    public override void Unpin() { }
    protected override void Dispose(bool disposing) { }
}
