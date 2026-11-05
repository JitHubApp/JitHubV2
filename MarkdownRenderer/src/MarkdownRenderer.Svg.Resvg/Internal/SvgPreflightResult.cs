using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Holds the provider-owned bytes through preflight, worker attachment and the
/// document's admitted render lifetime. Retained completed inspection tasks can
/// refer to this holder without pinning bytes after the document retires it.
/// Metadata remains readable after source retirement.
/// </summary>
internal sealed class SvgPreflightResult(
    SvgSourceBuffer Source,
    byte[] Hash,
    MarkdownSvgDocumentInfo Info,
    int StructuralCost,
    long EmbeddedBytes,
    long EmbeddedPixels,
    int FilterPrimitives)
{
    private SvgSourceBuffer? _source = Source;
    private SvgSnapshotAdmission.Lease? _snapshotAdmission;
    private SvgProviderSnapshotLedger.Lease? _ownership;

    internal void AttachOwnership(SvgProviderSnapshotLedger.Lease ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        if (Interlocked.CompareExchange(ref _ownership, ownership, null) is not null)
            throw new InvalidOperationException("The SVG snapshot already has an ownership ticket.");
        try
        {
            Source.AttachRetirementResource(ownership);
        }
        catch
        {
            Interlocked.CompareExchange(ref _ownership, null, ownership);
            throw;
        }
    }

    internal long SourceReservationBytes => Volatile.Read(ref _snapshotAdmission)?.Bytes ?? 0;

    internal void AttachSnapshotAdmission(SvgSnapshotAdmission.Lease admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (Interlocked.CompareExchange(ref _snapshotAdmission, admission, null) is not null)
            throw new InvalidOperationException("The SVG snapshot already has a process admission reservation.");
        try
        {
            Source.AttachRetirementResource(admission);
        }
        catch
        {
            Interlocked.CompareExchange(ref _snapshotAdmission, null, admission);
            throw;
        }
    }

    public SvgSourceBuffer Source => Volatile.Read(ref _source) ?? SvgSourceBuffer.Empty;
    public byte[] Hash { get; } = Hash;
    public MarkdownSvgDocumentInfo Info { get; } = Info;
    public int StructuralCost { get; } = StructuralCost;
    public long EmbeddedBytes { get; } = EmbeddedBytes;
    public long EmbeddedPixels { get; } = EmbeddedPixels;
    public int FilterPrimitives { get; } = FilterPrimitives;

    // Only the owning open operation (on failure), or a successfully transferred
    // document after its render/cancellation barrier drains, may retire bytes.
    // WorkerPool borrows this holder and must never release it independently.
    internal void ReleaseSource()
    {
        Interlocked.Exchange(ref _source, null)?.Dispose();
        // Snapshot reservations and diagnostic ownership are attached to the
        // source storage itself. Owner retirement refunds them only after every
        // active read lease has drained and native bytes are actually freed.
    }
}
