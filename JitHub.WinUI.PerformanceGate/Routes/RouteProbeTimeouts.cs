namespace JitHub.WinUI.PerformanceGate.Routes;

/// <summary>
/// The time limits the route probe waits for elements, route transitions, observable scroll changes and committed text input.
/// </summary>
internal static class RouteProbeTimeouts
{
    internal static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan TransitionTimeout = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan ObservableTimeout = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan ProductPerformanceInputCommitTimeout = TimeSpan.FromSeconds(5);
}
