using System;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Compares public resolver identity against the effective document scope,
/// without mistaking a correctly bound progressive wrapper for a replacement.
/// </summary>
internal static class RasterPlacementResolverFence
{
    internal static bool Matches(
        IMarkdownImageResolver? currentPublicResolver,
        IMarkdownImageResolver? capturedEffectiveResolver,
        IMarkdownPerformanceSessionInternal? currentSession,
        IMarkdownPerformanceSessionInternal? capturedSession,
        MarkdownImageResolveContext currentResolveContext,
        MarkdownImageResolveContext capturedResolveContext)
    {
        ArgumentNullException.ThrowIfNull(currentResolveContext);
        ArgumentNullException.ThrowIfNull(capturedResolveContext);
        if (!ReferenceEquals(currentSession, capturedSession) ||
            currentSession?.IsDisposed == true ||
            !Equals(currentResolveContext, capturedResolveContext))
            return false;

        if (capturedEffectiveResolver is IMarkdownPerformanceDocumentScope scope)
        {
            if (currentSession is null || currentPublicResolver is null ||
                scope.IsDisposed ||
                !scope.Matches(currentSession, currentPublicResolver, currentResolveContext))
                return false;

            // Disposal may race the binding check on a background thread.
            return !currentSession.IsDisposed && !scope.IsDisposed;
        }

        // The built-in URI loader is selected by an intentional null pair.
        return ReferenceEquals(currentPublicResolver, capturedEffectiveResolver);
    }
}
