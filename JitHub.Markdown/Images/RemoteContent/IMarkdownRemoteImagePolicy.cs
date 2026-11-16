using System;

namespace JitHub.Markdown.Images.RemoteContent;

public interface IMarkdownRemoteImagePolicy
{
    MarkdownRemoteImageDecision Evaluate(Uri uri, bool userInitiated);
}
