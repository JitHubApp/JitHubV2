using System;
using JitHub.WinUI.ViewModels.Pages;
using Xunit;

namespace JitHub.WinUI.Tests.ViewModels;

public sealed class ShellRouteHistoryTests
{
    [Fact]
    public void PushBackForward_PreservesRouteIdentityAndParameter()
    {
        ShellRouteHistory history = new();
        object parameter = new();
        history.Push(new ShellRouteEntry(ShellWorkspaceTabIdentity.Home(), "Home", typeof(string), null));
        history.Push(new ShellRouteEntry(ShellWorkspaceTabIdentity.Settings(), "Settings", typeof(int), parameter));

        Assert.True(history.TryGoBack(out ShellRouteEntry? home));
        Assert.Equal("home", home!.Identity.Key);
        Assert.True(history.TryGoForward(out ShellRouteEntry? settings));
        Assert.Same(parameter, settings!.Parameter);
    }

    [Fact]
    public void PushAfterBack_DropsForwardHistory()
    {
        ShellRouteHistory history = new();
        history.Push(Route("one"));
        history.Push(Route("two"));
        history.Push(Route("three"));
        Assert.True(history.TryGoBack(out _));

        history.Push(Route("replacement"));

        Assert.False(history.CanGoForward);
        Assert.Equal(3, history.Count);
        Assert.Equal("replacement", history.Current!.Identity.Key);
    }

    [Fact]
    public void BackForward_PreservesSelectionAndScrollViewState()
    {
        ShellRouteHistory history = new();
        history.Push(Route("home"));
        Assert.True(history.UpdateCurrentViewState(new ShellRouteViewState(
            SelectedIndex: 2,
            VerticalOffset: 418.5,
            HorizontalOffset: 0)));
        history.Push(Route("settings"));
        Assert.True(history.UpdateCurrentViewState(new ShellRouteViewState(
            SelectedIndex: 3,
            VerticalOffset: 96,
            HorizontalOffset: 0)));

        Assert.True(history.TryGoBack(out ShellRouteEntry? home));
        Assert.Equal(2, home!.ViewState!.SelectedIndex);
        Assert.Equal(418.5, home.ViewState.VerticalOffset);

        Assert.True(history.TryGoForward(out ShellRouteEntry? settings));
        Assert.Equal(3, settings!.ViewState!.SelectedIndex);
        Assert.Equal(96, settings.ViewState.VerticalOffset);
    }

    [Fact]
    public void UpdateCurrentViewState_WithoutRouteIsRejected()
    {
        ShellRouteHistory history = new();

        Assert.False(history.UpdateCurrentViewState(new ShellRouteViewState(null, 0, 0)));
    }

    [Fact]
    public void BackForward_PreservesExplicitFocusReturnTarget()
    {
        ShellRouteHistory history = new();
        history.Push(Route("issues"));
        Assert.True(history.UpdateCurrentViewState(new ShellRouteViewState(
            SelectedIndex: 4,
            VerticalOffset: 240,
            HorizontalOffset: 0,
            FocusTargetId: "UserProfile_issue_list_author_42_octocat")));
        history.Push(Route("profile"));

        Assert.True(history.TryGoBack(out ShellRouteEntry? issues));
        Assert.Equal(
            "UserProfile_issue_list_author_42_octocat",
            issues!.ViewState!.FocusTargetId);
    }

    private static ShellRouteEntry Route(string key) =>
        new(new ShellWorkspaceTabIdentity(key, key), key, typeof(string), null);
}
