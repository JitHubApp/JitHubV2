using System;
using System.Threading;

namespace MarkdownRenderer.Performance;

internal interface IMarkdownScenePreparationLease : IDisposable
{
    CancellationToken CancellationToken { get; }
}
