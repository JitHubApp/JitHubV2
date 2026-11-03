namespace MarkdownRenderer.Mermaid;

/// <summary>A non-throwing result from decoding an untrusted MMIR buffer.</summary>
internal sealed class MermaidSceneDecodeResult
{
    internal MermaidSceneDecodeResult(
        MermaidSceneDecodeStatus status,
        MermaidScene? scene,
        int errorOffset,
        string? error)
    {
        Status = status;
        Scene = scene;
        ErrorOffset = errorOffset;
        Error = error;
    }

    public MermaidSceneDecodeStatus Status { get; }

    public MermaidScene? Scene { get; }

    public int ErrorOffset { get; }

    public string? Error { get; }

    public bool IsSuccess => Status == MermaidSceneDecodeStatus.Success && Scene is not null;
}
