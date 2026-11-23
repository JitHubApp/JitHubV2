using JitHub.Core.Services.Layout;
using Xunit;

namespace JitHub.Core.Tests.Services.Layout;

public sealed class RepositoryHeaderCommandLayoutTests
{
    [Fact]
    public void ButtonsShowWhenTheHeaderHasRoomBesideItsOtherContent()
    {
        Assert.True(RepositoryHeaderCommandLayout.CanShowStatButtons(900, 600, 260, areStatButtonsShown: false));
    }

    [Fact]
    public void ButtonsMoveToOverflowWhenTheyWouldNotFit()
    {
        Assert.False(RepositoryHeaderCommandLayout.CanShowStatButtons(800, 600, 260, areStatButtonsShown: true));
    }

    [Fact]
    public void ShownButtonsStayAtTheExactWidthThatHiddenButtonsNeedSpareRoomFor()
    {
        Assert.True(RepositoryHeaderCommandLayout.CanShowStatButtons(860, 600, 260, areStatButtonsShown: true));
        Assert.False(RepositoryHeaderCommandLayout.CanShowStatButtons(860, 600, 260, areStatButtonsShown: false));
        Assert.True(RepositoryHeaderCommandLayout.CanShowStatButtons(872, 600, 260, areStatButtonsShown: false));
    }

    [Theory]
    [InlineData(double.NaN, 600, 260)]
    [InlineData(900, double.PositiveInfinity, 260)]
    [InlineData(900, 600, double.NaN)]
    public void UnmeasuredLayoutKeepsButtonsInOverflow(double available, double occupied, double buttons)
    {
        Assert.False(RepositoryHeaderCommandLayout.CanShowStatButtons(available, occupied, buttons, areStatButtonsShown: true));
    }
}
