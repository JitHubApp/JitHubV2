namespace MarkdownRenderer.Svg.Resvg.Tests;

/// <summary>The transport failures the worker fault tests inject from the pipe peer.</summary>
public enum TransportFault
{
    WrongRequestId,
    MalformedResponse,
    PartialResponse,
    PipeLoss,
}
