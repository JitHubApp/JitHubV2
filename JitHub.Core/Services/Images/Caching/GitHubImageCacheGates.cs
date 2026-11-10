using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Core.Services.Images.Caching;

/// <summary>
/// Striped gates for the image cache store. One stripe serializes an entry's
/// writers and readers; operations that need an exclusive cache view take all
/// stripes in order.
/// </summary>
internal sealed class GitHubImageCacheGates
{
    private const int StripeCount = 64;
    private readonly SemaphoreSlim[] _stripes = CreateStripes();

    internal SemaphoreSlim ForKey(string cacheKey) => ForPrefix(GitHubImageCacheFiles.HashKey(cacheKey));

    internal SemaphoreSlim ForPrefix(string prefix)
    {
        int stripe = ((GetHexValue(prefix[0]) << 4) | GetHexValue(prefix[1])) % StripeCount;
        return _stripes[stripe];
    }

    /// <summary>Acquires every stripe and returns how many to release.</summary>
    internal async Task<int> AcquireAllAsync(CancellationToken cancellationToken)
    {
        int acquired = 0;
        try
        {
            for (; acquired < _stripes.Length; acquired++)
            {
                await _stripes[acquired].WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return acquired;
        }
        catch
        {
            Release(acquired);
            throw;
        }
    }

    internal void Release(int acquired)
    {
        for (int index = acquired - 1; index >= 0; index--)
        {
            _stripes[index].Release();
        }
    }

    private static SemaphoreSlim[] CreateStripes()
    {
        SemaphoreSlim[] gates = new SemaphoreSlim[StripeCount];
        for (int index = 0; index < gates.Length; index++)
        {
            gates[index] = new SemaphoreSlim(1, 1);
        }

        return gates;
    }

    private static int GetHexValue(char value) => value <= '9' ? value - '0' : value - 'a' + 10;
}
