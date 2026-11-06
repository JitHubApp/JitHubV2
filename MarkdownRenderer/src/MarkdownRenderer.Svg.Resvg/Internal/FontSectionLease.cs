namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>One installed-font section and the worker's duplicated handle to it.</summary>
internal sealed class FontSectionLease(
    SafeFileMappingHandle section,
    WorkerMappingLease remoteHandle,
    ulong byteLength)
{
    internal SafeFileMappingHandle Section { get; } = section;
    internal WorkerMappingLease RemoteHandle { get; } = remoteHandle;
    internal ulong ByteLength { get; } = byteLength;
}
