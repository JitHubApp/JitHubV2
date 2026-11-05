using System;

namespace JitHub.Services.Markdown;

public interface IMarkdownRemoteImagePolicy
{
    MarkdownRemoteImageDecision Evaluate(Uri uri, bool userInitiated);
}
