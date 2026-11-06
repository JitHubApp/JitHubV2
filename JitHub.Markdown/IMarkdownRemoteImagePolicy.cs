using System;

namespace JitHub.Markdown;

public interface IMarkdownRemoteImagePolicy
{
    MarkdownRemoteImageDecision Evaluate(Uri uri, bool userInitiated);
}
