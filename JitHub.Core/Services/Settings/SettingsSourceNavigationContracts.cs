using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public enum SettingsSourceNavigationResult
{
    Success,
    Unavailable,
    Empty,
    Error
}

public sealed record SettingsSourceNavigationOutcome(
    SettingsSourceNavigationResult Result,
    CacheState? CacheState = null);

public interface ISettingsSourceNavigationService
{
    Task<SettingsSourceNavigationOutcome> OpenAsync(CancellationToken cancellationToken = default);
}

internal sealed class UnavailableSettingsSourceNavigationService : ISettingsSourceNavigationService
{
    public static UnavailableSettingsSourceNavigationService Instance { get; } = new();

    private UnavailableSettingsSourceNavigationService()
    {
    }

    public Task<SettingsSourceNavigationOutcome> OpenAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SettingsSourceNavigationOutcome(SettingsSourceNavigationResult.Unavailable));
}
