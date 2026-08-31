namespace JitHub.WinUI
{
    internal static class Program
    {
        public static LaunchOptions CurrentLaunchOptions { get; set; } = new();
    }
}

namespace JitHub.Services
{
    public interface IGitHubService
    {
        void SetAccessToken(string? token);
    }

    public class NavigationService
    {
        public int UnauthorizedCallCount { get; private set; }

        public void Unauthorized()
        {
            UnauthorizedCallCount++;
        }
    }
}
