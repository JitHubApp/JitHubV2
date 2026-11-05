using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;

namespace JitHub.Markdown;

/// <summary>
/// Keeps lifecycle automation deterministic without changing production image resolution.
/// Every source except the fixture's repository-relative image uses the real resolver.
/// </summary>
public class MarkdownLifecycleImageResolver :
    IMarkdownImageSourceByteAdmittedResolver,
    IMarkdownImagePrefetcher
{
    private const string RelativeFixturePath = "docs/images/lifecycle-relative.png";
    private const string BlockedRemoteFixtureUrl =
        "https://example.invalid/jithub-markdown-lifecycle.png";
    private static readonly byte[] RelativeFixtureBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M/wHwAFAgI/azstAAAAAElFTkSuQmCC");

    private readonly IMarkdownImageResolver _inner;

    /// <param name="inner">The real resolver every other source goes to.</param>
    internal MarkdownLifecycleImageResolver(IMarkdownImageResolver inner)
    {
        _inner = inner;
    }

    /// <summary>Wraps <paramref name="inner"/>, keeping its source-lease capability when it has one.</summary>
    /// <param name="inner">The real resolver every other source goes to.</param>
    public static IMarkdownImageResolver Create(IMarkdownImageResolver inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return inner is IMarkdownLeasedImageResolver leased
            ? new LeasedLifecycleImageResolver(leased)
            : new MarkdownLifecycleImageResolver(inner);
    }

    private static MarkdownImageResolution ResolveRelativeFixture() =>
        MarkdownImageResolution.Resolved(new MarkdownImageAsset(
            RelativeFixtureBytes,
            "image/png",
            new Uri("https://github.com/JitHubApp/JitHubV2/blob/main/docs/images/lifecycle-relative.png"),
            "markdown-lifecycle:relative-image"));

    public ValueTask PrefetchAsync(
        IReadOnlyList<string> sources,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        if (_inner is not IMarkdownImagePrefetcher prefetcher)
        {
            return ValueTask.CompletedTask;
        }

        string[] forwarded = sources
            .Where(source =>
                !string.Equals(source.Trim(), RelativeFixturePath, StringComparison.Ordinal) &&
                !string.Equals(source.Trim(), BlockedRemoteFixtureUrl, StringComparison.Ordinal))
            .ToArray();
        return forwarded.Length == 0
            ? ValueTask.CompletedTask
            : prefetcher.PrefetchAsync(forwarded, context, cancellationToken);
    }

    public ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken) =>
        ResolveCoreAsync(source, context, null, cancellationToken);

    public ValueTask<MarkdownImageResolution> ResolveWithSourceByteAdmissionAsync(
        string source,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        return ResolveCoreAsync(source, context, admission, cancellationToken);
    }

    private ValueTask<MarkdownImageResolution> ResolveCoreAsync(
        string source,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(source.Trim(), RelativeFixturePath, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(ResolveRelativeFixture());
        }

        if (string.Equals(source.Trim(), BlockedRemoteFixtureUrl, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(MarkdownImageResolution.Blocked(
                MarkdownImageUnavailableReason.RemoteContentBlocked));
        }

        return admission is not null && _inner is IMarkdownImageSourceByteAdmittedResolver admitted
            ? admitted.ResolveWithSourceByteAdmissionAsync(
                source, context, admission, cancellationToken)
            : _inner.ResolveAsync(source, context, cancellationToken);
    }

    private sealed class LeasedLifecycleImageResolver(IMarkdownLeasedImageResolver leasedInner) :
        MarkdownLifecycleImageResolver(leasedInner),
        IMarkdownLeasedImageResolver
    {
        public async ValueTask<MarkdownImageResolutionLease> ResolveWithSourceLeaseAsync(
            string source,
            MarkdownImageResolveContext context,
            IMarkdownImageSourceByteAdmission admission,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(admission);
            cancellationToken.ThrowIfCancellationRequested();
            string normalized = source.Trim();
            if (string.Equals(normalized, RelativeFixturePath, StringComparison.Ordinal))
            {
                // This tiny immutable fixture is an independently bounded static
                // cache owner. Admit a consumer before exposing its borrowed bytes.
                IDisposable reservation = await admission.ReserveAsync(
                    RelativeFixtureBytes.Length, cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new MarkdownImageResolutionLease(ResolveRelativeFixture(), reservation);
                }
                catch
                {
                    reservation.Dispose();
                    throw;
                }
            }

            if (string.Equals(normalized, BlockedRemoteFixtureUrl, StringComparison.Ordinal))
                return new MarkdownImageResolutionLease(MarkdownImageResolution.Blocked(
                    MarkdownImageUnavailableReason.RemoteContentBlocked), sourceReservation: null);

            return await leasedInner.ResolveWithSourceLeaseAsync(
                source, context, admission, cancellationToken).ConfigureAwait(false);
        }
    }
}
