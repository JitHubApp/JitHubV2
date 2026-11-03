namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// The fixed request and response frame buffers that one worker transport
/// reuses for every exchange. The transport clears both after each exchange
/// so no request or response field outlives it.
/// </summary>
internal sealed class WorkerExchangeFrames
{
    internal byte[] Request { get; } = new byte[WorkerProtocol.RequestSize];

    internal byte[] Response { get; } = new byte[WorkerProtocol.ResponseSize];
}
