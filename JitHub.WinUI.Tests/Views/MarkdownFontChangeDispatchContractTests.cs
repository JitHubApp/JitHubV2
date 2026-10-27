using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class MarkdownFontChangeDispatchContractTests
{
    [Fact]
    public void PendingNotificationIsClearedBeforePublishingTheGeneration()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        string source = File.ReadAllText(Path.Combine(directory.FullName,
            "JitHub.WinUI", "Services", "Markdown", "JitHubMarkdownRuntime.cs"));
        int dispatchStart = source.IndexOf("internal static Task NotifyFontsChangedAsync()", StringComparison.Ordinal);
        int dispatchEnd = source.IndexOf("internal static Task ShutdownAsync()", dispatchStart, StringComparison.Ordinal);
        Assert.True(dispatchStart >= 0 && dispatchEnd > dispatchStart);
        string dispatch = source[dispatchStart..dispatchEnd];
        int clearPending = dispatch.IndexOf("_fontChangeTask = null;", StringComparison.Ordinal);
        int publishGeneration = dispatch.IndexOf("renderer.NotifyFontsChanged();", StringComparison.Ordinal);
        Assert.True(clearPending >= 0 && publishGeneration > clearPending);
        Assert.Contains("lock (Gate)", dispatch, StringComparison.Ordinal);
        Assert.Contains("!_isShuttingDown && ReferenceEquals(renderer, _svgRenderer)", dispatch, StringComparison.Ordinal);
    }
}
