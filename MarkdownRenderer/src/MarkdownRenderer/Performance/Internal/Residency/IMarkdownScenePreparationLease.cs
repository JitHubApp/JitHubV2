using System;
using System.Threading;

namespace MarkdownRenderer.Performance.Internal.Residency;

internal interface IMarkdownScenePreparationLease : IDisposable
{
    CancellationToken CancellationToken { get; }
}
