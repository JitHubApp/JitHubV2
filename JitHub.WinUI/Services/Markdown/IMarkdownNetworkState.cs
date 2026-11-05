namespace JitHub.Services.Markdown;

internal interface IMarkdownNetworkState
{
    bool IsOnline { get; }

    bool IsMetered { get; }
}
