namespace JitHub.Markdown.Images.RemoteContent.Internal;

internal interface IMarkdownNetworkState
{
    bool IsOnline { get; }

    bool IsMetered { get; }
}
