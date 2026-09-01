using System;
using System.IO;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ActivationThreadingContractTests
{
    [Fact]
    public void SecondaryInstance_StartsRedirectOutsideTheStaEntryPoint()
    {
        string source = ReadProductFile("Program.cs");
        string main = Slice(
            source,
            "private static int Main(string[] args)",
            "private static void RedirectActivationToCurrentInstance");
        string redirect = Slice(
            source,
            "private static void RedirectActivationToCurrentInstance",
            "private static void OnActivated");

        Assert.Contains("RedirectActivationToCurrentInstance(keyInstance, activationArguments);", main, StringComparison.Ordinal);
        Assert.DoesNotContain("RedirectActivationToAsync", main, StringComparison.Ordinal);
        Assert.Contains("Task.Run(async () =>", redirect, StringComparison.Ordinal);
        Assert.Contains("RedirectActivationToAsync(activationArguments)", redirect, StringComparison.Ordinal);
        Assert.Contains("ConfigureAwait(false)", redirect, StringComparison.Ordinal);
    }

    [Fact]
    public void RedirectedPayload_IsProjectedOnlyOnTheWinUiDispatcher()
    {
        string source = ReadProductFile("App.xaml.cs");
        string handle = Slice(
            source,
            "internal void HandleActivation(AppActivationArguments activationArguments)",
            "private void QueueActivation(AppActivationArguments activationArguments)");
        string queue = Slice(
            source,
            "private void QueueActivation(AppActivationArguments activationArguments)",
            "private async Task HandleActivationAsync");

        Assert.DoesNotContain("CreateActivationRequest", handle, StringComparison.Ordinal);
        Assert.Contains("_dispatcherQueue.TryEnqueue(", handle, StringComparison.Ordinal);
        Assert.Contains("() => QueueActivation(activationArguments)", handle, StringComparison.Ordinal);
        Assert.Contains("ActivationRequest activationRequest = CreateActivationRequest(activationArguments);", queue, StringComparison.Ordinal);
        Assert.Contains("try", queue, StringComparison.Ordinal);
        Assert.Contains("catch (Exception exception)", queue, StringComparison.Ordinal);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }

    private static string ReadProductFile(string fileName) =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", fileName));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
