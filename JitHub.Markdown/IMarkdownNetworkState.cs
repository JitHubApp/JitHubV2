namespace JitHub.Markdown;

internal interface IMarkdownNetworkState
{
    bool IsOnline { get; }

    bool IsMetered { get; }
}
