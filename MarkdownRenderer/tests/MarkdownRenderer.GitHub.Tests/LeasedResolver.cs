using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A leased image resolver that counts its leased calls and returns the
/// lease made by the callback the test passes in. Its unleased resolve
/// path reports the source as not handled.
/// </summary>
internal sealed class LeasedResolver(
    Func<string, IMarkdownImageSourceByteAdmission, CancellationToken,
        ValueTask<MarkdownImageResolutionLease>> resolve) : IMarkdownLeasedImageResolver
{
    private int _callCount;
    internal int CallCount => Volatile.Read(ref _callCount);

    public ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(MarkdownImageResolution.NotHandled);

    public ValueTask<MarkdownImageResolutionLease> ResolveWithSourceLeaseAsync(
        string source,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        return resolve(source, admission, cancellationToken);
    }
}
